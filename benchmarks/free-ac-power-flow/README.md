# AC power-flow benchmark

The benchmark runs balanced AC load flow on the original MATPOWER 8.1
IEEE 118-bus and 1,354-bus PEGASE cases. See the [results and pre-run
criteria](https://marks0485.github.io/GridSim/developer/benchmarks/free-ac-power-flow/)
and [solver roster](TOOLS.md).

## Prepare the Windows runtime

From the GridSim checkout, set the following in Windows PowerShell.
Use an existing empty scratch directory outside the checkout for the
runtime files. The GridSim checkout needs its pinned .NET SDK and
Python 3.12.

    $root = (Get-Location).Path
    $harness = Join-Path $root 'benchmarks/free-ac-power-flow'
    $runtime = 'C:/bench-runtime'
    python -m venv "$runtime/.venv"
    $python = "$runtime/.venv/Scripts/python.exe"
    & $python -m pip install --no-compile -r "$harness/requirements-windows-py312.lock"
    & $python -m pip check
    dotnet build "$harness/driver/BenchmarkDriver.csproj" -c Debug -m:1 -nr:false "-p:GridSimRoot=$root"

Extract MATPOWER 8.1, Octave 11.3.0 and Julia 1.10.12 to the paths
used below. The launcher checks the upstream MATPOWER case hashes.
Instantiate the Julia environment once.

    $env:JULIA_DEPOT_PATH = "$runtime/.julia"
    $env:JULIA_NUM_THREADS = '1'
    $julia = "$runtime/julia-1.10.12/bin/julia.exe"
    & $julia --startup-file=no "--project=$harness/julia-env" -e 'using Pkg; Pkg.instantiate()'

## Run the benchmark

In the same Windows PowerShell window, paste the following commands.
They invoke Python directly and also work when PowerShell script
execution is disabled. Set the Octave path to the CLI executable in
your installation.

    $env:OPENBLAS_NUM_THREADS = '1'
    $env:OMP_NUM_THREADS = '1'
    $env:MKL_NUM_THREADS = '1'
    $env:NUMBA_NUM_THREADS = '1'
    $out = "$runtime/measurement-$(Get-Date -Format yyyyMMdd-HHmmss)"
    & $python "$harness/run.py" --gridsim $root --output $out `
      --driver "$harness/driver/bin/Debug/net10.0/BenchmarkDriver.dll" `
      --matpower "$runtime/matpower-8.1" --external-python $python `
      --octave "$runtime/octave-11.3.0-w64/mingw64/bin/octave-cli.exe" `
      --julia $julia --julia-project "$harness/julia-env"

Add `--smoke` for the 118-bus case or `--rehearsal` for a development run.
Always use a fresh output path. Verify a full bundle with
`python <harness>/verify_bundle.py <output>`. The output contains the report,
manifest, original-case residual checks, raw attempts, logs and GridSim
physics audits. The report records the exact measured build and configuration.

The published measurement used the Debug Core build identified in its
manifest. Recreating a build from a different commit produces a new
measurement, with its own hashes and provenance.
