namespace Machine.Threading;

public static class NumaManager
{
    public static int GetNumaNodeCount() => 1; // stub
    public static void SetThreadAffinity(int threadId, int numaNode) { /* no-op */ }
    public static T[] AllocateOnNode<T>(int count, int numaNode) => new T[count];
}

