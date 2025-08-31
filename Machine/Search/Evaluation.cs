using System;
using Machine.Core;

namespace Machine.Search;

public static class Evaluation
{
    // Feature toggles (UCI configurable via UCIProtocol -> SetEvalOptions)
    public static bool UseEvaluation { get; private set; } = true;
    public static bool UsePST { get; private set; } = true;
    public static bool UsePawnStructure { get; private set; } = true;
    public static bool UseKingSafety { get; private set; } = true;

    public static void SetOptions(bool useEval, bool pst, bool pawn, bool king)
    {
        UseEvaluation = useEval;
        UsePST = pst; UsePawnStructure = pawn; UseKingSafety = king;
    }

    // Simple PSTs (MG/EG). Values are small and safe; tune later.
    // Indexed [pieceType 1..6][square 0..63], white perspective; black mirrored.
    private static readonly int[,] PstMG = new int[7,64];
    private static readonly int[,] PstEG = new int[7,64];

    static Evaluation()
    {
        // Initialize with modest piece-square trends.
        // Center preference for minors and queen, advancement for pawns, king safety.
        for (int sq = 0; sq < 64; sq++)
        {
            int r = sq / 8, f = sq % 8;
            int center = 3 - Math.Abs(3 - f) + 3 - Math.Abs(3 - r); // 0..6
            // Pawn
            PstMG[(int)PieceType.Pawn, sq] = 2 * center + r; // encourage advance
            PstEG[(int)PieceType.Pawn, sq] = 1 * center + r;
            // Knight
            PstMG[(int)PieceType.Knight, sq] = 5 * center - (r==0||r==7?4:0);
            PstEG[(int)PieceType.Knight, sq] = 4 * center;
            // Bishop
            PstMG[(int)PieceType.Bishop, sq] = 4 * center;
            PstEG[(int)PieceType.Bishop, sq] = 4 * center;
            // Rook
            PstMG[(int)PieceType.Rook, sq] = 2 * center + (r>=5?3:0);
            PstEG[(int)PieceType.Rook, sq] = 3 * center + (r>=5?2:0);
            // Queen
            PstMG[(int)PieceType.Queen, sq] = 2 * center;
            PstEG[(int)PieceType.Queen, sq] = 2 * center;
            // King
            PstMG[(int)PieceType.King, sq] = (r<=1?15:0) - 2*center; // prefer castled
            PstEG[(int)PieceType.King, sq] = 2*center + (r>=6?6:0);   // active king
        }
    }

    // Material values
    private static readonly int[] PieceValMG = {0, 100, 320, 330, 500, 900, 0};
    private static readonly int[] PieceValEG = {0, 100, 320, 330, 500, 900, 0};

    // Evaluate position in centipawns from side-to-move perspective
    public static int Evaluate(Position pos)
    {
        if (!UseEvaluation)
            return SimpleMaterial(pos);

        // Game phase: 0..24 based on non-pawn, non-king material
        int phase = ComputePhase(pos); // 0=EG, 24=MG
        int mgScore = 0, egScore = 0;

        // Sum material + PST
        for (Color c = Color.White; c <= Color.Black; c++)
        {
            int sign = c == Color.White ? 1 : -1;
            for (PieceType pt = PieceType.Pawn; pt <= PieceType.Queen; pt++)
            {
                ulong bb = pos.Pieces(c, pt);
                while (bb != 0)
                {
                    int sq = Bitboards.Lsb(bb);
                    bb &= bb - 1;
                    int sqW = c == Color.White ? sq : Mirror(sq);
                    if (UsePST)
                    {
                        mgScore += sign * (PieceValMG[(int)pt] + PstMG[(int)pt, sqW]);
                        egScore += sign * (PieceValEG[(int)pt] + PstEG[(int)pt, sqW]);
                    }
                    else
                    {
                        mgScore += sign * PieceValMG[(int)pt];
                        egScore += sign * PieceValEG[(int)pt];
                    }
                }
            }
        }

        // Pawn structure heuristics
        if (UsePawnStructure)
        {
            mgScore += PawnStructure(pos);
            egScore += PawnStructure(pos) / 2;
        }

        // King safety basics
        if (UseKingSafety)
        {
            mgScore += KingSafety(pos);
        }

        // Blend by phase
        int score = (mgScore * phase + egScore * (24 - phase)) / 24;

        // Return from side to move perspective
        return pos.SideToMove == Color.White ? score : -score;
    }

    private static int SimpleMaterial(Position pos)
    {
        int eval = 0;
        for (Color c = Color.White; c <= Color.Black; c++)
        {
            int s = c == Color.White ? 1 : -1;
            eval += s * Bitboards.PopCount(pos.Pieces(c, PieceType.Pawn)) * 100;
            eval += s * Bitboards.PopCount(pos.Pieces(c, PieceType.Knight)) * 320;
            eval += s * Bitboards.PopCount(pos.Pieces(c, PieceType.Bishop)) * 330;
            eval += s * Bitboards.PopCount(pos.Pieces(c, PieceType.Rook)) * 500;
            eval += s * Bitboards.PopCount(pos.Pieces(c, PieceType.Queen)) * 900;
        }
        return pos.SideToMove == Color.White ? eval : -eval;
    }

    private static int ComputePhase(Position pos)
    {
        int phase = 0;
        phase += 1 * Bitboards.PopCount(pos.Pieces(Color.White, PieceType.Knight) | pos.Pieces(Color.Black, PieceType.Knight));
        phase += 1 * Bitboards.PopCount(pos.Pieces(Color.White, PieceType.Bishop) | pos.Pieces(Color.Black, PieceType.Bishop));
        phase += 2 * Bitboards.PopCount(pos.Pieces(Color.White, PieceType.Rook) | pos.Pieces(Color.Black, PieceType.Rook));
        phase += 4 * Bitboards.PopCount(pos.Pieces(Color.White, PieceType.Queen) | pos.Pieces(Color.Black, PieceType.Queen));
        return Math.Clamp(phase, 0, 24);
    }

    private static int Mirror(int sq)
    {
        int r = sq / 8, f = sq % 8;
        return (7 - r) * 8 + f;
    }

    private static int PawnStructure(Position pos)
    {
        int score = 0;
        // Doubled, isolated, passed (very basic)
        for (Color c = Color.White; c <= Color.Black; c++)
        {
            int sign = c == Color.White ? 1 : -1;
            ulong pawns = pos.Pieces(c, PieceType.Pawn);
            // Doubled/isolated by file occupancy
            for (int file = 0; file < 8; file++)
            {
                ulong fileMask = Bitboards.FileMask(file);
                int count = Bitboards.PopCount(pawns & fileMask);
                if (count >= 2) score -= sign * 12 * (count - 1);
                // Isolated: no pawns on adjacent files
                bool hasAny = count > 0;
                if (hasAny)
                {
                    bool left = file > 0 && (pawns & Bitboards.FileMask(file-1)) != 0;
                    bool right = file < 7 && (pawns & Bitboards.FileMask(file+1)) != 0;
                    if (!left && !right) score -= sign * 15;
                }
            }
            // Passed pawns (naive)
            ulong oppPawns = pos.Pieces(c == Color.White ? Color.Black : Color.White, PieceType.Pawn);
            ulong temp = pawns;
            while (temp != 0)
            {
                int sq = Bitboards.Lsb(temp); temp &= temp - 1;
                int r = sq / 8, f = sq % 8;
                bool passed;
                if (c == Color.White)
                {
                    ulong mask = 0UL;
                    for (int rr = r+1; rr < 8; rr++)
                        for (int df = -1; df <= 1; df++)
                        {
                            int ff = f + df; if ((uint)ff >= 8) continue;
                            mask |= 1UL << (rr*8 + ff);
                        }
                    passed = (oppPawns & mask) == 0;
                }
                else
                {
                    ulong mask = 0UL;
                    for (int rr = r-1; rr >= 0; rr--)
                        for (int df = -1; df <= 1; df++)
                        {
                            int ff = f + df; if ((uint)ff >= 8) continue;
                            mask |= 1UL << (rr*8 + ff);
                        }
                    passed = (oppPawns & mask) == 0;
                }
                if (passed) score += sign * 20;
            }
        }
        return score;
    }

    private static int KingSafety(Position pos)
    {
        int score = 0;
        // Penalize open files near own king in MG.
        for (Color c = Color.White; c <= Color.Black; c++)
        {
            int sign = c == Color.White ? 1 : -1;
            ulong king = pos.Pieces(c, PieceType.King);
            if (king == 0) continue;
            int sq = Bitboards.Lsb(king);
            int f = sq % 8;
            // Files near king
            for (int df = -1; df <= 1; df++)
            {
                int ff = f + df; if ((uint)ff >= 8) continue;
                ulong mask = Bitboards.FileMask(ff);
                bool open = (pos.Pieces(c, PieceType.Pawn) & mask) == 0;
                if (open) score -= sign * 10;
            }
        }
        return score;
    }
}

