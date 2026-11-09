# Restores the normal Windows desktop at sign-in and removes Twinstar.
$ErrorActionPreference = 'Continue'
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Twinstar -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name HybridConsole -ErrorAction SilentlyContinue
Get-Process Twinstar, HybridConsole -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Twinstar') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\HybridConsole') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled. Explorer is your shell again. Settings kept in $env:APPDATA\Twinstar"
