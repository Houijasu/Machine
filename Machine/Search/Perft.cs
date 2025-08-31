using System;
using System.Diagnostics;
using Machine.Core;
using Machine.MoveGen;

namespace Machine.Search;

public static class Perft
{
    public static long Run(Position pos, int depth)
    {
        if (depth == 0) return 1;

        Span<Move> buffer = stackalloc Move[256];
        long nodes = 0;
        int count = MoveGenerator.GenerateMoves(pos, buffer);
        for (int i = 0; i < count; i++)
        {
            var m = buffer[i];
            pos.ApplyMove(m);
            
            // Check if this move is legal (doesn't leave king in check)
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);
            
            if (isLegal)
            {
                nodes += Run(pos, depth - 1);
            }
            pos.UndoMove(m);
        }
        return nodes;
    }

    public static void PerftCommand(Position pos, int depth, Action<string> writeLine)
    {
        var sw = Stopwatch.StartNew();
        long nodes = Run(pos, depth);
        sw.Stop();
        double ms = Math.Max(1.0, sw.Elapsed.TotalMilliseconds);
        long nps = (long)(nodes * 1000.0 / ms);
        writeLine($"info string perft depth {depth} nodes {nodes} time {sw.ElapsedMilliseconds} nps {nps}");
    }

    public static void Divide(Position pos, int depth, Action<string> writeLine)
    {
        if (depth <= 0)
        {
            writeLine("info string divide requires depth >= 1");
            return;
        }
        Span<Move> buffer = stackalloc Move[256];
        int count = MoveGenerator.GenerateMoves(pos, buffer);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var m = buffer[i];
            pos.ApplyMove(m);
            
            // Check if this move is legal (doesn't leave king in check)
            Color movedColor = pos.SideToMove == Color.White ? Color.Black : Color.White;
            bool isLegal = !pos.IsKingInCheck(movedColor);
            
            if (isLegal)
            {
                long nodes = Run(pos, depth - 1);
                total += nodes;
                writeLine($"{FormatMove(m)}: {nodes}");
            }
            pos.UndoMove(m);
        }
        writeLine($"Nodes searched: {total}");
    }

    private static string FormatMove(Move m)
    {
        static char File(int sq) => (char)('a' + (sq % 8));
        static char Rank(int sq) => (char)('1' + (sq / 8));
        return string.Create(4, m, (span, move) =>
        {
            span[0] = File(move.From);
            span[1] = Rank(move.From);
            span[2] = File(move.To);
            span[3] = Rank(move.To);
        });
    }
}

