using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Machine.Tables;

namespace Machine.Optimization;

public static class Prefetch
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TTEntry(AtomicTranspositionTable tt, ulong key)
    {
        if (!Sse.IsSupported) return;
        // Placeholder: no address computation in stub
    }
}

