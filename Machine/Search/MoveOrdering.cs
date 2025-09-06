using System;
using Machine.Core;

namespace Machine.Search;

public static class MoveOrdering
{
    public const int MaxPly = 128;

	    [ThreadStatic]
	    private static ThreadLocalSearchState? _tls;
	    public static void SetThreadState(ThreadLocalSearchState? state) => _tls = state;
    public static ThreadLocalSearchState? GetThreadState() => _tls;


    // Killer moves: two per ply
    private static Move[,] _killers = new Move[MaxPly, 2];
    // History heuristic table: from x to (64x64)
    private static int[,] _history = new int[64, 64];
    // Counter-move table: best response to a specific move (from x to)
    private static Move[,] _counterMoves = new Move[64, 64];

    // Simple MVV-LVA victim values
    private static readonly int[] PieceValue =
    [
        0, 100, 320, 330, 500, 900, 0 // None, P, N, B, R, Q, K
    ];

    private static bool _useSEEPruning = true;
    public static void SetSEEPruning(bool enabled) => _useSEEPruning = enabled;
    private static int _seeGoodCaptureThreshold = 0; // cp
    public static void SetSEEThreshold(int cp) => _seeGoodCaptureThreshold = cp;
    
    private static bool _dynamicMoveOrdering = true;
    public static void SetDynamicMoveOrdering(bool enabled) => _dynamicMoveOrdering = enabled;
    private static int _moveOrderingAggressiveness = 100; // 100% = normal
    public static void SetMoveOrderingAggressiveness(int aggressiveness) => _moveOrderingAggressiveness = Math.Clamp(aggressiveness, 50, 200);


    public static void Clear()
    {
        Array.Clear(_killers);
        Array.Clear(_history);
        ClearCounterMoves();
    }
    
    // Decay history scores by half (called on new search iteration)
    public static void DecayHistory()
    {
        // Decay global history
        for (int from = 0; from < 64; from++)
        {
            for (int to = 0; to < 64; to++)
            {
                _history[from, to] >>= 1; // Divide by 2
            }
        }
        
        // Also decay thread-local history if available
        var tls = _tls;
        if (tls?.History != null)
        {
            for (int from = 0; from < 64; from++)
            {
                for (int to = 0; to < 64; to++)
                {
                    tls.History[from, to] >>= 1;
                }
            }
        }
    }

    public static int ScoreMove(Position pos, Move m, Move ttMove, int ply, Move lastMove = default)
    {
        // TT move gets top priority
        if (IsSameMove(m, ttMove))
            return 1_000_000;
            
        // Apply aggressiveness multiplier to all scores
        int aggressivenessMultiplier = _moveOrderingAggressiveness;
            
        // Counter-move gets high priority (below TT move but above killers)
        if (lastMove.From >= 0 && lastMove.To >= 0 && IsSameMove(m, _counterMoves[lastMove.From, lastMove.To]))
        {
            int baseScore = 500_000;
            if (_dynamicMoveOrdering)
            {
                // Dynamic adjustment based on position and depth
                int dynamicScore = baseScore * aggressivenessMultiplier / 100;
                return Math.Min(dynamicScore, 999_999); // Cap below TT move
            }
            return baseScore;
        }

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
                    int baseScore = 500_000 + (PieceValue[(int)victimType] * 10) - PieceValue[(int)aggressorType];
                    if (isPromo) baseScore += 100; // prefer promoting captures

                    // Boost score based on SEE value
                    baseScore += Math.Min(seeValue, 1000); // cap SEE bonus
                    
                    if (_dynamicMoveOrdering)
                    {
                        // Dynamic adjustment based on position and depth
                        int dynamicScore = baseScore * aggressivenessMultiplier / 100;
                        return Math.Min(dynamicScore, 499_999); // Cap below counter-move
                    }
                    return baseScore;
                }
                else
                {
                    // Bad captures get scored below killers but above quiet moves
                    int baseScore = 100_000 + seeValue; // negative SEE will reduce score
                    if (_dynamicMoveOrdering)
                    {
                        // Dynamic adjustment for bad captures
                        int dynamicScore = baseScore * aggressivenessMultiplier / 100;
                        return Math.Max(dynamicScore, 1); // Ensure positive score
                    }
                    return baseScore;
                }
            }
            else
            {
                // Without SEE, fallback to MVV-LVA
                int victimIndex;
                PieceType victimType = PieceType.None;
                if (pos.PieceAtFast(m.To, out victimIndex)) victimType = (PieceType)((victimIndex % 6) + 1);
                var aggressorType = GetAggressorType(pos, m.From, pos.SideToMove, isPromo);
                int baseScore = 500_000 + (PieceValue[(int)victimType] * 10) - PieceValue[(int)aggressorType];
                if (isPromo) baseScore += 100;
                
                if (_dynamicMoveOrdering)
                {
                    // Dynamic adjustment based on position and depth
                    int dynamicScore = baseScore * aggressivenessMultiplier / 100;
                    return Math.Min(dynamicScore, 499_999); // Cap below counter-move
                }
                return baseScore;
            }
        }

        // Killers (quiet only)
        // Use thread-local killers/history if available
        var killers = _tls?.Killers ?? _killers;
        var history = _tls?.History ?? _history;

        ref var k0 = ref killers[ply, 0];
        ref var k1 = ref killers[ply, 1];
        if (IsSameMove(m, k0)) return 300_000;
        if (IsSameMove(m, k1)) return 299_000;

        // History for quiets
        int baseScore = history[m.From, m.To];
        if (_dynamicMoveOrdering)
        {
            // Dynamic adjustment for quiet moves based on aggressiveness
            int dynamicScore = baseScore * aggressivenessMultiplier / 100;
            // Cap quiet moves below captures
            return Math.Min(dynamicScore, 99_999);
        }
        return baseScore;
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

    public static void OnBetaCutoff(Move m, int ply, int depth, Move lastMove = default)
    {
        if (!IsCapture(m))
        {
            var killers = _tls?.Killers ?? _killers;
            var history = _tls?.History ?? _history;
            // Update killers
            ref var k0 = ref killers[ply, 0];
            ref var k1 = ref killers[ply, 1];
            if (!IsSameMove(m, k0))
            {
                k1 = k0;
                k0 = m;
            }
            // Update history (depth^2)
            int bonus = depth * depth;
            int newVal = history[m.From, m.To] + bonus;
            // Clamp to avoid overflow
            if (newVal > 1_000_000) newVal = 1_000_000;
            history[m.From, m.To] = newVal;
            
            // Update counter-move if we have a valid last move
            if (lastMove.From >= 0 && lastMove.To >= 0)
            {
                _counterMoves[lastMove.From, lastMove.To] = m;
            }
        }
    }
    
    public static int GetHistory(Move m)
    {
        var history = _tls?.History ?? _history;
        return history[m.From, m.To];
    }

    public static void UpdateKillers(Move m, int ply)
    {
        // Simple killer update without history
        var killers = _tls?.Killers ?? _killers;
        ref var k0 = ref killers[ply, 0];
        ref var k1 = ref killers[ply, 1];
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
        return move.Flag is >= MoveFlag.PromoQueen and <= MoveFlag.PromoCaptureKnight;
    }
    
    public static void ClearCounterMoves()
    {
        Array.Clear(_counterMoves);
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

