using System;
using System.Collections.Generic;
using System.IO;
using Machine.Core;
using Machine.MoveGen;
using Machine.Search;

namespace Machine.UCI;

public sealed class UCIProtocol
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly Position _position = new();
    private bool _positionInitialized = false;
    private SearchEngine _searchEngine = new(16);
    private bool _pondering;

    public string EngineName { get; init; } = "Machine";
    public string EngineAuthor { get; init; } = "Houijasu";

    private readonly Dictionary<string, string> _options = new();
    private bool _showStats = false;

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

                // Core engine settings
                Send("option name Hash type spin default 16 min 1 max 32768");  // Transposition table size in MB
                Send("option name PawnHash type spin default 4 min 1 max 256");  // Pawn hash table size in MB
                Send("option name TTAgingDepthThreshold type spin default 8 min 1 max 63");  // Depth threshold for aging (entries deeper than this age slower)
                Send("option name PawnHashAgingDepthThreshold type spin default 8 min 1 max 63");  // Depth threshold for pawn hash aging
                Send("option name EvalCache type spin default 8 min 1 max 512"); // Evaluation cache size in MB
                Send("option name Threads type spin default 1 min 1 max 512");  // Number of search threads

                // Search enhancements
                Send("option name NullMove type check default true");          // Enable null move pruning
                Send("option name Futility type check default true");          // Enable futility pruning at shallow depths
                Send("option name Aspiration type check default true");        // Enable aspiration windows
                Send("option name Razoring type check default true");          // Enable razoring at shallow depths
                Send("option name Extensions type check default true");        // Enable check extensions
                Send("option name ProbCut type check default true");           // Enable probabilistic cut
                Send("option name SingularExtensions type check default true"); // Extend singular moves

                // Move ordering
                Send("option name SEEPruning type check default true");        // Use SEE for move ordering
                Send("option name SEEThreshold type spin default 0 min -500 max 500"); // SEE pruning threshold
                Send("option name UseCounterMoves type check default true");   // Use counter-move heuristic
                Send("option name DynamicMoveOrdering type check default true"); // Enable dynamic move ordering adjustments
                Send("option name MoveOrderingAggressiveness type spin default 100 min 50 max 200"); // 100% = normal, >100% = more aggressive
                
                // History pruning
                Send("option name HistoryPruning type check default true");     // Enable history-based pruning
                Send("option name HistoryPruning_MinQuietIndex type spin default 4 min 0 max 16"); // Skip first N quiet moves
                Send("option name HistoryPruning_Threshold type spin default 50 min 0 max 10000"); // Min history score
                Send("option name HistoryPruning_MaxDepth type spin default 3 min 1 max 6"); // Max depth for pruning
                
                // Zobrist key verification (debug)
                Send("option name ZKeyAudit type check default false");         // Enable Zobrist key verification
                Send("option name ZKeyAudit_Interval type spin default 4096 min 1 max 1000000"); // Check every N nodes
                Send("option name ZKeyAudit_StopOnMismatch type check default true"); // Stop search on mismatch
                Send("option name DynamicPruning type check default true");     // Enable dynamic pruning adjustments
                Send("option name PruningAggressiveness type spin default 100 min 50 max 200"); // 100% = normal, >100% = more aggressive

                // Evaluation features
                Send("option name Eval type check default true");              // Enable position evaluation
                Send("option name DebugInfo type check default false");
                Send("option name MultiPV type spin default 1 min 1 max 10");   // Number of PVs to report
                Send("option name UseNeuralNetwork type check default false"); // Enable neural network evaluation
                Send("option name NeuralNetworkWeight type spin default 50 min 0 max 100"); // Weight of neural network evaluation (0-100%)
                Send("option name TTInfo type check default true");
                Send("option name ShowStats type check default false");  // Show detailed statistics during search
                Send("option name LazySMP_AspirationDelta type spin default 25 min 0 max 400");
                Send("option name LazySMP_DepthSkipping type check default true");
                Send("option name LazySMP_NullMoveVariation type check default true");
                Send("option name LazySMP_ShowInfo type check default false");  // Show threads_active and currmove
                Send("option name LazySMP_ShowMetrics type check default false"); // Show duplication/balance metrics

                Send("option name WorkStealing type check default true");  // Now default for multi-threaded
                Send("option name WorkStealing_MinSplitDepth type spin default 5 min 1 max 32");
                Send("option name WorkStealing_MinSplitMoves type spin default 4 min 1 max 64");
                Send("option name WorkStealing_ShowMetrics type check default false");  // Show splits/steals/cutoffs

                Send("option name UseLazySMP type check default false");  // Kill switch to force LazySMP even when WS is default

                Send("option name VerboseDebug type check default false");
                Send("option name PST type check default true");               // Use piece-square tables
                Send("option name PawnStructure type check default true");     // Evaluate pawn structure
                Send("option name KingSafety type check default true");        // Evaluate king safety

                // Hardware optimizations
                Send("option name UsePEXT type combo default false var auto var true var false"); // Magic bitboard indexing method
                
                // Endgame tablebase options
                Send("option name SyzygyPath type string default <empty>"); // Path to Syzygy tablebases
                Send("option name SyzygyProbeDepth type spin default 2 min 0 max 10"); // Minimum depth to probe
                Send("option name SyzygyProbeLimit type spin default 0 min 0 max 10000000"); // Node limit for probing
                Send("option name SyzygyMaxCardinality type spin default 6 min 3 max 6"); // Maximum pieces for probing
                
                // Debug and analysis
                Send("option name UseNUMA type check default false");          // NUMA optimization (unused)
                Send("option name HelperThreads type spin default 0 min 0 max 64"); // Extra helper threads (unused)
                
                bool pextSupported = System.Runtime.Intrinsics.X86.Bmi2.X64.IsSupported;
                Send($"info string PEXT hardware support: {(pextSupported ? "available" : "not available")}");
                Send($"info string PEXT mode: {Magics.Mode} (currently {(Magics.UsePEXT ? "using PEXT" : "using multiply/shift")})");
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
        // Reset to the requested base position, then apply an optional move list in UCI format
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
        if (name.Equals("Hash", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var hashSize))
        {
            _searchEngine.ResizeHash(hashSize);
        }
        else if (name.Equals("TTAgingDepthThreshold", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var threshold))
        {
            var tt = _searchEngine.GetTranspositionTable();
            if (tt is Tables.TranspositionTable concreteTT)
            {
                concreteTT.SetAgingDepthThreshold(threshold);
                Send($"info string TTAgingDepthThreshold set to {threshold}");
            }
        }
        else if (name.Equals("PawnHashAgingDepthThreshold", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var pawnThreshold))
        {
            var pawnHash = _searchEngine.GetPawnHash();
            if (pawnHash != null)
            {
                pawnHash.SetAgingDepthThreshold(pawnThreshold);
                Send($"info string PawnHashAgingDepthThreshold set to {pawnThreshold}");
            }
        }
        else if (name.Equals("PawnHash", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var pawnHashSize))
        {
            _searchEngine.ResizePawnHash(pawnHashSize);
        }
        else if (name.Equals("EvalCache", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var evalCacheSize))
        {
            _searchEngine.ResizeEvalCache(evalCacheSize);
        }
        else if (name.Equals("Eval", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) Machine.Search.Evaluation.SetOptions(b, Machine.Search.Evaluation.UsePST, Machine.Search.Evaluation.UsePawnStructure, Machine.Search.Evaluation.UseKingSafety);
        }
        else if (name.Equals("UseNeuralNetwork", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var useNN))
        {
            Machine.Search.Evaluation.SetNeuralNetworkOptions(useNN, Machine.Search.Evaluation.NeuralNetworkWeight);
            Send($"info string UseNeuralNetwork set to {useNN}");
        }
        else if (name.Equals("NeuralNetworkWeight", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var weight))
        {
            Machine.Search.Evaluation.SetNeuralNetworkOptions(Machine.Search.Evaluation.UseNeuralNetwork, weight / 100.0f);
            Send($"info string NeuralNetworkWeight set to {weight}%");
        }
        else if (name.Equals("PST", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) Machine.Search.Evaluation.SetOptions(Machine.Search.Evaluation.UseEvaluation, b, Machine.Search.Evaluation.UsePawnStructure, Machine.Search.Evaluation.UseKingSafety);
        }
        else if (name.Equals("PawnStructure", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) Machine.Search.Evaluation.SetOptions(Machine.Search.Evaluation.UseEvaluation, Machine.Search.Evaluation.UsePST, b, Machine.Search.Evaluation.UseKingSafety);
        }
        else if (name.Equals("KingSafety", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) Machine.Search.Evaluation.SetOptions(Machine.Search.Evaluation.UseEvaluation, Machine.Search.Evaluation.UsePST, Machine.Search.Evaluation.UsePawnStructure, b);
        }
        else if (name.Equals("SingularExtensions", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseExtensions), b);
        }
        else if (name.Equals("DebugInfo", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.EnableDebugInfo = b;
        }
        else if (name.Equals("Extensions", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseExtensions), b);
        }
        else if (name.Equals("NullMove", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseNullMove), b);
        }
        else if (name.Equals("VerboseDebug", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var vb)) _searchEngine.EnableDebugInfo = vb; // reuse the same switch for now
        }
        else if (name.Equals("SEEThreshold", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var thr))
        {
            MoveOrdering.SetSEEThreshold(thr);
        }
        else if (name.Equals("UseCounterMoves", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var useCounterMoves))
        {
            // Note: Counter-move heuristic is always enabled in current implementation
            // We could add a flag to disable it if needed
            Send($"info string UseCounterMoves set to {useCounterMoves}");
        }
        else if (name.Equals("DynamicMoveOrdering", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var dynamicMO))
        {
            // Store for use in MoveOrdering class
            MoveOrdering.SetDynamicMoveOrdering(dynamicMO);
            Send($"info string DynamicMoveOrdering set to {dynamicMO}");
        }
        else if (name.Equals("MoveOrderingAggressiveness", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var moAggressiveness))
        {
            MoveOrdering.SetMoveOrderingAggressiveness(moAggressiveness);
            Send($"info string MoveOrderingAggressiveness set to {moAggressiveness}");
        }
        else if (name.Equals("Futility", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseFutility), b);
        }
        else if (name.Equals("Aspiration", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseAspiration), b);
        }
        else if (name.Equals("SEEPruning", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) MoveOrdering.SetSEEPruning(b);
        }
        else if (name.Equals("Razoring", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseRazoring), b);
        }
        else if (name.Equals("Extensions", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseExtensions), b);
        }
        else if (name.Equals("ProbCut", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseProbCut), b);
        }
        else if (name.Equals("SingularExtensions", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (bool.TryParse(value, out var b)) _searchEngine.SetFeature(nameof(_searchEngine.UseSingularExtensions), b);
        }
        else if (name.Equals("Threads", StringComparison.OrdinalIgnoreCase) &&
                 value != null && int.TryParse(value, out var threadCount))
        {
            _searchEngine.SetThreads(threadCount);
            Send($"info string threads set to {threadCount}");
        }
        else if (name.Equals("LazySMP_AspirationDelta", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var lad))
        {
            _searchEngine.SetLazyAspirationDelta(lad);
            Send($"info string LazySMP_AspirationDelta set to {lad}");
        }
        else if (name.Equals("LazySMP_DepthSkipping", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var lds))
        {
            _searchEngine.SetLazyDepthSkipping(lds);
            Send($"info string LazySMP_DepthSkipping set to {lds}");
        }
        else if (name.Equals("LazySMP_NullMoveVariation", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var lnm))
        {
            _searchEngine.SetLazyNullMoveVariation(lnm);
            Send($"info string LazySMP_NullMoveVariation set to {lnm}");
        }
        else if (name.Equals("LazySMP_ShowInfo", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var lsi))
        {
            _searchEngine.SetLazyShowInfo(lsi);
        }
        else if (name.Equals("LazySMP_ShowMetrics", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var lsm))
        {
            _searchEngine.SetLazyShowMetrics(lsm);
        }
        else if (name.Equals("WorkStealing", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var ws))
        {
            _searchEngine.SetWorkStealing(ws);
            Send($"info string WorkStealing set to {ws}");
        }
        else if (name.Equals("WorkStealing_MinSplitDepth", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var md))
        {
            _searchEngine.SetWorkStealingMinSplitDepth(md);
            Send($"info string WorkStealing_MinSplitDepth set to {md}");
        }
        else if (name.Equals("WorkStealing_MinSplitMoves", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var mm))
        {
            _searchEngine.SetWorkStealingMinSplitMoves(mm);
            Send($"info string WorkStealing_MinSplitMoves set to {mm}");
        }
        else if (name.Equals("WorkStealing_ShowMetrics", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var wsm))
        {
            _searchEngine.SetWorkStealingShowMetrics(wsm);
            Send($"info string WorkStealing_ShowMetrics set to {wsm}");
        }
        else if (name.Equals("UseLazySMP", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var forceLazy))
        {
            _searchEngine.SetUseLazySMP(forceLazy);
            Send($"info string UseLazySMP set to {forceLazy}");
        }
        else if (line == "stats")
        {
            // Display detailed statistics
            var ttStats = _searchEngine.GetDetailedTTStats();
            var pawnStats = _searchEngine.GetPawnHashStats();
            
            Send($"info string TT Stats: Probes={ttStats.Probes} Hits={ttStats.Hits} HitRate={ttStats.HitRate:F3} Stores={ttStats.Stores}");
            Send($"info string TT Stats: SameKey={ttStats.SameKeyStores} Replacements={ttStats.ReplacementStores} EmptySlots={ttStats.EmptySlotStores}");
            Send($"info string TT Stats: AbdadaHits={ttStats.AbdadaHits} Collisions={ttStats.Collisions} DepthEvictions={ttStats.DepthEvictions}");
            Send($"info string TT Stats: ExactEvictions={ttStats.ExactEvictions} SkippedWrites={ttStats.SkippedWrites}");
            Send($"info string TT Stats: HighDepthHitRate={ttStats.HighDepthHitRate:F3} LowDepthHitRate={ttStats.LowDepthHitRate:F3}");
            
            Send($"info string PawnHash Stats: Probes={pawnStats.Probes} Hits={pawnStats.Hits} HitRate={pawnStats.HitRate:F3} Stores={pawnStats.Stores}");
            Send($"info string PawnHash Stats: IsolatedPawns={pawnStats.IsolatedPawns} DoubledPawns={pawnStats.DoubledPawns} PassedPawns={pawnStats.PassedPawns}");
            Send($"info string PawnHash Stats: OpenFiles={pawnStats.OpenFiles} HalfOpenFiles={pawnStats.HalfOpenFiles}");
        }
        else if (name.Equals("HistoryPruning", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var hp))
        {
            _searchEngine.SetFeature(nameof(_searchEngine.UseHistoryPruning), hp);
        }
        else if (name.Equals("DynamicPruning", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var dynamicPruning))
        {
            // Store for use in SearchEngine
            _searchEngine.SetDynamicPruning(dynamicPruning);
            Send($"info string DynamicPruning set to {dynamicPruning}");
        }
        else if (name.Equals("PruningAggressiveness", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var pruningAggressiveness))
        {
            _searchEngine.SetPruningAggressiveness(pruningAggressiveness);
            Send($"info string PruningAggressiveness set to {pruningAggressiveness}");
        }
        else if (name.Equals("HistoryPruning_MinQuietIndex", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var hpIdx))
        {
            _searchEngine.SetHistoryPruningMinQuietIndex(hpIdx);
        }
        else if (name.Equals("HistoryPruning_Threshold", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var hpThr))
        {
            _searchEngine.SetHistoryPruningThreshold(hpThr);
        }
        else if (name.Equals("HistoryPruning_MaxDepth", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var hpDep))
        {
            _searchEngine.SetHistoryPruningMaxDepth(hpDep);
        }
        else if (name.Equals("ZKeyAudit", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var zka))
        {
            _searchEngine.SetZKeyAudit(zka);
        }
        else if (name.Equals("ZKeyAudit_Interval", StringComparison.OrdinalIgnoreCase) && value != null && int.TryParse(value, out var zki))
        {
            _searchEngine.SetZKeyAuditInterval(zki);
        }
        else if (name.Equals("ZKeyAudit_StopOnMismatch", StringComparison.OrdinalIgnoreCase) && value != null && bool.TryParse(value, out var zks))
        {
            _searchEngine.SetZKeyAuditStopOnMismatch(zks);
        }
        else if (name.Equals("UsePEXT", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            switch (value.ToLowerInvariant())
            {
                case "auto":
                    Magics.Mode = Magics.PextMode.Auto;
                    Send("info string PEXT mode: auto (will benchmark at startup)");
                    break;
                case "true":
                    Magics.Mode = Magics.PextMode.Force;
                    Send($"info string PEXT mode: forced {(Magics.UsePEXT ? "on" : "off (not supported)")}");
                    break;
                case "false":
                    Magics.Mode = Magics.PextMode.Disable;
                    Send("info string PEXT mode: disabled (using multiply/shift)");
                    break;
                default:
                    Send($"info string Invalid PEXT value: {value}. Use auto, true, or false.");
                    break;
            }
        }
        else if (name.Equals("SyzygyPath", StringComparison.OrdinalIgnoreCase))
        {
            var tb = _searchEngine.GetSyzygyTablebase();
            if (tb != null)
            {
                tb.SetPath(value ?? "");
                Send($"info string SyzygyPath set to {(string.IsNullOrEmpty(value) ? "<empty>" : value)}");
            }
        }
        else if (name.Equals("SyzygyProbeDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var probeDepth))
        {
            var tb = _searchEngine.GetSyzygyTablebase();
            if (tb != null)
            {
                tb.SetProbeDepth(probeDepth);
                Send($"info string SyzygyProbeDepth set to {probeDepth}");
            }
        }
        else if (name.Equals("SyzygyProbeLimit", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var probeLimit))
        {
            var tb = _searchEngine.GetSyzygyTablebase();
            if (tb != null)
            {
                tb.SetProbeLimit(probeLimit);
                Send($"info string SyzygyProbeLimit set to {probeLimit}");
            }
        }
        else if (name.Equals("SyzygyMaxCardinality", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var maxCardinality))
        {
            var tb = _searchEngine.GetSyzygyTablebase();
            if (tb != null)
            {
                tb.SetOptions(tb.IsEnabled(), true, true, maxCardinality);
                Send($"info string SyzygyMaxCardinality set to {maxCardinality}");
            }
        }
    }

    private void HandleGo(string line)
    {
        var limits = new SearchEngine.SearchLimits();

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
        if (limits is { Infinite: false, TimeLimit: null, NodeLimit: null, MaxDepth: 64 })
        {
            if (wtime.HasValue || btime.HasValue)
            {
                int myTime = _position.SideToMove == Color.White ? (wtime ?? 0) : (btime ?? 0);
                int myInc  = _position.SideToMove == Color.White ? (winc ?? 0)  : (binc ?? 0);

                // Enhanced overhead calculation - more conservative for low time
                int overhead = myTime < 10000 ? 100 : 50; // Higher overhead for time trouble
                int emergencyReserve = Math.Min(1000, myTime / 10); // Keep 10% in reserve
                int availableTime = Math.Max(0, myTime - emergencyReserve);

                // Assess position complexity for fractional allocation
                int complexityBonus = 0;
                int legalMoves = MoveGenerator.GenerateMoves(_position, stackalloc Move[256]);
                
                // Bonus for positions with many legal moves (complex positions)
                if (legalMoves > 30) complexityBonus += 20;
                else if (legalMoves > 20) complexityBonus += 10;
                
                // Bonus for tactical positions (checks, captures, threats)
                int tacticalBonus = 0;
                if (_position.IsKingInCheck(_position.SideToMove)) tacticalBonus += 30;
                if (MoveGenerator.GenerateCapturesOnly(_position, stackalloc Move[256]) > 0) tacticalBonus += 20;
                
                int alloc;
                if (movestogo is > 0)
                {
                    // Tournament time control - divide remaining time by moves to go
                    alloc = availableTime / Math.Max(1, movestogo.Value + 2) + myInc - overhead;
                    
                    // Apply complexity and tactical bonuses
                    alloc = (int)(alloc * (1.0 + (complexityBonus + tacticalBonus) / 200.0));
                }
                else
                {
                    // Sudden death or increment - scale allocation based on time remaining
                    double basePercent = myTime > 60000 ? 0.03 :  // >1 minute: 3%
                                        myTime > 30000 ? 0.04 :  // 30s-1m: 4%
                                        myTime > 10000 ? 0.05 :  // 10-30s: 5%
                                                        0.08;    // <10s: 8% (faster decisions)
                    
                    // Apply complexity and tactical bonuses
                    double bonusPercent = (complexityBonus + tacticalBonus) / 2000.0; // Max 2.5% bonus
                    double timePercent = Math.Min(basePercent + bonusPercent, 0.15); // Cap at 15%
                    
                    alloc = (int)(availableTime * timePercent) + myInc - overhead;
                }

                // Safety bounds - never use more than 1/3 of the remaining time
                int maxAlloc = Math.Max(100, availableTime / 3);
                alloc = Math.Clamp(alloc, 50, maxAlloc);
                
                // Panic mode for very low time - reduce depth to ensure we don't run out of time
                if (myTime < 5000 && movestogo is null) // <5 seconds in sudden death
                {
                    limits.MaxDepth = Math.Min(6, limits.MaxDepth); // Reduce max depth
                }
                else if (myTime < 2000 && movestogo is null) // <2 seconds in sudden death
                {
                    limits.MaxDepth = Math.Min(4, limits.MaxDepth); // Further reduce max depth
                }
                
                limits.TimeLimit = TimeSpan.FromMilliseconds(alloc);
            }
            else
            {
                // Fallback to a reasonable fixed depth for analysis
                limits.MaxDepth = 8;
            }
        }

        // Emergency stop guard: store for engine ShouldStop check (soft/hard caps)

        // TT stats (if enabled) - removed annoying output at start of search

        limits.StartTime = DateTime.UtcNow;
        _pondering = ponder;

        try
        {
            // Ensure the position is initialized (default to start position if never set)
            if (!_positionInitialized)
            {
                _position.SetStartPosition();
                _positionInitialized = true;
            }

            _searchEngine.SetPosition(_position);
            var result = _searchEngine.Search(limits);

            // Send a search result
            if (!string.IsNullOrEmpty(result.Error))
            {
                Send($"info string error: {result.Error}");
                Send("bestmove 0000");
            }
            else
            {
                // Don't send redundant final line - bestmove is sufficient
                // The depth info was already sent during search
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
