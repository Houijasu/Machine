using Machine.Core;

namespace Machine.Memory;

public sealed class ThreadLocalMemoryPool
{
    [ThreadStatic] private static ThreadLocalMemoryPool? _instance;

    public static ThreadLocalMemoryPool Instance => _instance ??= new ThreadLocalMemoryPool();

    private readonly Move[] _moveBuf = new Move[256];
    private readonly int[] _scoreBuf = new int[256];

    public Span<Move> GetMoveBuffer() => _moveBuf;
    public Span<int> GetScoreBuffer() => _scoreBuf;
    public Position GetPositionCopy() => new Position();
}

