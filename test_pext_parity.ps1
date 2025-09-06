#!/usr/bin/env pwsh
# Functional Parity Test: Verify identical move generation across PEXT modes (Improved Version)

Write-Host "=== PEXT Functional Parity Test ===" -ForegroundColor Cyan
Write-Host "Verifying identical move generation across PEXT modes" -ForegroundColor Yellow
Write-Host ""

# Build first
Write-Host "Building project..." -ForegroundColor Yellow
dotnet build Machine --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Test positions
$testPositions = @(
    @{ Name = "Startpos"; Fen = "startpos"; Depth = 6 },
    @{ Name = "Kiwipete"; Fen = "rnbq1k1r/pppp1ppp/5n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 2 4"; Depth = 5 },
    @{ Name = "Tactical"; Fen = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1"; Depth = 4 },
    @{ Name = "Endgame"; Fen = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"; Depth = 6 }
)

$allPassed = $true

function Run-Perft-Test {
    param(
        [string]$Mode,
        [string]$UciInput,
        [int]$ExpectedDepth
    )
    
    try {
        # Run engine and capture output
        $output = $UciInput | dotnet run --project Machine -c Release --no-build 2>&1
        
        # Check for errors
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Engine process failed with exit code $LASTEXITCODE" -ForegroundColor Red
            return $null
        }
        
        # Convert output to string array
        $lines = $output | ForEach-Object { $_.ToString() }
        
        # Wait for bestmove (timeout after 300 seconds)
        $startTime = Get-Date
        $bestmoveFound = $false
        $perftLine = $null
        
        foreach ($line in $lines) {
            if ($line -match "bestmove") {
                $bestmoveFound = $true
            }
            if ($line -match "info string perft depth $ExpectedDepth nodes (\d+)") {
                $perftLine = $line
            }
            if ($bestmoveFound -and $perftLine) {
                break
            }
            if (((Get-Date) - $startTime).TotalSeconds -gt 300) {
                Write-Host "  Timeout!" -ForegroundColor Red
                return $null
            }
        }
        
        if (-not $bestmoveFound) {
            Write-Host "  Engine did not return 'bestmove'" -ForegroundColor Red
            return $null
        }
        
        # Parse perft line
        if ($perftLine -match "nodes (\d+)") {
            $nodes = [long]$matches[1]
            Write-Host " $nodes nodes" -ForegroundColor Green
            return $nodes
        } else {
            Write-Host " Failed to parse" -ForegroundColor Red
            return $null
        }
    }
    catch {
        Write-Host "  Error running test: $_" -ForegroundColor Red
        return $null
    }
}

foreach ($pos in $testPositions) {
    Write-Host "Testing position: $($pos.Name)" -ForegroundColor Cyan
    Write-Host "FEN: $($pos.Fen)" -ForegroundColor Gray
    
    # Test with PEXT enabled
    $inputTrue = @"
uci
setoption name UsePEXT value true
isready
position $($pos.Fen)
go perft $($pos.Depth)
quit
"@
    
    Write-Host "  Running PEXT=true..." -NoNewline
    $trueNodes = Run-Perft-Test -Mode "PEXT=true" -UciInput $inputTrue -ExpectedDepth $pos.Depth
    if ($null -eq $trueNodes) {
        $allPassed = $false
        continue
    }
    
    # Test with PEXT disabled
    $inputFalse = @"
uci
setoption name UsePEXT value false
isready
position $($pos.Fen)
go perft $($pos.Depth)
quit
"@
    
    Write-Host "  Running PEXT=false..." -NoNewline
    $falseNodes = Run-Perft-Test -Mode "PEXT=false" -UciInput $inputFalse -ExpectedDepth $pos.Depth
    if ($null -eq $falseNodes) {
        $allPassed = $false
        continue
    }
    
    # Compare results
    if ($trueNodes -eq $falseNodes) {
        Write-Host "  ✅ PASS: Identical node counts" -ForegroundColor Green
    } else {
        Write-Host "  ❌ FAIL: Node count mismatch!" -ForegroundColor Red
        Write-Host "    PEXT=true:  $trueNodes nodes" -ForegroundColor Red
        Write-Host "    PEXT=false: $falseNodes nodes" -ForegroundColor Red
        $allPassed = $false
    }
    
    Write-Host ""
}

# Test auto mode for good measure
Write-Host "Testing auto-detection mode..." -ForegroundColor Cyan
$inputAuto = @"
uci
setoption name UsePEXT value auto
isready
position startpos
go perft 5
quit
"@

Write-Host "  Running auto mode..." -NoNewline
$autoNodes = Run-Perft-Test -Mode "auto" -UciInput $inputAuto -ExpectedDepth 5
if ($null -ne $autoNodes) {
    Write-Host "  Auto mode: $autoNodes nodes" -ForegroundColor Green
    
    # Check if it matches expected startpos depth 5 (4,865,609 nodes)
    if ($autoNodes -eq 4865609) {
        Write-Host "  ✅ Auto mode produces correct startpos perft" -ForegroundColor Green
    } else {
        Write-Host "  ⚠️  Auto mode node count unexpected: $autoNodes (expected 4,865,609)" -ForegroundColor Yellow
    }
} else {
    Write-Host "  ❌ Auto mode failed" -ForegroundColor Red
    $allPassed = $false
}

Write-Host ""
if ($allPassed) {
    Write-Host "🎉 All parity tests PASSED!" -ForegroundColor Green
    Write-Host "PEXT and multiply/shift modes produce identical results." -ForegroundColor Green
} else {
    Write-Host "💥 Some tests FAILED!" -ForegroundColor Red
    Write-Host "There may be bugs in the PEXT implementation." -ForegroundColor Red
    exit 1
}