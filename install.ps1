<#
  Installs Twinstar for the current user.

  -Mode Shell   (default) Windows starts Twinstar INSTEAD of Explorer at sign-in.
                The desktop is never loaded unless you pick "Desktop". Best immersion.
  -Mode Startup Twinstar starts after Explorer and covers it. Desktop may flash at boot.

  Undo anytime with uninstall.ps1. Emergency: Ctrl+Alt+Del -> Task Manager -> File -> Run new task -> explorer.exe
#>
param([ValidateSet('Shell', 'Startup')][string]$Mode = 'Shell')
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'Twinstar.csproj'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Twinstar'
$exe = Join-Path $dest 'Twinstar.exe'

Write-Host "Building to $dest ..."
Get-Process Twinstar, HybridConsole -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish $src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $dest | Out-Host
if (-not (Test-Path $exe)) { throw "Build failed: $exe not found" }

$winlogon = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($Mode -eq 'Shell') {
    Set-ItemProperty -Path $winlogon -Name Shell -Value "`"$exe`""
    Remove-ItemProperty -Path $run -Name Twinstar -ErrorAction SilentlyContinue
    Write-Host "Installed as your shell. Sign out and back in (or reboot) to see the mode selector."
} else {
    Set-ItemProperty -Path $run -Name Twinstar -Value "`"$exe`""
    Remove-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue
    Write-Host "Installed as a startup app. It will run at next sign-in."
}
# Twinstar used to be called Hybrid Console: remove that install. (Its settings and artwork move over on first launch.)
Remove-ItemProperty -Path $run -Name HybridConsole -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\HybridConsole') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Add games / settings: `"$exe`" --edit"
