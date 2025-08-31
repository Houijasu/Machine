using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Machine.Core;

namespace Machine.Tables;

public enum TTFlag : byte
{
    None = 0,
    Exact = 1,
    Alpha = 2,  // Upper bound
    Beta = 3    // Lower bound
}

public struct TTEntry
{
    public ulong Key;
    public Move BestMove;
    public int Score;
    public byte Depth;
    public TTFlag Flag;
    public byte Age;

    public bool IsValid => Flag != TTFlag.None;
}

public sealed class TranspositionTable : ITranspositionTable
{
    private const int BucketSize = 4;
    private const int MaxAge = 63;

    private sealed class Bucket
    {
        public TTEntry Entry0;
        public TTEntry Entry1;
        public TTEntry Entry2;
        public TTEntry Entry3;
        public volatile int Version; // seqlock: even = stable, odd = write in progress
    }

    private Bucket[] _buckets = [];
    private int _bucketCount;
    private byte _currentAge;

	    // Statistics (lightweight; updated with Interlocked to avoid races)
	    private long _probeCount;
	    private long _hitCount;
	    private long _storeCount;
	    private long _sameKeyStores;
	    private long _replacementStores;
	    private long _emptySlotStores;



    public TranspositionTable(int sizeMb = 16)
    {
        Resize(sizeMb);
    }

    public void Resize(int sizeMb)
    {
        const int entrySize = 16; // approximate size
        int totalEntries = Math.Max(1, (sizeMb * 1024 * 1024) / entrySize);
        int bucketsNeeded = Math.Max(1, totalEntries / BucketSize);

        // power-of-two bucket count, choose the largest power-of-two <= bucketsNeeded
        int bc = 1;
        while (bc <= bucketsNeeded) bc <<= 1;
        if (bc > bucketsNeeded) bc >>= 1;

        _bucketCount = Math.Max(1, bc);
        _buckets = new Bucket[_bucketCount];
        for (int i = 0; i < _bucketCount; i++) _buckets[i] = new Bucket();
        _currentAge = 0;
    }

    public void Clear()
    {
        for (int i = 0; i < _bucketCount; i++)
        {
            var b = _buckets[i];
            b.Entry0 = default;
            b.Entry1 = default;
            b.Entry2 = default;
            b.Entry3 = default;
            b.Version = 0;
        }
        _currentAge = 0;
        // Reset stats
        Interlocked.Exchange(ref _probeCount, 0);
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _storeCount, 0);
        Interlocked.Exchange(ref _sameKeyStores, 0);
        Interlocked.Exchange(ref _replacementStores, 0);
        Interlocked.Exchange(ref _emptySlotStores, 0);
    }

    public void NewSearch()
    {
        _currentAge = (byte)((_currentAge + 1) & MaxAge);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryStableRead(Bucket b, out TTEntry e0, out TTEntry e1, out TTEntry e2, out TTEntry e3)
    {
        e0 = e1 = e2 = e3 = default;
        int v1 = Volatile.Read(ref b.Version);
        if ((v1 & 1) != 0) return false; // writer in progress
        e0 = b.Entry0; e1 = b.Entry1; e2 = b.Entry2; e3 = b.Entry3;
        int v2 = Volatile.Read(ref b.Version);
        return v1 == v2 && (v2 & 1) == 0;
    }

    public TTEntry Probe(Position pos)
    {
        Interlocked.Increment(ref _probeCount);

        ulong key = pos.ZobristKey;
        // Optional prefetch hint (no-op stub currently)
        Machine.Optimization.Prefetch.TTEntry(this, key);

        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        // Fast path: single snapshot attempt
        TTEntry e0, e1, e2, e3;
        if (TryStableRead(b, out e0, out e1, out e2, out e3))
        {
            if (e0.IsValid && e0.Key == key) { Interlocked.Increment(ref _hitCount); return e0; }
            if (e1.IsValid && e1.Key == key) { Interlocked.Increment(ref _hitCount); return e1; }
            if (e2.IsValid && e2.Key == key) { Interlocked.Increment(ref _hitCount); return e2; }
            if (e3.IsValid && e3.Key == key) { Interlocked.Increment(ref _hitCount); return e3; }
            return default;
        }

        // One lightweight retry if writer in progress
        Thread.SpinWait(1);
        if (TryStableRead(b, out e0, out e1, out e2, out e3))
        {
            if (e0.IsValid && e0.Key == key) { Interlocked.Increment(ref _hitCount); return e0; }
            if (e1.IsValid && e1.Key == key) { Interlocked.Increment(ref _hitCount); return e1; }
            if (e2.IsValid && e2.Key == key) { Interlocked.Increment(ref _hitCount); return e2; }
            if (e3.IsValid && e3.Key == key) { Interlocked.Increment(ref _hitCount); return e3; }
        }
        return default;
    }

    public void Store(Position pos, Move bestMove, int score, int depth, TTFlag flag)
    {
        Interlocked.Increment(ref _storeCount);

        ulong key = pos.ZobristKey;
        // Optional prefetch hint (no-op stub currently)
        Machine.Optimization.Prefetch.TTEntry(this, key);

        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        Interlocked.Increment(ref b.Version); // become odd
        try
        {
            // Ref locals for hot path checks to avoid extra copies
            ref TTEntry e0 = ref b.Entry0;
            ref TTEntry e1 = ref b.Entry1;
            ref TTEntry e2 = ref b.Entry2;
            ref TTEntry e3 = ref b.Entry3;

            // Same-key or empty slot fast paths (also increment counters)
            if (e0.Key == key) { Interlocked.Increment(ref _sameKeyStores); Write(ref e0); return; }
            if (!e0.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e0); return; }
            if (e1.Key == key) { Interlocked.Increment(ref _sameKeyStores); Write(ref e1); return; }
            if (!e1.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e1); return; }
            if (e2.Key == key) { Interlocked.Increment(ref _sameKeyStores); Write(ref e2); return; }
            if (!e2.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e2); return; }
            if (e3.Key == key) { Interlocked.Increment(ref _sameKeyStores); Write(ref e3); return; }
            if (!e3.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e3); return; }

            // Score existing entries: lower => better victim
            int s0 = ScoreForReplace(in e0);
            int s1 = ScoreForReplace(in e1);
            int s2 = ScoreForReplace(in e2);
            int s3 = ScoreForReplace(in e3);

            // Choose victim
            ref TTEntry victim = ref e0;
            int best = s0;
            if (s1 < best) { best = s1; victim = ref e1; }
            if (s2 < best) { best = s2; victim = ref e2; }
            if (s3 < best) { /*best = s3;*/ victim = ref e3; }

            Interlocked.Increment(ref _replacementStores);
            Write(ref victim);
            return;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Write(ref TTEntry dst)
            {
                dst.Key = key;
                dst.BestMove = bestMove;
                dst.Score = score;
                dst.Depth = (byte)Math.Clamp(depth, 0, 255);
                dst.Flag = flag;
                dst.Age = _currentAge;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int ScoreForReplace(in TTEntry e)
            {
                // Prefer replacing shallower and older entries
                int ageDiff = ((_currentAge - e.Age) & MaxAge);
                return e.IsValid ? (e.Depth * 256 + (MaxAge - ageDiff)) : int.MinValue + 1;
            }
        }
        finally
        {
            Interlocked.Increment(ref b.Version); // back to even
        }
    }

    public Move GetBestMove(Position pos)
    {
        var entry = Probe(pos);
        if (!entry.IsValid) return Move.NullMove;
        return entry.BestMove is { From: >= 0, To: >= 0 } ? entry.BestMove : Move.NullMove;
    }

    public int GetHashFull()
    {
        // Deterministic fixed-stride sampling across slots for stable estimates
        const int targetSamples = 1000;
        int totalSlots = _bucketCount * BucketSize;
        if (totalSlots == 0) return 0;

        int stride = Math.Max(1, totalSlots / targetSamples);
        if ((stride & 1) == 0) stride++; // prefer odd stride

        int samples = Math.Min(targetSamples, totalSlots);
        int filled = 0;
        int idx = 0;
        for (int i = 0; i < samples; i++)
        {
            int bIdx = idx / BucketSize;
            int slot = idx % BucketSize;
            var b = _buckets[bIdx];
            if (TryStableRead(b, out var e0, out var e1, out var e2, out var e3))
            {
                TTEntry e = slot switch { 0 => e0, 1 => e1, 2 => e2, _ => e3 };
                if (e.IsValid) filled++;
            }
            idx += stride;
            if (idx >= totalSlots) idx -= totalSlots;
        }

        return (filled * 1000) / samples;
    }

    // Public statistics snapshot
    public TTStats GetStats()
    {
        long probes = Volatile.Read(ref _probeCount);
        long hits = Volatile.Read(ref _hitCount);
        long stores = Volatile.Read(ref _storeCount);
        long sameKey = Volatile.Read(ref _sameKeyStores);
        long repl = Volatile.Read(ref _replacementStores);
        long empty = Volatile.Read(ref _emptySlotStores);
        return new TTStats(probes, hits, stores, sameKey, repl, empty);
    }
}
