<#
.SYNOPSIS
  Installs a built Jellyfin Xtream plugin zip into the local (native, tray-launched) Jellyfin server.

.DESCRIPTION
  1. Stops Jellyfin (tray app + jellyfin.exe).
  2. Backs up the plugin configuration and moves every existing "Jellyfin Xtream_*" plugin folder
     into G:\projects\jellyfin-xtream\.tmp\backup-<timestamp>\ (nothing is deleted).
  3. Extracts the zip to plugins\Jellyfin Xtream_<version>\.
  4. Optionally points the guide at an XMLTV URL (-EpgUrl), only if no custom guide is configured yet.
  5. Starts Jellyfin again via the tray app.

  Rollback: stop Jellyfin, delete the new plugin folder, move the backed-up folder(s) and
  Jellyfin.Xtream.xml back, start Jellyfin.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Install-Local.ps1 -EpgUrl https://mybunny.tv/epg.xml
#>
[CmdletBinding()]
param(
    [string]$Zip = '',
    [string]$DataDir = 'C:\ProgramData\Jellyfin\Server',
    [string]$ServerDir = 'C:\Program Files\Jellyfin\Server',
    [string]$EpgUrl = ''
)
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so resolve it here.
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Zip) {
    $Zip = (Get-ChildItem (Join-Path $scriptRoot '..\dist\jellyfin-xtream_*.zip') | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
}

if (-not $Zip -or -not (Test-Path $Zip)) { throw "Plugin zip not found: $Zip" }
$version = [regex]::Match([IO.Path]::GetFileName($Zip), '_(\d+\.\d+\.\d+\.\d+)\.zip$').Groups[1].Value
if (-not $version) { throw "Cannot read the version from $Zip" }

$plugins = Join-Path $DataDir 'plugins'
$configFile = Join-Path $plugins 'configurations\Jellyfin.Xtream.xml'
$backup = Join-Path $scriptRoot ("..\.tmp\backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $backup | Out-Null
$backup = (Resolve-Path $backup).Path

Write-Host "Stopping Jellyfin..."
Get-Process -Name 'Jellyfin.Windows.Tray', 'jellyfin' -ErrorAction SilentlyContinue | Stop-Process -Force
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Process -Name 'jellyfin' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (Get-Process -Name 'jellyfin' -ErrorAction SilentlyContinue) { throw 'jellyfin.exe did not exit' }

if (Test-Path $configFile) { Copy-Item $configFile $backup }
Get-ChildItem $plugins -Directory -Filter 'Jellyfin Xtream_*' | ForEach-Object {
    Write-Host "Moving old plugin folder $($_.Name) to backup"
    Move-Item $_.FullName (Join-Path $backup $_.Name)
}

$target = Join-Path $plugins "Jellyfin Xtream_$version"
Expand-Archive -Path $Zip -DestinationPath $target -Force
Write-Host "Installed $Zip -> $target"

if ($EpgUrl -and (Test-Path $configFile)) {
    [xml]$config = Get-Content $configFile
    $root = $config.PluginConfiguration
    $path = $root.SelectSingleNode('CustomEpgPath')
    if (-not $path -or [string]::IsNullOrWhiteSpace($path.InnerText)) {
        foreach ($pair in @(@('EpgMode', 'CustomXmlTvWithXtreamFallback'), @('CustomEpgPath', $EpgUrl))) {
            $node = $root.SelectSingleNode($pair[0])
            if (-not $node) { $node = $root.AppendChild($config.CreateElement($pair[0])) }
            $node.InnerText = $pair[1]
        }
        $config.Save($configFile)
        Write-Host "Guide set to XMLTV $EpgUrl with Xtream fallback"
    } else {
        Write-Host "A custom guide is already configured; left unchanged"
    }
}

Write-Host "Starting Jellyfin..."
# Start from the server folder: jellyfin.exe inherits the tray's working directory.
Start-Process (Join-Path $ServerDir 'jellyfin-windows-tray\Jellyfin.Windows.Tray.exe') -WorkingDirectory $ServerDir
Write-Host "Backup: $backup"
Write-Host "Next: Dashboard > Scheduled Tasks > Refresh Guide."
