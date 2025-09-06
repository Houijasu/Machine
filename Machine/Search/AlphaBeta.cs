using System;
using Machine.Core;
using Machine.MoveGen;
using Machine.Optimization;
using Machine.Tables;

namespace Machine.Search;

public static class AlphaBeta
{
    private const int MateValue = 30000;
    private const int MateInMaxPly = MateValue - 1000;

    // Null move pruning parameters
    private const int NullMoveReduction = 3;
    private const int NullMoveMinDepth = 3;

    // Singular extension parameters
    private const int SingularDepth = 8;
    private const int SingularMargin = 2;

    public static int Search(Position pos, int depth, int alpha, int beta, SearchEngine engine, ITranspositionTable tt, int ply = 0, bool doNullMove = true, PVTable? pvTable = null, LazySMPMetrics? metrics = null, int threadId = 0)
    {
        // Clear PV length for this ply if we are a PV node
        if ((beta - alpha) > 1)
            pvTable?.ClearPly(ply);

        // Update search statistics
        engine.UpdateStats(depth);
        
        // Zobrist key verification (debug mode only)
        if (engine.ZKeyAudit && engine.NodesSearched % (ulong)engine.ZKeyAuditInterval == 0)
        {
            if (!pos.VerifyZobrist(out string error))
            {
                Console.WriteLine($"info string ERROR: Zobrist mismatch at depth {depth}, ply {ply}: {error}");
                if (engine.ZKeyAuditStopOnMismatch)
                {
                    engine.Stop();
                    return 0;
                }
            }
        }


        // Check for stop request
        if (engine.ShouldStop())
            return 0;

        // Check for immediate mate/stalemate
        if (depth <= 0)
            return Quiescence.Search(pos, alpha, beta, engine);

        // Periodic stop check
        if (engine.NodesSearched % 1000 == 0 && engine.ShouldStop())
            return 0;

        bool inCheck = pos.IsKingInCheck(pos.SideToMove);

        // Check extension - extend search when in check
        if (inCheck && depth < 50) // limit max depth to avoid runaway
        {
            if (engine.EnableDebugInfo) engine.IncrementCheckExtensions();
            depth++;
        }

        // Transposition table probe
        var ttEntry = tt.Probe(pos);
        metrics?.RecordTTAccess(threadId, ttEntry.IsValid);
        Move ttMove = Move.NullMove;
        if (ttEntry.IsValid)
        {
            ttMove = ttEntry.BestMove;
            if (ttEntry.Depth >= depth)
            {
                // Potential duplicate work indicator: another thread already searched sufficiently
                metrics?.RecordDuplicateWork();
                if (ttEntry.Flag == TTFlag.Exact)
                    return ttEntry.Score;
                else if (ttEntry.Flag == TTFlag.Alpha && ttEntry.Score <= alpha)
                    return alpha;
                else if (ttEntry.Flag == TTFlag.Beta && ttEntry.Score >= beta)
                    return beta;
            }
        }

        // Singular Extension: extend when TT move is clearly best
        bool singularExtension = false;
        if (engine.UseSingularExtensions && !inCheck && depth >= SingularDepth && ply > 0 && ttEntry.IsValid &&
            ttEntry.Depth >= depth - 3 && ttEntry.Flag != TTFlag.Alpha && !ttMove.Equals(Move.NullMove))
        {
            // Dynamic depth and margin based on position and depth
            int reducedDepth = Math.Max(1, (depth - 1) / 2 + (depth / 8));
            int singularMargin = SingularMargin + (depth / 4);
            int singularBeta = ttEntry.Score - singularMargin;

            // Get static evaluation for better decision making
            int staticEval = Quiescence_EvalOnly(pos, depth);
            
            // Only try singular extension if TT score is significantly better than static eval
            if (ttEntry.Score > staticEval + 50)
            {
                // Search at reduced depth with exclusion window to see if TT move is singular
                int singularScore = SearchSingular(pos, reducedDepth, singularBeta - 1, singularBeta, engine, tt, ply, ttMove, metrics, threadId);

                // If all other moves fail low, the TT move is singular - extend it
                if (singularScore < singularBeta)
                {
                    // Verification search: try full depth search without TT move
                    if (depth >= 8) // Only verify at higher depths
                    {
                        int verifyDepth = depth - 2;
                        int verifyScore = SearchSingular(pos, verifyDepth, -MateValue, singularBeta, engine, tt, ply, ttMove, metrics, threadId);
                        if (verifyScore < singularBeta)
                        {
                            if (engine.EnableDebugInfo) engine.IncrementSingularExtensions();
                            singularExtension = true;
                            depth++;
                        }
                    }
                    else
                    {
                        if (engine.EnableDebugInfo) engine.IncrementSingularExtensions();
                        singularExtension = true;
                        depth++;
                    }
                }
            }
        }

        // Null Move Pruning
        if (engine.UseNullMove && doNullMove && !inCheck && depth >= NullMoveMinDepth && ply > 0)
        {
            // Don't do null move in endgame positions (simplified check)
            int pieces = Bitboards.PopCount(pos.Occupancy[0] | pos.Occupancy[1]);
            if (pieces > 7) // Not endgame
            {
                // Dynamic null move reduction based on pruning aggressiveness
                int dynamicReduction = NullMoveReduction;
                if (engine._dynamicPruning)
                {
                    dynamicReduction += (engine._pruningAggressiveness - 100) / 50; // +1 reduction for every 50% above 100%
                    dynamicReduction = Math.Max(1, dynamicReduction); // Minimum reduction of 1
                }
                
                // Make null move (skip turn)
                pos.MakeNullMove();
                
                // Search with reduced depth
                int nullScore = -Search(pos, depth - 1 - dynamicReduction, -beta, -beta + 1, engine, tt, ply + 1, false, pvTable, metrics, threadId);
                
                // Undo null move
                pos.UndoNullMove();
                
                // If null move causes beta cutoff, prune this node
                if (nullScore >= beta)
                {
                    // Avoid returning mate scores from null move
                    if (Math.Abs(nullScore) < MateInMaxPly)
                    {
                        if (engine.EnableDebugInfo) engine.IncrementNullMoveCutoffs();
                        return beta;
                    }
                }
            }
        }

        // Razoring: drop to quiescence at shallow depths when static eval is far below alpha
        if (!inCheck && depth <= 2 && ply > 0 && engine.UseRazoring)
        {
            int staticEval = Quiescence_EvalOnly(pos, depth);
            int baseMargin = depth == 1 ? Tuning.RazoringMarginDepth1 : Tuning.RazoringMarginDepth2;
            int dynamicMargin = baseMargin;
            
            if (engine._dynamicPruning)
            {
                // Dynamic margin based on pruning aggressiveness
                dynamicMargin += (engine._pruningAggressiveness - 100) * 10 / 100; // Adjust margin by 10% for every 10% of aggressiveness
                dynamicMargin = Math.Max(50, dynamicMargin); // Minimum margin of 50
            }
            
            if (staticEval + dynamicMargin <= alpha)
            {
                if (engine.EnableDebugInfo) engine.IncrementRazoringCutoffs();
                return Quiescence.Search(pos, alpha, beta, engine);
            }
        }

        // Generate moves
        // ProbCut: early beta cutoff via tactical captures
        if (engine.UseProbCut && depth >= 5 && !inCheck && ply > 0)
        {
            // Dynamic margin based on depth and position
            int baseMargin = Tuning.ProbCutMargin + (depth / 4);
            int probMargin = baseMargin;
            
            if (engine._dynamicPruning)
            {
                // Dynamic margin based on pruning aggressiveness
                probMargin -= (engine._pruningAggressiveness - 100) * 5 / 100; // Reduce margin by 5% for every 10% of aggressiveness
                probMargin = Math.Max(50, probMargin); // Minimum margin of 50
            }
            
            // Get static evaluation for better move ordering
            int staticEval = Quiescence_EvalOnly(pos, depth);
            if (staticEval > beta + probMargin) // Only try ProbCut if position is promising
            {
                Span<Move> caps = stackalloc Move[256];
                int capCount = MoveGenerator.GenerateCapturesOnly(pos, caps);
                
                // Score and order captures for ProbCut
                Span<int> capScores = stackalloc int[capCount];
                for (int i = 0; i < capCount; i++)
                {
                    int see = Quiescence.StaticExchangeEvaluation(pos, caps[i]);
                    // Enhanced SEE filter: require positive SEE for ProbCut
                    if (see > 0)
                    {
                        // Score based on SEE and MVV-LVA
                        capScores[i] = see * 10 + MoveOrdering.ScoreMove(pos, caps[i], Move.NullMove, ply);
                    }
                    else
                    {
                        capScores[i] = -1; // Mark for skipping
                    }
                }
                MoveOrdering.Sort(caps[..capCount], capScores[..capCount]);
                
                // Try ProbCut on top scoring captures
                int probCutTries = Math.Min(3, capCount); // Limit to top 3 moves
                for (int i = 0; i < probCutTries; i++)
                {
                    if (capScores[i] < 0) continue; // Skip negative SEE moves
                    
                    var m = caps[i];
                    pos.ApplyMove(m);
                    Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
                    bool legal = !pos.IsKingInCheck(movedColor);
                    int sc = -MateValue;
                    if (legal)
                    {
                        // Try ProbCut with verification search
                        sc = -Search(pos, depth - 2, -(beta + probMargin), -(beta + probMargin) + 1, engine, tt, ply + 1, true, null);
                        
                        // If ProbCut succeeds, verify with full window search
                        if (sc >= beta + probMargin)
                        {
                            int verify = -Search(pos, depth - 1, -beta, -alpha, engine, tt, ply + 1, true, null);
                            if (verify >= beta)
                            {
                                if (engine.EnableDebugInfo) engine.IncrementProbCutCutoffs();
                                pos.UndoMove(m);
                                return verify;
                            }
                        }
                    }
                    pos.UndoMove(m);
                }
            }
        }

        // Staged move generation in non-PV nodes: captures first
        bool isPvNode = (beta - alpha) > 1;
        bool searchPV = true;

        if (!isPvNode)
        {
            Move bestMoveSt = Move.NullMove;
            int bestScoreSt = -MateValue;
            TTFlag ttFlagSt = TTFlag.Alpha;
            bool didCutoff = false;

            // Phase 1: captures only
            Span<Move> capMoves = stackalloc Move[256];
            int capCount = MoveGenerator.GenerateCapturesOnly(pos, capMoves);
            Span<int> capScores = stackalloc int[capCount];
            for (int i = 0; i < capCount; i++) capScores[i] = MoveOrdering.ScoreMove(pos, capMoves[i], ttMove, ply);
            MoveOrdering.Sort(capMoves[..capCount], capScores[..capCount]);

            for (int i = 0; i < capCount; i++)
            {
                var m = capMoves[i];
                pos.ApplyMove(m);
                
                // Prefetch TT entry for captures phase
                Prefetch.TTEntry(tt, pos.ZobristKey);
                
                Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
                bool isLegal = !pos.IsKingInCheck(movedColor);
                int score;
                if (isLegal)
                {
                    int newDepth = depth - 1;
                    if (singularExtension && m.Equals(ttMove)) newDepth++;

                    // Captures: staged in non-PV nodes -> use null-window then re-search if needed
                    score = -Search(pos, newDepth, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable);
                    if (score > alpha && score < beta)
                        score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable);
                }
                else
                {
                    score = -MateValue;
                }
                pos.UndoMove(m);
                if (!isLegal) continue;

                if (score > bestScoreSt)
                {
                    bestScoreSt = score;
                    bestMoveSt = m;
                    if (score > alpha)
                    {
                        alpha = score;
                        ttFlagSt = TTFlag.Exact;
                        searchPV = false;
                        pvTable?.UpdatePV(ply, m);
                        if (alpha >= beta)
                        {
                            ttFlagSt = TTFlag.Beta;
                            didCutoff = true;
                            break;
                        }
                    }
                }
            }

            if (didCutoff)
            {
                tt.Store(pos, bestMoveSt, bestScoreSt, depth, ttFlagSt);
                return bestScoreSt;
            }

            // Phase 2: generate rest (quiets); skip captures already searched
            Span<Move> allMoves = stackalloc Move[256];
            int allCount = MoveGenerator.GenerateMoves(pos, allMoves);
            if (allCount == 0)
            {
                if (pos.IsKingInCheck(pos.SideToMove)) return -MateValue + depth; else return 0;
            }
            Span<int> allScores = stackalloc int[allCount];
            for (int i = 0; i < allCount; i++) allScores[i] = MoveOrdering.ScoreMove(pos, allMoves[i], ttMove, ply);
            MoveOrdering.Sort(allMoves[..allCount], allScores[..allCount]);

            // Phase 3: staged move generation for quiet moves
            int quietMovesSeen = 0;
            int maxScore = allCount > 0 ? allScores[0] : 0;
            int stageThreshold = maxScore;
            
            // Multiple stages with decreasing thresholds
            for (int stage = 0; stage < 3 && alpha < beta; stage++)
            {
                for (int i = 0; i < allCount && alpha < beta; i++)
                {
                    var move = allMoves[i];
                    if (IsCapture(move)) continue; // already handled in phase 1
                    if (allScores[i] < stageThreshold) continue; // Skip moves below threshold in this stage

                    // Pre-move history-based pruning for poor quiets at shallow depth (non-PV staged path)
                    // Skip pruning for first few quiet moves to ensure progress
                    quietMovesSeen++;
                    if (engine.UseHistoryPruning && depth <= engine.HistoryPruningMaxDepth && quietMovesSeen > engine.HistoryPruningMinQuietIndex)
                    {
                        int hist = MoveOrdering.GetHistory(move);
                        if (hist < engine.HistoryPruningThreshold) continue;
                    }

                    pos.ApplyMove(move);
                    
                    // Prefetch TT entry for next position in staged search
                    Prefetch.TTEntry(tt, pos.ZobristKey);
                    
                    Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
                    bool isLegal = !pos.IsKingInCheck(movedColor);
                    int score;
                    if (isLegal)
                    {
                        int newDepth = depth - 1;
                        if (singularExtension && move.Equals(ttMove)) newDepth++;

                        // Futility pruning for quiets at shallow depths
                        if (engine.UseFutility && !inCheck && depth <= 3)
                        {
                            int staticEval = Quiescence_EvalOnly(pos, depth);
                            int margin = Tuning.GetFutilityMargin(depth);
                            if (staticEval + margin <= alpha)
                            {
                                pos.UndoMove(move);
                                continue;
                            }
                        }

                        // Non-PV: use null-window with LMR for late quiets
                        if (depth >= 3 && i >= 4 && !IsCheck(pos))
                        {
                            int reduction = ComputeLMR(depth, i, inCheck: false, isCapture: false, moveScore: allScores[i]);
                            
                            // Dynamic LMR based on pruning aggressiveness
                            if (engine._dynamicPruning)
                            {
                                reduction += (engine._pruningAggressiveness - 100) / 50; // +1 reduction for every 50% above 100%
                                reduction = Math.Max(0, reduction); // Ensure non-negative
                            }
                            
                            if (reduction > 0)
                            {
                                score = -Search(pos, newDepth - reduction, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable);
                                if (score > alpha)
                                    score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable);
                            }
                            else
                            {
                                score = -Search(pos, newDepth, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable);
                                if (score > alpha && score < beta)
                                    score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable);
                            }
                        }
                        else
                        {
                            score = -Search(pos, newDepth, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable);
                            if (score > alpha && score < beta)
                                score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable);
                        }
                    }
                    else
                    {
                        score = -MateValue;
                    }

                    pos.UndoMove(move);
                    if (!isLegal) continue;

                    if (score > bestScoreSt)
                    {
                        bestScoreSt = score;
                        bestMoveSt = move;
                        if (score > alpha)
                        {
                            alpha = score;
                            ttFlagSt = TTFlag.Exact;
                            searchPV = false;
                            pvTable?.UpdatePV(ply, move);
                            if (alpha >= beta)
                            {
                                ttFlagSt = TTFlag.Beta;
                                // Quiet beta cutoff updates killers/history
                                MoveOrdering.OnBetaCutoff(move, ply, depth, lastMove);
                                break;
                            }
                        }
                    }
                }
                
                // Lower threshold for next stage
                stageThreshold = stageThreshold * 2 / 3;
            }

            tt.Store(pos, bestMoveSt, bestScoreSt, depth, ttFlagSt);
            return bestScoreSt;
        }

        Span<Move> moves = stackalloc Move[256];
        int moveCount = MoveGenerator.GenerateMoves(pos, moves);

        if (moveCount == 0)
        {
            // No legal moves - checkmate or stalemate
            if (pos.IsKingInCheck(pos.SideToMove))
                return -MateValue + depth; // Mate in N moves
            else
                return 0; // Stalemate
        }

        // Score and order moves
        Span<int> scores = stackalloc int[moveCount];
        Move lastMove = default; // Get last move from PV table or position
        if (ply > 0 && pvTable != null)
        {
            var pvMoves = pvTable.GetPV();
            if (ply - 1 < pvMoves.Length)
                lastMove = pvMoves[ply - 1];
        }
        
        for (int i = 0; i < moveCount; i++)
            scores[i] = MoveOrdering.ScoreMove(pos, moves[i], ttMove, ply, lastMove);
        MoveOrdering.Sort(moves[..moveCount], scores[..moveCount]);

        Move bestMove = moves[0];
        int bestScore = -MateValue;
        TTFlag ttFlag = TTFlag.Alpha;

        // Optionally publish a split point at suitable nodes (Phase 3)
        if (WorkStealingRuntime.Enabled && WorkStealingRuntime.GetPool() is { } pool && ply == 0 && depth >= WorkStealingRuntime.MinSplitDepth && moveCount >= WorkStealingRuntime.MinSplitMoves)
        {
            // Build move array to share
            var share = moves[..moveCount].ToArray();
            var sp = pool.CreateSplitPoint(pos.Clone(), share, depth, alpha, beta, ply);
            // Optional: print WS split event for debugging
            if (engine.EnableDebugInfo)
                Console.WriteLine($"info string ws_split depth {depth} moves {share.Length}");

            // Master participates in searching
            while (!sp.CutoffOccurred && sp.TryGetNextMove(out var m))
            {
                pos.ApplyMove(m);
                Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
                bool legal = !pos.IsKingInCheck(movedColor);
                pos.UndoMove(m);
                if (!legal) { sp.UpdateResult(m, -MateValue); continue; }

                pos.ApplyMove(m);
                int score = -Search(pos, depth - 1, -beta, -alpha, engine, tt, ply + 1, true, pvTable, null, 0);
                pos.UndoMove(m);
                sp.UpdateResult(m, score);
                if (score >= beta)
                {
                    sp.SignalCutoff();
                    return beta;
                }
                if (score > alpha)
                {
                    alpha = score;
                    bestMove = m;
                    bestScore = score;
                    ttFlag = TTFlag.Exact;
                }
            }

            // Wait for helpers to finish the rest but respect stop request
            while (!engine.ShouldStop())
            {
                if (sp.CompletionEvent.Wait(10)) break; // 10ms slices
            }
            // If stop requested or force-completed, return current alpha/best
            return bestScore > int.MinValue ? bestScore : alpha;
        }

        // Search all moves
        for (int i = 0; i < moveCount; i++)
        {
            var move = moves[i];

            // Pre-move history-based pruning for poor quiets at shallow depth (main loop)
            // Skip pruning for first few moves to ensure progress
            if (engine.UseHistoryPruning && !IsCapture(move) && depth <= engine.HistoryPruningMaxDepth && i >= engine.HistoryPruningMinQuietIndex)
            {
                int hist = MoveOrdering.GetHistory(move);
                if (hist < engine.HistoryPruningThreshold) continue;
            }

            // Apply move
            pos.ApplyMove(move);
            
            // Prefetch TT entry for next position
            Prefetch.TTEntry(tt, pos.ZobristKey);

            // Check legality
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);

            int score;
            if (isLegal)
            {
                // Apply singular extension only to TT move
                int newDepth = depth - 1;
                if (singularExtension && move.Equals(ttMove))
                    newDepth++;  // Extend only TT move (Option A)

                // Futility pruning for quiets at shallow depths (guarded by option)
                if (engine.UseFutility && !inCheck && depth <= 3 && !IsCapture(move))
                {
                    int staticEval = Quiescence_EvalOnly(pos, depth);
                    int margin = Tuning.GetFutilityMargin(depth);
                    if (staticEval + margin <= alpha)
                    {
                        if (engine.EnableDebugInfo) engine.IncrementFutilityPrunes();
                        pos.UndoMove(move);
                        continue;
                    }
                }

                // History-based pruning for poor quiets at shallow depth
                // DISABLED - causes issues with move state management
                /*
                if (!IsCapture(move) && depth <= 3)
                {
                    int hist = MoveOrdering.GetHistory(move);
                    if (hist < 50) // very low history score
                    {
                        // Do not double-undo; only skip if we have not pruned by futility.
                        // Since we are past futility block, the move is still applied.
                        pos.UndoMove(move);
                        continue;
                    }
                }
                */
                if (searchPV)
                {
                    // Principal Variation Search
                    score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable);
                }
                else
                {
                    // Late Move Reduction (LMR) candidate - search with reduced depth first
                    if (depth >= 3 && i >= 4 && !IsCapture(move) && !IsCheck(pos))
                    {
                        if (engine.EnableDebugInfo) engine.IncrementLMRReductions();
                        int baseReduction = Math.Min(2, depth / 4);
                        int reduction = baseReduction;
                        
                        // Dynamic LMR based on pruning aggressiveness
                        if (engine._dynamicPruning)
                        {
                            reduction += (engine._pruningAggressiveness - 100) / 50; // +1 reduction for every 50% above 100%
                            reduction = Math.Max(0, reduction); // Ensure non-negative
                        }
                        
                        score = -Search(pos, newDepth - reduction, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);

                        // If LMR search fails high, do full search
                        if (score > alpha)
                            score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                    }
                    else
                    {
                        // Try LMR with ComputeLMR in main loop as well
                        int reduction = ComputeLMR(depth, i, inCheck: false, isCapture: false, moveScore: scores[i]);
                        
                        // Dynamic LMR based on pruning aggressiveness
                        if (engine._dynamicPruning)
                        {
                            reduction += (engine._pruningAggressiveness - 100) / 50; // +1 reduction for every 50% above 100%
                            reduction = Math.Max(0, reduction); // Ensure non-negative
                        }
                        
                        if (reduction > 0)
                        {
                            score = -Search(pos, newDepth - reduction, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                            if (score > alpha)
                                score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                        }
                        else
                        {
                            // Null window search
                            score = -Search(pos, newDepth, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                            if (score > alpha && score < beta)
                                score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                        }
                    }
                }
            }
            else
            {
                score = -MateValue; // Illegal move
            }

            // Undo move
            pos.UndoMove(move);

            if (!isLegal)
                continue;

            // Check for new best move
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;

                if (score > alpha)
                {
                    alpha = score;
                    ttFlag = TTFlag.Exact;
                    searchPV = false; // No longer searching PV

                    // Update PV
                    pvTable?.UpdatePV(ply, move);

                    if (alpha >= beta)
                    {
                        // Beta cutoff
                        ttFlag = TTFlag.Beta;

                        // Update killer moves and counter-moves for quiet moves
                        if (!IsCapture(move))
                        {
                            MoveOrdering.OnBetaCutoff(move, ply, depth, lastMove);
                        }

                        break;
                    }
                }
            }
        }

        // Store in transposition table
        tt.Store(pos, bestMove, bestScore, depth, ttFlag);

        return bestScore;
    }

    // Helper: static eval for pruning decisions (uses Evaluation)
    private static int Quiescence_EvalOnly(Position pos, int depth = 0)
    {
        return Evaluation.Evaluate(pos, depth);
    }

    private static bool IsCapture(Move move)
    {
        return move.Flag == MoveFlag.Capture ||
               move.Flag == MoveFlag.EnPassant ||
               move.Flag >= MoveFlag.PromoCaptureQueen;
    }

    private static bool IsCheck(Position pos)
    {
        return pos.IsKingInCheck(pos.SideToMove);
    }

    // Search excluding a specific move to test if it's singular
    private static int SearchSingular(Position pos, int depth, int alpha, int beta, SearchEngine engine, ITranspositionTable tt, int ply, Move excludeMove, LazySMPMetrics? metrics, int threadId)
    {
        if (depth <= 0)
            return Quiescence.Search(pos, alpha, beta, engine);

        // Keep full move list here; staged gen would complicate exclusion logic unnecessarily
        Span<Move> moves = stackalloc Move[256];
        int moveCount = MoveGenerator.GenerateMoves(pos, moves);

        if (moveCount == 0)
        {
            if (pos.IsKingInCheck(pos.SideToMove))
                return -MateValue + depth;
            else
                return 0;
        }

        // Score and order moves
        Span<int> scores = stackalloc int[moveCount];
        for (int i = 0; i < moveCount; i++)
            scores[i] = MoveOrdering.ScoreMove(pos, moves[i], Move.NullMove, ply); // No TT move for exclusion search
        MoveOrdering.Sort(moves[..moveCount], scores[..moveCount]);

        int bestScore = -MateValue;

        for (int i = 0; i < moveCount; i++)
        {
            var move = moves[i];

            // Skip the excluded move
            if (move.From == excludeMove.From && move.To == excludeMove.To && move.Flag == excludeMove.Flag)
                continue;

            pos.ApplyMove(move);
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);

            if (isLegal)
            {
                int score = -Search(pos, depth - 1, -beta, -alpha, engine, tt, ply + 1, true, null, metrics, threadId);
                bestScore = Math.Max(bestScore, score);

                if (score >= beta)
                {
                    pos.UndoMove(move);
                    return beta; // Beta cutoff
                }

                alpha = Math.Max(alpha, score);
            }

            pos.UndoMove(move);
        }

        return bestScore;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int ComputeLMR(int depth, int moveIndex, bool inCheck, bool isCapture, int moveScore)
    {
        if (inCheck || isCapture) return 0;
        if (depth < 3 || moveIndex < 4) return 0;
        
        // Base reduction: depth and move index dependent
        int baseR = depth / 4 + moveIndex / 8;
        
        // Stronger reduction at deeper depths
        if (depth >= 12) baseR++;
        if (depth >= 14 && moveIndex >= 8) baseR++;  // Allow reduction 3 at depth >= 14 for late moves
        
        // Poor-scoring moves get more reduction
        if (moveScore < 800) baseR++;
        
        // Conservative cap at 3 for depths < 14, allow up to 4 for very deep searches
        int maxReduction = depth >= 14 ? 4 : 3;
        if (baseR > maxReduction) baseR = maxReduction;
        
        return baseR;
    }

}