"""Prepare the MkDocs page and its downloads from a verified measurement."""
import argparse
import json
import shutil
from pathlib import Path

from run import report
from verify_bundle import validate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("destination", type=Path,
                        help="New staging folder for the page and its downloads")
    parser.add_argument("--evidence-url", required=True,
                        help="URL of the exact evidence revision")
    parser.add_argument("--feedback-url", required=True,
                        help="URL of the benchmark issue tracker")
    args = parser.parse_args()
    manifest = json.loads((args.bundle / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("runType") != "measurement" or manifest.get("case") != "case1354pegase.m":
        parser.error("The public report requires a PEGASE measurement bundle.")
    if issues := validate(args.bundle, manifest):
        parser.error("\n".join(issues))
    if "differencesFromMatpower" not in manifest:
        parser.error("The measurement has no MATPOWER comparison point.")

    args.destination.mkdir(parents=True)
    assets = args.destination / "free-ac-power-flow-evidence"
    assets.mkdir()
    shutil.copyfile(args.bundle / "manifest.json", assets / "manifest.json")
    page = report(manifest)
    for row in manifest["paths"]:
        mode = row["mode"]
        for audit in row.get("audits", []):
            mode_assets = assets / mode
            mode_assets.mkdir(exist_ok=True)
            for key in ("export", "audit"):
                path = Path(mode) / audit[key]
                shutil.copyfile(args.bundle / path, assets / path)
                page = page.replace(f"]({path.as_posix()})", f"]({assets.name}/{path.as_posix()})")

    header = """These results record an unattended run of the listed builds and settings.
Each path made ten fresh attempts after one warm-up. The timed interval
covers the solve API. Input loading, exports and audits are outside that
interval. MATPOWER is the numerical comparison point.

The input is [MATPOWER's 1,354-bus PEGASE case](https://github.com/MATPOWER/matpower/blob/8.1/data/case1354pegase.m).
Data citation: C. Josz, S. Fliscounakis, J. Maeght and P. Panciatici,
*AC Power Flow Data in MATPOWER and QCQP Format: iTesla, RTE Snapshots,
and PEGASE*.

"""
    heading, details = page.split("\n\n", 1)
    page = heading + "\n\n" + header + details
    page += f"""
## Reproduce this run

Use the benchmark launcher under `benchmarks/free-ac-power-flow` in the
GridSim checkout. With the documented prerequisites installed, run:

```powershell
pwsh -NoProfile -File benchmarks/free-ac-power-flow/run-benchmark.ps1 -RuntimeRoot <runtime-directory> -Output <new-results-directory>
```

Download the [run manifest]({assets.name}/manifest.json) or browse the
[full evidence at the recorded revision]({args.evidence_url}).

## Report a discrepancy

I ran this benchmark to give people something concrete to check. Please
share reproduced timings, convergence failures, numerical differences,
bugs or concerns about the method. Include the case hash, command, tool
build, hardware and relevant output.

[Report a discrepancy]({args.feedback_url})
"""
    (args.destination / "free-ac-power-flow.md").write_text(page, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
