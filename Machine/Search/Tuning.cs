namespace Machine.Search;

public static class Tuning
{
    // Depth thresholds
    public const int DeepStackThreshold = 16; // use pooled buffers deeper than this

    // Margins (centipawns)
    public const int RazoringMarginDepth1 = 180;
    public const int RazoringMarginDepth2 = 260;
    public const int FutilityMarginPerDepth = 90;
    public const int ProbCutMargin = 150;
}

