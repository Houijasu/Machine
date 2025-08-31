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

var uci = new UCIProtocol();
uci.Run();