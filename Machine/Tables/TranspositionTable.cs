using System;
using System.Runtime.CompilerServices;
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
    
    private TTEntry[] _entries;
    private int _bucketCount;
    private byte _currentAge;
    
    public TranspositionTable(int sizeMb)
    {
        Resize(sizeMb);
    }
    
    public void Resize(int sizeMb)
    {
        const int entrySize = 16; // Approximate size of TTEntry in bytes
        int totalEntries = (sizeMb * 1024 * 1024) / entrySize;
        _bucketCount = totalEntries / BucketSize;
        
        // Ensure power of 2 for fast modulo
        _bucketCount = 1;
        while (_bucketCount < totalEntries / BucketSize)
            _bucketCount <<= 1;
        // Don't reduce by half - we want the largest power of 2 that fits
        if (_bucketCount > totalEntries / BucketSize)
            _bucketCount >>= 1;
        
        _entries = new TTEntry[_bucketCount * BucketSize];
        _currentAge = 0;
    }
    
    public void Clear()
    {
        Array.Clear(_entries);
        _currentAge = 0;
    }
    
    public void NewSearch()
    {
        _currentAge = (byte)((_currentAge + 1) & MaxAge);
    }
    
    public TTEntry Probe(Position pos)
    {
        ulong key = pos.ZobristKey;
        int bucketIndex = (int)(key & (uint)(_bucketCount - 1)) * BucketSize;
        
        
        // Check all entries in the bucket
        for (int i = 0; i < BucketSize; i++)
        {
            ref var entry = ref _entries[bucketIndex + i];
            if (entry.Key == key && entry.IsValid)
                return entry;
        }
        
        return default; // Not found
    }
    
    public void Store(Position pos, Move bestMove, int score, int depth, TTFlag flag)
    {
        ulong key = pos.ZobristKey;
        int bucketIndex = (int)(key & (uint)(_bucketCount - 1)) * BucketSize;
        
        // Find best slot to replace using depth-preferred scheme
        int replaceIndex = bucketIndex;
        int minScore = int.MaxValue;
        
        for (int i = 0; i < BucketSize; i++)
        {
            int index = bucketIndex + i;
            ref var entry = ref _entries[index];
            
            // If same key, always replace
            if (entry.Key == key)
            {
                replaceIndex = index;
                break;
            }
            
            // If empty slot, use it
            if (!entry.IsValid)
            {
                replaceIndex = index;
                break;
            }
            
            // Calculate replacement score (lower is better for replacement)
            // Prefer replacing entries with: lower depth, older age
            int replaceScore = entry.Depth * 256 + (MaxAge - AgeDifference(entry.Age));
            
            if (replaceScore < minScore)
            {
                minScore = replaceScore;
                replaceIndex = index;
            }
        }
        
        // Store the entry
        ref var targetEntry = ref _entries[replaceIndex];
        targetEntry.Key = key;
        targetEntry.BestMove = bestMove;
        targetEntry.Score = score;
        targetEntry.Depth = (byte)Math.Min(depth, 255);
        targetEntry.Flag = flag;
        targetEntry.Age = _currentAge;
    }
    
    public Move GetBestMove(Position pos)
    {
        var entry = Probe(pos);
        if (!entry.IsValid) return Move.NullMove;
        // Guard against default(struct) moves
        return (entry.BestMove.From >= 0 && entry.BestMove.To >= 0) ? entry.BestMove : Move.NullMove;
    }
    
    public int GetHashFull()
    {
        // Randomized sampling across the table to estimate fullness
        const int sampleSize = 1000;
        int filledCount = 0;
        int totalEntries = _entries.Length;
        if (totalEntries == 0) return 0;
        var random = new Random(unchecked((int)DateTime.UtcNow.Ticks));
        for (int i = 0; i < sampleSize; i++)
        {
            int index = random.Next(totalEntries);
            if (_entries[index].IsValid)
                filledCount++;
        }
        return (filledCount * 1000) / sampleSize; // per-mille
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AgeDifference(byte entryAge)
    {
        return (_currentAge - entryAge) & MaxAge;
    }
}