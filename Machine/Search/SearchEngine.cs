using System;
using System.Diagnostics;
using System.Text;

using Machine.Core;
using Machine.MoveGen;
using Machine.Tables;

namespace Machine.Search;

public sealed class SearchEngine
{
    private readonly TranspositionTable _tt;
    private Position _position;
    private bool _stopRequested;
    private SearchLimits? _limits;


    // Search statistics
    public ulong NodesSearched { get; private set; }
    public ulong QNodesSearched { get; private set; }
    public int SelectiveDepth { get; private set; }

    public SearchEngine(int hashSizeMb = 16)
    {
        _tt = new TranspositionTable(hashSizeMb);
        _position = new Position();
    }

    public void SetPosition(Position position)
    {
        _position = position;
    }

    public void Stop()
    {
        _stopRequested = true;
    }

    public void ClearHash()
    {
        _tt.Clear();
    }

    public void ResizeHash(int sizeMb)
    {
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

        var result = new SearchResult();

        try
        {
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

                    // Build PV for info output
                    var pv = BuildPV(_position, 32);
                    Console.WriteLine(BuildInfoLine(depth, result.Score, result.SearchTime, result.NodesSearched, SelectiveDepth, pv));
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

        // Aspiration windows for depths > 1
        int windowAlpha = alpha;
        int windowBeta = beta;

        // For first iterations, use result from previous depth
        // For now, disable aspiration windows to avoid infinite loop
        // if (depth > 3)
        // {
        //     const int aspirationWindow = 50;
        //     windowAlpha = Math.Max(alpha, result.Score - aspirationWindow);
        //     windowBeta = Math.Min(beta, result.Score + aspirationWindow);
        // }

        int score = AlphaBeta.Search(_position, depth, windowAlpha, windowBeta, this, _tt);

        if (!_stopRequested)
        {
            result.Score = score;
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
            if (DateTime.UtcNow >= _limits.StartTime.Add(_limits.TimeLimit.Value))
                return true;
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
        NodesSearched++;
        SelectiveDepth = Math.Max(SelectiveDepth, depth);
    }

    public void UpdateQStats()
    {
        QNodesSearched++;
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