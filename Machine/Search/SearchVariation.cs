namespace Machine.Search;

public struct SearchVariation
{
    public int LMRReduction;
    public int NullMoveReduction;
    public int FutilityMargin;
    public int WindowSize;
    public bool UseAspirationWindows;
    public float MoveOrderingNoise;

    public static SearchVariation[] CreateVariations(int threadCount)
    {
        var arr = new SearchVariation[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            arr[i] = new SearchVariation
            {
                LMRReduction = 1 + (i % 3),
                NullMoveReduction = 2 + (i % 2),
                FutilityMargin = 80 + (i * 5 % 40),
                WindowSize = 50 + (i * 10 % 100),
                UseAspirationWindows = i % 2 == 0,
                MoveOrderingNoise = (i % 5) * 0.01f
            };
        }
        if (threadCount > 0)
            arr[0] = new SearchVariation { LMRReduction = 1, NullMoveReduction = 2, FutilityMargin = 80, WindowSize = 50, UseAspirationWindows = false, MoveOrderingNoise = 0f };
        return arr;
    }
}

