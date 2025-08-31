using System;
using Machine.Core;
using Machine.MoveGen;

namespace Machine.UCI;

internal static class MoveParser
{
    public static bool TryParseUciMove(Position pos, ReadOnlySpan<char> s, out Move move)
    {
        move = Move.NullMove;
        if (s.Length < 4) return false;

        int fromFile = s[0] - 'a';
        int fromRank = s[1] - '1';
        int toFile = s[2] - 'a';
        int toRank = s[3] - '1';
        if ((uint)fromFile >= 8 || (uint)toFile >= 8 || (uint)fromRank >= 8 || (uint)toRank >= 8) return false;
        int from = fromRank * 8 + fromFile;
        int to = toRank * 8 + toFile;
        char promoChar = s.Length >= 5 ? char.ToLowerInvariant(s[4]) : '\0';

        Span<Move> buf = stackalloc Move[256];
        int n = MoveGenerator.GenerateMoves(pos, buf);
        for (int i = 0; i < n; i++)
        {
            var m = buf[i];
            if (m.From != from || m.To != to) continue;
            if (promoChar != '\0')
            {
                // Require matching promotion flag
                if (!PromotionMatches(m.Flag, promoChar)) continue;
            }
            else
            {
                // Skip promotion moves if no letter provided
                if (IsPromotion(m.Flag)) continue;
            }

            // Legality check
            pos.ApplyMove(m);
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool legal = !pos.IsKingInCheck(movedColor);
            pos.UndoMove(m);
            if (!legal) continue;

            move = m;
            return true;
        }
        return false;
    }

    private static bool PromotionMatches(MoveFlag flag, char promo)
    {
        return (promo, flag) switch
        {
            ('q', MoveFlag.PromoQueen) or ('q', MoveFlag.PromoCaptureQueen) => true,
            ('r', MoveFlag.PromoRook) or ('r', MoveFlag.PromoCaptureRook) => true,
            ('b', MoveFlag.PromoBishop) or ('b', MoveFlag.PromoCaptureBishop) => true,
            ('n', MoveFlag.PromoKnight) or ('n', MoveFlag.PromoCaptureKnight) => true,
            _ => false
        };
    }

    private static bool IsPromotion(MoveFlag flag)
    {
        return flag >= MoveFlag.PromoQueen && flag <= MoveFlag.PromoCaptureKnight;
    }
}

