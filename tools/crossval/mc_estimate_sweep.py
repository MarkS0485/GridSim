#!/usr/bin/env python3
"""Monte-Carlo accuracy sweep for the WLS estimator (hardening plan G-2).

Sweeps seeds x noise scale x measurement coverage on a reference case via
`estimate <case> --synth` (which scores the estimate against the sampled
truth), and aggregates estimate-vs-truth error statistics per configuration.
The question the table answers: how does accuracy degrade as redundancy falls
and noise rises - and does the chi-square gate stay honest about it?

  python tools/crossval/mc_estimate_sweep.py                 # case118, 10 seeds
  python tools/crossval/mc_estimate_sweep.py --case case30 --seeds 5
  python tools/crossval/mc_estimate_sweep.py --cli /opt/gridsim/gridsim --out /tmp/sweep.json

--cli (or the GRIDSIM_CLI environment variable) names the GridSim CLI to run, for a published build or a read-only
tree; a path ending in .dll is run through `dotnet`. The default is this checkout's Release build. --out names the
report file; the default is tools/crossval/mc_estimate_sweep_<case>.json.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
# R214: the Release CLI, resolved per OS (an apphost has the .exe suffix only on Windows).
CLI_DIR = REPO / "src" / "GridSim.Cli" / "bin" / "Release" / "net10.0"
CLI = CLI_DIR / ("gridsim.exe" if sys.platform.startswith("win") else "gridsim")  # F03: the binary is named gridsim
EXIT_NO_CLI = 2

# (label, noise scale, vm/inj/flow coverage)
CONFIGS = [
    ("full/n0.5",   0.5, 1.0, 1.0, 1.0),
    ("full/n1",     1.0, 1.0, 1.0, 1.0),
    ("full/n2",     2.0, 1.0, 1.0, 1.0),
    ("half-flow",   1.0, 1.0, 1.0, 0.5),
    ("no-flow",     1.0, 1.0, 1.0, 0.0),
    ("sparse",      1.0, 0.5, 1.0, 0.25),
    ("inj-only",    1.0, 0.25, 1.0, 0.0),
]

RX = {
    "dvm": re.compile(r"max \|dVm\| = ([0-9.E+-]+) pu"),
    "dva": re.compile(r"max \|dVa\| = ([0-9.E+-]+) deg"),
    "dof": re.compile(r"redundancy \(dof\): (\d+)"),
    "chi": re.compile(r"-> (PASS|FAIL)"),
    "status": re.compile(r"status: (\w+)"),
}


def run(cli: list[str], case: str, seed: int, noise: float, cvm: float, cinj: float, cflow: float,
        out: Path) -> dict | None:
    r = subprocess.run(
        cli + ["estimate", case, "--synth", "--seed", str(seed),
         "--noise", str(noise), "--coverage-vm", str(cvm),
         "--coverage-inj", str(cinj), "--coverage-flow", str(cflow),
         "--out", str(out)],
        capture_output=True, text=True, timeout=600, check=False)
    text = r.stdout
    m = {k: rx.search(text) for k, rx in RX.items()}
    if not m["status"]:
        return None
    return {
        "status": m["status"].group(1),
        "dof": int(m["dof"].group(1)) if m["dof"] else -1,
        "chi_pass": m["chi"].group(1) == "PASS" if m["chi"] else None,
        "dvm": float(m["dvm"].group(1)) if m["dvm"] else float("nan"),
        "dva": float(m["dva"].group(1)) if m["dva"] else float("nan"),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--case", default="case118")
    ap.add_argument("--seeds", type=int, default=10)
    # Bug #50: the CLI and the report path can be given, so the sweep runs against a published CLI or a read-only tree.
    ap.add_argument("--cli", default=os.environ.get("GRIDSIM_CLI") or None,
                    help="the GridSim CLI to run (default: GRIDSIM_CLI, else this checkout's Release build); a .dll runs via dotnet")
    ap.add_argument("--out", default=None,
                    help="the report file (default: tools/crossval/mc_estimate_sweep_<case>.json)")
    args = ap.parse_args()

    cli_path = Path(args.cli) if args.cli else CLI
    if not cli_path.is_file():
        # Fail loud: every run would otherwise 'fail to parse' and the table would report 0 runs per config.
        hint = ("check --cli / GRIDSIM_CLI" if args.cli
                else "build it first (dotnet build GridSim.slnx -c Release), or give --cli / GRIDSIM_CLI")
        print(f"mc_estimate_sweep: GridSim CLI not found at {cli_path}; {hint}.", file=sys.stderr)
        return EXIT_NO_CLI
    cli = ["dotnet", str(cli_path)] if cli_path.suffix.lower() == ".dll" else [str(cli_path)]

    work = Path(tempfile.mkdtemp(prefix="mcsweep_"))
    rows = []
    try:
        for label, noise, cvm, cinj, cflow in CONFIGS:
            per = []
            for seed in range(1, args.seeds + 1):
                res = run(cli, args.case, seed, noise, cvm, cinj, cflow, work / f"o{seed}")
                if res and res["status"] == "Converged":
                    per.append(res)
            if not per:
                rows.append({"config": label, "runs": 0})
                continue
            dvms = [p["dvm"] for p in per]
            dvas = [p["dva"] for p in per]
            rows.append({
                "config": label, "runs": len(per),
                "dof": per[0]["dof"],
                "chi_pass_rate": round(sum(1 for p in per if p["chi_pass"]) / len(per), 2),
                "dvm_mean_pu": round(statistics.mean(dvms), 6),
                "dvm_max_pu": round(max(dvms), 6),
                "dva_mean_deg": round(statistics.mean(dvas), 4),
                "dva_max_deg": round(max(dvas), 4),
            })
            print(f"  {label:10s} runs={len(per)} dof={per[0]['dof']} "
                  f"dVm mean {statistics.mean(dvms):.2e} max {max(dvms):.2e} pu   "
                  f"chi pass {sum(1 for p in per if p['chi_pass'])}/{len(per)}")
    finally:
        shutil.rmtree(work, ignore_errors=True)

    report = {"case": args.case, "seeds": args.seeds, "configs": rows}
    out = Path(args.out) if args.out else HERE / f"mc_estimate_sweep_{args.case}.json"
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"[ok] {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
