<#
push_wiki.ps1 - publish tools\wiki\pages to the GridSim GitHub wiki
(Windows PowerShell 5.1 compatible).

BEFORE THIS CAN EVER WORK: the wiki must be enabled in the repository
settings (Settings > Features > Wikis) and its first page must be created once
by hand in the web UI. Until then the .wiki.git repository does not exist and
the clone below fails.

What it does:
  1. clones https://github.com/MarkS0485/GridSim.wiki.git into a temp folder,
  2. runs gen_cli_reference.py to (re)build pages\,
  3. mirrors pages\ into the clone (adds and updates pages, deletes wiki
     pages that no longer exist in pages\),
  4. commits with the fixed message "Sync wiki from tools/wiki", but only if
     something changed,
  5. pushes using your own git credentials.

Set WIKI_REMOTE to use a different remote URL. Do not put a token in a script
or a log; this script never prints the remote URL.

Any failure stops the script with a non-zero exit code and a clear message.
#>

$ErrorActionPreference = 'Stop'

$DefaultRemote = 'https://github.com/MarkS0485/GridSim.wiki.git'
$CommitMessage = 'Sync wiki from tools/wiki'
$AuthorName    = 'GridSim wiki sync'
$AuthorEmail   = 'noreply@github.com'

$Here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Pages = Join-Path $Here 'pages'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

# Run a native command and stop on a non-zero exit code. git writes progress
# to stderr, which Windows PowerShell 5.1 treats as an error record, so the
# preference is relaxed around the call and the exit code is checked instead.
function Run-Native([string]$What, [string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments | Out-Host
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) { Fail "$What failed (exit $code)" }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git is not installed or not on PATH' }

# Find a Python 3: the py launcher first, then python.
$PyExe = $null
$PyArgs = @()
if (Get-Command py -ErrorAction SilentlyContinue) { $PyExe = 'py'; $PyArgs = @('-3') }
elseif (Get-Command python -ErrorAction SilentlyContinue) { $PyExe = 'python' }
else { Fail 'python 3 is not installed or not on PATH' }

Run-Native 'gen_cli_reference.py' $PyExe ($PyArgs + @((Join-Path $Here 'gen_cli_reference.py')))

if (-not (Test-Path -LiteralPath $Pages)) { Fail "pages folder missing: $Pages" }
$PageFiles = @(Get-ChildItem -LiteralPath $Pages -Filter '*.md' -File)
if ($PageFiles.Count -eq 0) { Fail "no pages found in $Pages" }

$Remote = $env:WIKI_REMOTE
if ([string]::IsNullOrEmpty($Remote)) { $Remote = $DefaultRemote }

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ('gridsim-wiki-' + [guid]::NewGuid().ToString('N'))
$Clone = Join-Path $Work 'wiki'
New-Item -ItemType Directory -Path $Work | Out-Null

try {
    Write-Host 'cloning the wiki repository ...'
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git clone -q $Remote $Clone | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $saved
    if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Clone '.git'))) {
        Fail 'clone failed. Is the wiki enabled in the repository settings and its first page created in the web UI?'
    }

    Push-Location $Clone
    try {
        # Delete wiki pages (top-level .md files) that no longer exist in pages\.
        foreach ($existing in @(Get-ChildItem -LiteralPath $Clone -Filter '*.md' -File)) {
            if (-not (Test-Path -LiteralPath (Join-Path $Pages $existing.Name))) {
                Write-Host "removing $($existing.Name)"
                Remove-Item -LiteralPath $existing.FullName -Force
            }
        }
        # Add or update everything else.
        foreach ($page in $PageFiles) {
            Copy-Item -LiteralPath $page.FullName -Destination (Join-Path $Clone $page.Name) -Force
        }

        Run-Native 'git add' 'git' @('add', '-A')

        # 'git diff --cached --quiet' exits 1 when there are staged changes.
        $saved = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & git diff --cached --quiet
        $changed = ($LASTEXITCODE -ne 0)
        $ErrorActionPreference = $saved

        if (-not $changed) {
            Write-Host 'wiki is already up to date; nothing to commit'
        } else {
            Write-Host 'changes to publish:'
            Run-Native 'git diff' 'git' @('--no-pager', 'diff', '--cached', '--stat')
            Run-Native 'git commit' 'git' @('-c', "user.name=$AuthorName", '-c', "user.email=$AuthorEmail", 'commit', '-q', '-m', $CommitMessage)
            Write-Host 'pushing ...'
            Run-Native 'git push' 'git' @('push', '-q', 'origin', 'HEAD')
            Write-Host 'wiki updated'
        }
    } finally {
        Pop-Location
    }
} finally {
    if (Test-Path -LiteralPath $Work) {
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
