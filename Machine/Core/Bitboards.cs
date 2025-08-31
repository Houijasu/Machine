namespace Machine.Core;

using System;
using System.Numerics;

public static class Bitboards
{
    public const ulong FileA = 0x0101010101010101UL;
    public const ulong FileH = 0x8080808080808080UL;
    public const ulong Rank1 = 0x00000000000000FFUL;
    public const ulong Rank8 = 0xFF00000000000000UL;

    public static int PopCount(ulong bb) => BitOperations.PopCount(bb);
    public static int Lsb(ulong bb) => bb == 0 ? -1 : (int)BitOperations.TrailingZeroCount(bb);
    public static int Msb(ulong bb) => bb == 0 ? -1 : 63 - (int)BitOperations.LeadingZeroCount(bb);
}

