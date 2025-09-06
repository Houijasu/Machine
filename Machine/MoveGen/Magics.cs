using Machine.Core;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Diagnostics;

namespace Machine.MoveGen;

public static class Magics
{
    // PEXT control modes
    public enum PextMode
    {
        Auto,    // Benchmark at startup and pick fastest
        Force,   // Force PEXT if supported
        Disable  // Force multiply/shift
    }

    private static PextMode _pextMode = PextMode.Disable; // Default to multiply/shift for best performance
    private static bool _usePEXT = false; // Will be set by auto-detection or UCI
    private static bool _autoDetectionComplete = true; // Start with multiply/shift, no auto-detection needed
    private static readonly object _autoDetectionLock = new object(); // Thread-safety for auto-detection

    public static bool UsePEXT
    {
        get => _usePEXT;
        set
        {
            if (value && !Bmi2.X64.IsSupported) return; // Can't enable if not supported
            _usePEXT = value;
            _pextMode = value ? PextMode.Force : PextMode.Disable;
            _autoDetectionComplete = true;
        }
    }

    public static PextMode Mode
    {
        get => _pextMode;
        set
        {
            lock (_autoDetectionLock)
            {
                _pextMode = value;
                _autoDetectionComplete = false;

                if (value == PextMode.Disable)
                {
                    _usePEXT = false;
                    _autoDetectionComplete = true;
                }
                else if (value == PextMode.Force)
                {
                    _usePEXT = Bmi2.X64.IsSupported;
                    _autoDetectionComplete = true;
                }
                else if (value == PextMode.Auto && Bmi2.X64.IsSupported)
                {
                    // Trigger immediate auto-detection when explicitly set to auto
                    PerformAutoDetection();
                    _autoDetectionComplete = true;
                }
                else
                {
                    // Auto mode but PEXT not supported - fall back to multiply
                    _usePEXT = false;
                    _autoDetectionComplete = true;
                }
            }
        }
    }
    
    // Per-square masks and tables built at init - marked readonly for JIT optimizations
    private static readonly ulong[] BishopMasks = new ulong[64];
    private static readonly ulong[] RookMasks = new ulong[64];
    private static readonly int[] BishopBits = new int[64];
    private static readonly int[] RookBits = new int[64];

    private static readonly ulong[] BishopMagics = new ulong[64];
    private static readonly ulong[] RookMagics = new ulong[64];
    private static readonly int[] BishopShifts = new int[64];
    private static readonly int[] RookShifts = new int[64];

    // Attack tables - using jagged arrays for better cache locality per square
    public static readonly ulong[][] BishopAttacks = new ulong[64][];
    public static readonly ulong[][] RookAttacks = new ulong[64][];

    // PEXT-specific attack tables (indexed by PEXT values)
    public static readonly ulong[][] BishopAttacksPEXT = new ulong[64][];
    public static readonly ulong[][] RookAttacksPEXT = new ulong[64][];

    static Magics()
    {
        InitMasks();
        // Try to initialize from precomputed constants; fall back to runtime search
        if (!InitTablesFromConstantsIfAvailable())
            InitTablesWithRuntimeMagics();

        // Perform auto-detection if in Auto mode
        if (_pextMode == PextMode.Auto && Bmi2.X64.IsSupported)
        {
            PerformAutoDetection();
        }
        else if (_pextMode == PextMode.Force)
        {
            _usePEXT = Bmi2.X64.IsSupported;
        }
        // Disable mode already sets _usePEXT = false

        _autoDetectionComplete = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetBishopAttacks(int sq, ulong occ)
    {
        // Thread-safe auto-detection check
        if (!_autoDetectionComplete && _pextMode == PextMode.Auto && Bmi2.X64.IsSupported)
        {
            lock (_autoDetectionLock)
            {
                if (!_autoDetectionComplete && _pextMode == PextMode.Auto)
                {
                    PerformAutoDetection();
                    _autoDetectionComplete = true;
                }
            }
        }

        var mask = BishopMasks[sq];

        // Use hardware PEXT if enabled and available
        if (_usePEXT)
        {
            int idx = (int)Bmi2.X64.ParallelBitExtract(occ, mask);
            return BishopAttacksPEXT[sq][idx];
        }
        else
        {
            // Optimized multiply/shift path with hoisted constants
            int idx = (int)(((occ & mask) * BishopMagics[sq]) >> BishopShifts[sq]);
            return BishopAttacks[sq][idx];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRookAttacks(int sq, ulong occ)
    {
        // Thread-safe auto-detection check
        if (!_autoDetectionComplete && _pextMode == PextMode.Auto && Bmi2.X64.IsSupported)
        {
            lock (_autoDetectionLock)
            {
                if (!_autoDetectionComplete && _pextMode == PextMode.Auto)
                {
                    PerformAutoDetection();
                    _autoDetectionComplete = true;
                }
            }
        }

        var mask = RookMasks[sq];

        // Use hardware PEXT if enabled and available
        if (_usePEXT)
        {
            int idx = (int)Bmi2.X64.ParallelBitExtract(occ, mask);
            return RookAttacksPEXT[sq][idx];
        }
        else
        {
            // Optimized multiply/shift path with hoisted constants
            int idx = (int)(((occ & mask) * RookMagics[sq]) >> RookShifts[sq]);
            return RookAttacks[sq][idx];
        }
    }

    private static void InitMasks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            BishopMasks[sq] = ComputeBishopMask(sq);
            RookMasks[sq] = ComputeRookMask(sq);
            BishopBits[sq] = Bitboards.PopCount(BishopMasks[sq]);
            RookBits[sq] = Bitboards.PopCount(RookMasks[sq]);
            BishopShifts[sq] = 64 - BishopBits[sq];
            RookShifts[sq] = 64 - RookBits[sq];
        }
    }

    private static ulong ComputeBishopMask(int sq)
    {
        ulong mask = 0;
        int r = sq / 8, f = sq % 8;

        // NE diagonal
        for (int rr = r + 1, ff = f + 1; rr < 7 && ff < 7; rr++, ff++)
            mask |= 1UL << (rr * 8 + ff);
        // NW diagonal
        for (int rr = r + 1, ff = f - 1; rr < 7 && ff > 0; rr++, ff--)
            mask |= 1UL << (rr * 8 + ff);
        // SE diagonal
        for (int rr = r - 1, ff = f + 1; rr > 0 && ff < 7; rr--, ff++)
            mask |= 1UL << (rr * 8 + ff);
        // SW diagonal
        for (int rr = r - 1, ff = f - 1; rr > 0 && ff > 0; rr--, ff--)
            mask |= 1UL << (rr * 8 + ff);

        return mask;
    }

    private static ulong ComputeRookMask(int sq)
    {
        ulong mask = 0;
        int r = sq / 8, f = sq % 8;

        // North
        for (int rr = r + 1; rr < 7; rr++)
            mask |= 1UL << (rr * 8 + f);
        // South
        for (int rr = r - 1; rr > 0; rr--)
            mask |= 1UL << (rr * 8 + f);
        // East
        for (int ff = f + 1; ff < 7; ff++)
            mask |= 1UL << (r * 8 + ff);
        // West
        for (int ff = f - 1; ff > 0; ff--)
            mask |= 1UL << (r * 8 + ff);

        return mask;
    }

    private static bool InitTablesFromConstantsIfAvailable()
    {
        if (MagicConstants.BishopMagics is null || MagicConstants.RookMagics is null)
            return false;

        for (int sq = 0; sq < 64; sq++)
        {
            BishopMagics[sq] = MagicConstants.BishopMagics[sq].Magic;
            RookMagics[sq] = MagicConstants.RookMagics[sq].Magic;

            int bSize = 1 << BishopBits[sq];
            int rSize = 1 << RookBits[sq];

            // Initialize magic multiplication tables
            BishopAttacks[sq] = new ulong[bSize];
            RookAttacks[sq] = new ulong[rSize];

            // Initialize PEXT tables (same size as they use the same masks)
            BishopAttacksPEXT[sq] = new ulong[bSize];
            RookAttacksPEXT[sq] = new ulong[rSize];

            // Fill magic multiplication tables
            for (int i = 0; i < bSize; i++)
            {
                ulong occ = ExpandFromIndex((uint)i, BishopMasks[sq]);
                int magicIdx = (int)(((occ & BishopMasks[sq]) * BishopMagics[sq]) >> BishopShifts[sq]);
                BishopAttacks[sq][magicIdx] = AttackTablesExt.BishopRaysFrom(sq, occ);

                // For PEXT, the index is simply the PEXT of the occupancy
                if (Bmi2.X64.IsSupported)
                {
                    int pextIdx = (int)Bmi2.X64.ParallelBitExtract(occ, BishopMasks[sq]);
                    BishopAttacksPEXT[sq][pextIdx] = AttackTablesExt.BishopRaysFrom(sq, occ);
                }
            }

            for (int i = 0; i < rSize; i++)
            {
                ulong occ = ExpandFromIndex((uint)i, RookMasks[sq]);
                int magicIdx = (int)(((occ & RookMasks[sq]) * RookMagics[sq]) >> RookShifts[sq]);
                RookAttacks[sq][magicIdx] = AttackTablesExt.RookRaysFrom(sq, occ);

                // For PEXT, the index is simply the PEXT of the occupancy
                if (Bmi2.X64.IsSupported)
                {
                    int pextIdx = (int)Bmi2.X64.ParallelBitExtract(occ, RookMasks[sq]);
                    RookAttacksPEXT[sq][pextIdx] = AttackTablesExt.RookRaysFrom(sq, occ);
                }
            }
        }
        return true;
    }

    private static void InitTablesWithRuntimeMagics()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            // Find bishop magic and table
            BishopMagics[sq] = StockfishMagicInit.FindMagicForSquare(sq, BishopMasks[sq], AttackTablesExt.BishopRaysFrom, BishopShifts[sq], out var bTable);
            BishopAttacks[sq] = bTable;

            // Find rook magic and table
            RookMagics[sq] = StockfishMagicInit.FindMagicForSquare(sq, RookMasks[sq], AttackTablesExt.RookRaysFrom, RookShifts[sq], out var rTable);
            RookAttacks[sq] = rTable;

            // Build PEXT tables if PEXT is supported
            if (Bmi2.X64.IsSupported)
            {
                int bSize = 1 << BishopBits[sq];
                int rSize = 1 << RookBits[sq];
                BishopAttacksPEXT[sq] = new ulong[bSize];
                RookAttacksPEXT[sq] = new ulong[rSize];

                // Fill PEXT tables
                for (int i = 0; i < bSize; i++)
                {
                    ulong occ = ExpandFromIndex((uint)i, BishopMasks[sq]);
                    int pextIdx = (int)Bmi2.X64.ParallelBitExtract(occ, BishopMasks[sq]);
                    BishopAttacksPEXT[sq][pextIdx] = AttackTablesExt.BishopRaysFrom(sq, occ);
                }

                for (int i = 0; i < rSize; i++)
                {
                    ulong occ = ExpandFromIndex((uint)i, RookMasks[sq]);
                    int pextIdx = (int)Bmi2.X64.ParallelBitExtract(occ, RookMasks[sq]);
                    RookAttacksPEXT[sq][pextIdx] = AttackTablesExt.RookRaysFrom(sq, occ);
                }
            }
        }
    }

    private static ulong ExpandFromIndex(uint idx, ulong mask)
    {
        // Software PDEP: scatter bits of idx into mask positions
        ulong result = 0;
        int bit = 0;
        while (mask != 0)
        {
            ulong lsb = mask & (~mask + 1);
            if (((idx >> bit) & 1) != 0) result |= lsb;
            mask ^= lsb;
            bit++;
        }
        return result;
    }

    private static int CompressToIndex(ulong bits, ulong mask)
    {
        // Software PEXT fallback
        int idx = 0;
        int bit = 0;
        while (mask != 0)
        {
            ulong lsb = mask & (~mask + 1);
            if ((bits & lsb) != 0) idx |= 1 << bit;
            mask ^= lsb;
            bit++;
        }
        return idx;
    }

    private static void PerformAutoDetection()
    {
        // Ensure PEXT is supported before attempting benchmark
        if (!Bmi2.X64.IsSupported)
        {
            _usePEXT = false;
            return;
        }

        const int benchmarkIterations = 100000;
        const int warmupIterations = 10000;

        // Test squares with different occupancy patterns for comprehensive coverage
        var testSquares = new[] { 28, 35, 42, 49 }; // Central squares
        var testOccupancies = new ulong[]
        {
            0x0000000000000000UL, // Empty board
            0x0081422418000000UL, // Sparse occupancy
            0x00003C7E7E3C0000UL, // Dense center
            0xFF818181818181FFUL  // Dense edges
        };

        var sw = Stopwatch.StartNew();

        // Warmup both paths
        for (int i = 0; i < warmupIterations; i++)
        {
            foreach (var sq in testSquares)
            {
                foreach (var occ in testOccupancies)
                {
                    // PEXT path
                    var mask = RookMasks[sq];
                    var idx1 = (int)Bmi2.X64.ParallelBitExtract(occ, mask);
                    var result1 = RookAttacks[sq][idx1];

                    // Multiply path
                    var idx2 = (int)(((occ & mask) * RookMagics[sq]) >> RookShifts[sq]);
                    var result2 = RookAttacks[sq][idx2];
                }
            }
        }

        // Benchmark PEXT path
        sw.Restart();
        for (int i = 0; i < benchmarkIterations; i++)
        {
            foreach (var sq in testSquares)
            {
                foreach (var occ in testOccupancies)
                {
                    var mask = RookMasks[sq];
                    var idx = (int)Bmi2.X64.ParallelBitExtract(occ, mask);
                    var result = RookAttacks[sq][idx];
                    // Prevent optimization
                    if (result == 0xFFFFFFFFFFFFFFFFUL) break;
                }
            }
        }
        var pextTime = sw.ElapsedTicks;

        // Benchmark multiply/shift path
        sw.Restart();
        for (int i = 0; i < benchmarkIterations; i++)
        {
            foreach (var sq in testSquares)
            {
                foreach (var occ in testOccupancies)
                {
                    var mask = RookMasks[sq];
                    var idx = (int)(((occ & mask) * RookMagics[sq]) >> RookShifts[sq]);
                    var result = RookAttacks[sq][idx];
                    // Prevent optimization
                    if (result == 0xFFFFFFFFFFFFFFFFUL) break;
                }
            }
        }
        var multiplyTime = sw.ElapsedTicks;

        // Choose the faster method with safety margin
        _usePEXT = pextTime < multiplyTime;

        // Calculate performance metrics
        var pextMs = (double)pextTime / Stopwatch.Frequency * 1000;
        var multiplyMs = (double)multiplyTime / Stopwatch.Frequency * 1000;
        var winner = _usePEXT ? "PEXT" : "Multiply";
        var speedup = _usePEXT ? (multiplyMs / pextMs - 1) * 100 : (pextMs / multiplyMs - 1) * 100;

        // Calculate operations per millisecond for more intuitive metrics
        var totalOps = (long)benchmarkIterations * testSquares.Length * testOccupancies.Length;
        var pextOpsPerMs = totalOps / Math.Max(pextMs, 0.001);
        var multiplyOpsPerMs = totalOps / Math.Max(multiplyMs, 0.001);

        // Log the decision (enabled via debug flag or when explicitly requested)
        bool shouldLog = Environment.GetEnvironmentVariable("MACHINE_DEBUG_PEXT") == "1";

        if (shouldLog)
        {
            Console.WriteLine($"info string Magic indexing auto-detection: {winner} selected ({speedup:F1}% faster)");
            Console.WriteLine($"info string PEXT: {pextOpsPerMs:F0} ops/ms, Multiply: {multiplyOpsPerMs:F0} ops/ms");
            Console.WriteLine($"info string Benchmark: {totalOps:N0} operations, PEXT: {pextMs:F2}ms, Multiply: {multiplyMs:F2}ms");
        }
    }
}