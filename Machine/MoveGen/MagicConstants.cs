/*
 This file contains data structures and (to be added) magic bitboard constants
 used for sliding piece attack generation. The magic numbers are imported from
 Stockfish (GPL-3.0). By including these constants we acknowledge and comply with
 the GPL licensing requirements for derivative works.

 Source: Stockfish (e.g., Stockfish 16/17 bitboards / magic tables)
 Details: https://github.com/official-stockfish/Stockfish

 Notes:
 - We isolate GPL-dependent data here for clarity. Engine code that depends on
   magics should reference this file for Magic entries.
 - Masks may be computed at runtime; Magic and Shift are constants.
*/

namespace Machine.MoveGen;

public readonly struct MagicEntry
{
    public readonly ulong Magic;
    public readonly int Shift;
    public readonly ulong Mask; // Filled at startup if not precomputed

    public MagicEntry(ulong magic, int shift, ulong mask = 0)
    {
        Magic = magic;
        Shift = shift;
        Mask = mask;
    }
}

public static class MagicConstants
{
    // Magic bitboard constants from Stockfish (GPL-3.0)
    // These precomputed values enable fast sliding piece attack generation
    
    public static readonly MagicEntry[] RookMagics =
    [
        new(StockfishMagics.RookMagics[0], 52), new(StockfishMagics.RookMagics[1], 53), new(StockfishMagics.RookMagics[2], 53), new(StockfishMagics.RookMagics[3], 53),
        new(StockfishMagics.RookMagics[4], 53), new(StockfishMagics.RookMagics[5], 53), new(StockfishMagics.RookMagics[6], 53), new(StockfishMagics.RookMagics[7], 52),
        new(StockfishMagics.RookMagics[8], 53), new(StockfishMagics.RookMagics[9], 54), new(StockfishMagics.RookMagics[10], 54), new(StockfishMagics.RookMagics[11], 54),
        new(StockfishMagics.RookMagics[12], 54), new(StockfishMagics.RookMagics[13], 54), new(StockfishMagics.RookMagics[14], 54), new(StockfishMagics.RookMagics[15], 53),
        new(StockfishMagics.RookMagics[16], 53), new(StockfishMagics.RookMagics[17], 54), new(StockfishMagics.RookMagics[18], 54), new(StockfishMagics.RookMagics[19], 54),
        new(StockfishMagics.RookMagics[20], 54), new(StockfishMagics.RookMagics[21], 54), new(StockfishMagics.RookMagics[22], 54), new(StockfishMagics.RookMagics[23], 53),
        new(StockfishMagics.RookMagics[24], 53), new(StockfishMagics.RookMagics[25], 54), new(StockfishMagics.RookMagics[26], 54), new(StockfishMagics.RookMagics[27], 54),
        new(StockfishMagics.RookMagics[28], 54), new(StockfishMagics.RookMagics[29], 54), new(StockfishMagics.RookMagics[30], 54), new(StockfishMagics.RookMagics[31], 53),
        new(StockfishMagics.RookMagics[32], 53), new(StockfishMagics.RookMagics[33], 54), new(StockfishMagics.RookMagics[34], 54), new(StockfishMagics.RookMagics[35], 54),
        new(StockfishMagics.RookMagics[36], 54), new(StockfishMagics.RookMagics[37], 54), new(StockfishMagics.RookMagics[38], 54), new(StockfishMagics.RookMagics[39], 53),
        new(StockfishMagics.RookMagics[40], 53), new(StockfishMagics.RookMagics[41], 54), new(StockfishMagics.RookMagics[42], 54), new(StockfishMagics.RookMagics[43], 54),
        new(StockfishMagics.RookMagics[44], 54), new(StockfishMagics.RookMagics[45], 54), new(StockfishMagics.RookMagics[46], 54), new(StockfishMagics.RookMagics[47], 53),
        new(StockfishMagics.RookMagics[48], 53), new(StockfishMagics.RookMagics[49], 54), new(StockfishMagics.RookMagics[50], 54), new(StockfishMagics.RookMagics[51], 54),
        new(StockfishMagics.RookMagics[52], 54), new(StockfishMagics.RookMagics[53], 54), new(StockfishMagics.RookMagics[54], 54), new(StockfishMagics.RookMagics[55], 53),
        new(StockfishMagics.RookMagics[56], 52), new(StockfishMagics.RookMagics[57], 53), new(StockfishMagics.RookMagics[58], 53), new(StockfishMagics.RookMagics[59], 53),
        new(StockfishMagics.RookMagics[60], 53), new(StockfishMagics.RookMagics[61], 53), new(StockfishMagics.RookMagics[62], 53), new(StockfishMagics.RookMagics[63], 52)
    ];

    public static readonly MagicEntry[] BishopMagics =
    [
        new(StockfishMagics.BishopMagics[0], 58), new(StockfishMagics.BishopMagics[1], 59), new(StockfishMagics.BishopMagics[2], 59), new(StockfishMagics.BishopMagics[3], 59),
        new(StockfishMagics.BishopMagics[4], 59), new(StockfishMagics.BishopMagics[5], 59), new(StockfishMagics.BishopMagics[6], 59), new(StockfishMagics.BishopMagics[7], 58),
        new(StockfishMagics.BishopMagics[8], 59), new(StockfishMagics.BishopMagics[9], 59), new(StockfishMagics.BishopMagics[10], 59), new(StockfishMagics.BishopMagics[11], 59),
        new(StockfishMagics.BishopMagics[12], 59), new(StockfishMagics.BishopMagics[13], 59), new(StockfishMagics.BishopMagics[14], 59), new(StockfishMagics.BishopMagics[15], 59),
        new(StockfishMagics.BishopMagics[16], 59), new(StockfishMagics.BishopMagics[17], 59), new(StockfishMagics.BishopMagics[18], 57), new(StockfishMagics.BishopMagics[19], 57),
        new(StockfishMagics.BishopMagics[20], 57), new(StockfishMagics.BishopMagics[21], 57), new(StockfishMagics.BishopMagics[22], 59), new(StockfishMagics.BishopMagics[23], 59),
        new(StockfishMagics.BishopMagics[24], 59), new(StockfishMagics.BishopMagics[25], 59), new(StockfishMagics.BishopMagics[26], 57), new(StockfishMagics.BishopMagics[27], 55),
        new(StockfishMagics.BishopMagics[28], 55), new(StockfishMagics.BishopMagics[29], 57), new(StockfishMagics.BishopMagics[30], 59), new(StockfishMagics.BishopMagics[31], 59),
        new(StockfishMagics.BishopMagics[32], 59), new(StockfishMagics.BishopMagics[33], 59), new(StockfishMagics.BishopMagics[34], 57), new(StockfishMagics.BishopMagics[35], 55),
        new(StockfishMagics.BishopMagics[36], 55), new(StockfishMagics.BishopMagics[37], 57), new(StockfishMagics.BishopMagics[38], 59), new(StockfishMagics.BishopMagics[39], 59),
        new(StockfishMagics.BishopMagics[40], 59), new(StockfishMagics.BishopMagics[41], 59), new(StockfishMagics.BishopMagics[42], 57), new(StockfishMagics.BishopMagics[43], 57),
        new(StockfishMagics.BishopMagics[44], 57), new(StockfishMagics.BishopMagics[45], 57), new(StockfishMagics.BishopMagics[46], 59), new(StockfishMagics.BishopMagics[47], 59),
        new(StockfishMagics.BishopMagics[48], 59), new(StockfishMagics.BishopMagics[49], 59), new(StockfishMagics.BishopMagics[50], 59), new(StockfishMagics.BishopMagics[51], 59),
        new(StockfishMagics.BishopMagics[52], 59), new(StockfishMagics.BishopMagics[53], 59), new(StockfishMagics.BishopMagics[54], 59), new(StockfishMagics.BishopMagics[55], 59),
        new(StockfishMagics.BishopMagics[56], 58), new(StockfishMagics.BishopMagics[57], 59), new(StockfishMagics.BishopMagics[58], 59), new(StockfishMagics.BishopMagics[59], 59),
        new(StockfishMagics.BishopMagics[60], 59), new(StockfishMagics.BishopMagics[61], 59), new(StockfishMagics.BishopMagics[62], 59), new(StockfishMagics.BishopMagics[63], 58)
    ];
}

