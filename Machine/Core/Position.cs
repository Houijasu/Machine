using System;
using System.Collections.Generic;
using System.Numerics;
using Machine.MoveGen;

namespace Machine.Core;

public sealed class Position
{
    // Piece bitboards indexed as: 0..5 white (P,N,B,R,Q,K), 6..11 black (p,n,b,r,q,k)
    public ulong[] PieceBB { get; } = new ulong[12];
    public ulong[] Occupancy { get; } = new ulong[2]; // [white, black]
    public ulong AllOccupied => Occupancy[0] | Occupancy[1];

    private readonly byte[] _pieceOnSquare = new byte[64]; // 255 = empty, else 0..11 piece index

    public Color SideToMove { get; private set; } = Color.White;
    public CastlingRights Castling { get; private set; } = CastlingRights.All;
    public int EnPassantSquare { get; private set; } = -1;
    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;


	    private ulong _zobristKey;

    public struct UndoInfo
    {
        public PieceType Captured;
        public CastlingRights Castling;
        public int EnPassant;
        public int HalfMoveClock;
        public int FullMoveNumber;
        public ulong Hash; // reserved for future Zobrist use
        public int CapturedSquare; // for EP
    }

    private readonly Stack<UndoInfo> _undo = new();

    public ulong Pieces(Color c, PieceType pt)
    {
        int idx = PieceIndex(c, pt);
        return idx >= 0 ? PieceBB[idx] : 0UL;
    }
    public bool PieceAtFast(int sq, out int pieceIndex)
    {
        pieceIndex = _pieceOnSquare[sq];
        return pieceIndex != 255;
    }


    public ulong Occupied(Color c) => Occupancy[(int)c];



    public void Clear()
    {
        Array.Clear(PieceBB, 0, PieceBB.Length);
        Occupancy[0] = Occupancy[1] = 0UL;
        SideToMove = Color.White;
        Castling = CastlingRights.None;
        EnPassantSquare = -1;
        HalfmoveClock = 0;
            for (int i = 0; i < 64; i++) _pieceOnSquare[i] = 255;

        FullmoveNumber = 1;
            _zobristKey = 0;

        _undo.Clear();
    }

    public void SetStartPosition()
    {
        SetFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
    }

    public void SetFen(string fen)
    {
        Clear();

        // Example: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Invalid FEN");

        // Pieces
        ParsePieces(parts[0]);

        // Side to move
        SideToMove = parts[1] == "b" ? Color.Black : Color.White;

        // Castling
        Castling = CastlingRights.None;
        if (parts.Length > 2)
        {
            var c = parts[2];
            if (c.Contains('K')) Castling |= CastlingRights.WhiteKing;
            if (c.Contains('Q')) Castling |= CastlingRights.WhiteQueen;
            if (c.Contains('k')) Castling |= CastlingRights.BlackKing;
            if (c.Contains('q')) Castling |= CastlingRights.BlackQueen;
        }

        // En passant
        EnPassantSquare = -1;
        if (parts.Length > 3 && parts[3] != "-")
        {
            var file = parts[3][0] - 'a';
            var rank = parts[3][1] - '1';
            if (file is >= 0 and <= 7 && rank is >= 0 and <= 7)
            {
                EnPassantSquare = rank * 8 + file;
            }
        }

        // Clocks
        HalfmoveClock = parts.Length > 4 && int.TryParse(parts[4], out var hmc) ? hmc : 0;
        FullmoveNumber = parts.Length > 5 && int.TryParse(parts[5], out var fmn) ? fmn : 1;

        // Occupancy
        RecomputeOccupancy();
        RecomputeZobrist();
    }

    private void ParsePieces(string placement)
    {
        int rank = 7;
        int file = 0;
        foreach (var ch in placement)
        {
            if (ch == '/')
            {
                rank--; file = 0;
                continue;
            }
            if (ch >= '1' && ch <= '8')
            {
                file += ch - '0';
                continue;

            }
            int sq = rank * 8 + file;
            AddPieceChar(ch, sq);
            file++;
        }
    }

    private void AddPieceChar(char c, int sq)
    {
        bool isWhite = char.IsUpper(c);
        PieceType pt = c switch
        {
            'P' or 'p' => PieceType.Pawn,
            'N' or 'n' => PieceType.Knight,
            'B' or 'b' => PieceType.Bishop,
            'R' or 'r' => PieceType.Rook,

            'Q' or 'q' => PieceType.Queen,
            'K' or 'k' => PieceType.King,
            _ => PieceType.None
        };
        if (pt == PieceType.None) return;
        AddPiece(isWhite ? Color.White : Color.Black, pt, sq);
    }
    private void RecomputeZobrist()
    {
        ulong key = 0;
        for (Color color = Color.White; color <= Color.Black; color++)
        {
            for (PieceType pieceType = PieceType.Pawn; pieceType <= PieceType.King; pieceType++)
            {
                ulong pieces = Pieces(color, pieceType);
                while (pieces != 0)
                {
                    int square = System.Numerics.BitOperations.TrailingZeroCount(pieces);
                    pieces &= pieces - 1;
                    int pieceIndex = PieceIndex(color, pieceType);
                    key ^= Zobrist.PieceSquare[pieceIndex, square];
                }
            }
        }
        key ^= Zobrist.Castle[(int)Castling];
        if (EnPassantSquare != -1)
        {
            int file = EnPassantSquare % 8;
            key ^= Zobrist.EnPassantFile[file];
        }
        if (SideToMove == Color.Black) key ^= Zobrist.SideToMove;
        _zobristKey = key;
    }

#if DEBUG
        public ulong ComputeZobristFromScratch()
        {
            ulong key = 0;
            for (Color color = Color.White; color <= Color.Black; color++)
            {
                for (PieceType pieceType = PieceType.Pawn; pieceType <= PieceType.King; pieceType++)
                {
                    int idx = PieceIndex(color, pieceType);
                    ulong bb = PieceBB[idx];
                    while (bb != 0)
                    {
                        int sq = Bitboards.Lsb(bb);
                        bb &= bb - 1;
                        key ^= Zobrist.PieceSquare[idx, sq];
                    }
                }
            }
            if (EnPassantSquare != -1) key ^= Zobrist.EnPassantFile[EnPassantSquare % 8];
            key ^= Zobrist.Castle[(int)Castling];
            if (SideToMove == Color.Black) key ^= Zobrist.SideToMove;
            return key;
        }

        public bool VerifyZobrist(out string message)
        {
            ulong recomputed = ComputeZobristFromScratch();
            if (recomputed != _zobristKey)
            {
                message = $"zobrist mismatch: inc={_zobristKey} rec={recomputed} castling={Castling} ep={EnPassantSquare} stm={SideToMove}";
                return false;
            }
            message = string.Empty;
            return true;
        }
#endif



    public static int PieceIndex(Color c, PieceType pt)
    {
        int baseIdx = c == Color.White ? 0 : 6;
        return baseIdx + ((int)pt - 1);
    }

    private void RecomputeOccupancy()
    {
        Occupancy[0] = Occupancy[1] = 0UL;
        for (int i = 0; i < 6; i++) Occupancy[0] |= PieceBB[i];
        for (int i = 6; i < 12; i++) Occupancy[1] |= PieceBB[i];
    }

    private void AddPiece(Color c, PieceType pt, int sq)
    {
        int idx = PieceIndex(c, pt);
        ulong mask = 1UL << sq;
        PieceBB[idx] |= mask;
        Occupancy[(int)c] |= mask;
        _pieceOnSquare[sq] = (byte)idx;

        _zobristKey ^= Zobrist.PieceSquare[idx, sq];
    }

    private void RemovePiece(Color c, PieceType pt, int sq)
    {
        int idx = PieceIndex(c, pt);
        ulong mask = 1UL << sq;
        PieceBB[idx] &= ~mask;
        Occupancy[(int)c] &= ~mask;
        _pieceOnSquare[sq] = 255;
        _zobristKey ^= Zobrist.PieceSquare[idx, sq];
    }

    private bool PieceAt(int sq, out Color color, out PieceType pt)
    {
        ulong mask = 1UL << sq;
        if ((Occupancy[0] & mask) != 0)
        {
            color = Color.White;
            for (int i = 0; i < 6; i++) if ((PieceBB[i] & mask) != 0) { pt = (PieceType)(i + 1); return true; }
        }
        else if ((Occupancy[1] & mask) != 0)
        {
            color = Color.Black;
            for (int i = 6; i < 12; i++) if ((PieceBB[i] & mask) != 0) { pt = (PieceType)(i - 5); return true; }
        }
        color = Color.White; pt = PieceType.None; return false;
    }

#if DEBUG
        private const bool CheckZobristOnUndo = true;
#else
        private const bool CheckZobristOnUndo = false;
#endif

    public void ApplyMove(Move m)
    {
        // Save undo info
        var u = new UndoInfo
        {
            Castling = Castling,
            EnPassant = EnPassantSquare,
            HalfMoveClock = HalfmoveClock,
            FullMoveNumber = FullmoveNumber,
            Captured = PieceType.None,
            Hash = _zobristKey,
            CapturedSquare = -1
        };

        int from = m.From;
        int to = m.To;
        if (!PieceAt(from, out var us, out var pt)) throw new InvalidOperationException("No piece on from square");
        var them = us == Color.White ? Color.Black : Color.White;

        // Move piece (handle special later)
        RemovePiece(us, pt, from);

        // Captures (normal)
        if ((m.Flag == MoveFlag.Capture) || (m.Flag >= MoveFlag.PromoCaptureKnight && m.Flag <= MoveFlag.PromoCaptureQueen))
        {
            // EP handled separately
            if (PieceAt(to, out var capColor, out var capType))
            {
                RemovePiece(capColor, capType, to);
                u.Captured = capType;
                u.CapturedSquare = to;
                HalfmoveClock = 0;
            }
        }

        // En passant capture
        if (m.Flag == MoveFlag.EnPassant)
        {
            int capSq = us == Color.White ? to - 8 : to + 8;
            RemovePiece(them, PieceType.Pawn, capSq);
            u.Captured = PieceType.Pawn;
            u.CapturedSquare = capSq;
            HalfmoveClock = 0;
        }

        // Promotions
        if (m.Flag is MoveFlag.PromoKnight or MoveFlag.PromoBishop or MoveFlag.PromoRook or MoveFlag.PromoQueen
            or MoveFlag.PromoCaptureKnight or MoveFlag.PromoCaptureBishop or MoveFlag.PromoCaptureRook or MoveFlag.PromoCaptureQueen)
        {
            var promo = m.Flag switch
            {
                MoveFlag.PromoKnight or MoveFlag.PromoCaptureKnight => PieceType.Knight,
                MoveFlag.PromoBishop or MoveFlag.PromoCaptureBishop => PieceType.Bishop,
                MoveFlag.PromoRook or MoveFlag.PromoCaptureRook => PieceType.Rook,
                _ => PieceType.Queen
            };
            // Remove pawn already removed from 'from', add promoted piece at 'to'
            AddPiece(us, promo, to);
            HalfmoveClock = 0;
        }
        else
        {
            // Non-promotion: place moving piece at 'to'
            AddPiece(us, pt, to);
        }

        // Castling rook move
        if (m.Flag == MoveFlag.KingCastle)
        {
            if (us == Color.White)
            {
                // Move rook H1->F1
                RemovePiece(us, PieceType.Rook, 7);
                AddPiece(us, PieceType.Rook, 5);
            }
            else
            {
                // H8->F8
                RemovePiece(us, PieceType.Rook, 63);
                AddPiece(us, PieceType.Rook, 61);
            }
        }
        else if (m.Flag == MoveFlag.QueenCastle)
        {
            if (us == Color.White)
            {
                // A1->D1
                RemovePiece(us, PieceType.Rook, 0);
                AddPiece(us, PieceType.Rook, 3);
            }
            else
            {
                // A8->D8
                RemovePiece(us, PieceType.Rook, 56);
                AddPiece(us, PieceType.Rook, 59);
            }


        }

        // Update state: castling rights
        // If king moved, lose both sides
        if (pt == PieceType.King)
        {
            if (us == Color.White) Castling &= ~(CastlingRights.WhiteKing | CastlingRights.WhiteQueen);
            else Castling &= ~(CastlingRights.BlackKing | CastlingRights.BlackQueen);
        }
        // If rook moved from corner, lose that side
        if (pt == PieceType.Rook)
        {
            if (us == Color.White)
            {
                if (from == 0) Castling &= ~CastlingRights.WhiteQueen;
                else if (from == 7) Castling &= ~CastlingRights.WhiteKing;
            }
            else
            {
                if (from == 56) Castling &= ~CastlingRights.BlackQueen;
                else if (from == 63) Castling &= ~CastlingRights.BlackKing;
            }
        }
        // If rook got captured on corner, update opponent rights
        if (u.Captured == PieceType.Rook)
#if DEBUG
            if (Machine.Search.SearchEngine.DebugEnabled)
            {
                if (!VerifyZobrist(out var msg))
                {
                    System.Diagnostics.Debug.WriteLine($"[ApplyMove] {msg}");
                }
            }
#endif

        {
            if (them == Color.White)
            {
                if (u.CapturedSquare == 0) Castling &= ~CastlingRights.WhiteQueen;
                else if (u.CapturedSquare == 7) Castling &= ~CastlingRights.WhiteKing;
            }
            else
            {
                if (u.CapturedSquare == 56) Castling &= ~CastlingRights.BlackQueen;
                else if (u.CapturedSquare == 63) Castling &= ~CastlingRights.BlackKing;
            }
        }

        // EP square update candidate for next move
        int newEp = -1;
        if (pt == PieceType.Pawn && m.Flag == MoveFlag.DoublePush)
        {
            newEp = us == Color.White ? (from + 8) : (from - 8);
            HalfmoveClock = 0;
        }
        else
        {
            if (pt == PieceType.Pawn) HalfmoveClock = 0; else HalfmoveClock++;
        }

        // Consolidated Zobrist updates at end (always execute)
        // 1) Toggle side to move
        _zobristKey ^= Zobrist.SideToMove;
        // 2) Castling rights change
        if (Castling != u.Castling)
        {
            _zobristKey ^= Zobrist.Castle[(int)u.Castling];
            _zobristKey ^= Zobrist.Castle[(int)Castling];
        }
        // 3) En passant changes: clear old, set new
        if (u.EnPassant != -1) _zobristKey ^= Zobrist.EnPassantFile[u.EnPassant % 8];
        if (newEp != -1) _zobristKey ^= Zobrist.EnPassantFile[newEp % 8];

        EnPassantSquare = newEp;

        // Fullmove number and side
        if (us == Color.Black) FullmoveNumber++;
        SideToMove = them;

        _undo.Push(u);
    }

    public void UndoMove(Move m)
    {
        if (_undo.Count == 0) throw new InvalidOperationException("Undo stack empty");
        var u = _undo.Pop();

        int from = m.From;
        int to = m.To;

        // Side that moved is the opposite of current side-to-move
        var them = SideToMove; // player to move now
        var us = them == Color.White ? Color.Black : Color.White; // player who moved

        // Revert castling rook moves if needed before moving king back
        if (m.Flag == MoveFlag.KingCastle)
        {
            if (us == Color.White)
            {
                // rook F1->H1
                RemovePiece(us, PieceType.Rook, 5);
                AddPiece(us, PieceType.Rook, 7);
            }
            else
            {
                RemovePiece(us, PieceType.Rook, 61);
                AddPiece(us, PieceType.Rook, 63);
            }
        }
        else if (m.Flag == MoveFlag.QueenCastle)
        {
            if (us == Color.White)
            {
                RemovePiece(us, PieceType.Rook, 3);
                AddPiece(us, PieceType.Rook, 0);
            }
            else
            {
                RemovePiece(us, PieceType.Rook, 59);
                AddPiece(us, PieceType.Rook, 56);
            }
        }

        // Remove piece from 'to' and restore to 'from'
        if (m.Flag is MoveFlag.PromoKnight or MoveFlag.PromoBishop or MoveFlag.PromoRook or MoveFlag.PromoQueen
            or MoveFlag.PromoCaptureKnight or MoveFlag.PromoCaptureBishop or MoveFlag.PromoCaptureRook or MoveFlag.PromoCaptureQueen)
        {
            // Promotion: remove promoted piece from 'to', add pawn back to 'from'
            var promo = m.Flag switch
            {
                MoveFlag.PromoKnight or MoveFlag.PromoCaptureKnight => PieceType.Knight,
                MoveFlag.PromoBishop or MoveFlag.PromoCaptureBishop => PieceType.Bishop,
                MoveFlag.PromoRook or MoveFlag.PromoCaptureRook => PieceType.Rook,
                _ => PieceType.Queen
            };
            RemovePiece(us, promo, to);
            AddPiece(us, PieceType.Pawn, from);
        }
        else
        {
            // Non-promotion: detect the moved piece currently on 'to' and move it back
            if (!PieceAt(to, out var movedColor, out var movedType)) movedType = PieceType.None;
            if (movedType == PieceType.None)
            {
                // Fallback guard: assume pawn
                movedType = PieceType.Pawn;
            }
            RemovePiece(us, movedType, to);
            AddPiece(us, movedType, from);
        }

        // Restore captured piece
        if (m.Flag == MoveFlag.EnPassant)
        {


            int capSq = us == Color.White ? to - 8 : to + 8;
            AddPiece(SideToMove, PieceType.Pawn, capSq);
        }
        else if (u.Captured != PieceType.None)
        {
            AddPiece(SideToMove, u.Captured, u.CapturedSquare);
        }

        // Capture current (post-move) state for Zobrist reversal
        var newCastle = Castling;
        var newEp = EnPassantSquare;

        // Restore state fields
        Castling = u.Castling;
        EnPassantSquare = u.EnPassant;
        HalfmoveClock = u.HalfMoveClock;
        FullmoveNumber = u.FullMoveNumber;
        SideToMove = us; // it's now our turn again

        // Undo Zobrist changes matching restored state
        _zobristKey ^= Zobrist.SideToMove; // toggle back
        // Castling: remove new, add old
        _zobristKey ^= Zobrist.Castle[(int)newCastle];
        _zobristKey ^= Zobrist.Castle[(int)u.Castling];
        // En passant: remove new, add old
        if (newEp != -1) _zobristKey ^= Zobrist.EnPassantFile[newEp % 8];
        if (u.EnPassant != -1) _zobristKey ^= Zobrist.EnPassantFile[u.EnPassant % 8];

            if (CheckZobristOnUndo)
            {
                // After UndoMove restores state, zobrist should match saved hash from undo info
                System.Diagnostics.Debug.Assert(_zobristKey == u.Hash, "Zobrist mismatch after undo");
            }
        }


    public bool IsSquareAttacked(int square, Color byColor)
    {
        // Check pawn attacks
        ulong pawnBB = Pieces(byColor, PieceType.Pawn);
        if (byColor == Color.White)
        {
            // White pawns attack diagonally up
            if (square >= 16 && ((pawnBB & (1UL << (square - 9))) != 0 && (square % 8 != 0))) return true; // down-left
            if (square >= 16 && ((pawnBB & (1UL << (square - 7))) != 0 && (square % 8 != 7))) return true; // down-right
        }
        else
        {
            // Black pawns attack diagonally down
            if (square < 48 && ((pawnBB & (1UL << (square + 7))) != 0 && (square % 8 != 0))) return true; // up-left
            if (square < 48 && ((pawnBB & (1UL << (square + 9))) != 0 && (square % 8 != 7))) return true; // up-right
        }

        // Check knight attacks
        ulong knightBB = Pieces(byColor, PieceType.Knight);
        var knightAttacks = AttackTables.KnightAttacks[square];
        if ((knightBB & knightAttacks) != 0) return true;

        // Check king attacks
        ulong kingBB = Pieces(byColor, PieceType.King);
        var kingAttacks = AttackTables.KingAttacks[square];
        if ((kingBB & kingAttacks) != 0) return true;

        // Check sliding piece attacks (bishop, rook, queen)
        ulong bishopBB = Pieces(byColor, PieceType.Bishop) | Pieces(byColor, PieceType.Queen);
        if (bishopBB != 0)
        {
            var bishopAttacks = Magics.GetBishopAttacks(square, AllOccupied);
            if ((bishopBB & bishopAttacks) != 0) return true;
        }

        ulong rookBB = Pieces(byColor, PieceType.Rook) | Pieces(byColor, PieceType.Queen);
        if (rookBB != 0)
        {
            var rookAttacks = Magics.GetRookAttacks(square, AllOccupied);
            if ((rookBB & rookAttacks) != 0) return true;
        }
        return false;
    }

    public bool IsKingInCheck(Color color)
    {
        ulong kingBB = Pieces(color, PieceType.King);
        if (kingBB == 0) return false;
        int kingSquare = Bitboards.Lsb(kingBB);
        Color enemyColor = color == Color.White ? Color.Black : Color.White;
        return IsSquareAttacked(kingSquare, enemyColor);
    }

    public ulong ZobristKey => _zobristKey;

    public Position Clone()
    {
        var clone = new Position();
        Array.Copy(PieceBB, clone.PieceBB, 12);
        Array.Copy(Occupancy, clone.Occupancy, 2);
        clone.SideToMove = SideToMove;
        clone.Castling = Castling;
        clone.EnPassantSquare = EnPassantSquare;
        clone.HalfmoveClock = HalfmoveClock;
        clone.FullmoveNumber = FullmoveNumber;
        // Preserve Zobrist so TT probes and repetition checks remain valid
        clone._zobristKey = _zobristKey;
        return clone;
    }

    // Bitboard of all pieces of byColor attacking 'square' with given occupancy
    public ulong GetAttackers(int square, Color byColor, ulong occupied)
    {
        ulong mask = 1UL << square;
        ulong attackers = 0UL;

        // Knights
        attackers |= (Pieces(byColor, PieceType.Knight) & AttackTables.KnightAttacks[square]);
        // King
        attackers |= (Pieces(byColor, PieceType.King) & AttackTables.KingAttacks[square]);

        // Pawns (iterate pawns; asymmetric attacks)
        ulong pawns = Pieces(byColor, PieceType.Pawn);
        while (pawns != 0)
        {
            int from = Bitboards.Lsb(pawns);
            pawns &= pawns - 1;
            if ((AttackTables.PawnAttacks[(int)byColor, from] & mask) != 0)
                attackers |= 1UL << from;
        }

        // Sliding pieces
        ulong bishops = Pieces(byColor, PieceType.Bishop);
        ulong rooks = Pieces(byColor, PieceType.Rook);
        ulong queens = Pieces(byColor, PieceType.Queen);

        attackers |= (Magics.GetBishopAttacks(square, occupied) & (bishops | queens));
        attackers |= (Magics.GetRookAttacks(square, occupied) & (rooks | queens));

        return attackers;
    }

    public void MakeNullMove()
    {
        // Save state for undo
        _undo.Push(new UndoInfo
        {
            Captured = PieceType.None,
            Castling = Castling,
            EnPassant = EnPassantSquare,
            HalfMoveClock = HalfmoveClock,
            FullMoveNumber = FullmoveNumber,
            Hash = _zobristKey,
            CapturedSquare = -1
        });

        // Update zobrist for side to move
        _zobristKey ^= Zobrist.SideToMove;

        // Update state
        EnPassantSquare = -1;
        HalfmoveClock++;
        if (SideToMove == Color.Black)
            FullmoveNumber++;
        SideToMove = SideToMove == Color.White ? Color.Black : Color.White;
    }

    public void UndoNullMove()
    {
        if (_undo.Count == 0) return;
        var info = _undo.Pop();


        // Restore state
        SideToMove = SideToMove == Color.White ? Color.Black : Color.White;
        Castling = info.Castling;
        EnPassantSquare = info.EnPassant;
        HalfmoveClock = info.HalfMoveClock;
        FullmoveNumber = info.FullMoveNumber;
        _zobristKey = info.Hash;
    }

}
