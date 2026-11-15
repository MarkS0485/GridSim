param(
    [Parameter(Mandatory)]
    [string] $Output,
    [switch] $Smoke,
    [switch] $Rehearsal,
    [string] $GridSim = '',
    [string] $RuntimeRoot = $PSScriptRoot,
    [string] $Octave = '',
    [string] $Python = ''
)
$ErrorActionPreference = 'Stop'
if (!$GridSim) {
    $checkout = Join-Path $PSScriptRoot '../..'
    if (Test-Path -LiteralPath (Join-Path $checkout 'src/GridSim.Core/GridSim.Core.csproj')) {
        $GridSim = $checkout
    } else {
        $GridSim = Join-Path $PSScriptRoot '../GridSim'
    }
}
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$root = (Resolve-Path -LiteralPath $GridSim).Path
if (!$Python) {
    $Python = Join-Path $runtime '.venv/Scripts/python.exe'
}
if (!$Octave) {
    $Octave = Join-Path $runtime 'octave-11.3.0-w64/mingw64/bin/octave-cli.exe'
    $cliOnly = Join-Path $runtime 'octave-runtime/octave-11.3.0-w64/mingw64/bin/octave-cli.exe'
    if (Test-Path -LiteralPath $cliOnly -PathType Leaf) {
        $Octave = $cliOnly
    }
}
$julia = Join-Path $runtime 'julia-1.10.12/bin/julia.exe'
$project = Join-Path $runtime 'julia-env'
$matpower = Join-Path $runtime 'matpower-8.1'
$driver = Join-Path $runtime 'driver/bin/Debug/net10.0/BenchmarkDriver.dll'
foreach ($path in @($python, $octave, $julia, "$project/Manifest.toml",
                     "$matpower/lib/runpf.m", $driver)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Benchmark prerequisite missing: $path"
    }
}
$env:JULIA_DEPOT_PATH = Join-Path $runtime '.julia'
$env:JULIA_NUM_THREADS = '1'
$env:OPENBLAS_NUM_THREADS = '1'
$env:OMP_NUM_THREADS = '1'
$env:MKL_NUM_THREADS = '1'
$env:NUMBA_NUM_THREADS = '1'

$runArguments = @(
    (Join-Path $PSScriptRoot 'run.py'), '--gridsim', $root, '--driver', $driver,
    '--output', $Output, '--matpower', $matpower, '--external-python', $python,
    '--octave', $octave, '--julia', $julia, '--julia-project', $project
)
if ($Smoke) { $runArguments += '--smoke' }
if ($Rehearsal) { $runArguments += '--rehearsal' }
& $python @runArguments
exit $LASTEXITCODE
