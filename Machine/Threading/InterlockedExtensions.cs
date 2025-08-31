using System.Threading;

namespace Machine.Threading;

public static class InterlockedExtensions
{
    // Atomically sets location to max(location, value) and returns the new value
    public static int Max(ref int location, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
            if (current >= value) return current;
        }
        while (Interlocked.CompareExchange(ref location, value, current) != current);
        return value;
    }
}

