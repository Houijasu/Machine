# Project Overview

This is a .NET console application that appears to be a chess engine. It uses the Universal Chess Interface (UCI) protocol for communication. The project also includes performance testing capabilities using `BenchmarkDotNet`.

**Key Technologies:**

*   .NET 10
*   Microsoft.ML
*   BenchmarkDotNet
*   Spectre.Console

**Architecture:**

*   The main entry point is in `Program.cs`.
*   The application can be run in two modes:
    *   UCI mode: For interacting with a chess GUI.
    *   Performance testing mode: For running performance tests on the chess engine's move generation.

# Building and Running

**Building:**

To build the project, use the `dotnet build` command:

```shell
dotnet build
```

**Running:**

To run the application in UCI mode:

```shell
dotnet run --project Machine/Machine.csproj
```

To run the performance tests:

```shell
dotnet run --project Machine/Machine.csproj -- --perft <depth>
```

or

```shell
dotnet run --project Machine/Machine.csproj -- --divide <depth>
```

Replace `<depth>` with the desired search depth for the performance test.

# Development Conventions

*   The project uses C# 11 with nullable enabled.
*   Dependencies are managed using NuGet packages.
*   The project follows the standard .NET project structure.
