using System;
using System.Threading;
using Machine.Core;
using Machine.Tables;

namespace Machine.Search;

// Minimal skeleton that delegates to single-threaded SearchEngine for now
public sealed class LazySMPEngine
{
    private readonly int _threads;
    private readonly SearchEngine _single;

    public LazySMPEngine(int threads = 1)
    {
        _threads = Math.Max(1, threads);
        _single = new SearchEngine();
    }

    public SearchResult Search(Position pos, SearchLimits limits)
    {
        // TODO: parallelize; for now, just run single-threaded to preserve behavior
        _single.SetPosition(pos);
        return _single.Search(limits);
    }
}

