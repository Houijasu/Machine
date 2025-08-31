namespace Machine.Core;

public enum Color : int
{
    White = 0,
    Black = 1
}

public enum PieceType : int
{
    None = 0,
    Pawn = 1,
    Knight = 2,
    Bishop = 3,
    Rook = 4,
    Queen = 5,
    King = 6
}

public enum MoveFlag : int
{
    None = 0,
    Quiet = 1,
    Capture = 2,
    DoublePush = 3,
    KingCastle = 4,
    QueenCastle = 5,
    EnPassant = 6,
    PromoKnight = 7,
    PromoBishop = 8,
    PromoRook = 9,
    PromoQueen = 10,
    PromoCaptureKnight = 11,
    PromoCaptureBishop = 12,
    PromoCaptureRook = 13,
    PromoCaptureQueen = 14,
}

public enum Square : int
{
    A1 = 0, B1, C1, D1, E1, F1, G1, H1,
    A2, B2, C2, D2, E2, F2, G2, H2,
    A3, B3, C3, D3, E3, F3, G3, H3,
    A4, B4, C4, D4, E4, F4, G4, H4,
    A5, B5, C5, D5, E5, F5, G5, H5,
    A6, B6, C6, D6, E6, F6, G6, H6,
    A7, B7, C7, D7, E7, F7, G7, H7,
    A8, B8, C8, D8, E8, F8, G8, H8
}
[Flags]
public enum CastlingRights : byte
{
    None = 0,
    WhiteKing = 1,
    WhiteQueen = 2,
    BlackKing = 4,
    BlackQueen = 8,
    All = WhiteKing | WhiteQueen | BlackKing | BlackQueen
}


