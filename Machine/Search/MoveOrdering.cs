using System;
using Machine.Core;

namespace Machine.Search;

public static class MoveOrdering
{
    public const int MaxPly = 128;

    // Killer moves: two per ply
    private static Move[,] _killers = new Move[MaxPly, 2];
    // History heuristic table: from x to (64x64)
    private static int[,] _history = new int[64, 64];

    // Simple MVV-LVA victim values
    private static readonly int[] PieceValue =
    [
        0, 100, 320, 330, 500, 900, 0 // None, P, N, B, R, Q, K
    ];

    private static bool _useSEEPruning = true;
    public static void SetSEEPruning(bool enabled) => _useSEEPruning = enabled;
    private static int _seeGoodCaptureThreshold = 0; // cp
    public static void SetSEEThreshold(int cp) => _seeGoodCaptureThreshold = cp;


    public static void Clear()
    {
        Array.Clear(_killers);
        Array.Clear(_history);
    }

    public static int ScoreMove(Position pos, Move m, Move ttMove, int ply)
    {
        // TT move gets top priority
        if (IsSameMove(m, ttMove))
            return 1_000_000;

        // Promotions boost
        bool isPromo = IsPromotion(m);

        if (IsCapture(m))
        {
            if (_useSEEPruning)
            {
                // Use SEE to evaluate capture quality
                int seeValue = Quiescence.StaticExchangeEvaluation(pos, m);

                if (seeValue >= _seeGoodCaptureThreshold)
                {
                    // MVV-LVA via fast piece lookup for good captures
                    int victimIndex;
                    PieceType victimType = PieceType.None;
                    if (pos.PieceAtFast(m.To, out victimIndex))
                    {
                        victimType = (PieceType)((victimIndex % 6) + 1);
                    }
                    var aggressorType = GetAggressorType(pos, m.From, pos.SideToMove, isPromo);
                    int score = 500_000 + (PieceValue[(int)victimType] * 10) - PieceValue[(int)aggressorType];
                    if (isPromo) score += 100; // prefer promoting captures

                    // Boost score based on SEE value
                    score += Math.Min(seeValue, 1000); // cap SEE bonus
                    return score;
                }
                else
                {
                    // Bad captures get scored below killers but above quiet moves
                    return 100_000 + seeValue; // negative SEE will reduce score
                }
            }
            else
            {
                // Without SEE, fallback to MVV-LVA
                int victimIndex;
                PieceType victimType = PieceType.None;
                if (pos.PieceAtFast(m.To, out victimIndex)) victimType = (PieceType)((victimIndex % 6) + 1);
                var aggressorType = GetAggressorType(pos, m.From, pos.SideToMove, isPromo);
                int score = 500_000 + (PieceValue[(int)victimType] * 10) - PieceValue[(int)aggressorType];
                if (isPromo) score += 100;
                return score;
            }
        }

        // Killers (quiet only)
        ref var k0 = ref _killers[ply, 0];
        ref var k1 = ref _killers[ply, 1];
        if (IsSameMove(m, k0)) return 300_000;
        if (IsSameMove(m, k1)) return 299_000;

        // History for quiets
        return _history[m.From, m.To];
    }

    public static void Sort(Span<Move> moves, Span<int> scores)
    {
        // Simple insertion sort by scores descending (N<=256)
        for (int i = 1; i < moves.Length; i++)
        {
            var keyMove = moves[i];
            int keyScore = scores[i];
            int j = i - 1;
            while (j >= 0 && scores[j] < keyScore)
            {
                moves[j + 1] = moves[j];
                scores[j + 1] = scores[j];
                j--;
            }
            moves[j + 1] = keyMove;
            scores[j + 1] = keyScore;
        }
    }

    public static void OnBetaCutoff(Move m, int ply, int depth)
    {
        if (!IsCapture(m))
        {
            // Update killers
            ref var k0 = ref _killers[ply, 0];
            ref var k1 = ref _killers[ply, 1];
            if (!IsSameMove(m, k0))
            {
                k1 = k0;
                k0 = m;
            }
            // Update history (depth^2)
            int bonus = depth * depth;
            int newVal = _history[m.From, m.To] + bonus;
            // Clamp to avoid overflow
            if (newVal > 1_000_000) newVal = 1_000_000;
            _history[m.From, m.To] = newVal;
        }
    }

    public static void UpdateKillers(Move m, int ply)
    {
        // Simple killer update without history
        ref var k0 = ref _killers[ply, 0];
        ref var k1 = ref _killers[ply, 1];
        if (!IsSameMove(m, k0))
        {
            k1 = k0;
            k0 = m;
        }
    }

    private static bool IsSameMove(Move a, Move b)
    {
        return a.From == b.From && a.To == b.To && a.Flag == b.Flag;
    }

    private static bool IsCapture(Move move)
    {
        return move.Flag == MoveFlag.Capture || move.Flag == MoveFlag.EnPassant || move.Flag >= MoveFlag.PromoCaptureQueen;
    }

    private static bool IsPromotion(Move move)
    {
        return move.Flag >= MoveFlag.PromoQueen && move.Flag <= MoveFlag.PromoCaptureKnight;
    }

    private static PieceType GetAggressorType(Position pos, int from, Color us, bool isPromo)
    {
        if (isPromo) return PieceType.Pawn; // promotions originate from pawn
        if (pos.PieceAtFast(from, out var pieceIndex))
        {
            int typeIdx = pieceIndex % 6;
            return (PieceType)(typeIdx + 1);
        }
        return PieceType.Pawn;
    }

    private static PieceType GetPieceTypeAt(Position pos, int sq)
    {
        ulong mask = 1UL << sq;
        // White pieces 0..5
        for (int i = 0; i < 6; i++)
            if ((pos.PieceBB[i] & mask) != 0) return (PieceType)(i + 1);
        // Black pieces 6..11
        for (int i = 6; i < 12; i++)
            if ((pos.PieceBB[i] & mask) != 0) return (PieceType)(i - 5);
        return PieceType.None;
    }
}

