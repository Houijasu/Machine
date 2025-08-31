using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;

using Machine.Core;
using Machine.MoveGen;
using Machine.Tables;
using Machine.Threading;

namespace Machine.Search;

public sealed class SearchEngine
{
    private readonly ITranspositionTable _tt;
    private Position _position;
    private volatile bool _stopRequested;
    private SearchLimits? _limits;

    private int _currentHashSize;
    // Threading
    private int _threadCount = 1;
    // Root-split infrastructure
    private ConcurrentQueue<(Move move, int moveIndex)>? _rootMoveQueue;
    private ConcurrentDictionary<int, int>? _rootMoveScores;
    private Move[]? _rootMoves;
    private volatile bool _useRootSplit;
    private bool _rootSplitEnabled = true;

    private bool _lastUsedRootSplit;

    public void SetRootSplit(bool enabled) => _rootSplitEnabled = enabled;

    private readonly List<Thread> _helperThreads = [];
    private readonly PVTable _pvTable = new();

    // atomic counters for SMP
    private ulong _nodesSearched;
    private ulong _qNodesSearched;
    private int _selectiveDepth;

    // Debug instrumentation counters
    private ulong _nullMoveCutoffs;
    private ulong _futilityPrunes;
    private ulong _razoringCutoffs;
    private ulong _probCutCutoffs;
    private ulong _singularExtensions;
    private ulong _checkExtensions;
    private ulong _lmrReductions;

    // Global debug enable (readable from other components in DEBUG builds)
    private static SearchEngine? _instance;
    public static bool DebugEnabled => _instance?.EnableDebugInfo == true;
    public bool EnableDebugInfo { get; set; } = false;

    // Aspiration window last score
    private int _lastScore = int.MinValue;

    // Feature toggles (UCI configurable)
    public bool UseNullMove { get; private set; } = true;
    public bool UseFutility { get; private set; } = true;
    public bool UseAspiration { get; private set; } = true;
    public bool UseRazoring { get; private set; } = true;
    public bool UseExtensions { get; private set; } = true;
    public bool UseProbCut { get; private set; } = true;
    public bool UseSingularExtensions { get; private set; } = true;


    public void SetFeature(string name, bool enabled)
    {
        switch (name)
        {
            case nameof(UseNullMove): UseNullMove = enabled; break;
            case nameof(UseFutility): UseFutility = enabled; break;
            case nameof(UseAspiration): UseAspiration = enabled; break;
            case nameof(UseRazoring): UseRazoring = enabled; break;
            case nameof(UseExtensions): UseExtensions = enabled; break;
            case nameof(UseProbCut): UseProbCut = enabled; break;
            case nameof(UseSingularExtensions): UseSingularExtensions = enabled; break;
        }
    }

    public ulong NodesSearched { get; private set; }
    public ulong QNodesSearched { get; private set; }
    public int SelectiveDepth { get; private set; }

    public SearchEngine(int hashSizeMb = 16, bool useAtomicTT = false)
    {
        _tt = useAtomicTT ? new AtomicTranspositionTable(hashSizeMb) : new TranspositionTable(hashSizeMb);
        _currentHashSize = hashSizeMb;
        _position = new Position();
        _instance = this; // publish for debug-only helpers
    }

    public void SetPosition(Position position)
    {
        _position = position;
    }

    public void Stop()
    {
        _stopRequested = true;
    }

    public void SetThreads(int threads)
    {
        _threadCount = Math.Clamp(threads, 1, 512);

        // Auto-scale hash based on thread count
        int recommendedHash = threads switch
        {
            <= 2 => 16,
            <= 4 => 64,
            <= 8 => 128,
            <= 16 => 256,
            _ => 512
        };
        if (_currentHashSize != recommendedHash)
        {
            ResizeHash(recommendedHash);
            _currentHashSize = recommendedHash;
        }
    }

    [Conditional("DEBUG")]
    public void DebugLog(string kind, int depth, int ply, int alpha, int beta, int eval, string detail)
    {
        if (!EnableDebugInfo) return;
        Console.WriteLine($"info string dbg kind={kind} d={depth} ply={ply} a={alpha} b={beta} e={eval} {detail}");
    }

    public void ClearHash()
    {
        _tt.Clear();
    }

    public void ResizeHash(int sizeMb)
    {
        // reset atomic counters for this search
        _nodesSearched = 0;
        _qNodesSearched = 0;
        _selectiveDepth = 0;

        _tt.Resize(sizeMb);
    }

    public SearchResult Search(SearchLimits limits)
    {
        _limits = limits;
        _stopRequested = false;
        var sw = Stopwatch.StartNew();

        NodesSearched = 0;
        QNodesSearched = 0;
        SelectiveDepth = 0;

        if (EnableDebugInfo)
            ResetDebugCounters();

        var result = new SearchResult();

        try
        {
                // Launch helper threads for LazySMP if requested
                if (_threadCount > 1 && _helperThreads.Count == 0)
                {
                    for (int i = 1; i < _threadCount; i++)
                    {
                        var helperPos = _position.Clone();
                        int threadIdx = i; // use thread index for diversification
                        var thread = new Thread(() => HelperThreadLoop(helperPos, limits, threadIdx)) { IsBackground = true };
                        _helperThreads.Add(thread);
                        thread.Start();
                    }
                }

            // Iterative deepening framework
            for (int depth = 1; depth <= limits.MaxDepth && !ShouldStop(); depth++)
            {
                var iterResult = SearchAtDepth(depth);

                if (!ShouldStop())
                {
                    result = iterResult;
                    result.Depth = depth;
                    result.NodesSearched = NodesSearched + QNodesSearched;
                    result.SelectiveDepth = SelectiveDepth;
                    result.SearchTime = sw.Elapsed;

                    // Build PV from TT for robust principal variation (handles TT cutoffs)
                    var pvMoves = BuildPV(_position.Clone(), 32);

                    if (pvMoves.Length > 0)
                        result.BestMove = pvMoves[0];

                    Console.WriteLine(BuildInfoLine(depth, result.Score, result.SearchTime, result.NodesSearched, SelectiveDepth, pvMoves.AsSpan()));

                    // Print debug info if enabled
                    if (EnableDebugInfo && depth >= 5)
                    {
                        Console.WriteLine(GetDebugInfo());
                    }
                }

                // Check time limits
                if (limits.TimeLimit.HasValue && DateTime.UtcNow >= limits.StartTime.Add(limits.TimeLimit.Value))
                    break;

                // Stop on mate found
                if (Math.Abs(result.Score) >= 29000)
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }


    private SearchResult SearchAtDepth(int depth, int threadIdx = 0)

    {
        const int alpha = -30000;
        const int beta = 30000;

        // Enable root-split from the main thread only
        _lastUsedRootSplit = false;
        if (_rootSplitEnabled && depth >= 2 && _threadCount > 1 && threadIdx == 0)
        {
            _lastUsedRootSplit = true;
            return SearchAtDepthRootSplit(depth);
        }

        var result = new SearchResult { Depth = depth };

        int windowAlpha = alpha;
        int windowBeta = beta;

        // Root aspiration windows around lastScore
        if (UseAspiration && _lastScore != int.MinValue && depth >= 4)
        {
            int delta = 40; // 40cp initial half-window
            windowAlpha = Math.Max(alpha, _lastScore - delta);
            windowBeta = Math.Min(beta, _lastScore + delta);
        }

        // Light diversification for helpers remains
        if (depth > 3 && threadIdx > 0)
        {
            int aspirationOffset = threadIdx * 50;
            if ((threadIdx & 1) == 1) windowBeta = Math.Min(beta, windowBeta + aspirationOffset);
            else windowAlpha = Math.Max(alpha, windowAlpha - aspirationOffset);
        }

        int score;
        while (true)
        {
            _pvTable.Clear();
            score = AlphaBeta.Search(_position, depth, windowAlpha, windowBeta, this, _tt, 0, true, _pvTable);

            if (score <= windowAlpha)
            {
                // Fail-low: widen downwards
                int expand = (windowBeta - windowAlpha) * 2;
                windowAlpha = Math.Max(alpha, windowAlpha - expand);
            }
            else if (score >= windowBeta)
            {
                // Fail-high: widen upwards
                int expand = (windowBeta - windowAlpha) * 2;
                windowBeta = Math.Min(beta, windowBeta + expand);
            }
            else break;
        }

        _lastScore = score;

        if (!_stopRequested)
        {
            result.Score = score;
        // Rebuild PV if root-split was used
            _lastUsedRootSplit = true;

        if (_lastUsedRootSplit)
        {
            var clone = _position.Clone();
            var pvMoves = BuildPV(clone, 32);
            _pvTable.Clear();
            for (int i = 0; i < pvMoves.Length; i++) _pvTable.Set(0, i, pvMoves[i]);
        }

            result.BestMove = _pvTable.GetBestMove();
            if (result.BestMove.Equals(Move.NullMove))
                result.BestMove = _tt.GetBestMove(_position);
        }

        return result;
    }
    private SearchResult SearchAtDepthRootSplit(int depth)
    {
        // Generate root moves once
        Span<Move> moveBuffer = stackalloc Move[256];
        int moveCount = MoveGenerator.GenerateMoves(_position, moveBuffer);

        _rootMoves = new Move[moveCount];
        _rootMoveQueue = new ConcurrentQueue<(Move move, int moveIndex)>();
        _rootMoveScores = new ConcurrentDictionary<int, int>();

        int legalCount = 0;
        for (int i = 0; i < moveCount; i++)
        {
            var move = moveBuffer[i];
            _position.ApplyMove(move);
            Color moved = _position.SideToMove == Color.White ? Color.Black : Color.White;
            bool legal = !_position.IsKingInCheck(moved);
            _position.UndoMove(move);

            if (legal)
            {
                _rootMoves[legalCount] = move;
                _rootMoveQueue.Enqueue((move, legalCount));
                legalCount++;
            }
        }

        // Signal helpers root-split is active and queue is ready
        _useRootSplit = true;

        // Wait for helpers to process queue
        while (_rootMoveQueue.Count > 0 && !ShouldStop())
            Thread.Sleep(1);

        // Aggregate results
        Move bestMove = Move.NullMove;
        int bestScore = -30000;
        foreach (var kvp in _rootMoveScores)
        {
            if (kvp.Value > bestScore)
            {
                bestScore = kvp.Value;
                bestMove = _rootMoves[kvp.Key];
            }
        }

        // Done with root-split for this depth
        _useRootSplit = false;

        // Store best in TT for PV
        _tt.Store(_position, bestMove, bestScore, depth, TTFlag.Exact);

        return new SearchResult
        {
            BestMove = bestMove,
            Score = bestScore,
            Depth = depth
        };
    }



    private string BuildInfoLine(int depth, int score, TimeSpan elapsed, ulong nodes, int selDepth, ReadOnlySpan<Move> pv)
    {
        long ms = Math.Max(1, (long)elapsed.TotalMilliseconds);
        ulong nps = (ulong)((nodes * 1000UL) / (ulong)ms);
        int hashfull = _tt.GetHashFull();
        var sb = new StringBuilder();
        sb.Append($"info depth {depth} seldepth {selDepth} time {ms} nodes {nodes} nps {nps} hashfull {hashfull} ");
        if (Math.Abs(score) >= 29000)
        {
            int mate = (30000 - Math.Abs(score) + 1) / 2;
            if (score < 0) mate = -mate;
            sb.Append($"score mate {mate} ");
        }
        else
        {
            sb.Append($"score cp {score} ");
        }
        sb.Append("pv");
        for (int i = 0; i < pv.Length; i++)
        {
            sb.Append(' ').Append(pv[i].ToString());
        }
        return sb.ToString();
    }

    private Move[] BuildPV(Position pos, int maxLen)
    {
        var pv = new System.Collections.Generic.List<Move>(Math.Min(maxLen, 32)); // Reasonable PV limit
        Span<Move> moveBuffer = stackalloc Move[256]; // Move allocation outside loop
        var seenPositions = new System.Collections.Generic.HashSet<ulong>(); // Repetition detection

        for (int i = 0; i < Math.Min(maxLen, 32); i++) // Cap at 32 moves to prevent excessive work
        {
            var best = _tt.GetBestMove(pos);
            if (best.Equals(Move.NullMove) || best.From < 0) break;

            // Check for position repetition before making move
            ulong currentKey = pos.ZobristKey;
            if (seenPositions.Contains(currentKey)) break;
            seenPositions.Add(currentKey);

            // Verify move is legal in current position
            int n = MoveGenerator.GenerateMoves(pos, moveBuffer);
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                var m = moveBuffer[k];
                if (m.From == best.From && m.To == best.To && m.Flag == best.Flag)
                {
                    pos.ApplyMove(m);
                    Color moved = pos.SideToMove == Color.White ? Color.Black : Color.White;
                    bool legal = !pos.IsKingInCheck(moved);
                    pos.UndoMove(m);
                    if (!legal) { found = false; break; }
                    found = true;
                    break;
                }
            }
            if (!found) break;

            pv.Add(best);
            pos.ApplyMove(best);
        }

        // Undo all moves in reverse order
        for (int i = pv.Count - 1; i >= 0; i--)
            pos.UndoMove(pv[i]);

        return pv.ToArray();
    }

    public bool ShouldStop()
    {
        if (_stopRequested) return true;
        if (_limits is null) return false;
        if (_limits.TimeLimit.HasValue && !_limits.Infinite)
        {
            var hard = _limits.StartTime.Add(_limits.TimeLimit.Value);
            // Soft cap: try to avoid stopping exactly at cap; give 5ms slack for finishing PV
            var soft = hard - TimeSpan.FromMilliseconds(5);
            if (DateTime.UtcNow >= hard) return true;
            // Optionally, could return false at soft to allow finishing current move; keep simple for now
        }
        if (_limits.NodeLimit.HasValue)
        {
            var total = NodesSearched + QNodesSearched;
            if (total >= _limits.NodeLimit.Value) return true;
        }
        return false;
    }

    private void HelperThreadLoop(Position pos, SearchLimits limits, int threadIdx)
    {
        try
        {
            int startDepth = threadIdx > 4 ? 2 : 1; // skip very shallow for some helpers
            for (int depth = startDepth; depth <= limits.MaxDepth && !ShouldStop(); depth++)
            {
                // Root-split: process queued root moves when active
                if (_useRootSplit && _rootMoveQueue != null && _rootMoveScores != null)
                {
                    while (_rootMoveQueue.TryDequeue(out var task) && !ShouldStop())
                    {
                        pos.ApplyMove(task.move);
                        int score = -AlphaBeta.Search(pos, depth - 1, -30000, 30000, this, _tt, 1, true, null);
                        pos.UndoMove(task.move);
                        _rootMoveScores.AddOrUpdate(task.moveIndex, score, (key, oldValue) => Math.Max(oldValue, score));
                    }
                    // Wait until root-split deactivates or more tasks appear
                    while (_useRootSplit && !ShouldStop() && (_rootMoveQueue == null || _rootMoveQueue.IsEmpty))
                        Thread.Sleep(1);
                }
                else
                {
                    int alpha = -30000, beta = 30000;

                    if (depth > 3)
                    {
                        int aspirationOffset = threadIdx * 75;
                        if ((threadIdx & 1) == 1) beta = Math.Min(30000, beta + aspirationOffset);
                        else alpha = Math.Max(-30000, alpha - aspirationOffset);
                    }
                    AlphaBeta.Search(pos, depth, alpha, beta, this, _tt, 0, true, null);
                }
            }
        }
        catch
        {
            // Swallow exceptions in helpers to avoid crashing main thread
        }
    }



    public void UpdateStats(int depth)
    {
        Interlocked.Increment(ref _nodesSearched);
        InterlockedExtensions.Max(ref _selectiveDepth, depth);
        // Publish sampled counters for reporting; avoids volatile reads in hot path
        NodesSearched = _nodesSearched;
        SelectiveDepth = _selectiveDepth;
    }

    public void UpdateQStats()
    {
        Interlocked.Increment(ref _qNodesSearched);
        QNodesSearched = _qNodesSearched;
    }

    // Debug instrumentation update methods
    public void IncrementNullMoveCutoffs() => Interlocked.Increment(ref _nullMoveCutoffs);
    public void IncrementFutilityPrunes() => Interlocked.Increment(ref _futilityPrunes);
    public void IncrementRazoringCutoffs() => Interlocked.Increment(ref _razoringCutoffs);
    public void IncrementProbCutCutoffs() => Interlocked.Increment(ref _probCutCutoffs);
    public void IncrementSingularExtensions() => Interlocked.Increment(ref _singularExtensions);
    public void IncrementCheckExtensions() => Interlocked.Increment(ref _checkExtensions);
    public void IncrementLMRReductions() => Interlocked.Increment(ref _lmrReductions);

    public string GetDebugInfo()
    {
        if (!EnableDebugInfo) return string.Empty;
        return $"info string debug NullCuts:{_nullMoveCutoffs} Futility:{_futilityPrunes} Razor:{_razoringCutoffs} " +
               $"ProbCut:{_probCutCutoffs} Singular:{_singularExtensions} CheckExt:{_checkExtensions} LMR:{_lmrReductions}";
    }

    private void ResetDebugCounters()
    {
        _nullMoveCutoffs = 0;
        _futilityPrunes = 0;
        _razoringCutoffs = 0;
        _probCutCutoffs = 0;
        _singularExtensions = 0;
        _checkExtensions = 0;
        _lmrReductions = 0;
    }
}

public sealed class SearchResult
{
    public Move BestMove { get; set; }
    public int Score { get; set; }
    public int Depth { get; set; }
    public int SelectiveDepth { get; set; }
    public ulong NodesSearched { get; set; }
    public TimeSpan SearchTime { get; set; }
    public string? Error { get; set; }
}

public sealed class SearchLimits
{
    public int MaxDepth { get; set; } = 64;
    public TimeSpan? TimeLimit { get; set; }
    public ulong? NodeLimit { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public bool Infinite { get; set; }
}