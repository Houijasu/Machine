using Machine.Core;
using Machine.Search;
using TUnit;

namespace Machine.Tests;

public class PerftEdgeCasesTests
{
    // Position 3 (EP edge cases)
    private const string Pos3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    // Position 4 (Promotions)
    private const string Pos4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    // Position 5 (Castling through check)
    private const string Pos5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    [Test]
    public void Position3_Depth5_Is674624()
    {
        var pos = new Position();
        pos.SetFen(Pos3);
        var nodes = Perft.Run(pos, 5);
        if (nodes != 674624) throw new Exception($"Expected 674624, got {nodes}");
    }

    [Test]
    public void Position4_Depth4_Is422333()
    {
        var pos = new Position();
        pos.SetFen(Pos4);
        var nodes = Perft.Run(pos, 4);
        if (nodes != 422333) throw new Exception($"Expected 422333, got {nodes}");
    }

    [Test]
    public void Position5_Depth4_Is2103487()
    {
        var pos = new Position();
        pos.SetFen(Pos5);
        var nodes = Perft.Run(pos, 4);
        if (nodes != 2103487) throw new Exception($"Expected 2103487, got {nodes}");
    }
}

