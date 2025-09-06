# Machine

A high-performance UCI-compatible chess engine built with .NET 10.0.

## Overview

Machine is a chess engine that implements the Universal Chess Interface (UCI) protocol, featuring advanced parallel search algorithms and optimized move generation. The engine is designed for both position analysis and competitive play.

## Features

- **UCI Protocol Compliance**: Full implementation of the UCI standard for compatibility with all major chess GUIs
- **Parallel Search**: Work-Stealing and LazySMP algorithms for multi-threaded position analysis
- **Magic Bitboards**: Efficient sliding piece attack generation using precomputed lookup tables
- **Transposition Table**: Advanced hash table with ABDADA support for parallel search optimization
- **Move Ordering**: Killer moves, history heuristic, and MVV-LVA for optimal search efficiency
- **Pruning Techniques**: Null move, futility, razoring, and late move reductions
- **Perft Validation**: Complete move generation validation suite

## Prerequisites

- .NET 10.0 SDK or later
- Any C# IDE (Visual Studio, Rider, VS Code)

## Installation

```bash
# Clone the repository
git clone https://github.com/yourusername/machine.git
cd machine

# Build the project
dotnet build -c Release
```

## Usage

### Command Line Interface

```bash
# Start the engine in UCI mode
dotnet run --project Machine

# Run perft validation suite
dotnet run --project Machine -- --perft 6

# Run perft with move breakdown
dotnet run --project Machine -- --divide 5
```

### UCI Protocol

The engine communicates using the standard UCI protocol:

```
uci                              # Initialize engine
setoption name Threads value 4   # Configure threads
setoption name Hash value 256    # Set hash table size (MB)
position startpos                # Set position
go depth 10                      # Start analysis
quit                            # Exit engine
```

## Configuration

### Engine Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Threads` | Integer | 1 | Number of search threads (1-32) |
| `Hash` | Integer | 16 | Transposition table size in MB (16-8192) |
| `WorkStealing` | Boolean | true | Enable Work-Stealing parallel search |
| `UseLazySMP` | Boolean | false | Use LazySMP algorithm instead of Work-Stealing |
| `DebugInfo` | Boolean | false | Enable debug instrumentation |
| `TTInfo` | Boolean | false | Display transposition table statistics |

### Advanced Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `WorkStealing_MinSplitDepth` | Integer | 6 | Minimum depth for work splitting |
| `WorkStealing_MinSplitMoves` | Integer | 5 | Minimum moves to allow splitting |
| `WorkStealing_ShowMetrics` | Boolean | false | Display work-stealing metrics |

## Development

### Project Structure

```
Machine/
├── Core/           # Board representation and move structures
├── MoveGen/        # Move generation and attack tables
├── Search/         # Search algorithms and parallel implementation
├── Tables/         # Transposition table implementation
├── UCI/            # UCI protocol handler
└── Program.cs      # Application entry point

Machine.Tools/
└── PerfComparator.cs   # Perft validation utilities
```

### Testing

The project includes comprehensive test suites for move generation validation:

```bash
# Run perft validation
dotnet run --project Machine -- --perft 6

# Compare with reference engine
dotnet run --project Machine.Tools -- --startpos --depth 5 --divide
```

## Building from Source

### Requirements
- .NET 10.0 SDK or later
- C# 13.0 language features

### Build Configurations
- `Debug`: Development build with assertions and debug symbols
- `Release`: Optimized build for production use

```bash
# Debug build
dotnet build

# Release build
dotnet build -c Release

# Run tests
dotnet test
```

## Deployment

The engine can be deployed as a standalone executable:

```bash
# Create self-contained executable
dotnet publish -c Release -r win-x64 --self-contained

# Create framework-dependent executable
dotnet publish -c Release
```

## Performance Tuning Guide

### Hardware Optimization

1. **CPU**: Machine performs best on modern CPUs with good single-thread performance. Intel i7/i9 or AMD Ryzen 7/9 series are recommended.
2. **Memory**: Allocate sufficient hash table size based on available RAM:
   - 16GB RAM: 2-4GB hash
   - 32GB RAM: 8-16GB hash
   - 64GB+ RAM: 32GB+ hash
3. **PEXT vs Multiply/Shift**: Machine automatically benchmarks and selects the fastest method, but you can force a specific method:
   ```bash
   setoption name UsePEXT value auto    # Auto-detect (recommended)
   setoption name UsePEXT value true    # Force PEXT (if supported)
   setoption name UsePEXT value false   # Force Multiply/Shift
   ```

### Configuration Recommendations

#### For Analysis (Chess GUIs)
```
setoption name Hash value 8192
setoption name Threads value 4
setoption name MultiPV value 3
setoption name Contempt value 20
```

#### For Tournament Play
```
setoption name Hash value 32768
setoption name Threads value 8
setoption name Contempt value 50
setoption name UseLazySMP value false
```

#### For Low-End Systems
```
setoption name Hash value 256
setoption name Threads value 1
setoption name UseCounterMoves value false
setoption name HistoryPruning value true
```

### Benchmarking Guidelines

1. **Use the built-in benchmark tool**:
   ```bash
   dotnet run --project Machine -- --benchmark
   ```

2. **Compare different configurations**:
   ```bash
   # Test different thread counts
   setoption name Threads value 1
   go depth 10
   setoption name Threads value 4
   go depth 10
   ```

3. **Monitor performance metrics**:
   - NPS (Nodes Per Second)
   - Hash full percentage
   - TT hit rate

### Troubleshooting Common Issues

1. **Low NPS**:
   - Reduce hash size if system is swapping
   - Try different PEXT settings
   - Reduce thread count

2. **High memory usage**:
   - Reduce hash size
   - Disable debug options
   - Use release build

3. **Inconsistent results**:
   - Ensure consistent starting position
   - Check for hardware issues (overheating, etc.)
   - Run with debug info enabled to identify issues

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For bug reports and feature requests, please use the GitHub issue tracker.

## Additional Documentation

For detailed architecture information, see [ARCHITECTURE.md](ARCHITECTURE.md).