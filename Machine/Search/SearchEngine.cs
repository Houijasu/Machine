using System;
using System.Collections.Generic;
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
    private ITranspositionTable _tt;
    private PawnHashTable? _pawnHash;
    private EvalCache? _evalCache;
    private SyzygyTablebase? _syzygyTablebase;
    private Position _position;
    private volatile bool _stopRequested;
    private SearchLimits? _limits;

    private int _currentHashSize;
    // Threading
    private int _threadCount = 1;
    private int _multiPV = 1; // Number of PVs to report
    // Root-split infrastructure



    private readonly PVTable _pvTable = new();

        // Thread-local search state for main thread
        private ThreadLocalSearchState? _mainState;

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
    public static SearchEngine? Instance => _instance;
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

    // History pruning options
    public bool UseHistoryPruning { get; private set; } = true;
    public int HistoryPruningMinQuietIndex { get; private set; } = 4; // Skip first N quiet moves
    public int HistoryPruningThreshold { get; private set; } = 50; // Min history score to avoid pruning
    public int HistoryPruningMaxDepth { get; private set; } = 3; // Max depth to apply pruning

    // Zobrist key verification
    public bool ZKeyAudit { get; private set; } = false;
    public int ZKeyAuditInterval { get; private set; } = 4096; // Check every N nodes
    public bool ZKeyAuditStopOnMismatch { get; private set; } = true;
    
    // Dynamic pruning options
    private bool _dynamicPruning = true;
    private int _pruningAggressiveness = 100; // 100% = normal


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
            case nameof(UseHistoryPruning): UseHistoryPruning = enabled; break;
        }
    }

    public void SetHistoryPruningMinQuietIndex(int value) => HistoryPruningMinQuietIndex = Math.Max(0, Math.Min(16, value));
    public void SetHistoryPruningThreshold(int value) => HistoryPruningThreshold = Math.Max(0, Math.Min(10000, value));
    public void SetHistoryPruningMaxDepth(int value) => HistoryPruningMaxDepth = Math.Max(1, Math.Min(6, value));

    public void SetZKeyAudit(bool enabled) => ZKeyAudit = enabled;
    public void SetZKeyAuditInterval(int value) => ZKeyAuditInterval = Math.Max(1, Math.Min(1000000, value));
    public void SetZKeyAuditStopOnMismatch(bool enabled) => ZKeyAuditStopOnMismatch = enabled;
    
    public void SetDynamicPruning(bool enabled) => _dynamicPruning = enabled;
    public void SetPruningAggressiveness(int aggressiveness) => _pruningAggressiveness = Math.Clamp(aggressiveness, 50, 200);

    public ulong NodesSearched { get; private set; }
    public ulong QNodesSearched { get; private set; }

	    // LazySMP options
	    private int _lazyAspirationDelta = 25;
	    private bool _lazyDepthSkipping = true;
	    private bool _lazyNullMoveVariation = true;
	    private bool _lazyShowInfo = false;
	    private bool _lazyShowMetrics = false;
	    public void SetLazyAspirationDelta(int delta) => _lazyAspirationDelta = Math.Max(0, delta);
	    public void SetLazyDepthSkipping(bool enabled) => _lazyDepthSkipping = enabled;
	    public void SetLazyNullMoveVariation(bool enabled) => _lazyNullMoveVariation = enabled;

	    // Work-stealing options
	    private bool _wsEnabled = true;  // Now default for multi-threaded
	    private int _wsMinSplitDepth = 5;
	    private int _wsMinSplitMoves = 4;
	    private bool _wsShowMetrics = false;
	    private ThreadPool? _wsPool;
	    public void SetWorkStealing(bool enabled) => _wsEnabled = enabled;
	    public void SetWorkStealingThresholds(int minDepth, int minMoves) { _wsMinSplitDepth = minDepth; _wsMinSplitMoves = minMoves; }
	    public void SetWorkStealingMinSplitDepth(int minDepth) { _wsMinSplitDepth = minDepth; }
	    public void SetWorkStealingMinSplitMoves(int minMoves) { _wsMinSplitMoves = minMoves; }
	    public void SetWorkStealingShowMetrics(bool enabled) { _wsShowMetrics = enabled; }

	    public void SetLazyShowInfo(bool enabled) => _lazyShowInfo = enabled;
	    public void SetLazyShowMetrics(bool enabled) => _lazyShowMetrics = enabled;

	    // Kill switch - force LazySMP even when WS is default
	    private bool _useLazySMP = false;
	    public void SetUseLazySMP(bool forceLazy) => _useLazySMP = forceLazy;
	    
	    // Multi-PV support
	    public void SetMultiPV(int multiPV) => _multiPV = Math.Clamp(multiPV, 1, 10);
	    public int GetMultiPV() => _multiPV;

    public int SelectiveDepth { get; private set; }

    public SearchEngine(int hashSizeMb = 16)
    {
        _tt = new TranspositionTable(hashSizeMb);
        _pawnHash = new PawnHashTable(4); // 4 MB default
        _evalCache = new EvalCache(8); // 8 MB default
        _syzygyTablebase = new SyzygyTablebase(); // Initialize with empty path
        _currentHashSize = hashSizeMb;
        _position = new Position();
        _instance = this; // publish for debug-only helpers
    }

    // internal worker ctor sharing TT and caches
    internal SearchEngine(ITranspositionTable tt)
    {
        _tt = tt;
        // Share caches from main thread (accessed through GetPawnHash/GetEvalCache)
        _currentHashSize = 16;
        _position = new Position();
        _instance = this;
    }

    public void SetPosition(Position position)
    {
        _position = position;
    }


	    public ITranspositionTable GetTranspositionTable() => _tt;
	    public PawnHashTable? GetPawnHash() => _pawnHash;
	    public EvalCache? GetEvalCache() => _evalCache;
/*
	    public (long probes, long hits, double hitRate) GetTTStats()
	    {
	        var stats = _tt.GetStats();
	        // Using reflection-safe layout: fields exposed in TTStats
	        var ttStatsType = stats.GetType();
	        long probes = (long)ttStatsType.GetField("Probes")!.GetValue(stats)!;
	        long hits = (long)ttStatsType.GetField("Hits")!.GetValue(stats)!;
	        double rate = probes > 0 ? (double)hits / probes : 0.0;
	        return (probes, hits, rate);
	    }
*/

	    public (long probes, long hits, double hitRate) GetTTStats()
	    {
	        var s = _tt.GetStats();
	        return (s.Probes, s.Hits, s.HitRate);
	    }
	    
	    public TTStats GetDetailedTTStats()
	    {
	        return _tt.GetStats();
	    }
	    
	    public PawnHashStats GetPawnHashStats()
	    {
	        return _pawnHash?.GetStats() ?? new PawnHashStats(0, 0, 0, 0, 0, 0, 0, 0);
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

    public void ResizePawnHash(int sizeMB)
    {
        _pawnHash?.Resize(sizeMB);
    }

    public void ResizeEvalCache(int sizeMB)
    {
        _evalCache?.Resize(sizeMB);
    }

    public SearchResult Search(SearchLimits limits)
    {
        // Check for tablebase hit in root position
        if (_syzygyTablebase?.IsEnabled() == true && CountPieces(_position) <= _syzygyTablebase.GetMaxCardinality())
        {
            var (tbScore, tbMove, tbFound) = _syzygyTablebase.Probe(_position, 0, 0);
            if (tbFound)
            {
                var result = new SearchResult
                {
                    BestMove = tbMove,
                    Score = tbScore,
                    Depth = 0,
                    NodesSearched = 0,
                    SelectiveDepth = 0,
                    SearchTime = TimeSpan.Zero
                };
                Console.WriteLine($"info string tablebase score cp {tbScore} dtz 0");
                Console.WriteLine($"bestmove {tbMove}");
                return result;
            }
        }
        
        // Initialize multi-PV results
        var multiPVResults = new List<MultiPVResult>();
        // Check for tablebase hit in root position
        if (_syzygyTablebase?.IsEnabled() == true && CountPieces(_position) <= _syzygyTablebase.GetMaxCardinality())
        {
            var (tbScore, tbMove, tbFound) = _syzygyTablebase.Probe(_position, 0, 0);
            if (tbFound)
            {
                var result = new SearchResult
                {
                    BestMove = tbMove,
                    Score = tbScore,
                    Depth = 0,
                    NodesSearched = 0,
                    SelectiveDepth = 0,
                    SearchTime = TimeSpan.Zero
                };
                Console.WriteLine($"info string tablebase score cp {tbScore} dtz 0");
                Console.WriteLine($"bestmove {tbMove}");
                return result;
            }
        }

	        // Initialize thread-local state for main thread
	        _mainState ??= new ThreadLocalSearchState(0);
	        _mainState.Clear();
	        MoveOrdering.SetThreadState(_mainState);

	        // Notify caches of new search
	        _evalCache?.NewSearch();

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
            // For multi-threaded runs, choose parallel mode
            if (_threadCount > 1)
            {
                // UseLazySMP kill switch overrides WS default
                if (_wsEnabled && !_useLazySMP)
                {
                    // Initialize work-stealing pool for this search
                    _wsPool?.Dispose();
                    _wsPool = new ThreadPool(_threadCount, this, _tt);
                    WorkStealingRuntime.Enabled = true;
                    WorkStealingRuntime.MinSplitDepth = _wsMinSplitDepth;
                    WorkStealingRuntime.MinSplitMoves = _wsMinSplitMoves;
                    WorkStealingRuntime.SetPool(_wsPool);  // Register pool with runtime!
                    _wsPool.StartSearch();

                    // Run single-threaded master search (splits occur inside AlphaBeta)
                    var resultWS = new SearchResult();
                    for (int depth = 1; depth <= limits.MaxDepth && !ShouldStop(); depth++)
                    {
                        // Decay history at each iteration
                        if (depth > 1) MoveOrdering.DecayHistory();

                        var iter = SearchAtDepth(depth);
                        if (!ShouldStop())
                        {
                            // Mirror single-thread reporting
                            resultWS = iter;
                            resultWS.Depth = depth;
                            resultWS.NodesSearched = NodesSearched + QNodesSearched;
                            resultWS.SelectiveDepth = SelectiveDepth;
                            resultWS.SearchTime = sw.Elapsed;

                            // Print metrics periodically if enabled
                            if (_wsShowMetrics && _wsPool?.Metrics != null)
                            {
                                _wsPool.Metrics.PrintIfDue();
                            }

                            // Build PV from TT for robust principal variation (handles TT cutoffs)
                            var pvMoves = BuildPV(_position.Clone(), 32);
                            if (pvMoves.Length == 0)
                            {
                                // Fallback to live PVTable when TT path is unavailable/racy
                                var pvFallback = (_mainState?.PV ?? _pvTable).GetPV();
                                pvMoves = pvFallback;
                            }
                            if (pvMoves.Length > 0)
                                resultWS.BestMove = pvMoves[0];

                            Console.WriteLine(BuildInfoLine(depth, resultWS.Score, resultWS.SearchTime, resultWS.NodesSearched, SelectiveDepth, pvMoves.AsSpan()));

                            // Optional debug dump
                            if (EnableDebugInfo && depth >= 5)
                                Console.WriteLine(GetDebugInfo());
                        }
                    }

                    // Tear down pool
                    _wsPool?.StopSearch();

                    // Print final metrics if enabled
                    if (_wsShowMetrics && _wsPool?.Metrics != null)
                    {
                        _wsPool.Metrics.PrintIfDue(force: true);
                    }

                    _wsPool?.Dispose();
                    _wsPool = null;
                    WorkStealingRuntime.SetPool(null);  // Clear pool reference
                    WorkStealingRuntime.Enabled = false;
                    return resultWS;
                }
                else
                {
                    var lazy = new LazySMPEngine(_threadCount, _tt, _lazyAspirationDelta, _lazyDepthSkipping, _lazyNullMoveVariation, _lazyShowInfo, _lazyShowMetrics);
                    var lazyResult = lazy.Search(_position, limits);
                    return lazyResult;
                }
            }

            // Iterative deepening framework (single-thread)
            for (int depth = 1; depth <= limits.MaxDepth && !ShouldStop(); depth++)
            {
                // Decay history at each iteration
                if (depth > 1) MoveOrdering.DecayHistory();

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
                    if (pvMoves.Length == 0)
                    {
                        // Fallback to live PVTable when TT path is unavailable/racy
                        var pvFallback = (_mainState?.PV ?? _pvTable).GetPV();
                        pvMoves = pvFallback;
                    }

                    if (pvMoves.Length > 0)
                        result.BestMove = pvMoves[0];
                        
                    // Store multi-PV result
                    multiPVResults.Add(new MultiPVResult
                    {
                        Depth = depth,
                        Score = result.Score,
                        BestMove = result.BestMove,
                        PV = pvMoves
                    });
                    
                    // Output multi-PV info
                    if (_multiPV > 1)
                    {
                        for (int i = 0; i < Math.Min(_multiPV, multiPVResults.Count); i++)
                        {
                            var pvResult = multiPVResults[i];
                            Console.WriteLine(BuildMultiPVInfoLine(i + 1, pvResult.Depth, pvResult.Score, result.SearchTime, result.NodesSearched, SelectiveDepth, pvResult.PV.AsSpan()));
                        }
                    }
                    else
                    {
                        Console.WriteLine(BuildInfoLine(depth, result.Score, result.SearchTime, result.NodesSearched, SelectiveDepth, pvMoves.AsSpan()));
                    }

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

        // Ensure all thread-local counters are flushed
        FlushLocalCounters();

        return result;
    }


    private SearchResult SearchAtDepth(int depth)

    {
        const int alpha = -30000;
        const int beta = 30000;


        var result = new SearchResult { Depth = depth };

        int windowAlpha = alpha;
        int windowBeta = beta;

        // Aspiration windows around lastScore
        if (UseAspiration && _lastScore != int.MinValue && depth >= 4)
        {
            // Dynamic window size based on depth and position
            int baseDelta = 40;
            int depthDelta = depth * 5; // Increase window with depth
            int ttDelta = 0;
            
            // Use TT score if available for better window
            var ttEntry = _tt.Probe(_position);
            if (ttEntry.IsValid && ttEntry.Depth >= depth - 2)
            {
                if (ttEntry.Flag == TTFlag.Exact)
                {
                    _lastScore = ttEntry.Score;
                    ttDelta = 20; // Smaller window when TT has exact score
                }
                else if (ttEntry.Flag == TTFlag.Beta)
                {
                    windowAlpha = Math.Max(alpha, ttEntry.Score);
                    ttDelta = 30;
                }
                else if (ttEntry.Flag == TTFlag.Alpha)
                {
                    windowBeta = Math.Min(beta, ttEntry.Score);
                    ttDelta = 30;
                }
            }
            
            int delta = Math.Min(baseDelta + depthDelta + ttDelta, 200); // Cap at 200cp
            windowAlpha = Math.Max(alpha, _lastScore - delta);
            windowBeta = Math.Min(beta, _lastScore + delta);
        }


        int score;
        while (true)
        {
            // Use main thread-local PV if available; fallback to shared PVTable
            var pvTable = _mainState?.PV ?? _pvTable;
            pvTable.Clear();
            score = AlphaBeta.Search(_position, depth, windowAlpha, windowBeta, this, _tt, 0, true, pvTable, null, 0);

            if (score <= windowAlpha)
            {
                // Fail-low: widen downwards
                int expand = (windowBeta - windowAlpha) * 2;
                windowAlpha = Math.Max(alpha, windowAlpha - expand);
                
                // If we fail low multiple times, consider using TT score
                if (ttEntry.IsValid && ttEntry.Flag == TTFlag.Alpha && ttEntry.Score > windowAlpha)
                {
                    windowAlpha = Math.Max(windowAlpha, ttEntry.Score);
                }
            }
            else if (score >= windowBeta)
            {
                // Fail-high: widen upwards
                int expand = (windowBeta - windowAlpha) * 2;
                windowBeta = Math.Min(beta, windowBeta + expand);
                
                // If we fail high multiple times, consider using TT score
                if (ttEntry.IsValid && ttEntry.Flag == TTFlag.Beta && ttEntry.Score < windowBeta)
                {
                    windowBeta = Math.Min(windowBeta, ttEntry.Score);
                }
            }
            else break;
            
            // Limit the number of aspiration window iterations
            if (windowBeta - windowAlpha > 800) // 800cp max window
                break;
        }

        _lastScore = score;

        if (!_stopRequested)
        {
            result.Score = score;
            var clone = _position.Clone();
            var pvMoves = BuildPV(clone, 32);
            var pvTable = _mainState?.PV ?? _pvTable;
            pvTable.Clear();
            for (int i = 0; i < pvMoves.Length; i++) pvTable.Set(0, i, pvMoves[i]);

            result.BestMove = (_mainState?.PV ?? _pvTable).GetBestMove();
            if (result.BestMove.Equals(Move.NullMove))
                result.BestMove = _tt.GetBestMove(_position);
        }

        // Ensure thread-local counters are flushed
        FlushLocalCounters();

        return result;
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
    
    private string BuildMultiPVInfoLine(int pvNum, int depth, int score, TimeSpan elapsed, ulong nodes, int selDepth, ReadOnlySpan<Move> pv)
    {
        long ms = Math.Max(1, (long)elapsed.TotalMilliseconds);
        ulong nps = (ulong)((nodes * 1000UL) / (ulong)ms);
        int hashfull = _tt.GetHashFull();
        var sb = new StringBuilder();
        sb.Append($"info multipv {pvNum} depth {depth} seldepth {selDepth} time {ms} nodes {nodes} nps {nps} hashfull {hashfull} ");
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




    public void UpdateStats(int depth)
    {
        // Fast path: thread-local aggregation, flush occasionally
        var tls = MoveOrdering_GetTLS();
        if (tls != null)
        {
            int n = ++tls.LocalNodes;
            if (depth > tls.LocalSelDepth) tls.LocalSelDepth = depth;
            if ((n & 2047) == 0) // every 2048 nodes
            {
                Interlocked.Add(ref _nodesSearched, (ulong)tls.LocalNodes);
                InterlockedExtensions.Max(ref _selectiveDepth, tls.LocalSelDepth);
                tls.LocalNodes = 0;
                tls.LocalSelDepth = 0;
                // Publish sampled counters for reporting
                NodesSearched = _nodesSearched;
                SelectiveDepth = _selectiveDepth;
            }
            return;
        }
        // Fallback (no TLS set): use atomics
        Interlocked.Increment(ref _nodesSearched);
        InterlockedExtensions.Max(ref _selectiveDepth, depth);
        NodesSearched = _nodesSearched;
        SelectiveDepth = _selectiveDepth;
    }

    public void UpdateQStats()
    {
        var tls = MoveOrdering_GetTLS();
        if (tls != null)
        {
            int q = ++tls.LocalQNodes;
            if ((q & 2047) == 0)
            {
                Interlocked.Add(ref _qNodesSearched, (ulong)tls.LocalQNodes);
                tls.LocalQNodes = 0;
                QNodesSearched = _qNodesSearched;
            }
            return;
        }
        Interlocked.Increment(ref _qNodesSearched);
        QNodesSearched = _qNodesSearched;
    }

    private static ThreadLocalSearchState? MoveOrdering_GetTLS()
    {
        return MoveOrdering.GetThreadState();
    }

    private void FlushLocalCounters()
    {
        // Flush any remaining thread-local counters
        var tls = MoveOrdering_GetTLS();
        if (tls != null && (tls.LocalNodes > 0 || tls.LocalQNodes > 0))
        {
            if (tls.LocalNodes > 0)
            {
                Interlocked.Add(ref _nodesSearched, (ulong)tls.LocalNodes);
                tls.LocalNodes = 0;
            }
            if (tls.LocalQNodes > 0)
            {
                Interlocked.Add(ref _qNodesSearched, (ulong)tls.LocalQNodes);
                tls.LocalQNodes = 0;
            }
            if (tls.LocalSelDepth > 0)
            {
                InterlockedExtensions.Max(ref _selectiveDepth, tls.LocalSelDepth);
                tls.LocalSelDepth = 0;
            }
            NodesSearched = _nodesSearched;
            QNodesSearched = _qNodesSearched;
            SelectiveDepth = _selectiveDepth;
        }
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
            // This is misplaced - MultiPV should be a property of SearchResult, not inside ResetDebugCounters
        }
    }
    
    public SyzygyTablebase? GetSyzygyTablebase() => _syzygyTablebase;
    public void SetSyzygyTablebase(SyzygyTablebase? tb) => _syzygyTablebase = tb;

    public sealed class MultiPVResult
    {
        public int Depth { get; set; }
        public int Score { get; set; }
        public Move BestMove { get; set; }
        public Move[] PV { get; set; } = [];
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
    public Move[]? MultiPV { get; set; }
}

public sealed class SearchLimits
{
    public int MaxDepth { get; set; } = 64;
    public TimeSpan? TimeLimit { get; set; }
    public ulong? NodeLimit { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public bool Infinite { get; set; }
}