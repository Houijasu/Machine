using Machine.Core;
using Machine.Search;
using TUnit;

namespace Machine.Tests;

public class PerftKiwipeteTests
{
    private const string Kiwipete = "rnbq1k1r/pppp1ppp/5n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 2 4";

    [Test]
    public void Kiwipete_Depth1_Is48()
    {
        var pos = new Position();
        pos.SetFen(Kiwipete);
        var nodes = Perft.Run(pos, 1);
        if (nodes != 48) throw new Exception($"Expected 48, got {nodes}");
    }

    [Test]
    public void Kiwipete_Depth2_Is2039()
    {
        var pos = new Position();
        pos.SetFen(Kiwipete);
        var nodes = Perft.Run(pos, 2);
        if (nodes != 2039) throw new Exception($"Expected 2039, got {nodes}");
    }

    [Test]
    public void Kiwipete_Depth3_Is97862()
    {
        var pos = new Position();
        pos.SetFen(Kiwipete);
        var nodes = Perft.Run(pos, 3);
        if (nodes != 97862) throw new Exception($"Expected 97862, got {nodes}");
    }

    [Test]
    public void Kiwipete_Depth4_Is4085603()
    {
        var pos = new Position();
        pos.SetFen(Kiwipete);
        var nodes = Perft.Run(pos, 4);
        if (nodes != 4085603) throw new Exception($"Expected 4085603, got {nodes}");
    }
}

