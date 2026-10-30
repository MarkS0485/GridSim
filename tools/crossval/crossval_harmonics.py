#!/usr/bin/env python3
"""Independent NumPy cross-validation of a GridSim harmonic penetration study.

Where crossval.py asks "does an independent iterative solver land on the same power flow?", this
asks the same question for the NEW harmonic engine: for each harmonic order h, does GridSim's
Y(h) V(h) = I(h) solve - the per-order bus voltages and the voltage-THD indices it reports - agree
with an admittance matrix, current injection, and complex solve rebuilt from the raw case inputs
with nothing but NumPy?

It is deliberately independent, not a wrapper: it does NOT call any GridSim code. It reads the
human-editable case directory (system/buses/transmission[/transformer]), rebuilds the
frequency-dependent nodal admittance Y(h) from the standard pi-model, assembles the nodal harmonic
current injection I(h) from a `gridsim-harmonics/1` source file, solves each order with
numpy.linalg.solve, forms the per-bus voltage THD, and diffs all of it against GridSim's
`gridsim-harmonic-export/1`. Exit 0 iff every (order,bus) voltage magnitude and every bus THD agree
within tolerance; exit 1 on any mismatch.

Physics reproduced (matches GridSim's PowerFlow/AdmittanceStamp + Harmonics/FrequencyDependentYBus,
default HarmonicModelOptions: no skin effect, no long-line correction, no load-as-shunt damping):
  * series admittance  ys = 1 / (R + j*h*X)              (reactance scales with h)
  * half line charging  j*h*B/2  stamped at each end
  * transformer tap 2x2 (tap != 1): yff=(ys+bc)/a^2, ytt=ys+bc, yft=-ys/conj(T), ytf=-ys/T,
    with T = a*exp(j*shift), a = |tapRatio|
  * bus shunt  (Gs + j*Bs*(h if Bs>=0 else 1/h)) / BaseMva   (capacitive B scales with h, reactive
    B inversely; a resistive Gs is frequency-independent)
  * active-front-end converters (N348, when the export's harmonicModel.activeFrontEnd block is present): a shunt
    1 / (R + j*h*X) at each recorded unit's bus, R and X the recorded system-base values
  * I(h) = sum over sources of (magnitudePercent/100 * fundamentalCurrentPu) at angle phaseDeg
  * THD_V(bus) = sqrt(sum_{h>1} |V_h|^2) / |V_1| * 100, with |V_1| the solved fundamental the export records
    (indices.fundamentalVm, N244); an older export without it falls back to 1.0 pu nominal
  * the fundamental (h=1) carries no harmonic injection -> zero response, excluded from THD

Reference cases this can be pointed at
--------------------------------------
The engine and this checker are meant to be exercised against the published harmonic benchmark
networks, whose resonance behaviour is documented so a validator has a known answer to hit:

  * IEEE 14-bus harmonic test system (the fundamental IEEE 14-bus load-flow case augmented with
    harmonic source spectra and shunt-capacitor data). A capacitor bank resonating against the
    system source reactance produces a driving-point impedance peak in the low harmonic range
    (typically around the 5th-7th order), which sharply amplifies the corresponding injected
    current harmonic - the canonical "parallel resonance near a compensated bus" signature.

  * IEEE Task Force / CIGRE benchmark harmonic networks (IEEE PES Harmonics Working Group /
    Task Force on Harmonics Modeling and Simulation test systems, and the CIGRE JTF C4.307-style
    industrial and HV benchmark systems). These publish frequency scans exhibiting one or more
    parallel-resonance peaks whose height and order shift with the amount of shunt capacitance and
    the network damping - exactly the low-inertia amplification behaviour the GridSim harmonic
    engine is built to expose. A grid shedding synchronous (harmonic-damping) machines develops
    taller, sharper resonances at these same orders.

These are cited as the physical reference points; this script validates GridSim's numerics against
its OWN recorded inputs on whatever case it is handed (a benchmark case or a GB/GDA-derived one).

Usage:
    python crossval_harmonics.py --case <case-dir> --sources <harmonics.json> \
                                 --export <harmonic-export.json> [--order h] [--tol R] [--atol A]

Default tolerances (N005, October 2026): 4096 machine epsilons, both relative and absolute (about 9.09e-13; the
absolute one in pu, the harmonic voltages being at most of order 1 pu). Observed agreement is max |dVm| 9.7e-17 pu on
uk and 1.1e-16 pu on gb-synthetic (about one epsilon), so the default leaves a factor of about 8,000 for the
conditioning of Y(h) near a resonance while still failing any real regression: the old defaults (1e-6 relative,
1e-9 absolute) would have passed a 1e-7 error. Pass --tol / --atol to loosen them deliberately.

Exit codes: 0 = all within tol, 1 = mismatch, 3 = unreadable input, 77 = numpy missing (nothing validated).
"""
import argparse
import json
import os

# N005: the default tolerance, in machine epsilons (see the module docstring). 2**-52 is the IEEE-754 double machine
# epsilon (sys.float_info.epsilon).
EPS = 2.0 ** -52
DEFAULT_TOL_EPS = 4096
DEFAULT_RTOL = DEFAULT_TOL_EPS * EPS
DEFAULT_ATOL = DEFAULT_TOL_EPS * EPS
import sys

from sidecar_common import require

# D-DOC-127 (October 2026): a missing NumPy is a skip (exit 77, with the interpreter named). It was an uncaught
# ImportError, a traceback and exit 1, read as a mismatch, although the docstring promised exit 2.
require(None, "numpy")
import numpy as np  # noqa: E402  (checked just above)

SOURCES_SCHEMA = "gridsim-harmonics/1"
EXPORT_SCHEMA = "gridsim-harmonic-export/1"


def _f(v, default=0.0):
    """Coerce a possibly-stringified / missing numeric JSON field to float."""
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


# --------------------------------------------------------------------------------------------------
# Case loading (independent of GridSim: reads the same human-editable JSON the loader does)
# --------------------------------------------------------------------------------------------------
def _read_json(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def _read_array(dir_, name):
    path = os.path.join(dir_, name)
    if not os.path.exists(path):
        return []
    data = _read_json(path)
    return data if isinstance(data, list) else []


def load_case(case_dir):
    """Read a GridSim case directory into a plain dict of the fields Y(h) needs.

    Mirrors JsonGridLoader: a transmission line with `circuits` parallel circuits has its series
    impedance divided and its charging multiplied by the circuit count (R/=c, X/=c, B*=c); a
    transformer stamps with its tap ratio and phase shift and no line charging.
    """
    if not os.path.isdir(case_dir):
        raise NotADirectoryError(f"case directory not found: {case_dir}")

    sys_path = os.path.join(case_dir, "system.json")
    sysd = _read_json(sys_path) if os.path.exists(sys_path) else {}
    base_mva = _f(sysd.get("baseMva"), 100.0)

    bus_dtos = _read_array(case_dir, "buses.json")
    if not bus_dtos:
        raise ValueError("buses.json is empty or missing")
    buses = []
    for b in bus_dtos:
        buses.append({
            "id": int(b["id"]),
            "gs": _f(b.get("gs")),
            "bs": _f(b.get("bs")),
            "baseKv": _f(b.get("baseKv"), 400.0),
        })
    id2i = {b["id"]: i for i, b in enumerate(buses)}

    branches = []
    for l in _read_array(case_dir, "transmission.json"):
        c = max(1, int(l.get("circuits", 1) or 1))
        branches.append({
            "from": int(l["from"]), "to": int(l["to"]),
            "r": _f(l.get("rPu")) / c, "x": _f(l.get("xPu")) / c, "b": _f(l.get("bPu")) * c,
            "tap": 1.0, "shift": 0.0,
        })
    # Transformers (optional) stamp with tap/phase-shift and no line charging - the 2x2 tap path.
    for t in _read_array(case_dir, "transformer.json"):
        branches.append({
            "from": int(t["from"]), "to": int(t["to"]),
            "r": _f(t.get("rPu")), "x": _f(t.get("xPu")), "b": 0.0,
            "tap": _f(t.get("tapRatio"), 1.0), "shift": _f(t.get("phaseShiftDeg")),
        })

    return {
        "name": sysd.get("name", os.path.basename(os.path.normpath(case_dir))),
        "baseMva": base_mva,
        "frequencyHz": _f(sysd.get("frequencyHz"), 50.0),
        "buses": buses,
        "id2i": id2i,
        "branches": branches,
    }


def load_sources(path):
    doc = _read_json(path)
    if doc.get("schema") != SOURCES_SCHEMA:
        raise ValueError(f"not a gridsim harmonic source set (schema={doc.get('schema')!r})")
    return doc.get("sources", []) or []


def load_export(path):
    doc = _read_json(path)
    if doc.get("schema") != EXPORT_SCHEMA:
        raise ValueError(f"not a gridsim harmonic-export (schema={doc.get('schema')!r})")
    return doc


# --------------------------------------------------------------------------------------------------
# The independent physics: Y(h), I(h), and the per-order solve
# --------------------------------------------------------------------------------------------------
def build_yh(case, h, model=None):
    """Complex nodal admittance Y at harmonic order h, built from the shared pi-model stamp.

    `model` is the export's optional `harmonicModel` block. When it says machines and/or loads were part of
    the study, they are stamped here from the recorded inputs, with the physics written out independently:
      * machine:  1 / (j*h*X)              X = recorded source reactance on the system base
      * load:     (max(Pd,0) + j*B) / BaseMva at 1 pu, B = -Qd/h for Qd >= 0 (reactor), -Qd*h for Qd < 0
                  (capacitor); a net-exporting bus adds no conductance
      * balanced harmonics: a negative-sequence order (h mod 3 == 2) sees the transformer phase shift
                  reversed; a triplen (zero sequence) is refused
    Model features this checker does not implement (skin effect, long-line correction) are refused loudly
    rather than silently ignored.
    """
    model = model or {}
    if _f(model.get("skinCoefficient")) != 0 or _f(model.get("cableSkinCoefficient")) != 0 or model.get("longLineCorrection"):
        raise ValueError("export uses skin effect / long-line correction, which this checker does not model")
    shift_sign = 1.0
    if model.get("balancedHarmonicSequences"):
        if h != int(h) or h < 1:
            raise ValueError(f"balanced harmonics need a whole order, got {h}")
        if int(h) % 3 == 0:
            raise ValueError(f"order {h} is a triplen (zero sequence): not representable in this model")
        if int(h) % 3 == 2:
            shift_sign = -1.0
    buses = case["buses"]
    id2i = case["id2i"]
    base = case["baseMva"]
    n = len(buses)
    Y = np.zeros((n, n), complex)

    for e in case["branches"]:
        f, t = id2i[e["from"]], id2i[e["to"]]
        z = complex(e["r"], h * e["x"])          # series impedance at order h (R + jhX)
        if z == 0:
            continue
        ys = 1.0 / z
        bc = 1j * (h * e["b"]) / 2.0             # half line charging each end (jhB/2)
        a = e["tap"] if e["tap"] > 0 else 1.0
        T = a * np.exp(1j * np.radians(shift_sign * e["shift"]))
        Y[f, f] += (ys + bc) / (a * a)
        Y[t, t] += ys + bc
        Y[f, t] += -ys / np.conj(T)
        Y[t, f] += -ys / T

    for i, b in enumerate(buses):
        if b["gs"] == 0 and b["bs"] == 0:
            continue
        bs_h = b["bs"] * h if b["bs"] >= 0 else b["bs"] / h  # capacitive scales with h, reactive inversely
        Y[i, i] += complex(b["gs"] / base, bs_h / base)

    if model.get("includeLoadShunts"):
        for ld in model.get("loads") or []:
            i = id2i[int(ld["bus"])]
            pd, qd = _f(ld.get("pdMw")), _f(ld.get("qdMvar"))
            b_load = -(qd / h) if qd >= 0 else -(qd * h)
            Y[i, i] += complex(max(pd, 0.0) / base, b_load / base)
    if model.get("includeGeneratorSources"):
        for g in model.get("generatorSources") or []:
            x = _f(g.get("xSystemPu"))
            if not x > 0:
                raise ValueError(f"generator source at bus {g.get('bus')} has non-positive reactance {x}")
            Y[id2i[int(g["bus"])], id2i[int(g["bus"])]] += 1.0 / (1j * h * x)
    # N348 (CF4, October 2026): active-front-end converters stamped as resonant plant (GridSim N149, G5 Issue 5
    # s.3.29): the internal impedance R + j*h*X (system base, as recorded) as a shunt to ground at each unit's bus.
    # Absent block = no stamp (every older export, and every study without --afe-reactance).
    afe = model.get("activeFrontEnd")
    if afe:
        for u in afe.get("units") or []:
            r, x = _f(u.get("rSystemPu")), _f(u.get("xSystemPu"))
            if not x > 0 or not r >= 0:
                raise ValueError(f"active-front-end unit at bus {u.get('bus')} has a non-physical impedance R={r} X={x}")
            i = id2i[int(u["bus"])]
            Y[i, i] += 1.0 / complex(r, h * x)
    return Y


def injection(sources, h, id2i, n):
    """Nodal harmonic current I(h): sum of (magPct/100 * I_1) at angle phaseDeg, per source bus."""
    inj = np.zeros(n, complex)
    for src in sources:
        bus = int(src["bus"])
        if bus not in id2i:
            continue
        i1 = _f(src.get("fundamentalCurrentPu"))
        for comp in src.get("spectrum", []):
            if int(comp.get("order", 0)) != h:
                continue
            mag = (_f(comp.get("magnitudePercent")) / 100.0) * i1
            ang = np.radians(_f(comp.get("phaseDeg")))
            inj[id2i[bus]] += mag * np.exp(1j * ang)
    return inj


def solve_order(case, sources, h, model=None):
    """Solve Y(h) V(h) = I(h) for the bus voltage phasors. h=1 (no injection) -> zeros."""
    n = len(case["buses"])
    if h == 1:
        return np.zeros(n, complex)
    inj = injection(sources, h, case["id2i"], n)
    if not np.any(inj):
        return np.zeros(n, complex)
    Y = build_yh(case, h, model)
    try:
        return np.linalg.solve(Y, inj)
    except np.linalg.LinAlgError:
        # GridSim fails loud on a singular Y(h) (since 1.95.002) and never exports one, so a singular order
        # here means the two sides disagree about the network: fail the comparison.
        raise ValueError(f"Y(h) is singular at order {h}: no harmonic voltage exists for this network")


# --------------------------------------------------------------------------------------------------
# Diff against the export
# --------------------------------------------------------------------------------------------------
def _close(a, b, rtol, atol):
    return abs(a - b) <= atol + rtol * abs(b)


def run(case, sources, export, order_filter, rtol, atol):
    id2i = case["id2i"]
    bus_ids = [b["id"] for b in case["buses"]]
    n = len(bus_ids)

    orders = [int(o) for o in export.get("orders", [])]
    if not orders:  # fall back to the union of source spectrum orders
        orders = sorted({int(c.get("order", 0)) for s in sources for c in s.get("spectrum", [])})
    if order_filter is not None:
        orders = [h for h in orders if h == order_filter]

    # Export voltage lookup: (order, bus) -> vmPu (sparse; only nonzero entries were written).
    exp_vm = {(int(v["order"]), int(v["bus"])): _f(v.get("vmPu")) for v in export.get("voltage", [])}
    exp_thd = {int(t["bus"]): _f(t.get("thdPct")) for t in export.get("indices", {}).get("thd", [])}
    exp_worst = _f(export.get("indices", {}).get("worstThdPct"))
    # N244 (October 2026): the THD denominator. GridSim forms THD over each bus's SOLVED fundamental |V_1|
    # (HarmonicIndices.SolvedFundamentalVm) and the export now records it (indices.fundamentalVm). This script used a
    # nominal 1.0 pu, so on uk every harmonic voltage agreed to 9e-17 pu while 20 THD values "failed" (worst 7.4821 %
    # vs 7.4695 %). |V_1| is an INPUT here (the fundamental power flow itself is cross-validated by crossval.py); an
    # older export without the list falls back to 1.0 pu and the report says so.
    exp_v1 = {int(r["bus"]): _f(r.get("vmPu")) for r in (export.get("indices", {}).get("fundamentalVm") or [])}
    v1_source = "export indices.fundamentalVm (solved fundamental)" if exp_v1 else "nominal 1.0 pu (older export)"

    # Independent solve per order; accumulate the sum of squares for THD.
    sum_sq = np.zeros(n)
    volt_rows = []
    max_dvm = 0.0
    vm_fail = 0
    for h in orders:
        V = solve_order(case, sources, h, export.get("harmonicModel"))
        for bi in range(n):
            mine = abs(V[bi])
            ref = exp_vm.get((h, bus_ids[bi]), 0.0)
            if h != 1:
                sum_sq[bi] += mine * mine
            dvm = abs(mine - ref)
            ok = _close(mine, ref, rtol, atol)
            if not ok:
                vm_fail += 1
            max_dvm = max(max_dvm, dvm)
            # Keep the table focused: rows the export recorded, plus any mismatch.
            if ref != 0.0 or mine > atol or not ok:
                volt_rows.append((h, bus_ids[bi], mine, ref, dvm, ok))

    # Per-bus THD from the independent voltages over |V_1| (N244: the solved fundamental from the export, else
    # 1.0 pu nominal). THD is a whole-spectrum
    # index, so it is only meaningful - and only diffed - when every order was analysed. Under an
    # --order filter the sum-of-squares is partial, so the THD comparison is skipped (not failed).
    thd_compared = order_filter is None
    thd_rows = []
    max_dthd = 0.0
    thd_fail = 0
    my_worst = 0.0
    if thd_compared:
        for bi in range(n):
            if exp_v1 and bus_ids[bi] not in exp_v1:
                raise ValueError(f"bus {bus_ids[bi]} is missing from the export's indices.fundamentalVm list")
            v1 = exp_v1[bus_ids[bi]] if exp_v1 else 1.0
            if not v1 > 0.0:
                raise ValueError(f"bus {bus_ids[bi]}: the export's fundamental |V_1| is {v1}; THD needs a positive one")
            mine = float(np.sqrt(sum_sq[bi]) / v1 * 100.0)
            my_worst = max(my_worst, mine)
            ref = exp_thd.get(bus_ids[bi], 0.0)
            dthd = abs(mine - ref)
            ok = _close(mine, ref, rtol, atol)
            if not ok:
                thd_fail += 1
            max_dthd = max(max_dthd, dthd)
            if ref != 0.0 or mine > atol or not ok:
                thd_rows.append((bus_ids[bi], mine, ref, dthd, ok))

    worst_ok = (not thd_compared) or _close(my_worst, exp_worst, rtol, atol)
    all_ok = vm_fail == 0 and thd_fail == 0 and worst_ok

    return {
        "orders": orders, "volt_rows": volt_rows, "thd_rows": thd_rows,
        "thd_compared": thd_compared,
        "max_dvm": max_dvm, "max_dthd": max_dthd,
        "vm_fail": vm_fail, "thd_fail": thd_fail,
        "my_worst": my_worst, "exp_worst": exp_worst, "worst_ok": worst_ok,
        "all_ok": all_ok,
        "v1_source": v1_source,   # N244: where the THD denominator came from
    }


def print_report(case, rep, rtol, atol):
    print(f"harmonic cross-validation: case '{case['name']}' "
          f"({len(case['buses'])} buses, {len(case['branches'])} branches, base {case['baseMva']} MVA)")
    print(f"orders analysed: {rep['orders']}   tol: rtol={rtol:g} atol={atol:g}")
    print()

    print("per-order bus voltage magnitude |V_h| (pu):")
    print(f"  {'order':>5} {'bus':>6} {'numpy':>14} {'gridsim':>14} {'|diff|':>12}  ok")
    for h, bus, mine, ref, dvm, ok in rep["volt_rows"]:
        print(f"  {h:>5} {bus:>6} {mine:>14.9f} {ref:>14.9f} {dvm:>12.2e}  {'OK' if ok else 'FAIL'}")
    if not rep["volt_rows"]:
        print("  (no nonzero harmonic voltages)")
    print()

    if not rep["thd_compared"]:
        print("per-bus voltage THD: skipped (single --order run; THD is a whole-spectrum index)")
        print()
    else:
        print("per-bus voltage THD (%):")
        print(f"  {'bus':>6} {'numpy':>14} {'gridsim':>14} {'|diff|':>12}  ok")
        for bus, mine, ref, dthd, ok in rep["thd_rows"]:
            print(f"  {bus:>6} {mine:>14.9f} {ref:>14.9f} {dthd:>12.2e}  {'OK' if ok else 'FAIL'}")
        if not rep["thd_rows"]:
            print("  (no distortion)")
        print()
        print(f"worst-bus THD: numpy={rep['my_worst']:.9f}%  gridsim={rep['exp_worst']:.9f}%  "
              f"{'OK' if rep['worst_ok'] else 'FAIL'}")
    print(f"THD denominator |V_1|: {rep['v1_source']}")
    print(f"max |dVm| = {rep['max_dvm']:.3e} pu   max |dTHD| = {rep['max_dthd']:.3e} %")
    print(f"voltage mismatches: {rep['vm_fail']}   THD mismatches: {rep['thd_fail']}")
    print("RESULT:", "PASS - all within tolerance" if rep["all_ok"] else "FAIL - mismatch(es) above")


def main():
    ap = argparse.ArgumentParser(description="Independent NumPy cross-validation of a GridSim harmonic study.")
    ap.add_argument("--case", required=True, help="GridSim case directory (system/buses/transmission[/transformer])")
    ap.add_argument("--sources", required=True, help="gridsim-harmonics/1 source-set JSON")
    ap.add_argument("--export", required=True, help="gridsim-harmonic-export/1 study JSON")
    ap.add_argument("--order", type=int, default=None, help="restrict the diff to a single harmonic order h")
    ap.add_argument("--tol", type=float, default=DEFAULT_RTOL,
                    help=f"relative tolerance (default {DEFAULT_TOL_EPS} machine epsilons = {DEFAULT_RTOL:.3g})")
    ap.add_argument("--atol", type=float, default=DEFAULT_ATOL,
                    help=f"absolute tolerance in pu (default {DEFAULT_TOL_EPS} machine epsilons = {DEFAULT_ATOL:.3g})")
    args = ap.parse_args()

    try:
        case = load_case(args.case)
        sources = load_sources(args.sources)
        export = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-harmonics: cannot read input: {e}", file=sys.stderr)
        return 3

    try:
        rep = run(case, sources, export, args.order, args.tol, args.atol)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-harmonics: comparison failed: {e}", file=sys.stderr)
        return 3

    print_report(case, rep, args.tol, args.atol)
    return 0 if rep["all_ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
