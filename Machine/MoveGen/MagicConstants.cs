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
    // TODO: Replace placeholder entries with Stockfish magics and (optionally) masks.
    // RookMagics[sq] and BishopMagics[sq] must be populated with correct constants.

    public static readonly MagicEntry[] RookMagics = new MagicEntry[64]
    {
        // Placeholder entries
        new(0UL, 52), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 52),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 53), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 54), new(0UL, 53),
        new(0UL, 52), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 53), new(0UL, 52),
    };

    public static readonly MagicEntry[] BishopMagics = new MagicEntry[64]
    {
        // Placeholder entries
        new(0UL, 58), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 58),
        new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59),
        new(0UL, 59), new(0UL, 59), new(0UL, 57), new(0UL, 57), new(0UL, 57), new(0UL, 57), new(0UL, 59), new(0UL, 59),
        new(0UL, 59), new(0UL, 59), new(0UL, 57), new(0UL, 55), new(0UL, 55), new(0UL, 57), new(0UL, 59), new(0UL, 59),
        new(0UL, 59), new(0UL, 59), new(0UL, 57), new(0UL, 55), new(0UL, 55), new(0UL, 57), new(0UL, 59), new(0UL, 59),
        new(0UL, 59), new(0UL, 59), new(0UL, 57), new(0UL, 57), new(0UL, 57), new(0UL, 57), new(0UL, 59), new(0UL, 59),
        new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59),
        new(0UL, 58), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 59), new(0UL, 58),
    };
}

