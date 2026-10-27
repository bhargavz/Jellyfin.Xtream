<#
.SYNOPSIS
  Builds the plugin and writes the installable zip to dist\jellyfin-xtream_<version>.zip.

.DESCRIPTION
  1. Reads name, guid, version, targetAbi, owner, category, overview, description and changelog from build.yaml,
     and checks that the version matches <AssemblyVersion> in Jellyfin.Xtream.csproj.
  2. Runs `dotnet publish -c Release` into .tmp\publish-<version>.
  3. Stages Jellyfin.Xtream.dll + meta.json and zips them (files at the zip root, as Jellyfin expects).
  4. Prints the zip's MD5 (the checksum the plugin repository manifest needs).
  5. Writes dist\jellyfin-xtream_<version>.json with the version metadata and checksum, which
     scripts\Update-RepositoryManifest.ps1 turns into a repository.json entry.

  Runs on Windows PowerShell 5.1 and PowerShell 7 (Windows and Linux). When $env:GITHUB_OUTPUT is set
  (GitHub Actions), it also writes version, zip, info and md5 step outputs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Package.ps1
  pwsh -File scripts/Package.ps1 -Timestamp 2026-10-27T17:00:00Z
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # ISO-8601 UTC timestamp for meta.json and the manifest entry; defaults to now.
    [string]$Timestamp = '',
    [string]$OutputDir = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so resolve it here.
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }

function Read-BuildYaml([string]$Path) {
    # Minimal reader for the flat build.yaml format: `key: value`, `key: "value"`, block scalars (`>`, `>-`, `|`, `|-`)
    # and lists of `- item`. Good enough for this file; not a general YAML parser.
    $result = [ordered]@{}
    $lines = [IO.File]::ReadAllLines($Path)
    $i = 0
    while ($i -lt $lines.Length) {
        $line = $lines[$i]
        $m = [regex]::Match($line, '^([A-Za-z0-9_]+):\s*(.*?)\s*$')
        if (-not $m.Success) { $i++; continue }
        $key = $m.Groups[1].Value
        $rest = $m.Groups[2].Value
        $i++
        if ($rest -match '^([>|])[-+]?$') {
            $folded = $Matches[1] -eq '>'
            $block = New-Object System.Collections.Generic.List[string]
            while ($i -lt $lines.Length -and ($lines[$i] -match '^\s+\S' -or $lines[$i] -match '^\s*$')) {
                $block.Add($lines[$i].Trim())
                $i++
            }
            if ($folded) {
                # Folding: adjacent lines join with a space, an empty line becomes a newline.
                $sb = New-Object System.Text.StringBuilder
                $prevEmpty = $true
                foreach ($b in $block) {
                    if ($b -eq '') { [void]$sb.Append("`n"); $prevEmpty = $true; continue }
                    if (-not $prevEmpty) { [void]$sb.Append(' ') }
                    [void]$sb.Append($b)
                    $prevEmpty = $false
                }
                $result[$key] = $sb.ToString().Trim()
            } else {
                $result[$key] = ($block -join "`n").Trim()
            }
        } elseif ($rest -eq '') {
            $items = New-Object System.Collections.Generic.List[string]
            while ($i -lt $lines.Length -and $lines[$i] -match '^\s+-\s*(.*?)\s*$') {
                $items.Add(($Matches[1] -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'))
                $i++
            }
            $result[$key] = $items.ToArray()
        } else {
            $result[$key] = $rest -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'
        }
    }
    return $result
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false))
}

$build = Read-BuildYaml (Join-Path $repoRoot 'build.yaml')
foreach ($k in 'name', 'guid', 'version', 'targetAbi', 'owner', 'category', 'overview', 'description', 'changelog') {
    if (-not $build.Contains($k) -or -not $build[$k]) { throw "build.yaml has no '$k'" }
}
$version = [string]$build['version']
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "build.yaml version '$version' must have four numeric parts" }

$csproj = Join-Path $repoRoot 'Jellyfin.Xtream/Jellyfin.Xtream.csproj'
$asmVersion = ([xml](Get-Content -Raw $csproj)).Project.PropertyGroup |
    ForEach-Object { if ($_.PSObject.Properties['AssemblyVersion']) { $_.AssemblyVersion } } | Select-Object -First 1
if ($asmVersion -ne $version) { throw "Version mismatch: build.yaml $version, Jellyfin.Xtream.csproj AssemblyVersion $asmVersion" }

if ($Timestamp) {
    $ts = [DateTime]::Parse($Timestamp, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
} else {
    $ts = [DateTime]::UtcNow
}
$timestampText = $ts.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)

# dotnet: PATH first, then the default Windows install location.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnet) { $dotnet.Source } elseif (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { 'C:\Program Files\dotnet\dotnet.exe' } else { throw 'dotnet SDK not found' }

$publishDir = Join-Path $repoRoot ".tmp/publish-$version"
$stageDir = Join-Path $repoRoot ".tmp/stage-$version"
foreach ($d in $publishDir, $stageDir) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }

Write-Host "Publishing Jellyfin.Xtream $version ($Configuration)..."
& $dotnetExe publish $csproj -c $Configuration -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $stageDir, $OutputDir | Out-Null
$artifacts = @($build['artifacts'])
if ($artifacts.Count -eq 0) { $artifacts = @('Jellyfin.Xtream.dll') }
foreach ($a in $artifacts) {
    $src = Join-Path $publishDir $a
    if (-not (Test-Path $src)) { throw "Published output has no $a" }
    Copy-Item $src $stageDir
}

$meta = [ordered]@{
    category    = $build['category']
    changelog   = $build['changelog']
    description = $build['description']
    guid        = $build['guid']
    name        = $build['name']
    overview    = $build['overview']
    owner       = $build['owner']
    targetAbi   = $build['targetAbi']
    timestamp   = $timestampText
    version     = $version
    status      = 'Active'
    autoUpdate  = $false
    imagePath   = ''
    assemblies  = [string[]]$artifacts
}
Write-Utf8NoBom (Join-Path $stageDir 'meta.json') (ConvertTo-Json -InputObject $meta -Depth 5)

$zipName = "jellyfin-xtream_$version.zip"
$zipPath = Join-Path $OutputDir $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

$md5 = (Get-FileHash -Algorithm MD5 -Path $zipPath).Hash.ToLowerInvariant()

$info = [ordered]@{
    guid        = $build['guid']
    name        = $build['name']
    description = $build['description']
    overview    = $build['overview']
    owner       = $build['owner']
    category    = $build['category']
    version     = $version
    changelog   = $build['changelog']
    targetAbi   = $build['targetAbi']
    checksum    = $md5
    timestamp   = $timestampText
    zip         = $zipName
}
$infoPath = Join-Path $OutputDir "jellyfin-xtream_$version.json"
Write-Utf8NoBom $infoPath (ConvertTo-Json -InputObject $info -Depth 5)

if ($env:GITHUB_OUTPUT) {
    $out = "version=$version`nzip=$zipPath`ninfo=$infoPath`nmd5=$md5`n"
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, $out, (New-Object System.Text.UTF8Encoding $false))
}

Write-Host "Zip:  $zipPath"
Write-Host "MD5:  $md5"
Write-Host "Info: $infoPath"
