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
    public ulong Key;           // 8 bytes
    public Move BestMove;       // 4 bytes (assuming Move is 4 bytes)
    public int Score;           // 4 bytes
    public byte Depth;          // 1 byte
    public TTFlag Flag;         // 1 byte
    public byte Age;            // 1 byte
    public byte AbdadaCount;    // 1 byte - Number of threads searching this position
    public byte AbdadaDepth;    // 1 byte - Depth of ongoing searches
    // Total: 21 bytes, add 3 bytes padding to make 24 bytes (multiple of 8)

    public bool IsValid => Flag != TTFlag.None;
    public bool IsBeingSearched => AbdadaCount > 0;
}

public sealed class TranspositionTable : ITranspositionTable
{
    private const int BucketSize = 4;
    private const int MaxAge = 63;
    private int _agingDepthThreshold = 8; // Default threshold for depth-weighted aging

    // Optimized cache-line aligned bucket
    // Each TTEntry is 24 bytes (including padding), 4 entries = 96 bytes
    // Version is 4 bytes, total 100 bytes
    // We'll add padding to align to 128 bytes (2 cache lines) for better performance
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private sealed class Bucket
    {
        public TTEntry Entry0;
        public TTEntry Entry1;
        public TTEntry Entry2;
        public TTEntry Entry3;
        public volatile int Version; // seqlock: even = stable, odd = write in progress
        private int _padding0;       // 4 bytes padding
        private long _padding1;      // 8 bytes padding
        private long _padding2;      // 8 bytes padding
        // Total: 128 bytes (2 cache lines)
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
	    private long _abdadaHits;        // Times we deferred work due to ABDADA
	    private long _collisions;        // Times all slots were full with different keys
	    private long _depthEvictions;    // Times we evicted a deeper entry
	    private long _exactEvictions;    // Times we evicted an EXACT entry
	    private long _skippedWrites;     // Times we skipped identical writes



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
        Interlocked.Exchange(ref _abdadaHits, 0);
        Interlocked.Exchange(ref _collisions, 0);
        Interlocked.Exchange(ref _depthEvictions, 0);
        Interlocked.Exchange(ref _exactEvictions, 0);
        Interlocked.Exchange(ref _skippedWrites, 0);
    }

    public void NewSearch()
    {
        // For depth-weighted aging, we'll handle aging during store operations
        // rather than incrementing globally. This allows us to age entries
        // based on their depth.
        _currentAge = (byte)((_currentAge + 1) & MaxAge);
    }

    public void SetAgingDepthThreshold(int threshold)
    {
        _agingDepthThreshold = Math.Max(1, Math.Min(63, threshold));
    }

    // Cache prefetch hint: touch likely bucket version to bring line into cache
    // Safe no-op on any platform; just a speculative read.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ulong key)
    {
        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];
        // Lightweight read; ignore value. This tends to pull bucket metadata and possibly entries into cache.
        _ = b.Version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BeginWrite(Bucket b)
    {
        while (true)
        {
            int v = b.Version;
            if ((v & 1) != 0) { Thread.SpinWait(1); continue; } // writer active
            if (Interlocked.CompareExchange(ref b.Version, v + 1, v) == v)
            {
                Thread.MemoryBarrier(); // acquire
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EndWrite(Bucket b)
    {
        Thread.MemoryBarrier(); // release
        Interlocked.Increment(ref b.Version); // back to even
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryStableRead(Bucket b, out TTEntry e0, out TTEntry e1, out TTEntry e2, out TTEntry e3)
    {
        e0 = e1 = e2 = e3 = default;
        // Read version without passing volatile field by ref
        int v1 = b.Version;
        if ((v1 & 1) != 0) return false; // writer in progress
        e0 = b.Entry0; e1 = b.Entry1; e2 = b.Entry2; e3 = b.Entry3;
        Thread.MemoryBarrier(); // Ensure reads complete before re-reading version
        int v2 = b.Version;
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

        BeginWrite(b);
        try
        {
            // Ref locals for hot path checks to avoid extra copies
            ref TTEntry e0 = ref b.Entry0;
            ref TTEntry e1 = ref b.Entry1;
            ref TTEntry e2 = ref b.Entry2;
            ref TTEntry e3 = ref b.Entry3;

            // Same-key or empty slot fast paths with rewrite skipping
            if (e0.Key == key) { Interlocked.Increment(ref _sameKeyStores); if (!ShouldSkipRewrite(in e0)) Write(ref e0); return; }
            if (!e0.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e0); return; }
            if (e1.Key == key) { Interlocked.Increment(ref _sameKeyStores); if (!ShouldSkipRewrite(in e1)) Write(ref e1); return; }
            if (!e1.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e1); return; }
            if (e2.Key == key) { Interlocked.Increment(ref _sameKeyStores); if (!ShouldSkipRewrite(in e2)) Write(ref e2); return; }
            if (!e2.IsValid) { Interlocked.Increment(ref _emptySlotStores); Write(ref e2); return; }
            if (e3.Key == key) { Interlocked.Increment(ref _sameKeyStores); if (!ShouldSkipRewrite(in e3)) Write(ref e3); return; }
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
            if (s3 < best) { best = s3; victim = ref e3; }

            // Track eviction metrics before overwriting
            if (victim.IsValid)
            {
                if (victim.Depth > depth) Interlocked.Increment(ref _depthEvictions);
                if (victim.Flag == TTFlag.Exact) Interlocked.Increment(ref _exactEvictions);
            }
            else
            {
                // All slots were full - collision
                Interlocked.Increment(ref _collisions);
            }
            
            Interlocked.Increment(ref _replacementStores);
            Write(ref victim);
            return;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool ShouldSkipRewrite(in TTEntry existing)
            {
                // Avoid identical or worse rewrites to reduce cache traffic
                int newDepth = Math.Clamp(depth, 0, 255);
                bool identical = existing.IsValid && existing.BestMove.Equals(bestMove) && existing.Score == score && existing.Flag == flag && existing.Depth >= newDepth;
                if (identical)
                {
                    Interlocked.Increment(ref _skippedWrites);
                    return true;
                }

                // Protect deeper EXACT from being overwritten by bounds or qsearch
                if (existing.IsValid && existing.Flag == TTFlag.Exact && existing.Depth >= newDepth && flag != TTFlag.Exact)
                {
                    if (existing.Depth > newDepth) Interlocked.Increment(ref _depthEvictions);
                    return true;
                }

                // Avoid letting qsearch (depth==0) overwrite deeper entries
                if (newDepth == 0 && existing.IsValid && existing.Depth > 0)
                    return true;

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Write(ref TTEntry dst)
            {
                // Payload first; seqlock version will close the write
                dst.BestMove = bestMove;
                dst.Score = score;
                dst.Depth = (byte)Math.Clamp(depth, 0, 255);
                dst.Flag = flag;
                dst.Age = _currentAge;
                dst.AbdadaCount = 0; // Clear ABDADA reservation on normal store
                dst.AbdadaDepth = 0;
                dst.Key = key; // publish key last within the seqlock window
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int ScoreForReplace(in TTEntry e)
            {
                if (!e.IsValid) return int.MinValue + 1;
                // Prefer replacing older and shallower entries; protect EXACT and deep entries; de-prioritize qsearch (depth==0)
                int ageDiff = ((_currentAge - e.Age) & MaxAge);
                
                // Depth-weighted aging: deeper entries age slower
                // Entries deeper than threshold get reduced aging effect
                int effectiveAgeDiff = e.Depth >= _agingDepthThreshold ?
                    ageDiff / 2 : // Half aging effect for deep entries
                    ageDiff;      // Full aging effect for shallow entries
                
                int scoreBase = (e.Depth << 8) + (MaxAge - effectiveAgeDiff);
                if (e.Flag == TTFlag.Exact) scoreBase += 1 << 20; // make much less likely to be evicted
                if (e.Depth == 0) scoreBase -= 1 << 18;            // prefer evicting qsearch entries
                return scoreBase;
            }
        }
        finally
        {
            EndWrite(b);
        }
    }

    // ABDADA: Try to reserve a position for searching
    public bool TryStartSearch(Position pos, int depth)
    {
        ulong key = pos.ZobristKey;
        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        BeginWrite(b);
        try
        {
            ref TTEntry e0 = ref b.Entry0;
            ref TTEntry e1 = ref b.Entry1;
            ref TTEntry e2 = ref b.Entry2;
            ref TTEntry e3 = ref b.Entry3;

            // Find matching entry and check/update ABDADA status
            if (e0.Key == key)
            {
                if (e0.AbdadaCount > 0 && e0.AbdadaDepth >= depth)
                {
                    Interlocked.Increment(ref _abdadaHits);
                    return false; // Someone else is searching at sufficient depth
                }
                e0.AbdadaCount++;
                e0.AbdadaDepth = (byte)Math.Max(e0.AbdadaDepth, Math.Min(depth, 255));
                return true;
            }
            if (e1.Key == key)
            {
                if (e1.AbdadaCount > 0 && e1.AbdadaDepth >= depth)
                {
                    Interlocked.Increment(ref _abdadaHits);
                    return false;
                }
                e1.AbdadaCount++;
                e1.AbdadaDepth = (byte)Math.Max(e1.AbdadaDepth, Math.Min(depth, 255));
                return true;
            }
            if (e2.Key == key)
            {
                if (e2.AbdadaCount > 0 && e2.AbdadaDepth >= depth)
                {
                    Interlocked.Increment(ref _abdadaHits);
                    return false;
                }
                e2.AbdadaCount++;
                e2.AbdadaDepth = (byte)Math.Max(e2.AbdadaDepth, Math.Min(depth, 255));
                return true;
            }
            if (e3.Key == key)
            {
                if (e3.AbdadaCount > 0 && e3.AbdadaDepth >= depth)
                {
                    Interlocked.Increment(ref _abdadaHits);
                    return false;
                }
                e3.AbdadaCount++;
                e3.AbdadaDepth = (byte)Math.Max(e3.AbdadaDepth, Math.Min(depth, 255));
                return true;
            }

            // Not found - we can search it (will be stored later)
            return true;
        }
        finally
        {
            EndWrite(b);
        }
    }

    // ABDADA: Mark search as complete
    public void EndSearch(Position pos)
    {
        ulong key = pos.ZobristKey;
        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        BeginWrite(b);
        try
        {
            ref TTEntry e0 = ref b.Entry0;
            ref TTEntry e1 = ref b.Entry1;
            ref TTEntry e2 = ref b.Entry2;
            ref TTEntry e3 = ref b.Entry3;

            if (e0.Key == key && e0.AbdadaCount > 0) { e0.AbdadaCount--; return; }
            if (e1.Key == key && e1.AbdadaCount > 0) { e1.AbdadaCount--; return; }
            if (e2.Key == key && e2.AbdadaCount > 0) { e2.AbdadaCount--; return; }
            if (e3.Key == key && e3.AbdadaCount > 0) { e3.AbdadaCount--; return; }
        }
        finally
        {
            EndWrite(b);
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
        long abdada = Volatile.Read(ref _abdadaHits);
        long collisions = Volatile.Read(ref _collisions);
        long depthEvictions = Volatile.Read(ref _depthEvictions);
        long exactEvictions = Volatile.Read(ref _exactEvictions);
        long skippedWrites = Volatile.Read(ref _skippedWrites);
        return new TTStats(probes, hits, stores, sameKey, repl, empty, abdada, collisions, depthEvictions, exactEvictions, skippedWrites);
    }

    // Extended statistics for performance analysis
    public (long abdada, long collisions, long depthEvict, long exactEvict, long skipped) GetExtendedStats()
    {
        return (
            Volatile.Read(ref _abdadaHits),
            Volatile.Read(ref _collisions),
            Volatile.Read(ref _depthEvictions),
            Volatile.Read(ref _exactEvictions),
            Volatile.Read(ref _skippedWrites)
        );
    }
}
