using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Machine.Tables;

namespace Machine.Optimization;

public static class Prefetch
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TTEntry(ITranspositionTable tt, ulong key)
    {
        // Platform-agnostic: speculative read to warm cache
        if (tt is TranspositionTable concreteTT)
        {
            concreteTT.Prefetch(key);
        }

        // If we had the actual address of the bucket entries, we could use _mm_prefetch via HW intrinsics here.
        // In managed code, the speculative read above is a safe, effective hint.
    }
}

