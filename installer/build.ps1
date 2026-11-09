# Builds dist\Twinstar-Setup-<version>.exe. Needs the .NET 8 SDK and Inno Setup 6.
# The app is self-contained, so people installing it don't need .NET.
# Pass -Sandbox for a test installer that can't touch a real install.
param([string]$Version = '1.0.0', [switch]$Sandbox)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'dist\publish'
dotnet publish (Join-Path $root 'Twinstar.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o $publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }

$defines = @("/DAppVersion=$Version")
if ($Sandbox) { $defines += '/DSANDBOX', '/FTwinstar-Setup-sandbox' }
& $iscc @defines (Join-Path $PSScriptRoot 'Twinstar.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
