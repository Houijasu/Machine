using System;
using Machine.Core;

namespace Machine.MoveGen;

public static class MoveGenerator
{
    // Temporary API: fill buffer with pseudo-legal moves and return count
    // Implements: knights, king, pawn pushes. Sliding pieces and captures next.
    public static int GenerateMoves(Position pos, Span<Move> buffer)
    {
        int count = 0;
        var us = pos.SideToMove;
        var them = us == Color.White ? Color.Black : Color.White;
        ulong usOcc = pos.Occupied(us);
        ulong themOcc = pos.Occupied(them);
        ulong occ = usOcc | themOcc;
        ulong empty = ~occ;

        // Knights
        int knightIndex = Position.PieceIndex(us, PieceType.Knight);
        ulong knights = pos.PieceBB[knightIndex];
        while (knights != 0)
        {
            int from = Bitboards.Lsb(knights);
            knights &= knights - 1;
            ulong attacks = AttackTables.KnightAttacks[from] & ~usOcc;
            Emit(from, attacks, themOcc, ref count, buffer);
        }

        // King
        int kingIndex = Position.PieceIndex(us, PieceType.King);
        ulong king = pos.PieceBB[kingIndex];
        if (king != 0)
        {
            int from = Bitboards.Lsb(king);
            ulong attacks = AttackTables.KingAttacks[from] & ~usOcc;
            Emit(from, attacks, themOcc, ref count, buffer);
        }

        // Pawn moves (pushes, captures, promotions, EP)
        if (us == Color.White)
        {
            ulong pawns = pos.Pieces(us, PieceType.Pawn);
            const ulong Rank2 = 0x000000000000FF00UL;
            const ulong Rank8 = 0xFF00000000000000UL;

            // single pushes
            ulong one = (pawns << 8) & empty;
            // promotions by push
            ulong promoPush = one & Rank8;
            ulong nonPromoPush = one & ~Rank8;
            EmitPushes(nonPromoPush, 8, ref count, buffer);
            // promotions
            EmitPromotionsPush(promoPush, 8, ref count, buffer);

            // double pushes from rank 2
            ulong two = (((pawns & Rank2) << 8) & empty) << 8;
            two &= empty;
            EmitPushes(two, 16, ref count, buffer, MoveFlag.DoublePush);

            // captures
            ulong leftCaps = ((pawns & ~Bitboards.FileA) << 7) & themOcc;
            ulong rightCaps = ((pawns & ~Bitboards.FileH) << 9) & themOcc;
            // promotions on capture
            ulong promoLeft = leftCaps & Rank8;
            ulong promoRight = rightCaps & Rank8;
            EmitPromotionsCapture(promoLeft, 7, ref count, buffer);
            EmitPromotionsCapture(promoRight, 9, ref count, buffer);
            // non-promo captures
            EmitCaps(leftCaps & ~Rank8, 7, ref count, buffer);
            EmitCaps(rightCaps & ~Rank8, 9, ref count, buffer);

            // en passant
            if (pos.EnPassantSquare >= 0)
            {
                int ep = pos.EnPassantSquare;
                int epFile = ep % 8; int epRank = ep / 8;
                if (epRank == 5) // target on rank 6 (0-based), capturing pawn on rank 5
                {
                    // from squares: left ep-9 (from file ep-1), right ep-7 (from file ep+1)
                    int fromLeft = ep - 9;
                    int fromRight = ep - 7;
                    if (epFile > 0 && (pawns & (1UL << fromLeft)) != 0)
                        buffer[count++] = new Move(fromLeft, ep, MoveFlag.EnPassant);
                    if (epFile < 7 && (pawns & (1UL << fromRight)) != 0)
                        buffer[count++] = new Move(fromRight, ep, MoveFlag.EnPassant);
                }
            }
        }
        else
        {
            ulong pawns = pos.Pieces(us, PieceType.Pawn);
            const ulong Rank7 = 0x00FF000000000000UL;
            const ulong Rank1 = 0x00000000000000FFUL;

            // single pushes
            ulong one = (pawns >> 8) & empty;
            // promotions by push (to rank 1)
            ulong promoPush = one & Rank1;
            ulong nonPromoPush = one & ~Rank1;
            EmitPushes(nonPromoPush, -8, ref count, buffer);
            EmitPromotionsPush(promoPush, -8, ref count, buffer);

            // double pushes from rank 7
            ulong two = (((pawns & Rank7) >> 8) & empty) >> 8;
            two &= empty;
            EmitPushes(two, -16, ref count, buffer, MoveFlag.DoublePush);

            // captures
            ulong leftCaps = ((pawns & ~Bitboards.FileA) >> 9) & themOcc;
            ulong rightCaps = ((pawns & ~Bitboards.FileH) >> 7) & themOcc;
            // promotions on capture
            ulong promoLeft = leftCaps & Rank1;
            ulong promoRight = rightCaps & Rank1;
            EmitPromotionsCaptureBlack(promoLeft, -9, ref count, buffer);
            EmitPromotionsCaptureBlack(promoRight, -7, ref count, buffer);
            // non-promo captures
            EmitCapsBlack(leftCaps & ~Rank1, -9, ref count, buffer);
            EmitCapsBlack(rightCaps & ~Rank1, -7, ref count, buffer);

            // en passant
            if (pos.EnPassantSquare >= 0)
            {
                int ep = pos.EnPassantSquare;
                int epFile = ep % 8; int epRank = ep / 8;
                if (epRank == 2) // target on rank 3 (0-based), capturing pawn on rank 4
                {
                    // from squares: left ep+7 (from file ep-1), right ep+9 (from file ep+1)
                    int fromLeft = ep + 7;
                    int fromRight = ep + 9;
                    if (epFile > 0 && (pawns & (1UL << fromLeft)) != 0)
                        buffer[count++] = new Move(fromLeft, ep, MoveFlag.EnPassant);
                    if (epFile < 7 && (pawns & (1UL << fromRight)) != 0)
                        buffer[count++] = new Move(fromRight, ep, MoveFlag.EnPassant);
                }
            }
        }

        // Sliding pieces using precomputed tables
        // Bishops
        ulong bishopsBB = pos.Pieces(us, PieceType.Bishop);
        while (bishopsBB != 0)
        {
            int from = Bitboards.Lsb(bishopsBB);
            bishopsBB &= bishopsBB - 1;
            ulong attacks = Magics.GetBishopAttacks(from, occ) & ~usOcc;
            Emit(from, attacks, themOcc, ref count, buffer);
        }
        // Rooks
        ulong rooksBB = pos.Pieces(us, PieceType.Rook);
        while (rooksBB != 0)
        {
            int from = Bitboards.Lsb(rooksBB);
            rooksBB &= rooksBB - 1;
            ulong attacks = Magics.GetRookAttacks(from, occ) & ~usOcc;
            Emit(from, attacks, themOcc, ref count, buffer);
        }
        // Queens
        ulong queensBB = pos.Pieces(us, PieceType.Queen);
        while (queensBB != 0)
        {
            int from = Bitboards.Lsb(queensBB);
            queensBB &= queensBB - 1;
            ulong attacks = (Magics.GetBishopAttacks(from, occ) | Magics.GetRookAttacks(from, occ)) & ~usOcc;
            Emit(from, attacks, themOcc, ref count, buffer);
        }

        // Castling (with through-check validation)
        if (us == Color.White)
        {
            if ((pos.Castling & CastlingRights.WhiteKing) != 0)
            {
                // Squares F1(5), G1(6) must be empty; rook on H1(7)
                // King path E1(4), F1(5), G1(6) must not be attacked
                if (((occ >> 5) & 1UL) == 0 && ((occ >> 6) & 1UL) == 0 &&
                    ((pos.Pieces(us, PieceType.Rook) >> 7) & 1UL) != 0 &&
                    !pos.IsSquareAttacked(4, them) &&
                    !pos.IsSquareAttacked(5, them) &&
                    !pos.IsSquareAttacked(6, them))
                    buffer[count++] = new Move(4, 6, MoveFlag.KingCastle);
            }
            if ((pos.Castling & CastlingRights.WhiteQueen) != 0)
            {
                // Squares B1(1), C1(2), D1(3) empty; rook on A1(0)
                // King path E1(4), D1(3), C1(2) must not be attacked
                if (((occ & ((1UL<<1)|(1UL<<2)|(1UL<<3))) == 0) &&
                    ((pos.Pieces(us, PieceType.Rook) & 1UL) != 0) &&
                    !pos.IsSquareAttacked(4, them) &&
                    !pos.IsSquareAttacked(3, them) &&
                    !pos.IsSquareAttacked(2, them))
                    buffer[count++] = new Move(4, 2, MoveFlag.QueenCastle);
            }
        }
        else
        {
            if ((pos.Castling & CastlingRights.BlackKing) != 0)
            {
                // F8(61), G8(62) empty; rook on H8(63)
                // King path E8(60), F8(61), G8(62) must not be attacked
                if (((occ >> 61) & 1UL) == 0 && ((occ >> 62) & 1UL) == 0 &&
                    ((pos.Pieces(us, PieceType.Rook) >> 63) & 1UL) != 0 &&
                    !pos.IsSquareAttacked(60, them) &&
                    !pos.IsSquareAttacked(61, them) &&
                    !pos.IsSquareAttacked(62, them))
                    buffer[count++] = new Move(60, 62, MoveFlag.KingCastle);
            }
            if ((pos.Castling & CastlingRights.BlackQueen) != 0)
            {
                // B8(57), C8(58), D8(59) empty; rook on A8(56)
                // King path E8(60), D8(59), C8(58) must not be attacked
                if (((occ & ((1UL<<57)|(1UL<<58)|(1UL<<59))) == 0) &&
                    ((pos.Pieces(us, PieceType.Rook) & (1UL<<56)) != 0) &&
                    !pos.IsSquareAttacked(60, them) &&
                    !pos.IsSquareAttacked(59, them) &&
                    !pos.IsSquareAttacked(58, them))
                    buffer[count++] = new Move(60, 58, MoveFlag.QueenCastle);
            }
        }


        return count;
    }

    public static int GenerateCapturesOnly(Position pos, Span<Move> buffer)
        {
            int count = 0;
            var us = pos.SideToMove;
            var them = us == Color.White ? Color.Black : Color.White;
            ulong usOcc = pos.Occupied(us);
            ulong themOcc = pos.Occupied(them);
            ulong occ = usOcc | themOcc;

            // Knights captures
            int knightIndex = Position.PieceIndex(us, PieceType.Knight);
            ulong knights = pos.PieceBB[knightIndex];
            while (knights != 0)
            {
                int from = Bitboards.Lsb(knights);
                knights &= knights - 1;
                ulong caps = AttackTables.KnightAttacks[from] & themOcc;
                EmitMoves(from, caps, ref count, buffer, isCapture: true);
            }

            // King captures
            int kingIndex = Position.PieceIndex(us, PieceType.King);
            ulong king = pos.PieceBB[kingIndex];
            if (king != 0)
            {
                int from = Bitboards.Lsb(king);
                ulong caps = AttackTables.KingAttacks[from] & themOcc;
                EmitMoves(from, caps, ref count, buffer, isCapture: true);
            }

            // Pawn capture moves and EP
            if (us == Color.White)
            {
                ulong pawns = pos.Pieces(us, PieceType.Pawn);
                const ulong Rank8 = 0xFF00000000000000UL;
                ulong leftCaps = ((pawns & ~Bitboards.FileA) << 7) & themOcc;
                ulong rightCaps = ((pawns & ~Bitboards.FileH) << 9) & themOcc;
                // promotions on capture
                ulong promoLeft = leftCaps & Rank8;
                ulong promoRight = rightCaps & Rank8;
                EmitPromotionsCapture(promoLeft, 7, ref count, buffer);
                EmitPromotionsCapture(promoRight, 9, ref count, buffer);
                // non-promo captures
                EmitCaps(leftCaps & ~Rank8, 7, ref count, buffer);
                EmitCaps(rightCaps & ~Rank8, 9, ref count, buffer);
                // en passant
                if (pos.EnPassantSquare >= 0)
                {
                    int ep = pos.EnPassantSquare;
                    int epFile = ep % 8; int epRank = ep / 8;
                    if (epRank == 5)
                    {
                        int fromLeft = ep - 9;
                        int fromRight = ep - 7;
                        if (epFile > 0 && (pawns & (1UL << fromLeft)) != 0)
                            buffer[count++] = new Move(fromLeft, ep, MoveFlag.EnPassant);
                        if (epFile < 7 && (pawns & (1UL << fromRight)) != 0)
                            buffer[count++] = new Move(fromRight, ep, MoveFlag.EnPassant);
                    }
                }
            }
            else
            {
                ulong pawns = pos.Pieces(us, PieceType.Pawn);
                const ulong Rank1 = 0x00000000000000FFUL;
                ulong leftCaps = ((pawns & ~Bitboards.FileA) >> 9) & themOcc;
                ulong rightCaps = ((pawns & ~Bitboards.FileH) >> 7) & themOcc;
                ulong promoLeft = leftCaps & Rank1;
                ulong promoRight = rightCaps & Rank1;
                EmitPromotionsCaptureBlack(promoLeft, -9, ref count, buffer);
                EmitPromotionsCaptureBlack(promoRight, -7, ref count, buffer);
                EmitCapsBlack(leftCaps & ~Rank1, -9, ref count, buffer);
                EmitCapsBlack(rightCaps & ~Rank1, -7, ref count, buffer);
                if (pos.EnPassantSquare >= 0)
                {
                    int ep = pos.EnPassantSquare;
                    int epFile = ep % 8; int epRank = ep / 8;
                    if (epRank == 2)
                    {
                        int fromLeft = ep + 7;
                        int fromRight = ep + 9;
                        if (epFile > 0 && (pawns & (1UL << fromLeft)) != 0)
                            buffer[count++] = new Move(fromLeft, ep, MoveFlag.EnPassant);
                        if (epFile < 7 && (pawns & (1UL << fromRight)) != 0)
                            buffer[count++] = new Move(fromRight, ep, MoveFlag.EnPassant);
                    }
                }
            }

            // Sliding captures
            ulong bishopsBB = pos.Pieces(us, PieceType.Bishop);
            while (bishopsBB != 0)
            {
                int from = Bitboards.Lsb(bishopsBB);
                bishopsBB &= bishopsBB - 1;
                ulong caps = Magics.GetBishopAttacks(from, occ) & themOcc;
                EmitMoves(from, caps, ref count, buffer, isCapture: true);
            }
            ulong rooksBB = pos.Pieces(us, PieceType.Rook);
            while (rooksBB != 0)
            {
                int from = Bitboards.Lsb(rooksBB);
                rooksBB &= rooksBB - 1;
                ulong caps = Magics.GetRookAttacks(from, occ) & themOcc;
                EmitMoves(from, caps, ref count, buffer, isCapture: true);
            }
            ulong queensBB = pos.Pieces(us, PieceType.Queen);
            while (queensBB != 0)
            {
                int from = Bitboards.Lsb(queensBB);
                queensBB &= queensBB - 1;
                ulong caps = (Magics.GetBishopAttacks(from, occ) | Magics.GetRookAttacks(from, occ)) & themOcc;
                EmitMoves(from, caps, ref count, buffer, isCapture: true);
            }

            return count;
        }

    private static void EmitCaps(ulong caps, int delta, ref int count, Span<Move> buffer)
    {
        while (caps != 0)
        {
            int to = Bitboards.Lsb(caps);
            caps &= caps - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, MoveFlag.Capture);
        }
    }
    private static void EmitCapsBlack(ulong caps, int delta, ref int count, Span<Move> buffer)
    {
        while (caps != 0)
        {
            int to = Bitboards.Lsb(caps);
            caps &= caps - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, MoveFlag.Capture);
        }
    }
    private static void EmitPromotionsPush(ulong pushTargets, int delta, ref int count, Span<Move> buffer)
    {
        while (pushTargets != 0)
        {
            int to = Bitboards.Lsb(pushTargets);
            pushTargets &= pushTargets - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, MoveFlag.PromoQueen);
            buffer[count++] = new Move(from, to, MoveFlag.PromoRook);
            buffer[count++] = new Move(from, to, MoveFlag.PromoBishop);
            buffer[count++] = new Move(from, to, MoveFlag.PromoKnight);
        }
    }
    private static void EmitPromotionsCapture(ulong caps, int delta, ref int count, Span<Move> buffer)
    {
        while (caps != 0)
        {
            int to = Bitboards.Lsb(caps);
            caps &= caps - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureQueen);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureRook);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureBishop);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureKnight);
        }
    }
    private static void EmitPromotionsCaptureBlack(ulong caps, int delta, ref int count, Span<Move> buffer)
    {
        while (caps != 0)
        {
            int to = Bitboards.Lsb(caps);
            caps &= caps - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureQueen);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureRook);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureBishop);
            buffer[count++] = new Move(from, to, MoveFlag.PromoCaptureKnight);
        }
    }

    private static void Emit(int from, ulong attacks, ulong themOcc, ref int count, Span<Move> buffer)
    {
        ulong caps = attacks & themOcc;
        ulong quiets = attacks & ~themOcc;
        EmitMoves(from, caps, ref count, buffer, isCapture: true);
        EmitMoves(from, quiets, ref count, buffer, isCapture: false);
    }

    private static void EmitMoves(int from, ulong targets, ref int count, Span<Move> buffer, bool isCapture)
    {
        while (targets != 0)
        {
            int to = Bitboards.Lsb(targets);
            targets &= targets - 1;
            buffer[count++] = new Move(from, to, isCapture ? MoveFlag.Capture : MoveFlag.Quiet);
        }
    }

    private static void EmitPushes(ulong targets, int delta, ref int count, Span<Move> buffer, MoveFlag flag = MoveFlag.Quiet)
    {
        while (targets != 0)
        {
            int to = Bitboards.Lsb(targets);
            targets &= targets - 1;
            int from = to - delta;
            buffer[count++] = new Move(from, to, flag);
        }
    }
}
