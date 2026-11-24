# Builds TurboRemote.exe (single file, no dependencies) with the .NET Framework compiler that ships with Windows.
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Out <path>]
param([string]$Out = (Join-Path $PSScriptRoot 'TurboRemote.exe'))

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Get-ChildItem (Join-Path $PSScriptRoot 'StandaloneSource\*.cs') | ForEach-Object { $_.FullName }

& $csc -nologo -target:winexe -platform:anycpu -optimize+ -unsafe -main:TurboUnified.UnifiedEntry "-out:$Out" `
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll $src |
    Where-Object { $_ -notmatch 'warning CS4014' }
exit $LASTEXITCODE
