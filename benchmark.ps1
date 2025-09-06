# Machine Chess Engine - Performance Benchmark Script
# Tests scaling across different thread counts and modes

Write-Host "`nMachine Chess Engine - Performance Benchmark" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$testDepth = 10
$testPosition = "startpos"
$hashSize = 512

# Thread counts to test
$threadCounts = @(1, 2, 4, 8)

# Results storage
$results = @()

Write-Host "`nTest Configuration:" -ForegroundColor Yellow
Write-Host "  Position: $testPosition"
Write-Host "  Depth: $testDepth"
Write-Host "  Hash: ${hashSize}MB"
Write-Host ""

# Function to run a single test
function Run-Test {
    param(
        [int]$threads,
        [bool]$workStealing
    )
    
    $mode = if ($workStealing) { "Work-Stealing" } else { "LazySMP" }
    $wsValue = if ($workStealing) { "true" } else { "false" }
    
    Write-Host "Testing $mode with $threads thread(s)..." -ForegroundColor Green
    
    $input = @"
uci
setoption name Hash value $hashSize
setoption name Threads value $threads
setoption name WorkStealing value $wsValue
isready
position $testPosition
go depth $testDepth
quit
"@
    
    $output = $input | dotnet run --project Machine -c Release --no-build 2>&1
    
    # Parse the final info line for nodes and time
    $lastInfo = $output | Where-Object { $_ -match "^info depth $testDepth" } | Select-Object -Last 1
    
    if ($lastInfo -match "nodes (\d+)") {
        $nodes = [long]$matches[1]
    } else {
        $nodes = 0
    }
    
    if ($lastInfo -match "time (\d+)") {
        $timeMs = [int]$matches[1]
    } else {
        $timeMs = 1000
    }
    
    if ($lastInfo -match "nps (\d+)") {
        $nps = [long]$matches[1]
    } else {
        $nps = if ($timeMs -gt 0) { [long]($nodes * 1000 / $timeMs) } else { 0 }
    }
    
    return @{
        Mode = $mode
        Threads = $threads
        Nodes = $nodes
        TimeMs = $timeMs
        NPS = $nps
    }
}

# Build first to ensure we're testing latest
Write-Host "Building Release configuration..." -ForegroundColor Yellow
dotnet build Machine -c Release | Out-Null

Write-Host "`nRunning benchmarks..." -ForegroundColor Yellow
Write-Host ""

# Test Work-Stealing for all thread counts
foreach ($threads in $threadCounts) {
    if ($threads -eq 1) {
        # Single thread - no parallelism needed
        $result = Run-Test -threads $threads -workStealing $false
        $result.Mode = "Single-thread"
    } else {
        $result = Run-Test -threads $threads -workStealing $true
    }
    $results += $result
    Start-Sleep -Seconds 1
}

# Also test LazySMP for comparison at 4 threads
if (4 -in $threadCounts) {
    Write-Host "Testing LazySMP with 4 threads for comparison..." -ForegroundColor Green
    $result = Run-Test -threads 4 -workStealing $false
    $results += $result
}

# Display results table
Write-Host "`n`nBenchmark Results:" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan
Write-Host ""

$results | Format-Table -Property @(
    @{Label="Mode"; Expression={$_.Mode}; Width=15},
    @{Label="Threads"; Expression={$_.Threads}; Width=8},
    @{Label="Nodes"; Expression={"{0:N0}" -f $_.Nodes}; Width=15},
    @{Label="Time (ms)"; Expression={$_.TimeMs}; Width=10},
    @{Label="NPS"; Expression={"{0:N0}" -f $_.NPS}; Width=15}
) -AutoSize

# Calculate speedups
$baseline = $results | Where-Object { $_.Threads -eq 1 } | Select-Object -First 1
if ($baseline) {
    Write-Host "`nSpeedup Analysis (vs single-thread):" -ForegroundColor Cyan
    Write-Host ""
    
    foreach ($result in $results | Where-Object { $_.Threads -gt 1 }) {
        $speedup = [math]::Round($result.Nodes / $baseline.Nodes, 2)
        $efficiency = [math]::Round($speedup / $result.Threads * 100, 1)
        Write-Host ("  {0,-15} {1,2} threads: {2,5}x speedup ({3,5}% efficiency)" -f 
            $result.Mode, $result.Threads, $speedup, $efficiency)
    }
}

Write-Host "`n✅ Benchmark complete!" -ForegroundColor Green