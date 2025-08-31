using Machine.Core;
using System.Numerics;

namespace Machine.MoveGen;

public static class Magics
{
    // Per-square masks and tables built at init.
    private static readonly ulong[] BishopMasks = new ulong[64];
    private static readonly ulong[] RookMasks = new ulong[64];
    private static readonly int[] BishopBits = new int[64];
    private static readonly int[] RookBits = new int[64];

    private static readonly ulong[] BishopMagics = new ulong[64];
    private static readonly ulong[] RookMagics = new ulong[64];
    private static readonly int[] BishopShifts = new int[64];
    private static readonly int[] RookShifts = new int[64];

    public static readonly ulong[][] BishopAttacks = new ulong[64][];
    public static readonly ulong[][] RookAttacks = new ulong[64][];

    static Magics()
    {
        InitMasks();
        InitTablesWithRuntimeMagics();
    }

    public static ulong GetBishopAttacks(int sq, ulong occ)
    {
        var mask = BishopMasks[sq];
        int idx;
        if (BishopMagics[sq] != 0)
            idx = (int)(((occ & mask) * BishopMagics[sq]) >> BishopShifts[sq]);
        else
            idx = CompressToIndex(occ & mask, mask); // fallback
        return BishopAttacks[sq][idx];
    }

    public static ulong GetRookAttacks(int sq, ulong occ)
    {
        var mask = RookMasks[sq];
        int idx;
        if (RookMagics[sq] != 0)
            idx = (int)(((occ & mask) * RookMagics[sq]) >> RookShifts[sq]);
        else
            idx = CompressToIndex(occ & mask, mask);
        return RookAttacks[sq][idx];
    }

    private static void InitMasks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            BishopMasks[sq] = ComputeBishopMask(sq);
            RookMasks[sq] = ComputeRookMask(sq);
            BishopBits[sq] = Bitboards.PopCount(BishopMasks[sq]);
            RookBits[sq] = Bitboards.PopCount(RookMasks[sq]);
            BishopShifts[sq] = 64 - BishopBits[sq];
            RookShifts[sq] = 64 - RookBits[sq];
        }
    }

    private static void InitTablesWithRuntimeMagics()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int bSize = 1 << BishopBits[sq];
            int rSize = 1 << RookBits[sq];
            // We'll fill tables using found magics below

            // Find bishop magic and table
            BishopMagics[sq] = StockfishMagicInit.FindMagicForSquare(sq, BishopMasks[sq], AttackTablesExt.BishopRaysFrom, BishopShifts[sq], out var bTable);
            BishopAttacks[sq] = bTable;

            // Find rook magic and table
            RookMagics[sq] = StockfishMagicInit.FindMagicForSquare(sq, RookMasks[sq], AttackTablesExt.RookRaysFrom, RookShifts[sq], out var rTable);
            RookAttacks[sq] = rTable;
        }
    }

    private static int CompressToIndex(ulong bits, ulong mask)
    {
        // Software PEXT fallback
        int idx = 0;
        int n = 0;
        while (mask != 0)
        {
            ulong lsb = mask & (~mask + 1);
            if ((bits & lsb) != 0) idx |= 1 << n;
            mask ^= lsb;
            n++;
        }
        return idx;
    }

    private static ulong ComputeBishopMask(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong mask = 0;
        // NE (exclude edge)
        for (int rr = r + 1, ff = f + 1; rr < 7 && ff < 7; rr++, ff++) mask |= 1UL << (rr * 8 + ff);
        // NW
        for (int rr = r + 1, ff = f - 1; rr < 7 && ff > 0; rr++, ff--) mask |= 1UL << (rr * 8 + ff);
        // SE
        for (int rr = r - 1, ff = f + 1; rr > 0 && ff < 7; rr--, ff++) mask |= 1UL << (rr * 8 + ff);
        // SW
        for (int rr = r - 1, ff = f - 1; rr > 0 && ff > 0; rr--, ff--) mask |= 1UL << (rr * 8 + ff);
        return mask;
    }

    private static ulong ComputeRookMask(int sq)
    {
        int r = sq / 8, f = sq % 8;
        ulong mask = 0;
        // North (exclude edge)
        for (int rr = r + 1; rr < 7; rr++) mask |= 1UL << (rr * 8 + f);
        // South
        for (int rr = r - 1; rr > 0; rr--) mask |= 1UL << (rr * 8 + f);
        // East
        for (int ff = f + 1; ff < 7; ff++) mask |= 1UL << (r * 8 + ff);
        // West
        for (int ff = f - 1; ff > 0; ff--) mask |= 1UL << (r * 8 + ff);
        return mask;
    }
}

