using Machine.Core;

namespace Machine.MoveGen;

public static class Sliding
{
    // Fallback sliding using ray walkers; magics will replace these later
    public static ulong GetBishopAttacks(int sq, ulong occupancy)
        => AttackTablesExt.BishopRaysFrom(sq, occupancy);

    public static ulong GetRookAttacks(int sq, ulong occupancy)
        => AttackTablesExt.RookRaysFrom(sq, occupancy);
}

