using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Machine.Tables;

// Evaluation cache entry
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EvalCacheEntry
{
    public ulong Key;     // 8 bytes: Position hash
    public short Score;   // 2 bytes: Evaluation score
    public byte Age;      // 1 byte: Search generation
    public byte Flags;    // 1 byte: Reserved for future use
    // Total: 12 bytes per entry (will be padded to 16 in array)
}

public class EvalCache
{
    private EvalCacheEntry[] _table;
    private int _mask;
    private ulong _hits;
    private ulong _probes;
    private byte _currentAge;
    
    public EvalCache(int sizeMB = 8)
    {
        // Calculate number of entries (16 bytes per entry with padding)
        int bytesPerEntry = 16; // Padded size for alignment
        int totalBytes = sizeMB * 1024 * 1024;
        int entryCount = totalBytes / bytesPerEntry;
        
        // Round down to power of 2 for fast masking
        entryCount = 1 << (31 - BitOperations.LeadingZeroCount((uint)entryCount));
        _mask = entryCount - 1;
        _table = new EvalCacheEntry[entryCount];
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(ulong key, out short score)
    {
        _probes++;
        int index = (int)(key & (uint)_mask);
        ref var entry = ref _table[index];
        
        if (entry.Key == key)
        {
            _hits++;
            score = entry.Score;
            entry.Age = _currentAge; // Update age on hit
            return true;
        }
        
        score = 0;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, short score)
    {
        int index = (int)(key & (uint)_mask);
        ref var entry = ref _table[index];
        
        // Always replace if empty or older
        if (entry.Key == 0 || entry.Age != _currentAge)
        {
            entry.Key = key;
            entry.Score = score;
            entry.Age = _currentAge;
        }
        // Replace if same position (update)
        else if (entry.Key == key)
        {
            entry.Score = score;
            entry.Age = _currentAge;
        }
    }
    
    public void Clear()
    {
        Array.Clear(_table);
        _hits = 0;
        _probes = 0;
    }
    
    public void NewSearch()
    {
        _currentAge++;
        if (_currentAge == 0) // Wrapped around
        {
            // Clear old entries
            for (int i = 0; i < _table.Length; i++)
            {
                if (_table[i].Age > 128)
                    _table[i] = default;
            }
        }
    }
    
    public void Resize(int sizeMB)
    {
        // Save hit rate before resizing
        double hitRate = _probes > 0 ? (double)_hits / _probes : 0;
        
        // Create new table
        int bytesPerEntry = 16;
        int totalBytes = sizeMB * 1024 * 1024;
        int entryCount = totalBytes / bytesPerEntry;
        entryCount = 1 << (31 - BitOperations.LeadingZeroCount((uint)entryCount));
        
        _table = new EvalCacheEntry[entryCount];
        _mask = entryCount - 1;
        _hits = 0;
        _probes = 0;
    }
    
    public double HitRate => _probes > 0 ? (double)_hits / _probes : 0;
    public ulong Hits => _hits;
    public ulong Probes => _probes;
    public int SizeMB => (_table.Length * 16) / (1024 * 1024);
}