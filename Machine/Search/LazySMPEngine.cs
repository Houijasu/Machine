using System;
using System.Collections.Generic;
using System.Threading;
using Machine.Core;
using Machine.Tables;

namespace Machine.Search;

// Pure LazySMP: N independent root searches sharing only the TT
public sealed class LazySMPEngine
{
    private readonly int _threads;
    private readonly ITranspositionTable _sharedTT;
    private readonly LazySMPMetrics _metrics;

    // Diversification params
    private readonly int _aspirationDelta;
    private readonly bool _depthSkipping;
    private readonly bool _nullMoveVariation;
    private readonly bool _showInfo;
    private readonly bool _showMetrics;


        // Depth completion tracking for UCI reporting
        private int _completedDepth = 0;
        private readonly Move[] _depthBestMoves = new Move[64];
        private readonly int[] _depthBestScores = new int[64];

    public LazySMPEngine(int threads, ITranspositionTable sharedTT, int aspirationDelta, bool depthSkipping, bool nullMoveVariation, bool showInfo, bool showMetrics)
    {
        _threads = Math.Max(1, threads);
        _sharedTT = sharedTT;
        _aspirationDelta = aspirationDelta;
        _depthSkipping = depthSkipping;
        _nullMoveVariation = nullMoveVariation;
        _showInfo = showInfo;
        _showMetrics = showMetrics;
        _metrics = new LazySMPMetrics(_threads, _sharedTT);
    }

    public SearchResult Search(Position root, SearchLimits limits)
    {
        var start = DateTime.UtcNow;
        var hardDeadline = limits.TimeLimit.HasValue ? start + limits.TimeLimit.Value : (DateTime?)null;

        // Shared best result
        var bestMove = Move.NullMove;
        int bestScore = int.MinValue;
        var bestLock = new object();

        // Global stop flag
        bool stop = false;

        // Workers list
        var threads = new List<Thread>(_threads);

        // Shared node counters [regular, qnodes]
        long[] totalNodes = new long[2];

        // Print threads active (only if info enabled)
        if (_showInfo)
            Console.WriteLine($"info string threads_active {_threads}");

        // Periodic info thread
        var infoThread = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Thread.Sleep(500);
                if (!_showInfo && !_showMetrics) continue; // Skip if both disabled

                lock (bestLock)
                {
                    if (!bestMove.Equals(Move.NullMove))
                    {
                        var elapsed = DateTime.UtcNow - start;
                        long nodes = Volatile.Read(ref totalNodes[0]) + Volatile.Read(ref totalNodes[1]);
                        long nps = nodes * 1000 / Math.Max(1, (long)elapsed.TotalMilliseconds);

                        if (_showInfo)
                            Console.WriteLine($"info string threads_active {_threads} currmove {bestMove} nodes {nodes} nps {nps}");

                        if (_showMetrics && (DateTime.UtcNow - start).TotalMilliseconds % 2000 < 600) // roughly every 2s
                            _metrics.PrintMetrics();
                    }
                }
            }
        }) { IsBackground = true };
        infoThread.Start();

        // Worker entry
        void Worker(int id)
        {
            try
            {
                var pos = root.Clone();
                var engine = new SearchEngine(_sharedTT); // worker engine sharing TT
                var tls = new ThreadLocalSearchState(id);
                tls.Clear();
                MoveOrdering.SetThreadState(tls);
                long prevReg = 0, prevQ = 0;

                // Initial window
                int baseAlpha = -30000, baseBeta = 30000;

                int startDepth = 1;
                if (_depthSkipping) startDepth = (id % 4) + 1;

                // Allocate buffer once outside the loop to avoid CA2014
                Span<Move> pvBuffer = stackalloc Move[256];
                
                for (int depth = startDepth; depth <= limits.MaxDepth && !Volatile.Read(ref stop); depth++)
                {
                    // Early terminate on time
                    if (hardDeadline.HasValue && DateTime.UtcNow >= hardDeadline.Value) break;

                    // Diversified aspiration
                    int alpha = baseAlpha, beta = baseBeta;
                    if (depth >= 4)
                    {
                        int off = id * _aspirationDelta;
                        if ((id & 1) == 1) beta = Math.Min(baseBeta, beta + off);
                        else alpha = Math.Max(baseAlpha, alpha - off);
                    }

                    // Toggle null-move R via engine flag (coarse)
                    if (_nullMoveVariation && (id & 1) == 1) engine.SetFeature(nameof(engine.UseNullMove), true);

                    int score;
                    var pvTable = tls.PV;

                    // Iterative aspiration loop
                    while (true)
                    {
                        pvTable.Clear();
                        score = AlphaBeta.Search(pos, depth, alpha, beta, engine, _sharedTT, 0, true, pvTable, _metrics, id);
                        if (score <= alpha) { int expand = (beta - alpha) * 2; alpha = Math.Max(baseAlpha, alpha - expand); }
                        else if (score >= beta) { int expand = (beta - alpha) * 2; beta = Math.Min(baseBeta, beta + expand); }
                        else break;
                    }

                    // Update global best
                    var move = pvTable.GetBestMove();
                    _metrics.RecordBestMove(move);
                    lock (bestLock)
                    {
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestMove = move;
                        }

                        // Depth completion reporting: first thread to complete depth reports it
                        if (depth > Volatile.Read(ref _completedDepth))
                        {
                            _completedDepth = depth;
                            _depthBestMoves[depth] = move;
                            _depthBestScores[depth] = score;

                            // ALWAYS emit standard UCI depth info (not gated by _showInfo)
                            // Rebuild PV from TT for consistent output
                            var pvMoves = new System.Collections.Generic.List<Move>(32);
                            var clone = root.Clone();
                            // Apply move sequence from TT using SearchEngine helper
                            // We use SearchEngine.BuildPV-like reconstruction via a minimal local method to avoid cross-deps
                            // Using pvBuffer allocated outside the loop
                            int safety = 0;
                            while (safety++ < 32)
                            {
                                var ttBest = _sharedTT.GetBestMove(clone);
                                if (ttBest.Equals(Move.NullMove)) break;
                                pvMoves.Add(ttBest);
                                clone.ApplyMove(ttBest);
                            }
                            for (int i = pvMoves.Count - 1; i >= 0; i--) clone.UndoMove(pvMoves[i]);

                            // Fallback to current thread PV if TT-based PV fails
                            if (pvMoves.Count == 0)
                            {
                                var pvArray = tls.PV.GetPV();
                                pvMoves.AddRange(pvArray);
                            }

                            // Emit UCI info line for this depth (match standard format)
                            long nodesSoFar = Volatile.Read(ref totalNodes[0]) + Volatile.Read(ref totalNodes[1]);
                            var elapsed = DateTime.UtcNow - start;
                            long ms = Math.Max(1, (long)elapsed.TotalMilliseconds);
                            long nps = nodesSoFar * 1000 / ms;
                            int hashfull = _sharedTT.GetHashFull();
                            int seldepth = engine.SelectiveDepth;
                            
                            var sb = new System.Text.StringBuilder();
                            sb.Append($"info depth {depth} seldepth {seldepth} time {ms} nodes {nodesSoFar} nps {nps} hashfull {hashfull} score cp {score} pv");
                            foreach (var mv in pvMoves)
                                sb.Append(' ').Append(mv.ToString());
                            Console.WriteLine(sb.ToString());
                        }
                    }

                    // Accumulate nodes after this iteration
                    long reg = (long)engine.NodesSearched;
                    long q = (long)engine.QNodesSearched;
                    Interlocked.Add(ref totalNodes[0], reg - prevReg);
                    Interlocked.Add(ref totalNodes[1], q - prevQ);
                    _metrics.RecordNodes(id, (reg - prevReg) + (q - prevQ));
                    prevReg = reg;
                    prevQ = q;

                    // Early stop on mate found
                    if (Math.Abs(score) >= 29000)
                    {
                        Volatile.Write(ref stop, true);
                        break;
                    }

                    // Node/time limit checks
                    long curNodes = Volatile.Read(ref totalNodes[0]) + Volatile.Read(ref totalNodes[1]);
                    if (limits.NodeLimit.HasValue && (ulong)curNodes >= limits.NodeLimit.Value)
                    {
                        Volatile.Write(ref stop, true);
                        break;
                    }
                }
            }
            catch
            {
                // Swallow to avoid crashing whole search
            }
        }

        // Launch workers (threads-1 in background), run one inline
        for (int i = 1; i < _threads; i++)
        {
            int tid = i;
            var t = new Thread(() => Worker(tid)) { IsBackground = true };
            threads.Add(t);
            t.Start();
        }
        // Run main worker on current thread
        Worker(0);

        // Join helpers
        foreach (var t in threads)
        {
            if (t.IsAlive) t.Join();
        }

        // Stop info thread
        Volatile.Write(ref stop, true);
        Thread.Sleep(10);

        // Build result with aggregated nodes
        long total = Volatile.Read(ref totalNodes[0]) + Volatile.Read(ref totalNodes[1]);
        return new SearchResult
        {
            BestMove = bestMove,
            Score = bestScore,
            Depth = limits.MaxDepth,
            NodesSearched = (ulong)total,
            SearchTime = DateTime.UtcNow - start
        };
    }
}

