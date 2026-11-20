# AC power-flow benchmark

The benchmark compares balanced AC load flow across nine solver paths.
The default case is MATPOWER 8.1 PEGASE with 1,354 buses. See the [results and pre-run
criteria](https://marks0485.github.io/GridSim/developer/benchmarks/)
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

## Run the case suite

Use the same runtime settings and arguments above, with `run_suite.py`
in place of `run.py`. The default `--max-buses 5000` selects all 67
pinned cases within the limit, from four to 3,374 buses. The next
bundled case has 6,468 buses. For an individual case, pass its MATPOWER
filename with `run.py --case`.

The suite runs cases and solver paths serially and writes
`comparison.csv` as cases finish. Per-case reports, manifests and logs
remain in the output directory. MATPOWER loads each case first, including
any upstream unit conversions, and writes the common bus, generator and
branch tables. A case with additional electrical elements such as DC
lines is recorded as unsupported in the materialisation log. In this
release, that applies to the 73-bus RTS-GMLC case. A reported convergence count reflects
the solver's own result. Use the original-case residuals, voltage
target errors, GridSim import checks and physics audits to assess
electrical agreement. Partial timings are based on the successful
attempts shown in the table. Use `--rehearsal` for development timings.

See the [suite rehearsal results](../../docs/developer/benchmarks/history/suite-rehearsal/index.md) for the case comparison, attempt data and charts.
