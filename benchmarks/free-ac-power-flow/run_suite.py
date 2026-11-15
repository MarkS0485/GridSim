"""Run every pinned MATPOWER case within a bus limit in ascending size order."""
import argparse
import csv
import json
import statistics
import subprocess
import sys
from pathlib import Path

from run import CASES, digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-buses", type=int, default=5000)
    for name in ("gridsim", "driver", "output", "matpower", "external-python",
                 "octave", "julia", "julia-project"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--rehearsal", action="store_true")
    args = parser.parse_args()
    cases = sorted((entry["buses"], name) for name, entry in CASES.items()
                   if entry["buses"] <= args.max_buses)
    if not cases:
        parser.error("No pinned cases within the bus limit")
    for _, name in cases:
        source = args.matpower / "data" / name
        if not source.is_file() or digest(source) != CASES[name]["sha256"]:
            parser.error(f"Missing or modified MATPOWER 8.1 input: {source}")
    paths = ("gridsim", "driver", "matpower", "external-python", "octave",
             "julia", "julia-project")
    common = []
    for name in paths:
        common.extend((f"--{name}", str(getattr(args, name.replace("-", "_")).resolve(strict=True))))
    args.output.mkdir(parents=True, exist_ok=False)
    materialised = args.output / "materialised"
    materialised.mkdir()
    with (args.output / "materialise.log").open("wb") as log:
        subprocess.run([str(args.octave.resolve()), "--no-init-file", "--quiet",
                        str(Path(__file__).with_name("materialise_cases.m")),
                        str(args.matpower.resolve()), str(materialised.resolve()),
                        *(name for _, name in cases)],
                       stdout=log, stderr=subprocess.STDOUT, timeout=900, check=True)
    modes = ("dense", "sparse", "matpower", "pandapower", "pypsa", "veragrid",
             "powsybl", "andes", "powermodels")
    failed = False
    with (args.output / "comparison.csv").open("w", newline="", encoding="ascii") as stream:
        table = csv.writer(stream)
        table.writerow(("case", "buses", "path", "status", "converged", "mean_ms",
                        "max_original_residual_pu", "case_exit_code"))
        for buses, name in cases:
            folder = args.output / Path(name).stem
            print(f"{name}: {buses} buses", flush=True)
            command = [sys.executable, str(Path(__file__).with_name("run.py")),
                       *common, "--case", name, "--output", str(folder),
                       "--materialised-dir", str(materialised)]
            if args.rehearsal:
                command.append("--rehearsal")
            with folder.with_suffix(".log").open("wb") as log:
                result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
            failed |= result.returncode != 0
            manifest = folder / "manifest.json"
            rows = {}
            if manifest.is_file():
                evidence = json.loads(manifest.read_text(encoding="utf-8"))
                rows = {row["mode"]: row for row in evidence["paths"]}
            for mode in modes:
                attempts = rows.get(mode, {}).get("attempts", [])
                converged = [a for a in attempts if a.get("converged")]
                durations = [a["elapsedMs"] for a in converged if a.get("elapsedMs") is not None]
                residuals = [a["referenceMismatchPu"][0] for a in converged
                             if a.get("referenceMismatchPu") is not None]
                if not (materialised / name).is_file():
                    status = "case not materialised"
                elif mode not in rows:
                    status = "no evidence"
                elif len(converged) != 10 or len(durations) != 10 or len(residuals) != 10:
                    status = "incomplete"
                else:
                    status = "complete"
                table.writerow((name, buses, mode, status, len(converged),
                                statistics.mean(durations) if durations else "",
                                max(residuals) if residuals else "", result.returncode))
            stream.flush()
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
