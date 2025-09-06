using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Machine.Core;

namespace Machine.Tables;

// Pawn hash entry: stores pawn structure evaluation results
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PawnHashEntry
{
    public ulong Key;           // 8 bytes: Zobrist key of pawn structure
    public short WhiteScore;    // 2 bytes: Score from white's perspective
    public short BlackScore;    // 2 bytes: Score from black's perspective
    public byte OpenFiles;      // 1 byte: Bitmask of open files
    public byte HalfOpenFiles;  // 1 byte: Bitmask of half-open files
    public ushort Flags;        // 2 bytes: Various pawn structure flags (isolated, doubled, passed, etc.)
    public byte Age;            // 1 byte: Age of the entry (for depth-weighted aging)
    // Total: 17 bytes per entry
}

public class PawnHashTable
{
    private PawnHashEntry[] _table;
    private int _mask;
    private ulong _hits;
    private ulong _probes;
    private ulong _stores;
    private ulong _isolatedPawns;
    private ulong _doubledPawns;
    private ulong _passedPawns;
    private ulong _openFiles;
    private ulong _halfOpenFiles;
    private byte _currentAge;
    private const int MaxAge = 63;
    private int _agingDepthThreshold = 8; // Default threshold for depth-weighted aging
    
    // Pawn structure evaluation flags
    [Flags]
    public enum PawnFlags : ushort
    {
        None = 0,
        WhiteIsolated = 1 << 0,
        BlackIsolated = 1 << 1,
        WhiteDoubled = 1 << 2,
        BlackDoubled = 1 << 3,
        WhitePassed = 1 << 4,
        BlackPassed = 1 << 5,
        WhiteBackward = 1 << 6,
        BlackBackward = 1 << 7,
        WhiteChain = 1 << 8,
        BlackChain = 1 << 9,
    }
    
    public PawnHashTable(int sizeMB = 4)
    {
        // Calculate number of entries (16 bytes per entry)
        int bytesPerEntry = Marshal.SizeOf<PawnHashEntry>();
        int totalBytes = sizeMB * 1024 * 1024;
        int entryCount = totalBytes / bytesPerEntry;
        
        // Round down to power of 2 for fast masking
        entryCount = 1 << (31 - BitOperations.LeadingZeroCount((uint)entryCount));
        _mask = entryCount - 1;
        _table = new PawnHashEntry[entryCount];
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Probe(ulong pawnKey, out PawnHashEntry entry)
    {
        _probes++;
        int index = (int)(pawnKey & (uint)_mask);
        entry = _table[index];
        
        if (entry.Key == pawnKey)
        {
            // Consider entry valid if not too old (within 32 age units)
            int ageDiff = ((_currentAge - entry.Age) & MaxAge);
            if (ageDiff < 32) // Entry is not too old
            {
                _hits++;
                return true;
            }
        }
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong pawnKey, short whiteScore, short blackScore, byte openFiles, byte halfOpenFiles, ushort flags, int depth = 0)
    {
        int index = (int)(pawnKey & (uint)_mask);
        ref var entry = ref _table[index];
        
        // Depth-weighted aging: deeper entries age slower
        byte effectiveAge = (byte)((depth >= _agingDepthThreshold) ?
            (_currentAge & MaxAge) >> 1 : // Half aging effect for deep entries
            (_currentAge & MaxAge));     // Full aging effect for shallow entries
        
        entry.Key = pawnKey;
        entry.WhiteScore = whiteScore;
        entry.BlackScore = blackScore;
        entry.OpenFiles = openFiles;
        entry.HalfOpenFiles = halfOpenFiles;
        entry.Flags = flags;
        entry.Age = effectiveAge;
        // Note: PawnHashEntry doesn't have an Age field yet, so we'll need to add it
    }
    
    public void NewSearch()
    {
        _currentAge = (byte)((_currentAge + 1) & MaxAge);
    }
    
    public void SetAgingDepthThreshold(int threshold)
    {
        _agingDepthThreshold = Math.Max(1, Math.Min(63, threshold));
    }
    
    public void Clear()
    {
        Array.Clear(_table);
        _hits = 0;
        _probes = 0;
    }
    
    public void Resize(int sizeMB)
    {
        // Save hit rate before resizing
        double hitRate = _probes > 0 ? (double)_hits / _probes : 0;
        
        // Create new table
        var oldTable = this;
        int bytesPerEntry = Marshal.SizeOf<PawnHashEntry>();
        int totalBytes = sizeMB * 1024 * 1024;
        int entryCount = totalBytes / bytesPerEntry;
        entryCount = 1 << (31 - BitOperations.LeadingZeroCount((uint)entryCount));
        
        _table = new PawnHashEntry[entryCount];
        _mask = entryCount - 1;
        _hits = 0;
        _probes = 0;
    }
    
    public double HitRate => _probes > 0 ? (double)_hits / _probes : 0;
    public ulong Hits => _hits;
    public ulong Probes => _probes;
    public ulong Stores => _stores;
    public ulong IsolatedPawns => _isolatedPawns;
    public ulong DoubledPawns => _doubledPawns;
    public ulong PassedPawns => _passedPawns;
    public ulong OpenFiles => _openFiles;
    public ulong HalfOpenFiles => _halfOpenFiles;
    
    public PawnHashStats GetStats()
    {
        return new PawnHashStats(_probes, _hits, _stores, _isolatedPawns, _doubledPawns, _passedPawns, _openFiles, _halfOpenFiles);
    }
    
    // Helper to compute pawn structure key (only pawn positions)
    public static ulong ComputePawnKey(Position pos)
    {
        ulong key = 0;
        
        // White pawns
        ulong whitePawns = pos.PieceBB[0]; // White pawn index
        while (whitePawns != 0)
        {
            int sq = BitOperations.TrailingZeroCount(whitePawns);
            key ^= Zobrist.PieceSquare[0, sq];
            whitePawns &= whitePawns - 1;
        }
        
        // Black pawns
        ulong blackPawns = pos.PieceBB[6]; // Black pawn index
        while (blackPawns != 0)
        {
            int sq = BitOperations.TrailingZeroCount(blackPawns);
            key ^= Zobrist.PieceSquare[6, sq];
            blackPawns &= blackPawns - 1;
        }
        
        return key;
    }
}