# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Machine is a state-of-the-art UCI-compatible chess engine written in C# targeting .NET 10.0. The engine is optimized for deep chess position analysis rather than time-controlled play, implementing advanced bitboard-based move generation with magic bitboards and comprehensive perft validation.

## Essential Commands

### Build and Run
```bash
# Build the project
dotnet build

# Run the main chess engine (UCI mode)
dotnet run --project Machine

# Run with specific perft depth for validation
dotnet run --project Machine -- --perft 3

# Run divide mode to see per-move node counts
dotnet run --project Machine -- --divide 3
```

### Perft Validation and Testing
```bash
# Compare engine perft results with Stockfish
dotnet run --project Machine.Tools -- --startpos --depth 3 --divide

# Test specific positions with comparator
dotnet run --project Machine.Tools -- --fen "rnbq1k1r/pppp1ppp/5n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 2 4" --depth 4 --divide

# Run tests (when available)
dotnet test
```

### Performance Testing
The engine includes extensive BenchmarkDotNet integration for performance analysis.

### Parallel Search Configuration
```bash
# Use Work-Stealing (default for multi-threaded)
dotnet run --project Machine
# Then: setoption name Threads value 4

# Force LazySMP mode (kill switch)
# Then: setoption name UseLazySMP value true

# Enable Work-Stealing metrics
# Then: setoption name WorkStealing_ShowMetrics value true

# Tune Work-Stealing thresholds
# Then: setoption name WorkStealing_MinSplitDepth value 6
# Then: setoption name WorkStealing_MinSplitMoves value 5
```

## Core Architecture

### Bitboard-Based Representation
The engine uses a sophisticated bitboard representation with separate bitboards for each piece type and color:
- `Position.PieceBB[12]`: Indexed as 0-5 for white pieces (P,N,B,R,Q,K), 6-11 for black
- `Position.Occupancy[2]`: Combined occupancy bitboards for white/black
- All position state including castling rights, en passant, and move clocks

### Magic Bitboards System
Located in `MoveGen/`, the engine implements:
- **Runtime magic generation**: Uses Stockfish-style algorithm to compute magic multipliers at startup
- **Precomputed attack tables**: Fast sliding piece attack lookups via magic indexing
- **Software PEXT fallback**: Ensures correctness when magic computation fails

### Move Generation Pipeline
`MoveGenerator.GenerateMoves()` produces pseudo-legal moves in optimized order:
1. Knights and King (simple piece moves)
2. Pawn pushes (single/double with promotion handling)  
3. Pawn captures (including en passant validation)
4. Sliding pieces (bishops, rooks, queens via magic lookups)
5. Castling (with through-check validation)

### Legality Filtering
The `Perft` class implements legal move filtering by:
- Applying each pseudo-legal move
- Checking if the moving side's king is in check via `Position.IsKingInCheck()`
- Undoing illegal moves during perft traversal

### UCI Protocol Implementation
`UCIProtocol.cs` provides full UCI compliance with analysis-specific extensions:
- Standard UCI commands (uci, isready, position, go, quit)
- Extended perft commands: `perft N`, `go perft N`, `divide N`
- Multi-threaded safe communication

## Key Implementation Details

### Move Representation
Moves are encoded as structs with source/destination squares and move flags for special moves (castling, en passant, promotions).

### Make/Unmake System
`Position.ApplyMove()/UndoMove()` implements complete game state transitions:
- Bitboard updates with piece movement
- Special move handling (EP capture, castling rook moves, promotions)
- State restoration via `UndoInfo` stack
- Castling rights and en passant square management

### Attack Detection
`Position.IsSquareAttacked()` efficiently determines if a square is attacked by:
- Pawn diagonal attacks (direction-dependent)
- Knight L-shaped attacks via precomputed tables
- King adjacent square attacks
- Sliding piece attacks through magic bitboard lookups

## Development Tools

### Perft Comparator (`Machine.Tools`)
Critical for validating move generation correctness:
- Spawns both Stockfish and Machine engines
- Compares perft node counts at specified depths
- Provides detailed divide analysis showing move-by-move differences
- Essential for debugging move generation bugs

### Perft Validation Targets
Standard positions for correctness validation:
- **Startpos**: depth 3 = 8,902 nodes (verified perfect)
- **Kiwipete**: `rnbq1k1r/pppp1ppp/5n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 2 4`
- Additional tactical positions for edge case testing

## Current Status

### Move Generation
The engine achieves **perfect perft validation** through depth 6:
- Depth 1: 20 nodes ✅
- Depth 2: 400 nodes ✅  
- Depth 3: 8,902 nodes ✅
- Depth 6: 119,060,324 nodes ✅

All core chess rules are correctly implemented including castling through check, en passant legality, and promotion handling.

### Parallel Search Performance
**Work-Stealing** is now the default for multi-threaded analysis, delivering superior performance:
- **4 threads**: 18.3M nodes @ 4.06M nps (2.7x more nodes than LazySMP)
- **8 threads**: 17.9M nodes @ 4.20M nps (excellent scaling)
- Features deepest-first selection, batch stealing, and efficient Apply/Undo

**LazySMP** remains available as an alternative:
- **4 threads**: 6.8M nodes @ 1.26M nps
- **16 threads**: 21M nodes (good for very high thread counts)
- Near-zero duplication with optimized settings (AspirationDelta=20)

## Framework and Dependencies

- **Target**: .NET 10.0 with preview language features
- **Testing**: TUnit framework for unit testing
- **Performance**: BenchmarkDotNet for micro-benchmarking
- **ML Integration**: Microsoft.ML packages (prepared for future NNUE evaluation)
- **Console UI**: Spectre.Console for enhanced output

## Project Structure

- `Machine/Core/`: Position representation, moves, bitboards, types
- `Machine/MoveGen/`: Magic bitboards, attack tables, move generation
- `Machine/Search/`: Perft validation and future search algorithms  
- `Machine/UCI/`: UCI protocol implementation
- `Machine.Tools/`: Perft comparison utilities

The codebase prioritizes correctness and maintainability, with comprehensive move validation ensuring chess rule compliance for analysis applications.