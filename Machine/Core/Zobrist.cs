using System;

namespace Machine.Core;

public static class Zobrist
{
    public static readonly ulong[,] PieceSquare = new ulong[12, 64];
    public static readonly ulong[] Castle = new ulong[16];
    public static readonly ulong[] EnPassantFile = new ulong[8];
    public static readonly ulong SideToMove;

    static Zobrist()
    {
        // Fixed seed for reproducible keys
        var rng = new XorShift64(0x9E3779B97F4A7C15UL);

        for (int p = 0; p < 12; p++)
            for (int sq = 0; sq < 64; sq++)
                PieceSquare[p, sq] = rng.NextULong();

        for (int i = 0; i < Castle.Length; i++) Castle[i] = rng.NextULong();
        for (int f = 0; f < 8; f++) EnPassantFile[f] = rng.NextULong();
        SideToMove = rng.NextULong();
    }

    private struct XorShift64
    {
        private ulong _s;
        public XorShift64(ulong seed) => _s = seed != 0 ? seed : 0x1UL;
        public ulong NextULong()
        {
            ulong x = _s;
            x ^= x << 7;
            x ^= x >> 9;
            x ^= x << 8;
            _s = x;
            return x;
        }
    }
}

