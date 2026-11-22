<#
.SYNOPSIS
  Publishes Paldium to artifacts/Paldium-<version>/ and zips it.
.PARAMETER WorldFolder
  Optional Palworld world folder; when given, the published exe reads it (--selfcheck) to prove libooz works.
.EXAMPLE
  powershell -File build/publish.ps1 -WorldFolder "$env:LOCALAPPDATA\Pal\Saved\SaveGames\<SteamID>\<WorldId>"
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

# Source paths in the compiled files (and their .pdb) are mapped to /_/, so the build folder of this machine is not embedded in the release.
# Global properties reach every project of the build, the PalCalc submodule included.
# A build that already exists would be reused with the old, unmapped paths: start from a clean state.
dotnet clean src/Paldium.App -c Release -r win-x64 -v q | Out-Null
dotnet clean tools/Paldium.Updater -c Release -r win-x64 -v q | Out-Null
$pathMap = @('-p:ContinuousIntegrationBuild=true', "-p:PathMap=$($root.TrimEnd('\'))\=/_/", '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:EnableSourceLink=false')
dotnet publish src/Paldium.App -c Release -r win-x64 --self-contained false -o $out @pathMap
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The updater (T131) goes next to the exe: the app copies it out of the app folder and runs it from there while it replaces the app folder.
dotnet publish tools/Paldium.Updater -c Release -r win-x64 --self-contained false -o (Join-Path $out 'updater') @pathMap
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the updater failed.' }

# Strip SDK XML docs for WebView2 and JS source maps before manifest generation and zipping (keep .pdb).
Get-ChildItem $out -Recurse -File -Include 'Microsoft.Web.WebView2.*.xml', '*.js.map' | Remove-Item -Force

# Fody (Costura in PalCalc.SaveReader) rewrites its assembly after the compiler ran, and the pdb path in the debug directory keeps the build folder. The path is
# a NUL-terminated string, so it is replaced in place: "<root>\x\y.pdb" becomes "/_/x/y.pdb" followed by NULs up to the old length (the file keeps its size).
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$rootPattern = [regex]::Escape($root.TrimEnd('\')) + '\\[^\x00]*'
foreach ($dll in Get-ChildItem $out -Recurse -Filter *.dll) {
    $bytes = [System.IO.File]::ReadAllBytes($dll.FullName)
    $text = $latin1.GetString($bytes)
    if ($text.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
    $patched = [regex]::Replace($text, $rootPattern, {
        param($m)
        $mapped = '/_/' + $m.Value.Substring($root.TrimEnd('\').Length + 1).Replace('\', '/')
        $mapped.PadRight($m.Value.Length, [char]0)
    }, 'IgnoreCase')
    [System.IO.File]::WriteAllBytes($dll.FullName, $latin1.GetBytes($patched))
    Write-Host "Mapped the build path in $($dll.Name)."
}

# Required content next to the exe.
foreach ($needed in 'Paldium.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'data/gamedata.json', 'data/breeding-rules.json', 'content/guides/en', 'content/guides/tr', 'wwwroot/gamedata', 'updater/Paldium.Updater.exe') {
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

# Record all packaged files in app-files.txt (relative "/" paths) so the updater only touches Paldium's own files.
$manifestPath = Join-Path $out 'app-files.txt'
$manifestBase = (Resolve-Path $out).Path.TrimEnd('\') + '\'
$manifestFiles = New-Object System.Collections.Generic.List[string]
foreach ($file in Get-ChildItem $out -Recurse -File) {
    $rel = $file.FullName.Substring($manifestBase.Length).Replace('\', '/')
    if (-not [string]::Equals($rel, 'app-files.txt', [StringComparison]::OrdinalIgnoreCase)) {
        $manifestFiles.Add($rel)
    }
}
$manifestFiles.Add('app-files.txt')
$manifestFiles.Sort([StringComparer]::Ordinal)
[System.IO.File]::WriteAllLines($manifestPath, $manifestFiles, (New-Object System.Text.UTF8Encoding($false)))

# Written with .NET, entry names with "/" under a top-level Paldium/ folder so extracting directly into Downloads/Desktop stays isolated.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [System.IO.File]::Create($zip)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path $out).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem $out -Recurse -File) {
        $name = 'Paldium/' + $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

# Scan the finished package for what must not be in a release: the build folder, user folders, the local private patterns (ASCII and UTF-16).
$needles = @('C:\Users\', 'C:/Users/', $root, $root.Replace('\', '/'))
$patternFile = Join-Path $PSScriptRoot 'private-patterns.local.txt'
$patterns = @()
if (Test-Path $patternFile) { $patterns = @(Get-Content $patternFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }) }
else { Write-Warning 'build/private-patterns.local.txt is missing: only the generic scan runs.' }
$regexes = @($patterns | ForEach-Object { New-Object System.Text.RegularExpressions.Regex($_, 'IgnoreCase') })
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$found = New-Object System.Collections.Generic.List[string]
$reader = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $reader.Entries) {
        if ($entry.FullName -notmatch '\.(dll|exe|pdb|json|lua|md|txt|config|xml|js|css|html)$') { continue }
        $ms = New-Object System.IO.MemoryStream
        $es = $entry.Open(); $es.CopyTo($ms); $es.Dispose()
        $bytes = $ms.ToArray()
        $texts = @($latin1.GetString($bytes), [System.Text.Encoding]::Unicode.GetString($bytes))
        if ($bytes.Length -gt 1) { $texts += [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) }
        foreach ($text in $texts) {
            $hit = $false
            foreach ($n in $needles) { if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $true; break } }
            if (-not $hit) { foreach ($r in $regexes) { if ($r.IsMatch($text)) { $hit = $true; break } } }
            if ($hit) { $found.Add($entry.FullName); break }
        }
    }
}
finally { $reader.Dispose() }
if ($found.Count -gt 0) {
    Remove-Item $zip -Force
    throw "The package was deleted: $($found.Count) file(s) contain a build path, user folder or private pattern: $(($found | Select-Object -First 15) -join ', ')"
}
Write-Host "Package scan: clean ($($patterns.Count) private pattern(s) + generic paths, ASCII and UTF-16)."

# The SHA-256 of the zip (attached to the GitHub release; the app checks the download against it).
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Set-Content -Path $sha -Value "$hash  $zipName" -Encoding ascii
Write-Host "Done: $zip"
Write-Host "SHA-256: $sha"
Write-Host "GitHub release: tag v$version, attach $zipName and $zipName.sha256"
