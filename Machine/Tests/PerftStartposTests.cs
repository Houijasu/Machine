using Machine.Core;
using Machine.Search;
using TUnit;

namespace Machine.Tests;

public class PerftStartposTests
{
    [Test]
    public void Startpos_Depth1_Is20()
    {
        var pos = new Position();
        pos.SetStartPosition();
        var nodes = Perft.Run(pos, 1);
        if (nodes != 20) throw new Exception($"Expected 20, got {nodes}");
    }

    [Test]
    public void Startpos_Depth2_Is400()
    {
        var pos = new Position();
        pos.SetStartPosition();
        var nodes = Perft.Run(pos, 2);
        if (nodes != 400) throw new Exception($"Expected 400, got {nodes}");
    }

    [Test]
    public void Startpos_Depth3_Is8902()
    {
        var pos = new Position();
        pos.SetStartPosition();
        var nodes = Perft.Run(pos, 3);
        if (nodes != 8902) throw new Exception($"Expected 8902, got {nodes}");
    }

    // Depth 4/5 can be long; enable locally if desired
    [Test]
    public void Startpos_Depth4_Is197281()
    {
        var pos = new Position();
        pos.SetStartPosition();
        var nodes = Perft.Run(pos, 4);
        if (nodes != 197281) throw new Exception($"Expected 197281, got {nodes}");
    }

    [Test]
    public void Startpos_Depth5_Is4865609()
    {
        var pos = new Position();
        pos.SetStartPosition();
        var nodes = Perft.Run(pos, 5);
        if (nodes != 4865609) throw new Exception($"Expected 4865609, got {nodes}");
    }
}

