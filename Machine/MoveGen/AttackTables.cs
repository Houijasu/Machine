using System;
using Machine.Core;

namespace Machine.MoveGen;

// Sliding rays (fallback while magics are being imported)
public static class AttackTablesExt
{
    public static ulong BishopRaysFrom(int sq, ulong occ)
    {
        ulong attacks = 0;
        int r = sq / 8, f = sq % 8;
        // NE
        for (int rr = r + 1, ff = f + 1; rr < 8 && ff < 8; rr++, ff++) { int s = rr * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // NW
        for (int rr = r + 1, ff = f - 1; rr < 8 && ff >= 0; rr++, ff--) { int s = rr * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // SE
        for (int rr = r - 1, ff = f + 1; rr >= 0 && ff < 8; rr--, ff++) { int s = rr * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // SW
        for (int rr = r - 1, ff = f - 1; rr >= 0 && ff >= 0; rr--, ff--) { int s = rr * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        return attacks;
    }

    public static ulong RookRaysFrom(int sq, ulong occ)
    {
        ulong attacks = 0;
        int r = sq / 8, f = sq % 8;
        // North
        for (int rr = r + 1; rr < 8; rr++) { int s = rr * 8 + f; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // South
        for (int rr = r - 1; rr >= 0; rr--) { int s = rr * 8 + f; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // East
        for (int ff = f + 1; ff < 8; ff++) { int s = r * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        // West
        for (int ff = f - 1; ff >= 0; ff--) { int s = r * 8 + ff; attacks |= 1UL << s; if (((occ >> s) & 1UL) != 0) break; }
        return attacks;
    }
}

public static class AttackTables
{
    public static readonly ulong[] KnightAttacks = new ulong[64];
    public static readonly ulong[] KingAttacks = new ulong[64];
    public static readonly ulong[,] PawnAttacks = new ulong[2, 64]; // [color, sq]

    static AttackTables()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            KnightAttacks[sq] = BuildKnightAttacks(sq);
            KingAttacks[sq] = BuildKingAttacks(sq);
            PawnAttacks[(int)Color.White, sq] = BuildPawnAttacks(Color.White, sq);
            PawnAttacks[(int)Color.Black, sq] = BuildPawnAttacks(Color.Black, sq);
        }
    }

    private static ulong BuildKnightAttacks(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong bb = 0;
        int[] dr = [2, 2, -2, -2, 1, 1, -1, -1];
        int[] df = [1, -1, 1, -1, 2, -2, 2, -2];
        for (int i = 0; i < 8; i++)
        {
            int nr = r + dr[i], nf = f + df[i];
            if ((uint)nr < 8 && (uint)nf < 8) bb |= 1UL << (nr * 8 + nf);
        }
        return bb;
    }

    private static ulong BuildKingAttacks(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong bb = 0;
        for (int dr = -1; dr <= 1; dr++)
        for (int df = -1; df <= 1; df++)
        {
            if (dr == 0 && df == 0) continue;
            int nr = r + dr, nf = f + df;
            if ((uint)nr < 8 && (uint)nf < 8) bb |= 1UL << (nr * 8 + nf);
        }
        return bb;
    }

    private static ulong BuildPawnAttacks(Color c, int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong bb = 0;
        int dir = c == Color.White ? 1 : -1;
        int nr = r + dir;
        if ((uint)nr < 8)
        {
            if (f - 1 >= 0) bb |= 1UL << (nr * 8 + (f - 1));
            if (f + 1 < 8) bb |= 1UL << (nr * 8 + (f + 1));
        }
        return bb;
    }
}

