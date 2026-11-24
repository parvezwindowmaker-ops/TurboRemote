# Builds TurboRemote.exe, then compiles and runs every Tests\*Tests.cs console test against it.
# Usage:  powershell -ExecutionPolicy Bypass -File .\Tests\run-tests.ps1 [-Only <TestName>]
# The tests never send keyboard or mouse input; they talk to the app through fake counterparts and UI Automation.
param([string]$Only)

$root = Split-Path $PSScriptRoot -Parent
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 1 }

$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $root 'TurboRemote.exe') $bin -Force
$exe = Join-Path $bin 'TurboRemote.exe'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs = @("-r:$exe", '-r:System.dll', '-r:System.Core.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll',
          '-r:System.Management.dll', "-r:$wpf\UIAutomationClient.dll", "-r:$wpf\UIAutomationTypes.dll", "-r:$wpf\WindowsBase.dll")

# Small helper programs some tests launch (e.g. a fake CLI that hangs).
$helpers = Join-Path $PSScriptRoot 'helpers'
if (Test-Path $helpers) {
    foreach ($h in Get-ChildItem (Join-Path $helpers '*.cs')) {
        & $csc -nologo -target:exe "-out:$(Join-Path $bin ($h.BaseName + '.exe'))" $h.FullName
        if ($LASTEXITCODE -ne 0) { Write-Host "COMPILE FAILED: helper $($h.BaseName)"; exit 1 }
    }
}

$tests = Get-ChildItem (Join-Path $PSScriptRoot '*Tests.cs')
if ($Only) { $tests = $tests | Where-Object { $_.BaseName -eq $Only } }
if (-not $tests) { Write-Host "NO TESTS MATCH '$Only'"; exit 1 }

$failed = 0
foreach ($t in $tests) {
    $testExe = Join-Path $bin ($t.BaseName + '.exe')
    & $csc -nologo -target:exe -platform:anycpu "-out:$testExe" @refs $t.FullName (Join-Path $PSScriptRoot 'TestKit.cs')
    if ($LASTEXITCODE -ne 0) { Write-Host "COMPILE FAILED: $($t.BaseName)"; $failed++; continue }
    Write-Host "=== $($t.BaseName) ==="
    Push-Location $bin
    & $testExe $exe
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { $failed++ }
}

if ($failed -gt 0) { Write-Host "$failed TEST PROGRAM(S) FAILED"; exit 1 }
Write-Host 'ALL TEST PROGRAMS PASSED'
exit 0
