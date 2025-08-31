using System.Runtime.InteropServices;

namespace Machine.Search;

[StructLayout(LayoutKind.Sequential, Pack = 64)]
public struct ThreadStatistics
{
    public long NodesSearched;
    public long QNodesSearched;
    public int SelectiveDepth;
    private long _pad1, _pad2, _pad3, _pad4, _pad5; // padding to a cache line
}

public sealed class GlobalStatistics
{
    private readonly ThreadStatistics[] _threads;
    public GlobalStatistics(int threadCount)
    {
        _threads = new ThreadStatistics[threadCount];
    }
    public ulong TotalNodes
    {
        get
        {
            ulong n = 0;
            for (int i = 0; i < _threads.Length; i++) n += (ulong)_threads[i].NodesSearched;
            return n;
        }
    }
    public int MaxSelectiveDepth
    {
        get
        {
            int d = 0;
            for (int i = 0; i < _threads.Length; i++) if (_threads[i].SelectiveDepth > d) d = _threads[i].SelectiveDepth;
            return d;
        }
    }
}

