#!/usr/bin/env pwsh
# Test the new PEXT auto-selection feature (Improved Version)

Write-Host "=== Testing PEXT Auto-Selection Feature ===" -ForegroundColor Cyan
Write-Host ""

# Build the project first
Write-Host "Building project..." -ForegroundColor Yellow
dotnet build Machine --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Check PEXT support
$pextSupported = [System.Runtime.Intrinsics.X86.Bmi2+X64]::IsSupported
if (-not $pextSupported) {
    Write-Host "⚠️  PEXT not supported on this hardware" -ForegroundColor Yellow
    Write-Host ""
}

# Test each PEXT mode
$modes = @("false", "auto", "true")

function Run-Test {
    param(
        [string]$Mode
    )
    
    Write-Host "Testing PEXT mode: $Mode" -ForegroundColor Cyan
    
    try {
        $input = @"
uci
setoption name UsePEXT value $Mode
setoption name Threads value 1
isready
position startpos
go depth 8
quit
"@
        
        # Run engine and capture output
        $output = $input | dotnet run --project Machine -c Release --no-build 2>&1
        
        # Check for errors
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Engine process failed with exit code $LASTEXITCODE" -ForegroundColor Red
            return $false
        }
        
        # Convert output to string array
        $lines = $output | ForEach-Object { $_.ToString() }
        
        # Wait for bestmove (timeout after 300 seconds)
        $startTime = Get-Date
        $bestmoveFound = $false
        $perfInfo = $null
        $pextInfo = $null
        $autoInfo = $null
        
        foreach ($line in $lines) {
            if ($line -match "bestmove") {
                $bestmoveFound = $true
            }
            if ($line -match "info string PEXT mode:") {
                $pextInfo = $line
            }
            if ($line -match "info string Magic indexing auto-detection:") {
                $autoInfo = $line
            }
            if ($line -match "info depth 8.*nps") {
                $perfInfo = $line
            }
            if ($bestmoveFound -and $perfInfo) {
                break
            }
            if (((Get-Date) - $startTime).TotalSeconds -gt 300) {
                Write-Host "  Timeout!" -ForegroundColor Red
                return $false
            }
        }
        
        if (-not $bestmoveFound) {
            Write-Host "  Engine did not return 'bestmove'" -ForegroundColor Red
            return $false
        }
        
        # Display PEXT mode info
        if ($pextInfo) {
            Write-Host "  $pextInfo" -ForegroundColor Green
        }
        
        # Display auto-detection info if in auto mode
        if ($Mode -eq "auto" -and $autoInfo) {
            Write-Host "  $autoInfo" -ForegroundColor Yellow
        }
        
        # Display performance info
        if ($perfInfo -match "time (\d+) nodes (\d+) nps (\d+)") {
            $time = [int]$matches[1]
            $nodes = [long]$matches[2]
            $nps = [long]$matches[3]
            Write-Host "  Performance: $nodes nodes @ $nps nps ($time ms)" -ForegroundColor White
        }
        
        Write-Host ""
        return $true
    }
    catch {
        Write-Host "  Error running test: $_" -ForegroundColor Red
        Write-Host ""
        return $false
    }
}

foreach ($mode in $modes) {
    # Skip "true" mode if PEXT not supported
    if ($mode -eq "true" -and -not $pextSupported) {
        Write-Host "Skipping PEXT mode: true (not supported on this hardware)" -ForegroundColor Yellow
        Write-Host ""
        continue
    }
    
    $success = Run-Test -Mode $mode
    if (-not $success) {
        Write-Host "Test failed for mode: $mode" -ForegroundColor Red
    }
}

Write-Host "PEXT auto-selection test completed!" -ForegroundColor Green
Write-Host ""
Write-Host "To enable debug output for auto-detection, set environment variable:" -ForegroundColor Yellow
Write-Host "  `$env:MACHINE_DEBUG_PEXT = '1'" -ForegroundColor Yellow