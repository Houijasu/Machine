#!/usr/bin/env pwsh
# A/B Test: PEXT vs Multiply for Magic Bitboards (Improved Version with System Monitoring)

param(
    [int]$Depth = 11,
    [int]$Threads = 1,
    [int]$Runs = 3,
    [string]$Position = "startpos",
    [string]$OutFile = "pext_benchmark_results.txt",
    [double]$MaxCpuLoad = 70.0,  # Maximum CPU load percentage before warning
    [double]$MaxMemoryLoad = 80.0  # Maximum memory load percentage before warning
)

Write-Host "=== Magic Bitboard A/B Test: PEXT vs Multiply ===" -ForegroundColor Cyan
Write-Host "Position: $Position"
Write-Host "Depth: $Depth"
Write-Host "Threads: $Threads"
Write-Host "Runs per mode: $Runs"
Write-Host "Max CPU Load: $MaxCpuLoad%"
Write-Host "Max Memory Load: $MaxMemoryLoad%"
Write-Host ""

# Build first to ensure we have the latest
Write-Host "Building project..." -ForegroundColor Yellow
dotnet build Machine --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

$results = @()

function Get-SystemLoad {
    try {
        # Get CPU load
        $cpuCounter = Get-Counter -Counter "\Processor(_Total)\% Processor Time" -ErrorAction Stop
        $cpuLoad = [math]::Round($cpuCounter.CounterSamples.CookedValue, 2)
        
        # Get memory load
        $memCounter = Get-Counter -Counter "\Memory\% Committed Bytes In Use" -ErrorAction Stop
        $memLoad = [math]::Round($memCounter.CounterSamples.CookedValue, 2)
        
        return @{
            CpuLoad = $cpuLoad
            MemoryLoad = $memLoad
        }
    }
    catch {
        Write-Host "  Warning: Could not get system load: $_" -ForegroundColor Yellow
        return @{
            CpuLoad = 0
            MemoryLoad = 0
        }
    }
}

function Check-SystemLoad {
    param(
        [double]$MaxCpu,
        [double]$MaxMemory
    )
    
    $load = Get-SystemLoad
    Write-Host "  System Load - CPU: $($load.CpuLoad)%, Memory: $($load.MemoryLoad)%" -ForegroundColor Gray
    
    $warnings = @()
    if ($load.CpuLoad -gt $MaxCpu) {
        $warnings += "High CPU load ($($load.CpuLoad)% > $MaxCpu%)"
    }
    if ($load.MemoryLoad -gt $MaxMemory) {
        $warnings += "High Memory load ($($load.MemoryLoad)% > $MaxMemory%)"
    }
    
    if ($warnings.Count -gt 0) {
        Write-Host "  ⚠️  Warning: $($warnings -join '; ')" -ForegroundColor Yellow
        return $false
    }
    
    return $true
}

function Run-Test {
    param(
        [bool]$UsePEXT,
        [int]$TestDepth,
        [int]$TestThreads,
        [string]$TestPosition
    )
    
    $mode = if ($UsePEXT) { "PEXT" } else { "Multiply" }
    Write-Host "`nTesting $mode mode..." -ForegroundColor Green
    
    # Check system load before test
    Write-Host "  Checking system load before test..." -ForegroundColor Gray
    $systemOk = Check-SystemLoad -MaxCpu $MaxCpuLoad -MaxMemory $MaxMemoryLoad
    
    # Prepare UCI commands
    $uciCommands = @"
uci
setoption name Threads value $TestThreads
setoption name UsePEXT value $($(if ($UsePEXT) { "true" } else { "false" }))
position $TestPosition
go depth $TestDepth
quit
"@

    try {
        # Run engine and capture output
        $output = $uciCommands | dotnet run --project Machine -c Release --no-build 2>&1
        
        # Check for errors
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Engine process failed with exit code $LASTEXITCODE" -ForegroundColor Red
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
            if ($line -match "^info.*depth $TestDepth.*nps") {
                $infoLine = $line
            }
            if ($bestmoveFound -and $infoLine) {
                break
            }
            if (((Get-Date) - $startTime).TotalSeconds -gt 300) {
                Write-Host "Timeout!" -ForegroundColor Red
                return $null
            }
        }
        
        if (-not $bestmoveFound) {
            Write-Host "Engine did not return 'bestmove'" -ForegroundColor Red
            return $null
        }
        
        # Parse info line
        if ($infoLine -match "nodes (\d+).*time (\d+).*nps (\d+)") {
            $nodes = [long]$matches[1]
            $time = [int]$matches[2]
            $nps = [long]$matches[3]
            
            return @{
                Mode = $mode
                Nodes = $nodes
                Time = $time
                NPS = $nps
            }
        } else {
            Write-Host "Failed to parse engine output" -ForegroundColor Red
            Write-Host "Last info line: $infoLine" -ForegroundColor Yellow
            return $null
        }
    }
    catch {
        Write-Host "Error running test: $_" -ForegroundColor Red
        return $null
    }
}

# Run tests
$allResults = @()

for ($run = 1; $run -le $Runs; $run++) {
    Write-Host "`n=== Run $run of $Runs ===" -ForegroundColor Cyan
    
    # Test with PEXT
    if ([System.Runtime.Intrinsics.X86.Bmi2+X64]::IsSupported) {
        $pextResult = Run-Test -UsePEXT $true -TestDepth $Depth -TestThreads $Threads -TestPosition $Position
        if ($pextResult) {
            $allResults += $pextResult
            Write-Host "PEXT: $($pextResult.Nodes) nodes @ $($pextResult.NPS) nps"
        }
    } else {
        Write-Host "PEXT not supported on this hardware" -ForegroundColor Yellow
    }
    
    # Test without PEXT  
    $multiplyResult = Run-Test -UsePEXT $false -TestDepth $Depth -TestThreads $Threads -TestPosition $Position
    if ($multiplyResult) {
        $allResults += $multiplyResult
        Write-Host "Multiply: $($multiplyResult.Nodes) nodes @ $($multiplyResult.NPS) nps"
    }
}

# Calculate averages
Write-Host "`n=== Summary ===" -ForegroundColor Cyan

$pextResults = $allResults | Where-Object { $_.Mode -eq "PEXT" }
$multiplyResults = $allResults | Where-Object { $_.Mode -eq "Multiply" }

if ($pextResults.Count -gt 0) {
    $avgPextNodes = ($pextResults | Measure-Object -Property Nodes -Average).Average
    $avgPextNPS = ($pextResults | Measure-Object -Property NPS -Average).Average
    $avgPextTime = ($pextResults | Measure-Object -Property Time -Average).Average
    
    Write-Host "PEXT Average: $([math]::Round($avgPextNodes)) nodes @ $([math]::Round($avgPextNPS)) nps in $([math]::Round($avgPextTime))ms"
}

if ($multiplyResults.Count -gt 0) {
    $avgMultNodes = ($multiplyResults | Measure-Object -Property Nodes -Average).Average
    $avgMultNPS = ($multiplyResults | Measure-Object -Property NPS -Average).Average
    $avgMultTime = ($multiplyResults | Measure-Object -Property Time -Average).Average
    
    Write-Host "Multiply Average: $([math]::Round($avgMultNodes)) nodes @ $([math]::Round($avgMultNPS)) nps in $([math]::Round($avgMultTime))ms"
}

if ($pextResults.Count -gt 0 -and $multiplyResults.Count -gt 0) {
    $speedup = ($avgPextNPS / $avgMultNPS - 1) * 100
    Write-Host "`nPEXT Speedup: $([math]::Round($speedup, 1))%" -ForegroundColor Green
}

# Save results
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$output = @"
=== Magic Bitboard Benchmark Results ===
Timestamp: $timestamp
Position: $Position
Depth: $Depth
Threads: $Threads
Runs: $Runs
Max CPU Load: $MaxCpuLoad%
Max Memory Load: $MaxMemoryLoad%

Raw Results:
$($allResults | Format-Table -AutoSize | Out-String)

Averages:
"@

if ($pextResults.Count -gt 0) {
    $output += "PEXT: $([math]::Round($avgPextNodes)) nodes @ $([math]::Round($avgPextNPS)) nps`n"
}
if ($multiplyResults.Count -gt 0) {
    $output += "Multiply: $([math]::Round($avgMultNodes)) nodes @ $([math]::Round($avgMultNPS)) nps`n"
}
if ($pextResults.Count -gt 0 -and $multiplyResults.Count -gt 0) {
    $output += "Speedup: $([math]::Round($speedup, 1))%`n"
}

$output | Out-File -FilePath $OutFile -Append
Write-Host "`nResults saved to $OutFile" -ForegroundColor Yellow