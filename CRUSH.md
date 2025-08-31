# CRUSH.md

## Build & Test Commands
```bash
# Build entire solution
dotnet build

# Run main chess engine (UCI mode)
dotnet run --project Machine

# Run with perft validation
dotnet run --project Machine -- --perft 3
dotnet run --project Machine -- --divide 3

# Run perft comparison tool
dotnet run --project Machine.Tools

# Run specific test class
dotnet test --filter "PerftStartposTests"

# Run specific test method
dotnet test --filter "Startpos_Depth1_Is20"

# Run all tests
dotnet test
```

## Code Style Guidelines

### Project Configuration
- Target Framework: .NET 10.0
- LangVersion: preview
- ImplicitUsings: enable
- Nullable: enable

### Coding Conventions
- Use PascalCase for public members (fields, properties, methods)
- Use camelCase for private/internal members
- Structs for performance-critical types (Move, etc.)
- Use [MethodImpl(MethodImplOptions.AggressiveInlining)] for hot paths
- Enums with explicit backing types when precision matters
- Use switch expressions for pattern matching
- Struct types should be readonly when possible
- Use file-scoped namespace declarations
- Prefer exception-based validation (throw Exception with message)

### Testing
- Use TUnit framework with [Test] attribute
- Test method names: Scenario_ExpectedValue_ActualValue()
- Simple exception-based assertions
- Perft validation tests are critical for correctness

### Performance
- Avoid LINQ in hot code paths
- Use Array<T> instead of List<T> for fixed-size collections
- Minimize allocations in move generation and search
- Use bitboard representation for performance