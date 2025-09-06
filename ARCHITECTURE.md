# Machine Chess Engine Architecture

## Overview

Machine is a high-performance UCI-compatible chess engine built with .NET 10.0. The architecture is designed for maximum performance while maintaining code clarity and maintainability.

## High-Level Architecture

```
┌─────────────────────┐
│     UCI Protocol    │
│   (Input/Output)    │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│    Search Engine    │
│  (Alpha-Beta with   │
│   enhancements)     │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐    ┌─────────────────────┐
│   Move Generation   │    │ Transposition Table │
│   (Magic Bitboards) │    │   (Hash Table)      │
└─────────┬───────────┘    └─────────┬───────────┘
          │                          │
          ▼                          ▼
┌─────────────────────┐    ┌─────────────────────┐
│   Board Position    │    │   Evaluation Cache  │
│   Representation    │    │   (Pawn Structure)  │
└─────────────────────┘    └─────────────────────┘
```

## Component Details

### 1. UCI Protocol Handler
- Handles communication with chess GUIs
- Parses UCI commands and parameters
- Manages engine options and configuration
- Formats and sends search results

### 2. Search Engine
- Implements Alpha-Beta search with enhancements:
  - Aspiration windows
  - Null move pruning
  - Late move reductions
  - ProbCut
  - Singular extensions
- Supports parallel search with Work-Stealing and LazySMP
- Manages iterative deepening
- Handles time management and search limits

### 3. Move Generation
- Uses magic bitboards for efficient sliding piece attacks
- Implements staged move generation
- Includes move ordering heuristics:
  - Transposition table moves
  - Killer moves
  - Counter moves
  - History heuristic
  - MVV-LVA scoring

### 4. Transposition Table
- Hash table for storing previously searched positions
- Implements ABDADA protocol for parallel search
- Uses depth-weighted aging to preserve valuable entries
- Includes two-tier structure (main table + pawn hash table)
- Provides detailed statistics for performance tuning

### 5. Board Position Representation
- Uses bitboards for efficient board representation
- Implements Zobrist hashing for position identification
- Handles move application and undo efficiently
- Manages castling rights, en passant, and other chess rules

### 6. Evaluation Cache
- Caches pawn structure evaluations
- Uses separate aging mechanism
- Provides fast access to frequently used evaluations
- Reduces redundant calculations

## Data Flow

1. **Search Initiation**: UCI handler receives "go" command and passes parameters to Search Engine
2. **Iterative Deepening**: Search Engine performs search at increasing depths
3. **Move Generation**: For each position, Move Generation component generates and orders moves
4. **Position Evaluation**: Board Position and Evaluation Cache provide static evaluations
5. **Transposition Table Lookup**: Search Engine checks TT for previously searched positions
6. **Pruning and Extensions**: Search Engine applies various pruning techniques and extensions
7. **Parallel Search**: Work-Stealing or LazySMP distributes work across threads
8. **Result Reporting**: Search Engine returns best move and score to UCI handler

## Class Relationships

```
UCIProtocol
    └── SearchEngine
        ├── AlphaBeta (static class)
        ├── MoveOrdering (static class)
        ├── TranspositionTable
        ├── PawnHashTable
        ├── EvalCache
        ├── Position
        └── MoveGenerator
```

## Parallel Search Architecture

Machine supports two parallel search algorithms:

### Work-Stealing
- Master thread generates root moves
- Worker threads "steal" work from other threads
- Uses ABDADA protocol for position reservation
- Good for positions with many root moves

### LazySMP
- Multiple threads search same position independently
- Shares transposition table for coordination
- Simpler implementation, good for most positions
- Can be faster than Work-Stealing in some scenarios

## Performance Considerations

- Cache-friendly data structures
- Minimal memory allocations during search
- Aggressive inlining of hot paths
- Prefetching for critical data access
- Lock-free synchronization where possible