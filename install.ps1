<#
.SYNOPSIS
Build, publish and (optionally) install dsh-wv2 as a normal per-user app.

.PARAMETER Uninstall
Remove the installed copy, desktop and Start-Menu shortcuts.

Usage:
  powershell -ExecutionPolicy Bypass -File .\install.ps1        # build+install
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj  = Join-Path $root 'src\DeepSeekHarness.Desktop\DeepSeekHarness.Desktop.csproj'
$exeName = 'DSH WV2.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH WV2'
$startMenu   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$desktop     = [Environment]::GetFolderPath('Desktop')

function New-Shortcut([string]$lnkPath, [string]$target) {
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($lnkPath)
    $s.TargetPath = $target
    $s.WorkingDirectory = Split-Path -Parent $target
    $s.Save()
}

if ($Uninstall) {
    Write-Host "Uninstalling dsh-wv2..."
    Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $startMenu "$exeName.lnk") -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $desktop  "$exeName.lnk") -ErrorAction SilentlyContinue
    Write-Host "dsh-wv2 removed."
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found on PATH. Install .NET 8 SDK first.'
}

Write-Host "Publishing (framework-dependent single file)..."
$pub = Join-Path $env:TEMP 'dshwv2-pub'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $pub | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host "Installing to $installDir ..."
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $pub '*') $installDir
# WebView2 runtime is bundled by the OS; the loader DLL + Assets ship with us.
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue

Write-Host "Creating shortcuts..."
New-Shortcut (Join-Path $startMenu "$exeName.lnk") (Join-Path $installDir $exeName)
New-Shortcut (Join-Path $desktop  "$exeName.lnk") (Join-Path $installDir $exeName)

Write-Host "Done. Run: $installDir\$exeName"
Write-Host "Installed files:"; Get-ChildItem $installDir | Select-Object Name, Length | Format-Table -AutoSize
