using System;

namespace Machine.MoveGen;

public static class MagicBitboards
{
    // Precomputed magics placeholder: fill with real constants next step
    public static readonly ulong[] RookMagics = new ulong[64];
    public static readonly ulong[] BishopMagics = new ulong[64];
    public static readonly ulong[] RookMasks = new ulong[64];
    public static readonly ulong[] BishopMasks = new ulong[64];
    public static readonly int[] RookShifts = new int[64];
    public static readonly int[] BishopShifts = new int[64];

    static MagicBitboards()
    {
        // TODO: Replace with Stockfish/verified constants.
        // For now, leave zeros to allow compilation while we wire tables.
    }
}

