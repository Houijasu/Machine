using System;
using Machine.Core;
using Machine.MoveGen;
using Machine.Tables;

namespace Machine.Search;

public static class AlphaBeta
{
    private const int MateValue = 30000;
    private const int MateInMaxPly = MateValue - 1000;
    
    public static int Search(Position pos, int depth, int alpha, int beta, SearchEngine engine, TranspositionTable tt)
    {
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
            
        // Transposition table probe
        var ttEntry = tt.Probe(pos);
        if (ttEntry.IsValid && ttEntry.Depth >= depth)
        {
            if (ttEntry.Flag == TTFlag.Exact)
                return ttEntry.Score;
            else if (ttEntry.Flag == TTFlag.Alpha && ttEntry.Score <= alpha)
                return alpha;
            else if (ttEntry.Flag == TTFlag.Beta && ttEntry.Score >= beta)
                return beta;
        }
        
        // Generate moves
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
                if (searchPV)
                {
                    // Principal Variation Search
                    score = -Search(pos, depth - 1, -beta, -alpha, engine, tt);
                }
                else
                {
                    // Late Move Reduction (LMR) candidate - search with reduced depth first
                    if (depth >= 3 && i >= 4 && !IsCapture(move) && !IsCheck(pos))
                    {
                        int reduction = Math.Min(2, depth / 4);
                        score = -Search(pos, depth - 1 - reduction, -alpha - 1, -alpha, engine, tt);
                        
                        // If LMR search fails high, do full search
                        if (score > alpha)
                            score = -Search(pos, depth - 1, -alpha - 1, -alpha, engine, tt);
                    }
                    else
                    {
                        // Null window search
                        score = -Search(pos, depth - 1, -alpha - 1, -alpha, engine, tt);
                    }
                    
                    // If null window search fails high, do full search
                    if (score > alpha && score < beta)
                        score = -Search(pos, depth - 1, -beta, -alpha, engine, tt);
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
                    
                    if (alpha >= beta)
                    {
                        // Beta cutoff
                        ttFlag = TTFlag.Beta;
                        break;
                    }
                }
            }
        }
        
        // Store in transposition table
        tt.Store(pos, bestMove, bestScore, depth, ttFlag);
        
        return bestScore;
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
}