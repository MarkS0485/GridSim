"""Run the GridSim dense and sparse benchmark paths and retain their evidence."""
import argparse
import hashlib
import json
import os
import platform
import statistics
import struct
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from case_reference import (calculate, controlled_voltage_error, gridsim_import_differences,
                            load_case, write_start_case)
from external import differences, run_external

CASE_HASHES = {
    "case118.m": "bc2e6f22b4b9e776572885ee4b50e4f4ab2ee0c5577e9126e86d906f14c4b5f7",
    "case1354pegase.m": "1b08b25a2f6c1d540d090009dfaff41ff2b05784a2d8d302a7ad695821557b89",
}

SOLVER_FIELDS = ("vm", "vaDeg", "pInjMw", "qInjMvar", "totalLossMw",
                 "totalLossMvar", "lineFlows")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def numbers(value, path):
    if type(value) in (int, float):
        yield path, value
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from numbers(item, f"{path}[{index}]")
    elif isinstance(value, dict):
        for key, item in value.items():
            yield from numbers(item, f"{path}.{key}")


def compare(left, right):
    first = left["result"]
    second = right["result"]
    differences = []
    for field in SOLVER_FIELDS:
        first_values = dict(numbers(first[field], field))
        second_values = dict(numbers(second[field], field))
        if first_values.keys() != second_values.keys():
            raise ValueError(f"Result shape changed for {field}")
        if first["busIds"] != second["busIds"]:
            raise ValueError("Bus order changed between GridSim results")
        for location, value in first_values.items():
            other = second_values[location]
            first_bits = struct.pack(">d", value).hex()
            second_bits = struct.pack(">d", other).hex()
            if first_bits != second_bits:
                differences.append(dict(location=location, first=first_bits,
                                        second=second_bits, delta=float(other) - float(value)))
    return differences


def run_path(mode, case, output, driver, source, commit):
    reference = load_case(case)
    folder = output / mode
    folder.mkdir()
    command = ["dotnet", str(driver), str(case), mode, str(folder), commit]
    row = dict(mode=mode, command=command, attempts=[])
    with (folder / "driver.log").open("wb") as log:
        try:
            status = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                    timeout=900, check=False)
            row["exitCode"] = status.returncode
        except (OSError, subprocess.TimeoutExpired) as error:
            row["error"] = str(error)
    attempts = []
    for index in range(1, 11):
        path = folder / f"attempt-{index:02}.json"
        if path.exists():
            attempt = json.loads(path.read_text(encoding="utf-8"),
                                 parse_int=lambda token: -0.0 if token == "-0" else int(token))
            row["attempts"].append(dict(attempt=index, elapsedMs=attempt["elapsedMs"],
                                        converged=attempt["result"]["converged"],
                                        iterations=attempt["result"]["iterations"]))
            if attempt["result"]["converged"]:
                result = attempt["result"]
                voltages = dict(zip(result["busIds"], zip(result["vm"], result["vaDeg"])))
                _, mismatch = calculate(reference, voltages)
                row["attempts"][-1]["referenceMismatchPu"] = mismatch
                row["attempts"][-1]["referenceVoltageTargetErrorPu"] = controlled_voltage_error(reference, voltages)
                attempts.append(attempt)
        else:
            row["attempts"].append(dict(attempt=index, status="missing"))
    row["repeatDifferences"] = []
    audit_indices = set()
    if attempts:
        first = attempts[0]
        audit_indices.add(first["attempt"])
        for attempt in attempts[1:]:
            differences = compare(first, attempt)
            if differences:
                audit_indices.add(attempt["attempt"])
                row["repeatDifferences"].append(dict(attempt=attempt["attempt"],
                                                     differences=differences))
    if attempts:
        export = folder / f"export-{attempts[0]['attempt']:02}.json"
        if export.exists():
            model = json.loads(export.read_text(encoding="utf-8"))["model"]
            row["importDifferences"] = gridsim_import_differences(reference, model)
    return row, attempts, audit_indices


def audit_exports(output, source, row, indices):
    folder = output / row["mode"]
    row["audits"] = []
    for index in sorted(indices):
        export = folder / f"export-{index:02}.json"
        audit = folder / f"audit-{index:02}.json"
        with audit.open("wb") as stream, audit.with_suffix(".log").open("wb") as log:
            status = subprocess.run([sys.executable, str(source / "tools/crossval/physics_audit.py"),
                                     str(export), "--json"], stdout=stream, stderr=log, check=False)
        row["audits"].append(dict(attempt=index, export=export.name, sha256=digest(export),
                                  audit=audit.name, exitCode=status.returncode))


def report(manifest):
    lines = ["# GridSim AC power-flow benchmark", "",
             f"Run type: **{manifest.get('runType', 'development')}**.",
             f"Case SHA-256: `{manifest['caseSha256']}`.",
             f"GridSim commit: `{manifest['gridSimCommit']}`.",
             f"GridSim build configuration: {manifest.get('gridSimBuildConfiguration', 'unrecorded')}.", "",
             "| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |",
             "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for row in manifest["paths"]:
        durations = [attempt["elapsedMs"] for attempt in row["attempts"]
                     if attempt.get("converged")]
        values = (f"{statistics.mean(durations):.6f} | {min(durations):.6f} | "
                  f"{max(durations):.6f}" if durations else "- | - | -")
        scores = [attempt["referenceMismatchPu"][0] for attempt in row["attempts"]
                  if attempt.get("referenceMismatchPu") is not None]
        passes = sum(score <= 1e-8 for score in scores)
        lines.append(f"| {row['mode']} | {len(durations)}/10 | {values} | {passes}/{len(scores)} |")
    lines += ["", "## Independent electrical checks", "",
              "| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |",
              "| --- | ---: | ---: |"]
    for row in manifest["paths"]:
        mismatch = [attempt["referenceMismatchPu"][0] for attempt in row["attempts"]
                    if attempt.get("referenceMismatchPu") is not None]
        voltage = [attempt["referenceVoltageTargetErrorPu"][0] for attempt in row["attempts"]
                   if attempt.get("referenceVoltageTargetErrorPu") is not None]
        max_mismatch = f"{max(mismatch):.8g}" if mismatch else "Unavailable"
        max_voltage = f"{max(voltage):.8g}" if voltage else "Unavailable"
        lines.append(f"| {row['mode']} | {max_mismatch} | {max_voltage} |")
    for row in manifest["paths"]:
        if "modelDifference" in row:
            residuals = [entry["regularisedModelMismatchPu"][0] for entry in row["attempts"]
                         if "regularisedModelMismatchPu" in entry]
            lines += ["", row["modelDifference"]]
            if residuals:
                lines.append(f"The largest independently reconstructed ANDES-model mismatch was {max(residuals):.8g} pu.")
    lines += ["", "## GridSim bitwise checks", "",
              "| Check | Compared | Scalar differences |",
              "| --- | --- | ---: |"]
    for row in manifest["paths"]:
        if "repeatDifferences" in row:
            count = sum(attempt.get("converged", False) for attempt in row["attempts"])
            delta = sum(len(entry["differences"]) for entry in row["repeatDifferences"])
            lines.append(f"| {row['mode']} repeats | {count}/10 solves | {delta if count > 1 else 'Unavailable'} |")
    cross = manifest["crossPathDifferences"]
    lines.append(f"| Dense vs Sparse | First converged solve on each path | {len(cross) if cross is not None else 'Unavailable'} |")
    lines += ["", "## GridSim physics audits", "",
              "| Path | Attempt | Export | Audit | Exit code |",
              "| --- | ---: | --- | --- | ---: |"]
    for row in manifest["paths"]:
        mode = row["mode"]
        for audit in row.get("audits", []):
            lines.append(f"| {mode} | {audit['attempt']} | [JSON]({mode}/{audit['export']})"
                         f" | [JSON]({mode}/{audit['audit']}) | {audit['exitCode']} |")
    if "differencesFromMatpower" in manifest:
        lines += ["", "## Differences from MATPOWER", "",
                  "Branch flows here are reconstructed on the common source case from each solver's voltages.",
                  "Each entry gives the largest absolute difference and its source bus or one-based branch row.",
                  "", "| Path | Quantity | Difference | Bus or branch |",
                  "| --- | --- | ---: | ---: |"]
        for mode, quantities in manifest["differencesFromMatpower"].items():
            for quantity, (delta, location) in quantities.items():
                lines.append(f"| {mode} | {quantity} | {delta:.8g} | {location} |")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gridsim", type=Path, required=True)
    parser.add_argument("--driver", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--matpower", type=Path, default=Path(__file__).parent / "matpower-8.1")
    parser.add_argument("--external-python", type=Path)
    parser.add_argument("--octave", type=Path)
    parser.add_argument("--julia", type=Path)
    parser.add_argument("--julia-project", type=Path, default=Path(__file__).parent / "julia-env")
    parser.add_argument("--only", nargs="+", choices=("dense", "sparse", "matpower",
                        "pandapower", "pypsa", "veragrid", "powsybl", "andes", "powermodels"),
                        help="Run selected paths for adapter development")
    parser.add_argument("--rehearsal", action="store_true", help="Label a noisy-system rehearsal")
    args = parser.parse_args()
    source = args.gridsim.resolve(strict=True)
    driver = args.driver.resolve(strict=True)
    output = args.output.resolve()
    case_name = "case118.m" if args.smoke else "case1354pegase.m"
    case = args.matpower.resolve() / "data" / case_name
    if digest(case) != CASE_HASHES[case_name]:
        parser.error(f"Case bytes do not match MATPOWER 8.1: {case}")
    commit = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"],
                                     text=True).strip()
    output.mkdir(parents=True, exist_ok=False)
    source_case = case
    (output / "input").mkdir()
    case = output / "input" / source_case.name
    write_start_case(source_case, case)
    workspace = Path(__file__).resolve().parent
    sources = ("run.py", "case_reference.py", "external.py", "python_worker.py",
               "matpower_worker.m", "powermodels_worker.jl", "driver/Program.cs",
               "driver/BenchmarkDriver.csproj", "run-benchmark.ps1", "requirements.in",
               "requirements-windows-py312.lock",
               "julia-env/Project.toml", "julia-env/Manifest.toml", "verify_bundle.py",
               "prepare_docs.py")
    source_hashes = {file: digest(workspace / file) for file in sources}
    power_mode = None
    if os.name == "nt":
        result = subprocess.run(["powercfg", "/getactivescheme"], capture_output=True,
                                text=True, errors="replace", check=False)
        power_mode = result.stdout.strip() if result.returncode == 0 else None
    manifest = dict(timestampUtc=datetime.now(timezone.utc).isoformat(),
                    runType="smoke" if args.smoke else "rehearsal" if args.rehearsal else "measurement",
                    platform=platform.platform(), python=platform.python_version(),
                    processor=platform.processor(), logicalCpus=os.cpu_count(), powerMode=power_mode,
                    threadSettings={name: os.environ.get(name) for name in (
                        "OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS",
                        "NUMBA_NUM_THREADS", "JULIA_NUM_THREADS")},
                    case=case.name, caseSha256=digest(source_case),
                    startCaseSha256=digest(case),
                    gridSimCommit=commit, driverSha256=digest(driver),
                    gridSimCoreSha256=digest(driver.with_name("GridSim.Core.dll")),
                    gridSimBuildConfiguration=driver.parent.parent.name,
                    harnessSourceSha256=source_hashes, paths=[])
    results = {}
    for mode in ("dense", "sparse"):
        if args.only and mode not in args.only:
            continue
        print(f"Running GridSim {mode}: {case.name}", flush=True)
        row, attempts, indices = run_path(mode, case, output, driver, source, commit)
        manifest["paths"].append(row)
        results[mode] = (attempts, indices)
    dense, _ = results.get("dense", ([], set()))
    sparse, _ = results.get("sparse", ([], set()))
    manifest["crossPathDifferences"] = None
    if dense and sparse:
        manifest["crossPathDifferences"] = compare(dense[0], sparse[0])
    for row in manifest["paths"]:
        _, indices = results[row["mode"]]
        audit_exports(output, source, row, indices)
    reference_case = load_case(source_case)
    states = {}
    for mode, (attempts, _) in results.items():
        if attempts:
            result = attempts[0]["result"]
            voltage = dict(zip(result["busIds"], zip(result["vm"], result["vaDeg"])))
            states[mode] = (voltage, calculate(reference_case, voltage)[0])
    workers = {}
    if args.octave:
        workers["matpower"] = [str(args.octave.resolve()), "--no-init-file", "--quiet",
                               str(workspace / "matpower_worker.m"), str(args.matpower.resolve()),
                               str(case), str(output / "matpower")]
    if args.external_python:
        for name in ("pandapower", "pypsa", "veragrid", "powsybl", "andes"):
            workers[name] = [str(args.external_python.resolve()), str(workspace / "python_worker.py"),
                             name, str(case), str(output / name)]
    if args.julia:
        workers["powermodels"] = [str(args.julia.resolve()), "--startup-file=no",
                                  f"--project={args.julia_project.resolve()}",
                                  str(workspace / "powermodels_worker.jl"), str(case),
                                  str(output / "powermodels")]
    for name, command in workers.items():
        if args.only and name not in args.only:
            continue
        print(f"Running {name}: {case.name}", flush=True)
        row, state = run_external(name, command, output, reference_case)
        manifest["paths"].append(row)
        if state:
            states[name] = state
    if args.only and {row["mode"] for row in manifest["paths"]} != set(args.only):
        parser.error("A requested adapter is missing its runtime argument")
    if "matpower" in states:
        manifest["differencesFromMatpower"] = {
            name: differences(reference_case, states["matpower"], state)
            for name, state in states.items() if name != "matpower"
        }
    manifest["changedHarnessFiles"] = [
        file for file, expected in source_hashes.items()
        if digest(workspace / file) != expected
    ]
    write_json(output / "manifest.json", manifest)
    (output / "report.md").write_text(report(manifest), encoding="utf-8")
    print(f"Evidence and report written to {output}", flush=True)
    return int(any(not attempt.get("converged") for row in manifest["paths"]
                   for attempt in row["attempts"]))


if __name__ == "__main__":
    sys.exit(main())
