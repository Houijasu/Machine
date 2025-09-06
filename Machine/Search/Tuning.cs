namespace Machine.Search;

public static class Tuning
{
    // Depth thresholds
    public const int DeepStackThreshold = 16; // use pooled buffers deeper than this

    // Margins (centipawns)
    public const int RazoringMarginDepth1 = 180;
    public const int RazoringMarginDepth2 = 260;
    public const int FutilityMarginBase = 100;  // Increased from 90
    public const int FutilityMarginPerDepth = 110;  // Increased from 90 for more aggressive pruning
    public const int ProbCutMargin = 150;
    
    // Get futility margin with depth-specific adjustments
    public static int GetFutilityMargin(int depth)
    {
        // More conservative margins for stability
        if (depth == 1) return 120;  // Base margin
        if (depth == 2) return 220;  // Was 240, rolled back to 220
        if (depth == 3) return 340;  // Was 380, rolled back to 340
        return depth * FutilityMarginPerDepth;
    }
}

