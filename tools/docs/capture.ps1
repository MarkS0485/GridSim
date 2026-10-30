<#
.SYNOPSIS
  Run the documented GridSim commands and save their exact output for the chapters to quote.

.DESCRIPTION
  Reads tools/docs/captures.txt. Each non-comment line is

      chapter-key | capture-name | arguments to gridsim

  For example:  power-flows | case14-bare | case14

  Each command runs against the Release build of this checkout from the repo root, and its stdout and stderr are
  written to docs/_captures/<chapter-key>/<capture-name>.txt, followed by a final line giving the exit code. The
  chapters paste these files exactly. Re-running the script after a code change and diffing docs/_captures shows
  which chapters have drifted.

  The script fails loud: a missing build or a missing captures.txt stops it. A command's non-zero exit code is
  recorded rather than treated as an error, because some chapters document failures on purpose.

.PARAMETER Chapter
  Run only the lines for this chapter key.

.PARAMETER Capture
  Run only the line with this capture name (with -Chapter, only in that chapter), to re-capture one command.

.PARAMETER Build
  Build Release first.

.PARAMETER OutDir
  The folder the captures are written under (<OutDir>/<chapter-key>/<capture-name>.txt). Default docs/_captures. Give a
  private folder for captures that read private data (the GDA lake), so they can be checked for leaks before any of
  them is copied into docs/_captures. A relative path is taken from the repository root.

.NOTES
  Arguments are split on spaces; a double-quoted argument ("a b") is one argument and may hold spaces. An argument
  that is or starts with $NAME (for example $GDA_ROOT, or "$GDA_ROOT/some folder/file.csv") takes that environment
  variable's value for the run; the value is written back as $NAME in the capture. A variable that is not set stops
  the script.
#>
param([string]$Chapter = "", [string]$Capture = "", [switch]$Build, [int]$TimeoutSeconds = 600, [string]$OutDir = "docs\_captures")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root
if ($Build) { dotnet build GridSim.slnx -c Release | Select-Object -Last 3 }
# The CLI assembly is named gridsim (GridSim.Cli.csproj AssemblyName); older builds produced GridSim.Cli.exe.
$exe = Join-Path $root "src\GridSim.Cli\bin\Release\net10.0\gridsim.exe"
if (-not (Test-Path $exe)) { throw "No Release build at $exe; run with -Build" }
# Bug #54: the output folder can be given (a private folder for GDA captures); relative paths are from the repo root.
$outRoot = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
$list = Join-Path $PSScriptRoot "captures.txt"
if (-not (Test-Path $list)) { throw "Missing $list" }

$n = 0
foreach ($line in Get-Content $list) {
    $t = $line.Trim()
    if ($t -eq "" -or $t.StartsWith("#")) { continue }
    $parts = $t.Split("|", 3) | ForEach-Object { $_.Trim() }
    if ($parts.Count -ne 3) { throw "Bad line in captures.txt: $line" }
    $key, $name, $cmdArgs = $parts
    if ($Chapter -and $key -ne $Chapter) { continue }
    if ($Capture -and $name -ne $Capture) { continue }
    $dir = Join-Path $outRoot $key
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir "$name.txt"
    # Bug #53: arguments are split on spaces, but a double-quoted argument ("a b") is one argument and keeps its spaces.
    $argv = @([regex]::Matches($cmdArgs, '"([^"]*)"|(\S+)') | ForEach-Object {
        if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
    })
    # An argument that is or starts with $NAME (for example $GDA_ROOT, or "$GDA_ROOT/a folder/file.csv") takes that
    # environment variable's value for the run, and the value is written back as $NAME in the capture, so no machine
    # path reaches the docs. A missing variable fails.
    $envSubs = @{}
    $argv = @($argv | ForEach-Object {
        if ($_ -match '^\$([A-Za-z_][A-Za-z0-9_]*)(?=$|[\\/])') {
            $varName = $Matches[1]
            $v = [Environment]::GetEnvironmentVariable($varName)
            if (-not $v) { throw "Environment variable $varName is not set (needed by: gridsim $cmdArgs)" }
            $envSubs[$v] = '$' + $varName
            $v + $_.Substring($varName.Length + 1)
        } else { $_ }
    })
    # Run with stdin closed and a hard timeout, so a verb that loops (the live view) cannot hang the script.
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
    # Windows PowerShell 5.1 has no ArgumentList: an argument holding a space (or empty) is passed in double quotes.
    $psi.Arguments = (($argv | ForEach-Object { if ($_ -eq "" -or $_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join " ")
    $psi.WorkingDirectory = $root
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
    $psi.UseShellExecute = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); throw "Timed out after $TimeoutSeconds s: gridsim $cmdArgs" }
    $p.WaitForExit()
    $code = $p.ExitCode
    $sw.Stop()
    # Absolute paths of this checkout become repo-relative, so no machine path reaches the docs.
    $raw = ($so.Result + $se.Result) -replace "`r`n", "`n"
    $raw = $raw.Replace("$root\", "").Replace("$root/", "").Replace(($root -replace '\\', '/') + "/", "")
    foreach ($k in $envSubs.Keys) { $raw = $raw.Replace($k, $envSubs[$k]) }
    # Transliterate the non-ASCII characters the CLI prints, so the ASCII file keeps their meaning instead of '?'.
    $map = @{ [char]0x00A3 = "GBP "; [char]0x2014 = "-"; [char]0x2013 = "-"; [char]0x2192 = "->"; [char]0x2190 = "<-";
              [char]0x00D7 = "x"; [char]0x00B7 = "*"; [char]0x2026 = "..."; [char]0x00B0 = " deg"; [char]0x03C3 = "sigma";
              [char]0x0394 = "Delta"; [char]0x00B5 = "u"; [char]0x2248 = "~="; [char]0x2264 = "<="; [char]0x2265 = ">=";
              [char]0x25B2 = "^"; [char]0x25BC = "v"; [char]0x2018 = "'"; [char]0x2019 = "'"; [char]0x201C = '"'; [char]0x201D = '"' }
    foreach ($k in $map.Keys) { $raw = $raw.Replace([string]$k, $map[$k]) }
    $text = $raw.TrimEnd("`n").Split("`n")
    $body = @("`$ gridsim $cmdArgs") + $text + @("[exit $code]")
    # ASCII only, LF endings, so the captures diff cleanly and paste straight into the docs.
    [System.IO.File]::WriteAllText($out, (($body -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
    Write-Host ("{0,-22} {1,-28} exit {2}  {3:N1} s" -f $key, $name, $code, $sw.Elapsed.TotalSeconds)
    $n++
}
if ($n -eq 0) { throw "No captures matched '$Chapter' '$Capture'" }
Write-Host "$n captures written under $outRoot"
