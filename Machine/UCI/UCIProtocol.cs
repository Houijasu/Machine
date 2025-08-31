using System;
using System.Collections.Generic;
using System.IO;
using Machine.Core;
using Machine.Search;

namespace Machine.UCI;

public sealed class UCIProtocol
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly Position _position = new();
    private bool _positionInitialized = false;
    private readonly SearchEngine _searchEngine = new();
    private bool _pondering;

    public string EngineName { get; init; } = "Machine";
    public string EngineAuthor { get; init; } = "Houijasu";

    private readonly Dictionary<string, string> _options = new();

    public UCIProtocol(TextReader? input = null, TextWriter? output = null)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Out;
    }

    public void Run()
    {
        string? line;
        while ((line = _in.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line == "uci")
            {
                Send($"id name {EngineName}");
                Send($"id author {EngineAuthor}");
                Send("option name Hash type spin default 16 min 1 max 32768");
                Send("option name Threads type spin default 1 min 1 max 512");
                Send("option name UseNUMA type check default false");
                Send("option name HelperThreads type spin default 0 min 0 max 64");
                Send("uciok");
            }
            else if (line == "isready")
            {
                Send("readyok");
            }
            else if (line.StartsWith("setoption ", StringComparison.Ordinal))
            {
                HandleSetOption(line);
            }
            else if (line == "ucinewgame")
            {
                _searchEngine.ClearHash();
            }
            else if (line.StartsWith("position ", StringComparison.Ordinal))
            {
                HandlePosition(line);
            }
            else if (line.StartsWith("perft ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(6), out var depth))
                {
                    Perft.PerftCommand(_position, depth, Send);
                    Send("bestmove 0000");
                }
                else Send("info string invalid depth");
            }
            else if (line.StartsWith("divide ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(7), out var depth))
                {
                    Perft.Divide(_position, depth, Send);
                }
                else Send("info string invalid depth");
            }
            else if (line.StartsWith("go", StringComparison.Ordinal))
            {
                // recognize Stockfish-style "go perft N"
                if (line.Contains(" perft ", StringComparison.Ordinal))
                {
                    var idx = line.IndexOf(" perft ", StringComparison.Ordinal);
                    if (idx >= 0 && int.TryParse(line[(idx + 7)..], out var d))
                    {
                        Perft.PerftCommand(_position, d, Send);
                        Send("bestmove 0000");
                        continue;
                    }
                }
                HandleGo(line);
            }
            else if (line == "stop")
            {
                _searchEngine.Stop();
                _pondering = false;
            }
            else if (line == "ponderhit")
            {
                // Opponent played the expected move, convert ponder search to regular search
                if (_pondering)
                {
                    _pondering = false;
                    // Search continues with current limits
                    Send("info string ponder hit - continuing search");
                }
            }
            else if (line == "quit")
            {
                break;
            }
        }
    }

    private void HandlePosition(string line)
    {
        // position [fen <fenstring> | startpos ] [moves <move1> ....]
        // Reset to requested base position, then apply optional move list in UCI format
        int movesTokenIdx = line.IndexOf(" moves ", StringComparison.Ordinal);
        string head = movesTokenIdx >= 0 ? line[..movesTokenIdx] : line;

        if (head.Contains(" startpos", StringComparison.Ordinal))
        {
            _position.SetStartPosition();
            _positionInitialized = true;
        }
        else
        {
            var fenIdx = head.IndexOf(" fen ", StringComparison.Ordinal);
            if (fenIdx >= 0)
            {
                var fen = head[(fenIdx + 5)..].Trim();
                if (!string.IsNullOrEmpty(fen))
                {
                    _position.SetFen(fen);
                    _positionInitialized = true;
                }
            }
            else
            {
                // Default to startpos if neither specified (robustness)
                _position.SetStartPosition();
                _positionInitialized = true;
            }
        }

        // Apply moves if provided
        if (movesTokenIdx >= 0)
        {
            var movesPart = line[(movesTokenIdx + 7)..].Trim();
            if (movesPart.Length > 0)
            {
                var tokens = movesPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var tok in tokens)
                {
                    if (!MoveParser.TryParseUciMove(_position, tok.AsSpan(), out var mv))
                    {
                        Send($"info string invalid move in 'position': {tok}");
                        break;
                    }
                    _position.ApplyMove(mv);
                }
            }
        }
    }

    private void HandleSetOption(string line)
    {
        // setoption name <id> [value <x>]
        var nameIndex = line.IndexOf(" name ", StringComparison.Ordinal);
        if (nameIndex < 0) return;
        nameIndex += 6;
        var valueIndex = line.IndexOf(" value ", StringComparison.Ordinal);
        string name;
        string? value = null;
        if (valueIndex > 0)
        {
            name = line.Substring(nameIndex, valueIndex - nameIndex).Trim();
            value = line[(valueIndex + 7)..].Trim();
        }
        else
        {
            name = line[nameIndex..].Trim();
        }
        if (name.Length == 0) return;
        _options[name] = value ?? string.Empty;
        
        // Handle engine options
        if (name.Equals("Hash", StringComparison.OrdinalIgnoreCase) && 
            value != null && int.TryParse(value, out var hashSize))
        {
            _searchEngine.ResizeHash(hashSize);
        }
        else if (name.Equals("Threads", StringComparison.OrdinalIgnoreCase) &&
                 value != null && int.TryParse(value, out var threadCount))
        {
            // Store thread count for future SMP implementation
            // For now, just acknowledge the setting without error
            Send($"info string threads set to {Math.Clamp(threadCount, 1, 512)}");
        }
    }

    private void HandleGo(string line)
    {
        var limits = new SearchLimits();

        // Parse go command parameters per UCI
        int? wtime = null, btime = null, winc = null, binc = null, movestogo = null;
        bool ponder = false;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "depth" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var depth):
                    limits.MaxDepth = depth; i++; break;
                case "movetime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var movetimeMs):
                    limits.TimeLimit = TimeSpan.FromMilliseconds(movetimeMs); i++; break;
                case "infinite":
                    limits.Infinite = true; break;
                case "ponder":
                    ponder = true; limits.Infinite = true; break;
                case "nodes" when i + 1 < tokens.Length && ulong.TryParse(tokens[i + 1], out var nodes):
                    limits.NodeLimit = nodes; i++; break;
                case "wtime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var w):
                    wtime = w; i++; break;
                case "btime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var b):
                    btime = b; i++; break;
                case "winc" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var wi):
                    winc = wi; i++; break;
                case "binc" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var bi):
                    binc = bi; i++; break;
                case "movestogo" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var mtg):
                    movestogo = mtg; i++; break;
            }
        }

        // Enhanced time management if no explicit movetime/nodes/depth was provided
        if (!limits.Infinite && limits.TimeLimit is null && limits.NodeLimit is null && limits.MaxDepth == 64)
        {
            if (wtime.HasValue || btime.HasValue)
            {
                int myTime = _position.SideToMove == Color.White ? (wtime ?? 0) : (btime ?? 0);
                int myInc  = _position.SideToMove == Color.White ? (winc ?? 0)  : (binc ?? 0);
                
                // Enhanced overhead calculation - more conservative for low time
                int overhead = myTime < 10000 ? 100 : 50; // Higher overhead for time trouble
                int emergencyReserve = Math.Min(1000, myTime / 10); // Keep 10% in reserve
                int availableTime = Math.Max(0, myTime - emergencyReserve);
                
                int alloc;
                if (movestogo.HasValue && movestogo.Value > 0)
                {
                    // Tournament time control - divide remaining time by moves to go
                    alloc = availableTime / Math.Max(1, movestogo.Value + 2) + myInc - overhead;
                }
                else
                {
                    // Sudden death or increment - scale allocation based on time remaining
                    double timePercent = myTime > 60000 ? 0.03 :  // >1 minute: 3%
                                        myTime > 30000 ? 0.04 :  // 30s-1m: 4% 
                                        myTime > 10000 ? 0.05 :  // 10-30s: 5%
                                                        0.08;    // <10s: 8% (faster decisions)
                    
                    alloc = (int)(availableTime * timePercent) + myInc - overhead;
                }
                
                // Safety bounds - never use more than 1/3 of remaining time
                int maxAlloc = Math.Max(100, availableTime / 3);
                alloc = Math.Clamp(alloc, 50, maxAlloc);
                limits.TimeLimit = TimeSpan.FromMilliseconds(alloc);
            }
            else
            {
                // Fallback to a reasonable fixed depth for analysis
                limits.MaxDepth = 8;
            }
        }

        limits.StartTime = DateTime.UtcNow;
        _pondering = ponder;

        try
        {
            // Ensure position is initialized (default to startpos if never set)
            if (!_positionInitialized)
            {
                _position.SetStartPosition();
                _positionInitialized = true;
            }
            
            _searchEngine.SetPosition(_position);
            var result = _searchEngine.Search(limits);

            // Send search result
            if (!string.IsNullOrEmpty(result.Error))
            {
                Send($"info string error: {result.Error}");
                Send("bestmove 0000");
            }
            else
            {
                Send($"info depth {result.Depth} score cp {result.Score} nodes {result.NodesSearched}");
                string moveStr = result.BestMove.Equals(Move.NullMove) ? "0000" : result.BestMove.ToString();
                Send($"bestmove {moveStr}");
            }
        }
        catch (Exception ex)
        {
            Send($"info string exception: {ex.Message}");
            Send("bestmove 0000");
        }
    }

    private void Send(string s)
    {
        _out.WriteLine(s);
        _out.Flush();
    }
}
