<#
.SYNOPSIS
  Publishes Paldium to artifacts/Paldium-<version>/ and zips it.
.PARAMETER WorldFolder
  Optional Palworld world folder; when given, the published exe reads it (--selfcheck) to prove libooz works.
.EXAMPLE
  pwsh build/publish.ps1 -WorldFolder "$env:LOCALAPPDATA\Pal\Saved\SaveGames\<SteamID>\<WorldId>"
#>
param([string]$WorldFolder)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# The version lives in Directory.Build.props (<Version>).
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Directory.Build.props has no <Version>.' }

$out = Join-Path $root "artifacts/Paldium-$version"
$zipName = "Paldium-$version-win-x64.zip"
$zip = Join-Path $root "artifacts/$zipName"
$sha = "$zip.sha256"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
if (Test-Path $sha) { Remove-Item $sha -Force }

dotnet publish src/Paldium.App -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The updater (T131) goes next to the exe: the app copies it out of the app folder and runs it from there while it replaces the app folder.
dotnet publish tools/Paldium.Updater -c Release -r win-x64 --self-contained false -o (Join-Path $out 'updater')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the updater failed.' }

# Required content next to the exe.
foreach ($needed in 'Paldium.exe', 'data/gamedata.json', 'data/breeding-rules.json', 'content/guides/en', 'content/guides/tr', 'wwwroot/gamedata', 'updater/Paldium.Updater.exe') {
    if (-not (Test-Path (Join-Path $out $needed))) { throw "Published output is missing $needed" }
}

# Headless check of the published copy: data, guides, WebView2, libooz and (optionally) a real save.
$report = Join-Path $out 'selfcheck.txt'
$args = @('--selfcheck', $report)
if ($WorldFolder) { $args += $WorldFolder }
$proc = Start-Process -FilePath (Join-Path $out 'Paldium.exe') -ArgumentList $args -Wait -PassThru
Get-Content $report
Remove-Item $report
if ($proc.ExitCode -ne 0) { throw "Self check failed (exit code $($proc.ExitCode))." }

# Written with .NET, entry names with "/" (Compress-Archive of Windows PowerShell 5.1 writes "\\", which other tools and the updater would have to guess about).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [System.IO.File]::Create($zip)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path $out).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem $out -Recurse -File) {
        $name = $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

# The SHA-256 of the zip (attached to the GitHub release; the app checks the download against it).
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Set-Content -Path $sha -Value "$hash  $zipName" -Encoding ascii
Write-Host "Done: $zip"
Write-Host "SHA-256: $sha"
Write-Host "GitHub release: tag v$version, attach $zipName and $zipName.sha256"
