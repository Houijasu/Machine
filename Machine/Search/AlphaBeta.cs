using System;
using Machine.Core;
using Machine.MoveGen;
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
        // Clear PV length for this ply
        pvTable?.ClearPly(ply);

        // Update search statistics
        engine.UpdateStats(depth);


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
            // Search at reduced depth with exclusion window to see if TT move is singular
            int reducedDepth = (depth - 1) / 2;
            int singularBeta = ttEntry.Score - depth * SingularMargin;
            
            // Temporarily exclude the TT move to test if it's singular
            // We do this by searching with a null window below the TT score
            int singularScore = SearchSingular(pos, reducedDepth, singularBeta - 1, singularBeta, engine, tt, ply, ttMove, metrics, threadId);

            // If all other moves fail low, the TT move is singular - extend it
            if (singularScore < singularBeta)
            {
                if (engine.EnableDebugInfo) engine.IncrementSingularExtensions();
                singularExtension = true;
                depth++;
            }
        }

        // Null Move Pruning
        if (engine.UseNullMove && doNullMove && !inCheck && depth >= NullMoveMinDepth && ply > 0)
        {
            // Don't do null move in endgame positions (simplified check)
            int pieces = Bitboards.PopCount(pos.Occupancy[0] | pos.Occupancy[1]);
            if (pieces > 7) // Not endgame
            {
                // Make null move (skip turn)
                pos.MakeNullMove();

                // Search with reduced depth
                int nullScore = -Search(pos, depth - 1 - NullMoveReduction, -beta, -beta + 1, engine, tt, ply + 1, false, pvTable, metrics, threadId);

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
            int staticEval = Quiescence_EvalOnly(pos);
            int margin = depth == 1 ? Tuning.RazoringMarginDepth1 : Tuning.RazoringMarginDepth2;
            if (staticEval + margin <= alpha)
            {
                if (engine.EnableDebugInfo) engine.IncrementRazoringCutoffs();
                return Quiescence.Search(pos, alpha, beta, engine);
            }
        }

        // Generate moves
        // ProbCut: early beta cutoff via tactical captures
        if (engine.UseProbCut && depth >= 5 && !inCheck && ply > 0)
        {
            const int probMargin = Tuning.ProbCutMargin;
            Span<Move> caps = stackalloc Move[256];
            int capCount = MoveGenerator.GenerateCapturesOnly(pos, caps);
            for (int i = 0; i < capCount; i++)
            {
                var m = caps[i];
                // Filter by SEE - only promising captures
                int see = Quiescence.StaticExchangeEvaluation(pos, m);
                if (see < (beta - alpha)) continue;

                pos.ApplyMove(m);
                Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
                bool legal = !pos.IsKingInCheck(movedColor);
                int sc = -MateValue;
                if (legal)
                {
                    sc = -Search(pos, depth - 2, -(beta + probMargin), -(beta + probMargin) + 1, engine, tt, ply + 1, true, null);
                }
                pos.UndoMove(m);

                if (legal && sc >= beta + probMargin)
                {
                    if (engine.EnableDebugInfo) engine.IncrementProbCutCutoffs();
                    return sc;
                }
            }
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
        for (int i = 0; i < moveCount; i++)
            scores[i] = MoveOrdering.ScoreMove(pos, moves[i], ttMove, ply);
        MoveOrdering.Sort(moves[..moveCount], scores[..moveCount]);

        Move bestMove = moves[0];
        int bestScore = -MateValue;
        TTFlag ttFlag = TTFlag.Alpha;
        bool searchPV = true;

        // Search all moves
        for (int i = 0; i < moveCount; i++)
        {
            var move = moves[i];

            // Apply move
            pos.ApplyMove(move);

            // Check legality
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);

            int score;
            if (isLegal)
            {
                // Apply singular extension only to TT move
                int newDepth = depth - 1;
                if (singularExtension && move.Equals(ttMove))
                    newDepth++;  // Already extended depth, now apply to this specific move
                else if (singularExtension)
                    newDepth--;  // Compensate non-TT moves when singular extension was triggered
                    
                // Futility pruning for quiets at shallow depths (guarded by option)
                if (engine.UseFutility && !inCheck && depth <= 3 && !IsCapture(move))
                {
                    int staticEval = Quiescence_EvalOnly(pos);
                    int margin = depth * Tuning.FutilityMarginPerDepth;
                    if (staticEval + margin <= alpha)
                    {
                        if (engine.EnableDebugInfo) engine.IncrementFutilityPrunes();
                        pos.UndoMove(move);
                        continue;
                    }
                }
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
                        int reduction = Math.Min(2, depth / 4);
                        score = -Search(pos, newDepth - reduction, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);

                        // If LMR search fails high, do full search
                        if (score > alpha)
                            score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                    }
                    else
                    {
                        // Null window search
                        score = -Search(pos, newDepth, -alpha - 1, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
                    }

                    // If null window search fails high, do full search
                    if (score > alpha && score < beta)
                        score = -Search(pos, newDepth, -beta, -alpha, engine, tt, ply + 1, true, pvTable, metrics, threadId);
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

                        // Update killer moves for quiet moves
                        if (!IsCapture(move))
                            MoveOrdering.UpdateKillers(move, ply);

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
    private static int Quiescence_EvalOnly(Position pos)
    {
        return Evaluation.Evaluate(pos);
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
}