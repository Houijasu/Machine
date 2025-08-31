using System;
using Machine.Core;

namespace Machine.Tables;

// Placeholder, non-lock-free scaffolding for future atomic TT
public readonly struct AtomicTTEntry
{
    public readonly ulong Key;
    public readonly Move BestMove;
    public readonly int Score;
    public readonly byte Depth;
    public readonly TTFlag Flag;

    public AtomicTTEntry(ulong key, Move move, int score, byte depth, TTFlag flag)
    {
        Key = key; BestMove = move; Score = score; Depth = depth; Flag = flag;
    }
}

public sealed class AtomicTranspositionTable
{
    private TTEntry[] _entries = Array.Empty<TTEntry>();
    private int _mask;

    public AtomicTranspositionTable(int sizeMb = 16)
    {
        Resize(sizeMb);
    }

    public void Resize(int sizeMb)
    {
        // Simple power-of-two bucket sizing
        int approxEntries = Math.Max(1, (sizeMb * 1024 * 1024) / 16);
        int buckets = 1;
        while (buckets < approxEntries) buckets <<= 1;
        _entries = new TTEntry[buckets];
        _mask = buckets - 1;
    }

    public void Clear() => Array.Clear(_entries);

    public void Store(ulong key, Move move, int score, int depth, TTFlag flag)
    {
        int idx = (int)(key & (uint)_mask);
        _entries[idx] = new TTEntry
        {
            Key = key,
            BestMove = move,
            Score = score,
            Depth = (byte)Math.Clamp(depth, 0, 255),
            Flag = flag,
            Age = 0
        };
    }

    public bool Probe(ulong key, out TTEntry entry)
    {
        int idx = (int)(key & (uint)_mask);
        entry = _entries[idx];
        return entry.IsValid && entry.Key == key;
    }

    // For potential prefetch stubs
    public IntPtr GetEntryAddress(ulong key) => IntPtr.Zero;
}

