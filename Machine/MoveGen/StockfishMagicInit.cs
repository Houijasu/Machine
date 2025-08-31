/*
 Based on Stockfish 17.1 magic initialization (GPL-3.0).
 Ported to C# for generating magic numbers and attack tables at init time.
 Source: https://github.com/official-stockfish/Stockfish/blob/master/src/bitboard.cpp
*/
using System;
using System.Numerics;

namespace Machine.MoveGen;

internal static class StockfishMagicInit
{
    private static readonly int[,] Seeds = new int[2,8]
    {
        { 8977, 44560, 54343, 38998, 5731, 95205, 104912, 17020 }, // 64-bit row
        { 728, 10316, 55013, 32803, 12281, 15100, 16645, 255 }      // 32-bit row (unused here)
    };

    private struct PRNG
    {
        private ulong s;
        public PRNG(ulong seed) { s = seed != 0 ? seed : 1UL; }
        public ulong Rand64() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; }
        public ulong SparseRand() => Rand64() & Rand64() & Rand64();
    }

    // Returns a collision-free magic for a square, along with an attack table indexed by that magic
    public static ulong FindMagicForSquare(int sq, ulong mask, Func<int, ulong, ulong> rayFunc, int shift, out ulong[] table)
    {
        int bits = BitOperations.PopCount(mask);
        int tableSize = 1 << bits;
        table = new ulong[tableSize];

        // Build reference attacks for all subsets
        var occupancy = new ulong[tableSize];
        var reference = new ulong[tableSize];
        int idx = 0;
        ulong subset = 0UL;
        do
        {
            occupancy[idx] = subset;
            reference[idx] = rayFunc(sq, subset);
            idx++;
            subset = (subset - mask) & mask;
        } while (subset != 0);

        var rng = new PRNG((ulong)Seeds[0, sq / 8]);
        while (true)
        {
            ulong magic;
            do { magic = rng.SparseRand(); }
            while (BitOperations.PopCount((magic * mask) >> 56) < 6);

            Array.Fill(table, 0UL);
            bool fail = false;
            for (int i = 0; i < tableSize; i++)
            {
                int index = (int)((occupancy[i] * magic) >> shift);
                if (table[index] == 0UL)
                    table[index] = reference[i];
                else if (table[index] != reference[i]) { fail = true; break; }
            }
            if (!fail) return magic;
        }
    }
}

