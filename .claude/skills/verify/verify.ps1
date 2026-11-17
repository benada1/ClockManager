#requires -Version 7
<#
.SYNOPSIS
  ClockManager quality gate: backend build/test, frontend build/lint, local-clock guard.
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'backend', 'frontend')]
    [string]$Scope = 'all'
)

$ErrorActionPreference = 'Continue'
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) { $root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path }
Set-Location $root

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Step, [string]$Status, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
}

# Runs a native command, shows output only on failure, records the result.
function Invoke-Step([string]$Step, [string]$WorkDir, [string]$Exe, [string[]]$Arguments) {
    Write-Host "=== $Step ===" -ForegroundColor Cyan
    Push-Location $WorkDir
    try {
        $out = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { Pop-Location }

    if ($code -eq 0) {
        $warn = ($out | Select-String -Pattern '(\d+) Warning\(s\)' | Select-Object -Last 1)
        $detail = if ($warn) { $warn.Matches[0].Groups[1].Value + ' warning(s)' } else { '' }
        $passed = ($out | Select-String -Pattern 'Passed!.*' | Select-Object -Last 1)
        if ($passed) { $detail = $passed.Matches[0].Value.Trim() }
        Add-Result $Step 'PASS' $detail
    } else {
        $errors = @($out | Where-Object { $_ -match '\berror\b|\bFailed\b|✗|×' } | Select-Object -Unique)
        if ($errors.Count -gt 0) {
            Write-Host "--- $Step failed (exit $code); error lines ---" -ForegroundColor Red
            $errors | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
        } else {
            Write-Host "--- $Step failed (exit $code); last 40 lines ---" -ForegroundColor Red
            $out | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        }
        $detail = "exit $code"
        if ($out -match 'MSB3027|MSB3021|being used by another process') {
            $detail += ' - output locked: stop the running API (dotnet run / VS / watch) and rerun'
        }
        Add-Result $Step 'FAIL' $detail
    }
}

# ---------- Backend ----------
if ($Scope -in 'all', 'backend') {
    $sln = Get-ChildItem -Path 'backend' -Filter '*.sln' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $buildTarget = if ($sln) { $sln.FullName } else { (Resolve-Path 'backend/ClockManager.Api/ClockManager.Api.csproj').Path }

    Invoke-Step 'backend build' $root 'dotnet' @('build', $buildTarget, '--nologo')

    $testProjects = @(Get-ChildItem -Path 'backend' -Recurse -Filter '*Tests.csproj' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    if ($testProjects.Count -eq 0) {
        Add-Result 'backend tests' 'SKIP' 'no *Tests.csproj found'
    } else {
        $testTarget = if ($sln) { $sln.FullName } else { $testProjects[0].FullName }
        Invoke-Step 'backend tests' $root 'dotnet' @('test', $testTarget, '--nologo')
    }
}

# ---------- Frontend ----------
if ($Scope -in 'all', 'frontend') {
    $fe = Join-Path $root 'frontend'
    if (-not (Test-Path (Join-Path $fe 'node_modules'))) {
        Invoke-Step 'frontend npm ci' $fe 'npm' @('ci', '--no-audit', '--no-fund')
    }
    Invoke-Step 'frontend build' $fe 'npm' @('run', 'build')
    Invoke-Step 'frontend lint'  $fe 'npm' @('run', 'lint')
}

# ---------- Clock guard (always) ----------
Write-Host '=== clock guard ===' -ForegroundColor Cyan
$excludeDir = '[\\/](bin|obj|node_modules|dist|[^\\/]*Tests)[\\/]'
$csPattern  = '\bDateTime\.(Now|UtcNow|Today)\b|\bDateTimeOffset\.(Now|UtcNow)\b|\bTimeProvider\.System\b'
$jsPattern  = '\bnew\s+Date\(\s*\)|\bDate\.now\s*\('

$files = @()
if (Test-Path 'backend')  { $files += Get-ChildItem 'backend'  -Recurse -File -Include '*.cs' }
if (Test-Path 'frontend/src') { $files += Get-ChildItem 'frontend/src' -Recurse -File -Include '*.js', '*.jsx', '*.ts', '*.tsx' }
$files = $files | Where-Object { $_.FullName -notmatch $excludeDir }

$hits = foreach ($f in $files) {
    $pattern = if ($f.Extension -eq '.cs') { $csPattern } else { $jsPattern }
    Select-String -Path $f.FullName -Pattern $pattern |
        Where-Object { $_.Line -notmatch 'clock-ok:' } |
        ForEach-Object { '{0}:{1}: {2}' -f (Resolve-Path -Relative $_.Path), $_.LineNumber, $_.Line.Trim() }
}
if ($hits) {
    Write-Host 'Forbidden local-clock usage (add "clock-ok: <reason>" only if it is NOT used for punch records):' -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "  $_" }
    Add-Result 'clock guard' 'FAIL' "$(@($hits).Count) hit(s)"
} else {
    Add-Result 'clock guard' 'PASS' "$(@($files).Count) files scanned"
}

# ---------- Summary ----------
Write-Host ''
Write-Host "=== verify summary (scope: $Scope) ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($results.Status -contains 'FAIL') { exit 1 } else { exit 0 }
