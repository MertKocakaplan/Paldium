<#
.SYNOPSIS
  Builds the local `public` branch from the files listed in build/public-files.txt. It never pushes.
.DESCRIPTION
  The public GitHub repository gets its own, clean history: the first run makes a root commit without any history, every later run adds one commit on top of it.
  The files are taken from the committed state of the source branch (default `main`), not from the working folder, so uncommitted work never leaks and the
  result is the same every time. A file that is not in the list never enters `public`; a file removed from the source branch is removed from `public` too.
  The script stops when a file of the new tree points to docs/, DESIGN.md, PRODUCT.md or an assistant folder, or when a file outside the list slipped in.
.PARAMETER Message
  Commit message. Default: "Paldium <version>" (the version of Directory.Build.props on the source branch).
.PARAMETER Source
  Branch or commit to take the files from. Default: main.
.PARAMETER Branch
  Local branch to update. Default: public.
.EXAMPLE
  powershell -File build/sync-public.ps1
  git remote add github https://github.com/MertKocakaplan/Paldium.git     # once
  git push github public:main                                             # by hand, after looking at the result
#>
param(
    [string]$Message,
    [string]$Source = 'main',
    [string]$Branch = 'public',
    # Identity of the public commit. The personal git identity never goes into the public history (the GitHub e-mail is private: use the noreply address).
    [string]$Author = 'MertKocakaplan <132152240+MertKocakaplan@users.noreply.github.com>'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Git {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit code $LASTEXITCODE)." }
    return $output
}

$sourceCommit = (Invoke-Git rev-parse --verify "$Source^{commit}").Trim()
$listText = (Invoke-Git show "${sourceCommit}:build/public-files.txt") -join "`n"
$paths = @($listText -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($paths.Count -eq 0) { throw 'build/public-files.txt on the source branch lists no path.' }

# What is not in the source commit at all is a mistake in the list, not something to skip silently.
$missing = @()
foreach ($path in $paths) {
    $entries = @(& git ls-tree $sourceCommit -- $path.TrimEnd('/'))
    if ($entries.Count -eq 0) { $missing += $path }
}
if ($missing.Count -gt 0) { throw "These paths of build/public-files.txt are not in ${Source}: $($missing -join ', ')" }

# The tree is built in a temporary index file, so neither the working folder nor the real index is touched.
$tempIndex = Join-Path ([System.IO.Path]::GetTempPath()) ("paldium-public-index-" + [guid]::NewGuid().ToString('N'))
$previousIndex = $env:GIT_INDEX_FILE
$env:GIT_INDEX_FILE = $tempIndex
try {
    $records = New-Object System.Collections.Generic.List[string]
    foreach ($path in $paths) {
        foreach ($line in (Invoke-Git -c core.quotepath=false ls-tree -r $sourceCommit -- $path.TrimEnd('/'))) {
            # "<mode> <type> <sha>\t<path>"  ->  "<mode> <sha>\t<path>" (the format of update-index --index-info)
            if ($line -match '^(\d+) \w+ ([0-9a-f]{40})\t(.+)$') { $records.Add("$($Matches[1]) $($Matches[2])`t$($Matches[3])") }
            else { throw "Unexpected git ls-tree line: $line" }
        }
    }
    # The records go in through a file with plain line feeds (a pipe from Windows PowerShell would end the lines with CR LF).
    $recordFile = "$tempIndex.records"
    [System.IO.File]::WriteAllText($recordFile, (($records -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    try {
        & cmd.exe /c "git update-index --add --index-info < `"$recordFile`""
        if ($LASTEXITCODE -ne 0) { throw 'git update-index failed.' }
    }
    finally { Remove-Item $recordFile -Force -ErrorAction SilentlyContinue }
    $tree = (Invoke-Git write-tree).Trim()
}
finally {
    $env:GIT_INDEX_FILE = $previousIndex
    if (Test-Path $tempIndex) { Remove-Item $tempIndex -Force }
}

# ---- checks on the new tree ----
$names = @(Invoke-Git -c core.quotepath=false ls-tree -r --name-only $tree)
$forbidden = @($names | Where-Object { $_ -match '^(docs|\.claude|\.agents?|\.gemini|\.impeccable|\.codex|\.argon-review|artifacts)/' -or $_ -in 'DESIGN.md', 'PRODUCT.md' })
if ($forbidden.Count -gt 0) { throw "Planning or assistant files are in the tree: $($forbidden[0..([Math]::Min(9, $forbidden.Count - 1))] -join ', ')" }

# Files of the tree must not point to docs/, DESIGN.md, PRODUCT.md or an assistant folder (.gitignore only lists patterns and is left out of this check).
$pattern = 'docs[/\\]|DESIGN\.md|PRODUCT\.md|\.claude[/\\]|\.agents?[/\\]|\.gemini[/\\]|\.codex[/\\]'
$hits = @(& git -c core.quotepath=false grep -n -I -E $pattern $tree -- . ':(exclude).gitignore' ':(exclude)external' ':(exclude)build/sync-public.ps1')
if ($hits.Count -gt 0) {
    $hits | Select-Object -First 15 | ForEach-Object { Write-Host $_ }
    throw "$($hits.Count) line(s) in the files to publish point to docs/, DESIGN.md, PRODUCT.md or an assistant folder. Remove them and run again."
}

# Private names, ids and paths of the developer and friends must not be in anything that is published. The patterns are personal, so they are not in this
# script: build/private-patterns.local.txt (one regular expression per line, "#" comments; ignored by git, never published) holds them on this machine.
$patternFile = Join-Path $PSScriptRoot 'private-patterns.local.txt'
$patterns = @()
if (Test-Path $patternFile) {
    $patterns = @(Get-Content $patternFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}
if ($patterns.Count -eq 0) {
    Write-Warning "build/private-patterns.local.txt is missing or empty: the check for private names, ids and paths was SKIPPED. Create it before publishing."
}
else {
    $privateHits = @(& git -c core.quotepath=false grep -n -I -i -E ($patterns -join '|') $tree -- . ':(exclude)external')
    if ($privateHits.Count -gt 0) {
        # Only the places are shown, not the lines: the lines contain what is private.
        $privateHits | ForEach-Object { ($_ -split ':', 4)[1] } | Sort-Object -Unique | Select-Object -First 15 | ForEach-Object { Write-Host $_ }
        throw "$($privateHits.Count) line(s) match build/private-patterns.local.txt. Replace them with made-up values and run again."
    }
}

# Size: GitHub refuses files over 100 MB.
$sizeLines = @(Invoke-Git -c core.quotepath=false ls-tree -r -l $tree)
$total = 0L; $biggest = 0L; $biggestName = ''
foreach ($line in $sizeLines) {
    if ($line -match '^\d+ blob [0-9a-f]{40}\s+(\d+)\t(.+)$') {
        $size = [long]$Matches[1]; $total += $size
        if ($size -gt $biggest) { $biggest = $size; $biggestName = $Matches[2] }
    }
}
if ($biggest -gt 90MB) { throw "$biggestName is $([math]::Round($biggest / 1MB, 1)) MB; GitHub refuses files over 100 MB." }

# ---- the commit ----
$parent = $null
& git rev-parse --verify --quiet "refs/heads/$Branch^{commit}" | Out-Null
if ($LASTEXITCODE -eq 0) { $parent = (Invoke-Git rev-parse "refs/heads/$Branch^{commit}").Trim() }

if ($parent -and (Invoke-Git rev-parse "$parent^{tree}").Trim() -eq $tree) {
    Write-Host "Nothing to do: $Branch already has exactly these files ($parent)."
}
else {
    if (-not $Message) {
        $propsXml = [xml]((Invoke-Git show "${sourceCommit}:Directory.Build.props") -join "`n")
        $version = $propsXml.Project.PropertyGroup.Version
        $Message = if ($version) { "Paldium $version" } else { "Paldium" }
    }
    $commitArgs = @('commit-tree', $tree, '-m', $Message)
    if ($parent) { $commitArgs += @('-p', $parent) }
    if ($Author -notmatch '^(.+?)\s*<([^>]+)>$') { throw "-Author must look like 'Name <email>'." }
    $env:GIT_AUTHOR_NAME = $Matches[1]; $env:GIT_AUTHOR_EMAIL = $Matches[2]
    $env:GIT_COMMITTER_NAME = $Matches[1]; $env:GIT_COMMITTER_EMAIL = $Matches[2]
    try { $commit = (Invoke-Git @commitArgs).Trim() }
    finally { Remove-Item Env:GIT_AUTHOR_NAME, Env:GIT_AUTHOR_EMAIL, Env:GIT_COMMITTER_NAME, Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue }
    Invoke-Git update-ref "refs/heads/$Branch" $commit | Out-Null
    Write-Host "$Branch is now $commit ($(if ($parent) { 'on top of the previous commit' } else { 'first commit, no history' }))."
}

$count = $names.Count
Write-Host ("To publish: {0} files, {1:N1} MB (largest: {2}, {3:N1} MB)." -f $count, ($total / 1MB), $biggestName, ($biggest / 1MB))
Write-Host 'Check: no docs/, .claude/ or other planning files in the tree, and no file points to them: passed.'
Write-Host "Nothing was pushed. After looking at it: git push github ${Branch}:main"
