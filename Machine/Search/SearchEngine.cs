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
    private Position _position;
    private volatile bool _stopRequested;
    private SearchLimits? _limits;

    private int _currentHashSize;
    // Threading
    private int _threadCount = 1;
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

	    // LazySMP options
	    private int _lazyAspirationDelta = 25;
	    private bool _lazyDepthSkipping = true;
	    private bool _lazyNullMoveVariation = true;
	    private bool _lazyShowInfo = false;
	    private bool _lazyShowMetrics = false;
	    public void SetLazyAspirationDelta(int delta) => _lazyAspirationDelta = Math.Max(0, delta);
	    public void SetLazyDepthSkipping(bool enabled) => _lazyDepthSkipping = enabled;
	    public void SetLazyNullMoveVariation(bool enabled) => _lazyNullMoveVariation = enabled;
	    public void SetLazyShowInfo(bool enabled) => _lazyShowInfo = enabled;
	    public void SetLazyShowMetrics(bool enabled) => _lazyShowMetrics = enabled;

    public int SelectiveDepth { get; private set; }

    public SearchEngine(int hashSizeMb = 16)
    {
        _tt = new TranspositionTable(hashSizeMb);
        _currentHashSize = hashSizeMb;
        _position = new Position();
        _instance = this; // publish for debug-only helpers
    }

    // internal worker ctor sharing TT
    internal SearchEngine(ITranspositionTable tt)
    {
        _tt = tt;
        _currentHashSize = 16;
        _position = new Position();
        _instance = this;
    }

    public void SetPosition(Position position)
    {
        _position = position;
    }


	    public ITranspositionTable GetTranspositionTable() => _tt;
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

	        // Initialize thread-local state for main thread
	        _mainState ??= new ThreadLocalSearchState(0);
	        _mainState.Clear();
	        MoveOrdering.SetThreadState(_mainState);

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
            // For multi-threaded runs, delegate to LazySMPEngine (pure LazySMP)
            if (_threadCount > 1)
            {
                var lazy = new LazySMPEngine(_threadCount, _tt, _lazyAspirationDelta, _lazyDepthSkipping, _lazyNullMoveVariation, _lazyShowInfo, _lazyShowMetrics);
                var lazyResult = lazy.Search(_position, limits);
                return lazyResult;
            }

            // Iterative deepening framework (single-thread)
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
            int delta = 40; // 40cp initial half-window
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
            var clone = _position.Clone();
            var pvMoves = BuildPV(clone, 32);
            var pvTable = _mainState?.PV ?? _pvTable;
            pvTable.Clear();
            for (int i = 0; i < pvMoves.Length; i++) pvTable.Set(0, i, pvMoves[i]);

            result.BestMove = (_mainState?.PV ?? _pvTable).GetBestMove();
            if (result.BestMove.Equals(Move.NullMove))
                result.BestMove = _tt.GetBestMove(_position);
        }

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