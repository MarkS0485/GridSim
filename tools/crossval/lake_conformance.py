#!/usr/bin/env python3
"""Physical-law conformance sweep over the STORED GDA state lake.

Runs the same independent NumPy law audit as ``physics_audit.py`` - but over the real,
already-solved states in the data lake rather than freshly re-solved benchmark cases. For each
settlement period stored in ``Derived/{presolved,estimated}_state/<case>/year=*/week=*/*.parquet`` it
reconstructs the network model (from the case's ``topology.json``) plus the period's solved voltages
and line flows (from the parquet ``result_json``), and checks the hard state-level laws (energy, KCL,
complex power, Joule non-negativity). It writes a case x era x route conformance matrix and a summary.

This is the lake-scale proof that the constrained/presolved states the estimator actually shipped are
physically admissible - across years of real GB grid history.

Usage:
  python lake_conformance.py [--lake <derived-dir>] [--case gb-spine] [--route presolved,estimated]
                             [--max-periods N] [--tol MVA] [--out conformance-lake.csv] [--gate]
The lake defaults to $GDA_DATA_ROOT/Derived when GDA_DATA_ROOT is set, else to $GDA_ROOT/Derived (D06, Ruling R1,
and N206: both variables name the DataSources folder itself, exactly as the CLI's `gda` verb reads them, so the
Derived directory sits directly under either). A variable that is set but is not a folder is refused naming the
variable (exit 2), with no fall back from GDA_DATA_ROOT to GDA_ROOT; a value is never printed. R214 (October 2026):
this used to default to a hard-coded Windows path, so on any other machine the run silently reported 'data-gated' and
exited 0.
Exit: 0 when the sweep ran (a reporting tool: a pass-rate deficit is in the report, not the exit code), unless
      --gate is given (GR-913): then 1 when any audited period is not physically admissible, so the tool can be
      scripted as a gate;
      2 when no lake is configured (no --lake, no GDA_DATA_ROOT and no GDA_ROOT) or the lake directory does not
      exist, or GDA_DATA_ROOT is set but is not a folder; 77 when NumPy or pyarrow is missing (an environment skip).
"""
import argparse
import glob
import json
import os
import sys
from collections import defaultdict

try:
    import numpy as np
    import pyarrow.parquet as pq
    from residual_check import build_ybus, _f
except ImportError as e:
    # GR-906: a missing package is an environment skip, exit 77 (sidecar_common.EXIT_SKIPPED, the automake / CTest
    # convention every sidecar uses), never 2, which Python also uses for a usage error.
    print(f"lake_conformance: environment not ready ({e})", file=sys.stderr)
    sys.exit(77)

# The Derived directory under the GDA lake root. N206 (October 2026): the lake root is the DataSources folder itself
# (the meaning the CLI's `gda` verb and the GDA estate give both variables), so Derived sits directly under it. Before
# N206 this tool read GDA_ROOT as the folder ABOVE DataSources and appended "DataSources/Derived", so one setting of
# GDA_ROOT pointed the CLI and this tool at different folders.
LAKE_DERIVED_SUBDIR = "Derived"
EXIT_NO_LAKE = 2


def default_lake():
    """(derived_dir, variable) from the environment, or (None, None) when neither variable is set.

    GDA_DATA_ROOT is read first, then GDA_ROOT (the order of GridSim.Gda.GdaRootEnvironment); both name the
    DataSources folder itself (N206), so the Derived directory is <root>/Derived. "Set" means a non-empty value, as in
    the CLI. A variable that is set but does not name an existing folder stops the run naming the variable: it never
    falls back to the next variable (a wrong setting must be fixed, not replaced by a different lake) and the value is
    never printed (machine settings may carry private paths). Returning the variable lets main() name it, rather than
    its value, when Derived itself is missing."""
    data_root = os.environ.get("GDA_DATA_ROOT")
    if data_root:
        if not os.path.isdir(data_root):
            print("lake_conformance: GDA_DATA_ROOT is set but does not name an existing folder (value not printed); "
                  "point it at the GDA DataSources folder, unset it, or pass --lake.", file=sys.stderr)
            sys.exit(EXIT_NO_LAKE)
        return os.path.join(data_root, LAKE_DERIVED_SUBDIR), "GDA_DATA_ROOT"
    root = os.environ.get("GDA_ROOT")
    if root:
        if not os.path.isdir(root):
            print("lake_conformance: GDA_ROOT is set but does not name an existing folder (value not printed); "
                  "point it at the GDA DataSources folder, unset it, or pass --lake.", file=sys.stderr)
            sys.exit(EXIT_NO_LAKE)
        return os.path.join(root, LAKE_DERIVED_SUBDIR), "GDA_ROOT"
    return None, None


def audit_period(model, res, tol):
    """Check a stored period's state against the network laws by an independent Ybus recompute. Unlike a
    re-solved bench case, the lake state is DEMAND-SCALED per period, so we compare S = V (x) conj(Y V)
    to the period's STORED injection (result_json pInjMw/qInjMvar), not the static model's Pg-Pd."""
    buses = model["buses"]
    id2i = {b["id"]: i for i, b in enumerate(buses)}
    n = len(buses)
    base = _f(model.get("baseMva"), 100.0)
    Y = build_ybus(buses, model["branches"], id2i, base)

    vm = np.ones(n); va = np.zeros(n)
    pinj = np.zeros(n); qinj = np.zeros(n)
    for b in res.get("buses", []):
        i = id2i.get(b["id"])
        if i is None:
            continue
        vm[i] = _f(b.get("vm"), 1.0)
        va[i] = np.radians(_f(b.get("vaDeg")))
        pinj[i] = _f(b.get("pInjMw"))
        qinj[i] = _f(b.get("qInjMvar"))
    V = vm * np.exp(1j * va)
    S = V * np.conj(Y @ V) * base

    # KCL / complex power: the network equation must reproduce the stored injection.
    kcl_mw = float(np.max(np.abs(S.real - pinj))) if n else 0.0
    svi_mvar = float(np.max(np.abs(S.imag - qinj))) if n else 0.0
    # Energy: stored net injection sums to the branch losses.
    losses = [_f(l.get("pLossMw")) for l in res.get("lineFlows", [])]
    energy_mw = abs(float(np.sum(pinj)) - float(sum(losses)))
    min_branch_loss = float(min(losses)) if losses else 0.0

    ok = kcl_mw <= tol and svi_mvar <= tol and energy_mw <= tol
    worst = max(kcl_mw, svi_mvar, energy_mw)
    return ok, worst, min_branch_loss


def sweep(lake, cases, routes, max_periods, tol):
    rows = []                       # (case, route, year, period_ok, worst_residual)
    agg = defaultdict(lambda: [0, 0])  # (case,route,year) -> [passed, total]

    for route in routes:
        product = f"{route}_state"
        base = os.path.join(lake, product)
        for case in cases:
            case_dir = os.path.join(base, case)
            topo_path = os.path.join(case_dir, "topology.json")
            if not os.path.exists(topo_path):
                continue
            model = json.load(open(topo_path, encoding="utf-8"))

            for pf in sorted(glob.glob(os.path.join(case_dir, "year=*", "week=*", "*.parquet"))):
                year = _partition(pf, "year")
                table = pq.read_table(pf, columns=["datetime_utc", "converged", "result_json"])
                n = table.num_rows
                take = n if max_periods <= 0 else min(n, max_periods)
                rj_col = table.column("result_json")
                cv_col = table.column("converged")
                for i in range(take):
                    if not bool(cv_col[i].as_py()):
                        continue
                    rj = rj_col[i].as_py()
                    if not rj:
                        continue
                    res = json.loads(rj) if isinstance(rj, str) else rj
                    ok, worst, _ = audit_period(model, res, tol)
                    agg[(case, route, year)][0] += 1 if ok else 0
                    agg[(case, route, year)][1] += 1
                    rows.append((case, route, year, ok, worst))
    return agg, rows


def _f(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def _partition(path, key):
    for part in path.replace("\\", "/").split("/"):
        if part.startswith(key + "="):
            return part[len(key) + 1:]
    return "?"


def main():
    ap = argparse.ArgumentParser(description="Physical-law conformance over the GDA state lake.")
    ap.add_argument("--lake", default=None, help="the lake's Derived directory (default: $GDA_DATA_ROOT/Derived, else $GDA_ROOT/Derived; both name the DataSources folder)")
    ap.add_argument("--case", default="gb-spine", help="comma-separated case names (dirs under the products)")
    ap.add_argument("--route", default="presolved,estimated")
    ap.add_argument("--max-periods", type=int, default=8, help="periods sampled per parquet partition (0 = all)")
    ap.add_argument("--tol", type=float, default=1e-3)
    ap.add_argument("--out", default=None)
    ap.add_argument("--gate", action="store_true",
                    help="exit 1 when any audited period is not physically admissible (default: report only, exit 0)")
    a = ap.parse_args()

    # An explicit --lake wins; otherwise the environment (the variable's name is kept so a message can name it).
    variable = None
    lake = a.lake
    if lake is None:
        lake, variable = default_lake()
    if lake is None:
        print("lake_conformance: no lake configured: pass --lake <derived-dir> or set GDA_DATA_ROOT (or GDA_ROOT).", file=sys.stderr)
        return EXIT_NO_LAKE
    if not os.path.isdir(lake):
        # Fail loud (R214): a missing lake is a configuration error, not a clean 'nothing to check'. A path the user
        # typed is echoed; a path built from an environment variable is not (its value is never printed), so the
        # message names the variable instead.
        if variable is None:
            print(f"lake_conformance: lake not found at {lake}.", file=sys.stderr)
        else:
            print(f"lake_conformance: no Derived directory under the folder {variable} names (value not printed); "
                  "the variable must name the GDA DataSources folder itself.", file=sys.stderr)
        return EXIT_NO_LAKE
    a.lake = lake

    cases = [c.strip() for c in a.case.split(",") if c.strip()]
    routes = [r.strip() for r in a.route.split(",") if r.strip()]
    agg, rows = sweep(a.lake, cases, routes, a.max_periods, a.tol)

    if not rows:
        print("lake_conformance: no stored periods matched (data-gated).", file=sys.stderr)
        return 0

    passed = sum(1 for _, _, _, ok, _ in rows if ok)
    total = len(rows)
    print(f"Lake physical-law conformance  (tol {a.tol:g} MVA, {total} periods audited)")
    print("| case | route | era | periods | admissible | worst residual |")
    print("|------|-------|-----|--------:|-----------:|---------------:|")
    for (case, route, year), (p, t) in sorted(agg.items()):
        worst = max((w for c, r, y, _, w in rows if (c, r, y) == (case, route, year)), default=0.0)
        print(f"| {case} | {route} | {year} | {t} | {p}/{t} | {worst:.2e} |")
    print(f"\nTOTAL: {passed}/{total} stored states physically admissible.")

    out = a.out or os.path.join(os.path.dirname(__file__), "lake-conformance.csv")
    with open(out, "w", encoding="utf-8") as f:
        f.write("case,route,era,periods,admissible,worst_residual_mva\n")
        for (case, route, year), (p, t) in sorted(agg.items()):
            worst = max((w for c, r, y, _, w in rows if (c, r, y) == (case, route, year)), default=0.0)
            f.write(f"{case},{route},{year},{t},{p},{worst:.6e}\n")
    print(f"-> {out}", file=sys.stderr)
    if a.gate and passed < total:
        print(f"lake_conformance: --gate: {total - passed} of {total} audited period(s) are not physically admissible.",
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
