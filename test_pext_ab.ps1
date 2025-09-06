#!/usr/bin/env pwsh
# Comprehensive A/B Test: PEXT vs Multiply (Improved Version)

Write-Host "`n=== PEXT vs Multiply A/B Benchmark ===" -ForegroundColor Cyan
Write-Host "Configuration:"
Write-Host "  Threads: 8"
Write-Host "  Hash: 1024 MB"
Write-Host "  Depth: 12"
Write-Host ""

# Check PEXT support
$pextSupported = [System.Runtime.Intrinsics.X86.Bmi2+X64]::IsSupported
if (-not $pextSupported) {
    Write-Host "⚠️  PEXT not supported on this hardware" -ForegroundColor Yellow
    Write-Host "Only Multiply mode will be tested" -ForegroundColor Yellow
    Write-Host ""
}

# Common UCI settings
$common = @"
uci
setoption name Threads value 8
setoption name Hash value 1024
setoption name PawnHash value 16
setoption name EvalCache value 32
setoption name WorkStealing value true
setoption name WorkStealing_MinSplitDepth value 3
setoption name WorkStealing_MinSplitMoves value 3
setoption name HistoryPruning_Threshold value 100
"@

$results = @()

function Run-Test {
    param(
        [string]$Mode,  # "PEXT" or "Multiply"
        [string]$UciInput
    )
    
    Write-Host "Running with $Mode mode..." -ForegroundColor Green
    
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
        $infoLine = $null
        
        foreach ($line in $lines) {
            if ($line -match "bestmove") {
                $bestmoveFound = $true
            }
            if ($line -match "^info depth 12.*nps") {
                $infoLine = $line
            }
            if ($bestmoveFound -and $infoLine) {
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
        
        # Parse info line
        if ($infoLine -match "time (\d+) nodes (\d+) nps (\d+)") {
            $time = [int]$matches[1]
            $nodes = [long]$matches[2]
            $nps = [long]$matches[3]
            
            Write-Host "  ${Mode}: $nodes nodes @ $nps nps ($time ms)" -ForegroundColor Green
            return @{
                Mode = $Mode
                Nodes = $nodes
                Time = $time
                NPS = $nps
            }
        } else {
            Write-Host "  Failed to parse engine output" -ForegroundColor Red
            Write-Host "  Last info line: $infoLine" -ForegroundColor Yellow
            return $null
        }
    }
    catch {
        Write-Host "  Error running test: $_" -ForegroundColor Red
        return $null
    }
}

# Run 3 iterations
for ($i = 1; $i -le 3; $i++) {
    Write-Host "`n=== Iteration $i of 3 ===" -ForegroundColor Yellow
    
    $pextResult = $null
    $multResult = $null
    
    # Test with PEXT if supported
    if ($pextSupported) {
        $pextInput = $common + @"
setoption name UsePEXT value true
isready
position startpos
go depth 12
quit
"@
        $pextResult = Run-Test -Mode "PEXT" -UciInput $pextInput
    }
    
    # Test without PEXT
    $multInput = $common + @"
setoption name UsePEXT value false
isready
position startpos
go depth 12
quit
"@
    $multResult = Run-Test -Mode "Multiply" -UciInput $multInput
    
    # Calculate speedup for this iteration
    if ($pextResult -and $multResult) {
        $speedup = [math]::Round(($pextResult.NPS / $multResult.NPS - 1) * 100, 1)
        Write-Host "  Speedup: $speedup%" -ForegroundColor Magenta
        
        $results += [PSCustomObject]@{
            Iteration = $i
            PEXT_Nodes = $pextResult.Nodes
            PEXT_NPS = $pextResult.NPS
            PEXT_Time = $pextResult.Time
            Mult_Nodes = $multResult.Nodes
            Mult_NPS = $multResult.NPS
            Mult_Time = $multResult.Time
            Speedup = $speedup
        }
    }
}

# Summary
if ($results.Count -gt 0) {
    Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
    
    $avgPextNPS = ($results | Measure-Object -Property PEXT_NPS -Average).Average
    $avgMultNPS = ($results | Measure-Object -Property Mult_NPS -Average).Average
    $avgSpeedup = ($results | Measure-Object -Property Speedup -Average).Average
    
    Write-Host "Average PEXT NPS: $([math]::Round($avgPextNPS))"
    Write-Host "Average Multiply NPS: $([math]::Round($avgMultNPS))"
    Write-Host "Average Speedup: $([math]::Round($avgSpeedup, 1))%" -ForegroundColor $(if ($avgSpeedup -gt 0) { "Green" } else { "Red" })
    
    Write-Host "`nDetailed Results:"
    $results | Format-Table -AutoSize
    
    # Save to file
    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $filename = "pext_benchmark_$timestamp.txt"
    $results | ConvertTo-Json | Out-File $filename
    Write-Host "`nResults saved to $filename" -ForegroundColor Yellow
}