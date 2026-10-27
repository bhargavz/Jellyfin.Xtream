<#
.SYNOPSIS
  Merges one packaged version into a Jellyfin plugin-repository manifest (repository.json).

.DESCRIPTION
  Reads the info file written by scripts\Package.ps1 (dist\jellyfin-xtream_<version>.json) and the currently
  published manifest (optional), then writes a manifest in Jellyfin's format:

    [ { guid, name, description, overview, owner, category,
        versions: [ { version, changelog, targetAbi, sourceUrl, checksum, timestamp }, ... ] } ]

  Other plugins and older versions in the existing manifest are kept. An entry with the same version is
  replaced (re-running a release is safe). Versions are sorted newest first.

.EXAMPLE
  pwsh -File scripts/Update-RepositoryManifest.ps1 -PackageInfo dist/jellyfin-xtream_0.10.1.0.json `
    -SourceUrl https://github.com/bhargavz/Jellyfin.Xtream/releases/download/v0.10.1.0/jellyfin-xtream_0.10.1.0.zip `
    -ExistingManifest .tmp/published.json -OutFile site/repository.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageInfo,
    [Parameter(Mandatory = $true)][string]$SourceUrl,
    [string]$ExistingManifest = '',
    [Parameter(Mandatory = $true)][string]$OutFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-Prop($Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $null
}

function Format-Timestamp($Value) {
    # PowerShell 7's ConvertFrom-Json turns ISO-8601 strings into DateTime; write them back in the manifest format.
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

$info = Get-Content -Raw -Encoding UTF8 $PackageInfo | ConvertFrom-Json

$plugins = New-Object System.Collections.ArrayList
if ($ExistingManifest -and (Test-Path $ExistingManifest)) {
    $raw = (Get-Content -Raw -Encoding UTF8 $ExistingManifest).Trim()
    if ($raw) {
        $parsed = ConvertFrom-Json $raw
        # Windows PowerShell 5.1 emits a JSON array as one object; @() + foreach handles both editions.
        foreach ($p in @($parsed)) { if ($null -ne $p) { [void]$plugins.Add($p) } }
    }
}

$newVersion = [ordered]@{
    version   = [string]$info.version
    changelog = [string]$info.changelog
    targetAbi = [string]$info.targetAbi
    sourceUrl = $SourceUrl
    checksum  = [string]$info.checksum
    timestamp = Format-Timestamp $info.timestamp
}

$output = New-Object System.Collections.ArrayList
$found = $false
foreach ($p in $plugins) {
    if ((Get-Prop $p 'guid') -ne $info.guid) { [void]$output.Add($p); continue }
    $found = $true
    $versions = New-Object System.Collections.ArrayList
    foreach ($v in @(Get-Prop $p 'versions')) {
        if ($null -eq $v -or (Get-Prop $v 'version') -eq $newVersion.version) { continue }
        [void]$versions.Add([ordered]@{
                version   = [string](Get-Prop $v 'version')
                changelog = [string](Get-Prop $v 'changelog')
                targetAbi = [string](Get-Prop $v 'targetAbi')
                sourceUrl = [string](Get-Prop $v 'sourceUrl')
                checksum  = [string](Get-Prop $v 'checksum')
                timestamp = Format-Timestamp (Get-Prop $v 'timestamp')
            })
    }
    [void]$versions.Add($newVersion)
    $sorted = @($versions | Sort-Object -Descending -Property { [version]$_.version })
    [void]$output.Add([ordered]@{
            guid        = [string]$info.guid
            name        = [string]$info.name
            description = [string]$info.description
            overview    = [string]$info.overview
            owner       = [string]$info.owner
            category    = [string]$info.category
            versions    = [object[]]$sorted
        })
}
if (-not $found) {
    [void]$output.Add([ordered]@{
            guid        = [string]$info.guid
            name        = [string]$info.name
            description = [string]$info.description
            overview    = [string]$info.overview
            owner       = [string]$info.owner
            category    = [string]$info.category
            versions    = [object[]]@($newVersion)
        })
}

$dir = Split-Path -Parent $OutFile
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$json = ConvertTo-Json -InputObject ([object[]]$output.ToArray()) -Depth 10
[IO.File]::WriteAllText($OutFile, $json + "`n", (New-Object System.Text.UTF8Encoding $false))

$count = 0
foreach ($p in $output) { if ((Get-Prop ([pscustomobject]$p) 'guid') -eq $info.guid) { $count = @($p.versions).Count } }
Write-Host "Wrote $OutFile ($($output.Count) plugin(s), $count version(s) of $($info.name), newest $($newVersion.version))"
