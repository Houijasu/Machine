using Machine.UCI;
using Machine.Core;
using Machine.Search;

if (args.Length >= 2 && (args[0] == "--perft" || args[0] == "--divide"))
{
    var pos = new Position();
    pos.SetStartPosition();
    if (int.TryParse(args[1], out var depth))
    {
        if (args[0] == "--perft")
            Perft.PerftCommand(pos, depth, Console.WriteLine);
        else
            Perft.Divide(pos, depth, Console.WriteLine);
        return;
    }
}

if (args is ["--test-atomic-tt", ..])
{
    Console.WriteLine("Testing Atomic TT vs Standard TT...");
    var pos = new Position();
    pos.SetStartPosition();
    
    // Test with CompactTT (now the default)
    var standardEngine = new SearchEngine(16);
    standardEngine.SetPosition(pos);
    var limits = new SearchLimits { MaxDepth = 4 };
    var start = DateTime.UtcNow;
    var standardResult = standardEngine.Search(limits);
    var standardTime = DateTime.UtcNow - start;
    
    // Test with another instance (same implementation)
    var atomicEngine = new SearchEngine(16);
    atomicEngine.SetPosition(pos);
    start = DateTime.UtcNow;
    var atomicResult = atomicEngine.Search(limits);
    var atomicTime = DateTime.UtcNow - start;
    
    Console.WriteLine($"Standard TT: Score={standardResult.Score}, Time={standardTime.TotalMilliseconds:F0}ms, Nodes={standardResult.NodesSearched}");
    Console.WriteLine($"Atomic TT:   Score={atomicResult.Score}, Time={atomicTime.TotalMilliseconds:F0}ms, Nodes={atomicResult.NodesSearched}");
    Console.WriteLine($"Results match: {standardResult.Score == atomicResult.Score && standardResult.BestMove.Equals(atomicResult.BestMove)}");
    return;
}

var uci = new UCIProtocol();
uci.Run();