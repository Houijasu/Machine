using System.Runtime.CompilerServices;

namespace Machine.Core;

public readonly struct Move
{
    public readonly int From;
    public readonly int To;
    public readonly MoveFlag Flag;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Move(int from, int to, MoveFlag flag = MoveFlag.Quiet)
    {
        From = from;
        To = to;
        Flag = flag;
    }

    public static readonly Move NullMove = new(-1, -1, MoveFlag.None);

    public override string ToString()
    {
        if (From < 0 || To < 0) return "0000";
        
        char fromFile = (char)('a' + From % 8);
        char fromRank = (char)('1' + From / 8);
        char toFile = (char)('a' + To % 8);
        char toRank = (char)('1' + To / 8);
        
        string promotion = "";
        if (Flag >= MoveFlag.PromoQueen && Flag <= MoveFlag.PromoCaptureKnight)
        {
            promotion = Flag switch
            {
                MoveFlag.PromoQueen or MoveFlag.PromoCaptureQueen => "q",
                MoveFlag.PromoRook or MoveFlag.PromoCaptureRook => "r",
                MoveFlag.PromoBishop or MoveFlag.PromoCaptureBishop => "b",
                MoveFlag.PromoKnight or MoveFlag.PromoCaptureKnight => "n",
                _ => ""
            };
        }
        
        return $"{fromFile}{fromRank}{toFile}{toRank}{promotion}";
    }
}

