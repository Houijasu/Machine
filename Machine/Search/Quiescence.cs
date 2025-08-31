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
        int standPat = Evaluation.Evaluate(pos);

        if (standPat >= beta)
            return beta;

        // Delta pruning - if even capturing the most valuable piece won't raise alpha
        const int queenValue = 900;
        if (standPat < alpha - queenValue)
            return alpha;

        if (standPat > alpha)
            alpha = standPat;
        
        // Generate only captures (dedicated generator)
        Span<Move> moves = stackalloc Move[256];
        int moveCount = MoveGenerator.GenerateCapturesOnly(pos, moves);
        
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
        return move.Flag is >= MoveFlag.PromoQueen and <= MoveFlag.PromoCaptureKnight;
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
    
    public static int StaticExchangeEvaluation(Position pos, Move move)
    {
        if (!IsCapture(move)) return 0;

        int to = move.To;
        int from = move.From;
        if ((uint)to >= 64 || (uint)from >= 64) return 0; // safety
        Color us = pos.SideToMove;
        Color them = us == Color.White ? Color.Black : Color.White;

        // Piece values
        Span<int> val = [100, 320, 330, 500, 900, 20000];

        // Determine victim piece type on 'to' square
        int victimIdx;
        PieceType victim = PieceType.None;
        if (move.Flag == MoveFlag.EnPassant)
            victim = PieceType.Pawn;
        else if (pos.PieceAtFast(to, out victimIdx))
            victim = (PieceType)((victimIdx % 6) + 1);

        if (victim == PieceType.None) return 0;

        // Occupancy used for x-ray recomputation
        ulong occTemp = pos.AllOccupied;
        ulong toMask = 1UL << to;
        ulong fromMask = 1UL << from;

        // Remove moving piece from origin and remove victim from target, then place mover on target
        occTemp &= ~fromMask;
        occTemp &= ~toMask;
        occTemp |= toMask;

        // Build swap list
        int balanceIdx = 0;
        Span<int> swapList = stackalloc int[32]; // guard upper bound
        swapList[balanceIdx++] = val[(int)victim - 1];

        // Track last captured type for value accounting
        PieceType captured = victim;
        Color side = them; // after our initial capture, opponent to move

        while (true)
        {
            // Recompute attackers with updated occupancy (x-rays)
            ulong atk = pos.GetAttackers(to, side, occTemp);
            if (atk == 0) break;

            // Choose least valuable attacker square
            PieceType lvaType = PieceType.None;
            int lvaSq = -1;
            for (PieceType pt = PieceType.Pawn; pt <= PieceType.King; pt++)
            {
                ulong bb = pos.Pieces(side, pt) & atk;
                if (bb != 0)
                {
                    lvaType = pt;
                    lvaSq = Bitboards.Lsb(bb);
                    break;
                }
            }
            if (lvaSq < 0 || lvaType == PieceType.None) break; // safety guard

            if (balanceIdx >= swapList.Length) break; // prevent overflow
            // Recapture: gain/loss relative to previous
            int gain = val[(int)captured - 1];
            swapList[balanceIdx] = -swapList[balanceIdx - 1] + gain;
            balanceIdx++;

            // Update occupancy: remove attacker from lvaSq, remove current target occupant, then place attacker on target
            occTemp &= ~(1UL << lvaSq);
            occTemp &= ~toMask;
            occTemp |= toMask;

            // Next captured piece is the recapturing piece type
            captured = lvaType;
            side = side == Color.White ? Color.Black : Color.White;
        }

        // Minimax the swap list backwards
        for (int i = balanceIdx - 2; i >= 0; --i)
        {
            if (swapList[i] < -swapList[i + 1])
                swapList[i] = -swapList[i + 1];
        }

        return swapList[0];
    }
    
    private static int PopCount(ulong bitboard)
    {
        return System.Numerics.BitOperations.PopCount(bitboard);
    }
}