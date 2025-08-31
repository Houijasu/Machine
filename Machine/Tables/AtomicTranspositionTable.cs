using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Machine.Core;

namespace Machine.Tables;

public sealed class AtomicTranspositionTable : ITranspositionTable
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

    private Bucket[] _buckets = Array.Empty<Bucket>();
    private int _bucketCount;
    private byte _currentAge;

    public AtomicTranspositionTable(int sizeMb = 16)
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
        ulong key = pos.ZobristKey;
        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        // a few attempts to get a stable snapshot
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (TryStableRead(b, out var e0, out var e1, out var e2, out var e3))
            {
                if (e0.IsValid && e0.Key == key) return e0;
                if (e1.IsValid && e1.Key == key) return e1;
                if (e2.IsValid && e2.Key == key) return e2;
                if (e3.IsValid && e3.Key == key) return e3;
                return default;
            }
            Thread.SpinWait(1 << attempt);
        }
        return default;
    }

    public void Store(Position pos, Move bestMove, int score, int depth, TTFlag flag)
    {
        ulong key = pos.ZobristKey;
        int idx = (int)(key & (uint)(_bucketCount - 1));
        var b = _buckets[idx];

        Interlocked.Increment(ref b.Version); // become odd
        try
        {
            // choose replacement index
            int replaceIndex = 0;
            int minScore = int.MaxValue;

            // helper local function to score entries
            int ScoreEntry(in TTEntry e)
            {
                if (!e.IsValid) return int.MinValue + 1; // prefer empty
                if (e.Key == key) return int.MinValue;    // replace same key immediately
                int ageDiff = ((_currentAge - e.Age) & MaxAge);
                return e.Depth * 256 + (MaxAge - ageDiff);
            }

            int s0 = ScoreEntry(in b.Entry0);
            int s1 = ScoreEntry(in b.Entry1);
            int s2 = ScoreEntry(in b.Entry2);
            int s3 = ScoreEntry(in b.Entry3);

            if (s0 <= s1 && s0 <= s2 && s0 <= s3) { replaceIndex = 0; minScore = s0; }
            else if (s1 <= s0 && s1 <= s2 && s1 <= s3) { replaceIndex = 1; minScore = s1; }
            else if (s2 <= s0 && s2 <= s1 && s2 <= s3) { replaceIndex = 2; minScore = s2; }
            else { replaceIndex = 3; minScore = s3; }
            _ = minScore; // silence analyzer if unused

            var newEntry = new TTEntry
            {
                Key = key,
                BestMove = bestMove,
                Score = score,
                Depth = (byte)Math.Clamp(depth, 0, 255),
                Flag = flag,
                Age = _currentAge
            };

            switch (replaceIndex)
            {
                case 0: b.Entry0 = newEntry; break;
                case 1: b.Entry1 = newEntry; break;
                case 2: b.Entry2 = newEntry; break;
                default: b.Entry3 = newEntry; break;
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
        return (entry.BestMove.From >= 0 && entry.BestMove.To >= 0) ? entry.BestMove : Move.NullMove;
    }

    public int GetHashFull()
    {
        // Randomized sampling across buckets for better estimate
        const int sampleSize = 1000;
        int filled = 0;
        int totalEntries = _bucketCount * BucketSize;
        if (totalEntries == 0) return 0;
        var random = new Random(unchecked((int)DateTime.UtcNow.Ticks));
        for (int i = 0; i < sampleSize; i++)
        {
            int flatIndex = random.Next(totalEntries);
            int bIdx = flatIndex / BucketSize;
            int slot = flatIndex % BucketSize;
            var b = _buckets[bIdx];
            if (!TryStableRead(b, out var e0, out var e1, out var e2, out var e3)) continue;
            TTEntry e = slot switch { 0 => e0, 1 => e1, 2 => e2, _ => e3 };
            if (e.IsValid) filled++;
        }
        return (filled * 1000) / sampleSize;
    }
}
