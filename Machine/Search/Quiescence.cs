using System;
using Machine.Core;
using Machine.MoveGen;

namespace Machine.Search;

public static class Quiescence
{
    private const int MateValue = 30000;
    
    public static int Search(Position pos, int alpha, int beta, SearchEngine engine)
    {
        // Update quiescence statistics
        engine.UpdateQStats();
        
        // Check for stop request
        if (engine.ShouldStop())
            return 0;
            
        // Periodic stop check during heavy qsearch
        if (engine.QNodesSearched % 1000 == 0 && engine.ShouldStop())
            return 0;
            
        // Standing pat - can we already beat beta?
        int standPat = Evaluate(pos);
        
        if (standPat >= beta)
            return beta;
            
        // Delta pruning - if even capturing the most valuable piece won't raise alpha
        const int queenValue = 900;
        if (standPat < alpha - queenValue)
            return alpha;
            
        if (standPat > alpha)
            alpha = standPat;
        
        // Generate only captures and checks
        Span<Move> moves = stackalloc Move[256];
        int moveCount = GenerateCaptures(pos, moves);
        
        int bestScore = standPat;
        
        for (int i = 0; i < moveCount; i++)
        {
            var move = moves[i];
            
            // SEE pruning - skip obviously bad captures
            if (StaticExchangeEvaluation(pos, move) < 0)
                continue;
            
            // Apply move
            pos.ApplyMove(move);
            
            // Check legality
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);
            
            int score;
            if (isLegal)
            {
                score = -Search(pos, -beta, -alpha, engine);
            }
            else
            {
                score = -MateValue; // Illegal move
            }
            
            // Undo move
            pos.UndoMove(move);
            
            if (!isLegal)
                continue;
                
            if (score > bestScore)
            {
                bestScore = score;
                
                if (score > alpha)
                {
                    alpha = score;
                    
                    if (alpha >= beta)
                        break; // Beta cutoff
                }
            }
        }
        
        return bestScore;
    }
    
    private static int GenerateCaptures(Position pos, Span<Move> buffer)
    {
        // For now, use the full move generator and filter captures
        // TODO: Implement dedicated capture-only generator
        Span<Move> allMoves = stackalloc Move[256];
        int totalMoves = MoveGenerator.GenerateMoves(pos, allMoves);
        
        int captureCount = 0;
        for (int i = 0; i < totalMoves; i++)
        {
            var move = allMoves[i];
            if (IsCapture(move) || IsPromotion(move))
            {
                if (captureCount < buffer.Length)
                    buffer[captureCount++] = move;
            }
        }
        
        return captureCount;
    }
    
    private static bool IsCapture(Move move)
    {
        return move.Flag == MoveFlag.Capture || 
               move.Flag == MoveFlag.EnPassant ||
               move.Flag >= MoveFlag.PromoCaptureQueen;
    }
    
    private static bool IsPromotion(Move move)
    {
        return move.Flag >= MoveFlag.PromoQueen && move.Flag <= MoveFlag.PromoCaptureKnight;
    }
    
    private static int Evaluate(Position pos)
    {
        // Simple material evaluation for now
        int eval = 0;
        
        // Material values
        const int pawnValue = 100;
        const int knightValue = 320;
        const int bishopValue = 330;
        const int rookValue = 500;
        const int queenValue = 900;
        
        // Count material for both sides
        for (Color color = Color.White; color <= Color.Black; color++)
        {
            int materialValue = 0;
            materialValue += PopCount(pos.Pieces(color, PieceType.Pawn)) * pawnValue;
            materialValue += PopCount(pos.Pieces(color, PieceType.Knight)) * knightValue;
            materialValue += PopCount(pos.Pieces(color, PieceType.Bishop)) * bishopValue;
            materialValue += PopCount(pos.Pieces(color, PieceType.Rook)) * rookValue;
            materialValue += PopCount(pos.Pieces(color, PieceType.Queen)) * queenValue;
            
            if (color == Color.White)
                eval += materialValue;
            else
                eval -= materialValue;
        }
        
        // Return from the perspective of the side to move
        return pos.SideToMove == Color.White ? eval : -eval;
    }
    
    private static int StaticExchangeEvaluation(Position pos, Move move)
    {
        // Simplified SEE - just check if we're capturing something more valuable
        // TODO: Implement proper SEE with full exchange sequence
        
        if (!IsCapture(move))
            return 0;
            
        // For now, assume all captures are good
        return 100;
    }
    
    private static int PopCount(ulong bitboard)
    {
        return System.Numerics.BitOperations.PopCount(bitboard);
    }
}