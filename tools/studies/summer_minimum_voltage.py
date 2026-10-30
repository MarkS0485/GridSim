#!/usr/bin/env python3
"""GB summer minimum-demand voltage study on gb-full (docs/validation/studies/summer-minimum-voltage.md).

Everything here is synthetic: the demand level, the embedded PV, the commitment and the reactor / condenser plans are
built by the rules below from the committed gb-full case files, never from measured data. The script writes the study
variants to a working folder (outside the repo by default), runs the gridsim CLI on them and prints the tables the
study document quotes. Run it from the repo root:

    python -I tools/studies/summer_minimum_voltage.py --gridsim src/GridSim.Cli/bin/Release/net10.0/gridsim.exe \
        --work <folder> --summary <folder>/summary.json

Fail loud: any gridsim run that exits non-zero, does not settle its controls, or lacks a table stops the script.

Rules (each one is a study assumption, recorded in the study document):
- Demand: every demand row's P and Q times the underlying-demand factor of the snapshot (midday 0.45, night 0.33).
- Embedded PV (midday only): 0.20 x the case's total demand, spread over the demand buses pro rata to demand P, at
  unity power factor (base) or IEEE 1547-2018 Category B volt-var (the inverter rating 1.1 x the PV MW).
- The case's power-flow-only voltage-control units at grid supply points are left out of every variant (they are
  synthesised modelling devices that hold 1.02 pu with up to 500 MVAr each; keeping them would hide the very absorption
  the study asks about). The national slack stays (one bus, the real-power balance) and its MVAr is reported.
- Commitment "all": every remaining unit scaled pro rata to meet the net demand plus a 2 % loss allowance.
  Commitment "min": transmission-connected synchronous thermal units (role pv, type ccgt, ocgt, gas, coal, biomass,
  chp, other) are left out; nuclear stays at its case output; everything else is scaled pro rata to fill the rest.
- Voltage limits per bus by level (GB Grid Code CC.6.1.4 and ESQCR 27): 400 kV 0.95-1.05 pu, 275 and 132 kV
  0.90-1.10 pu, 66 kV and below 0.94-1.06 pu.
- OLTCs regulate with instant timing (the settled state of a 30 minute snapshot); reactors are fixed shunts.
"""
import argparse
import collections
import json
import os
import shutil
import subprocess
import sys

CASE_DIR = os.path.join("data", "gb-full")
SYNC_THERMAL = {"ccgt", "ocgt", "gas", "coal", "biomass", "chp", "other"}
PV_SHARE = 0.20          # embedded PV at the midday trough, fraction of the case's total demand
LOSS_ALLOWANCE = 1.02    # generation target = net demand x this (the slack takes the remainder)
CABLE_BX_CAP = 32.0      # 132 kV: b/x above this is beyond any cable (C/L * Zbase^2 is about 16 for 132 kV XLPE)
CABLE_BX_TYPICAL = 16.0  # the capped sensitivity sets such a branch's b to 16 x its x

SCENARIOS = {
    # name: (underlying demand factor, PV share, commitment)
    "midday-all": (0.45, PV_SHARE, "all"),
    "midday-min": (0.45, PV_SHARE, "min"),
    "night-all": (0.33, 0.0, "all"),
    "night-min": (0.33, 0.0, "min"),
}


def die(msg):
    sys.exit("summer_minimum_voltage: " + msg)


def load(name):
    with open(os.path.join(CASE_DIR, name), encoding="utf-8") as f:
        return json.load(f)


def limits_for(kv):
    if kv >= 380:
        return 0.95, 1.05
    if kv >= 110:
        return 0.90, 1.10
    return 0.94, 1.06


def build_variant(work, name, demand_factor, pv_share, commitment, condensers=(), fold_pv=False, cap_charging=False,
                  no_absorb=False, absorb_scale=1.0):
    """Write a case folder <work>/<name>. fold_pv subtracts the unity-power-factor PV from each demand row (the pf
    equivalent of the QSTS PV units, which the engine folds in as negative load). no_absorb sets every unit's reactive
    lower limit to 0 (no generator absorbs; the slack still does); absorb_scale multiplies every unit's reactive lower
    limit (0.5 = half the absorption capability). Returns what the variant holds."""
    dst = os.path.join(work, name)
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(CASE_DIR, dst)
    buses = load("buses.json")
    for b in buses:
        lo, hi = limits_for(b["baseKv"])
        b["vminPu"], b["vmaxPu"] = lo, hi
    kv = {b["id"]: b["baseKv"] for b in buses}
    demand = load("demand.json")
    base_p = sum(d["pMw"] for d in demand)
    for d in demand:
        p0 = d["pMw"]
        d["pMw"] = p0 * (demand_factor - pv_share) if fold_pv else p0 * demand_factor
        d["qMvar"] *= demand_factor
    net = (demand_factor - pv_share) * base_p
    gens = load("generation.json")
    kept, left_out = [], {"voltage-control units": 0, "synchronous thermal": 0}
    for g in gens:
        if g.get("powerFlowOnly") and g.get("role") == "pv":
            left_out["voltage-control units"] += 1
            continue
        if commitment == "min" and g["role"] == "pv" and g["type"] in SYNC_THERMAL and not g.get("powerFlowOnly"):
            left_out["synchronous thermal"] += 1
            continue
        kept.append(g)
    target = net * LOSS_ALLOWANCE

    def fixed_unit(g):
        return g.get("role") == "slack" or (commitment == "min" and g["type"] == "nuclear")

    fixed = sum(g["outputMw"] for g in kept if fixed_unit(g))
    scalable = sum(g["outputMw"] for g in kept if not fixed_unit(g))
    if scalable <= 0 or target <= fixed:
        die(f"variant {name}: cannot meet {target:.1f} MW (fixed {fixed:.1f}, scalable {scalable:.1f})")
    k = (target - fixed) / scalable
    for g in kept:
        if not fixed_unit(g):
            g["outputMw"] *= k
    if no_absorb or absorb_scale != 1.0:
        for g in kept:
            if g.get("role") != "slack":
                g["qMinMvar"] = 0.0 if no_absorb else g["qMinMvar"] * absorb_scale
    for (bus, mva) in condensers:
        kept.append({"name": f"Study condenser @ {bus}", "bus": bus, "type": "grid", "capacityMva": mva, "outputMw": 0.0,
                     "voltageSetpoint": 1.0, "qMinMvar": -mva, "qMaxMvar": mva, "inertiaH": 0.0, "role": "pv",
                     "powerFlowOnly": True})
    capped = 0
    if cap_charging:
        lines = load("transmission.json")
        for ln in lines:
            if kv[ln["from"]] == 132 and ln["xPu"] > 0 and ln["bPu"] / ln["xPu"] > CABLE_BX_CAP:
                ln["bPu"] = CABLE_BX_TYPICAL * ln["xPu"]
                capped += 1
        with open(os.path.join(dst, "transmission.json"), "w", encoding="utf-8") as f:
            json.dump(lines, f, indent=1)
    for fname, obj in (("buses.json", buses), ("demand.json", demand), ("generation.json", kept)):
        with open(os.path.join(dst, fname), "w", encoding="utf-8") as f:
            json.dump(obj, f, indent=1)
    return {"dir": dst, "base_demand_mw": base_p, "demand_mw": base_p * demand_factor, "net_demand_mw": net,
            "gen_scale": k, "gen_target_mw": target, "left_out": left_out, "units": len(kept), "capped_branches": capped}


def pv_units(total_mw, control, held_buses=frozenset()):
    """Embedded PV at every demand bus, pro rata to the case demand P (rows on one bus merged). A unit on a bus whose
    voltage a generator holds stays at unity (a reactive function there has nothing to act on; the engine refuses it)."""
    if total_mw <= 0:
        return []
    per_bus = {}
    for d in load("demand.json"):
        if d["pMw"] > 0:
            per_bus[d["bus"]] = per_bus.get(d["bus"], 0.0) + d["pMw"]
    tot = sum(per_bus.values())
    units = []
    for bus in sorted(per_bus):
        mw = total_mw * per_bus[bus] / tot
        u = {"name": f"PV {bus}", "bus": bus, "ratedMw": mw, "ratedMva": 1.1 * mw}
        if control == "volt-var" and bus not in held_buses:
            u["control"] = {"function": "volt-var", "voltVar": "1547-B"}
        units.append(u)
    return units


def reactor(bus, mvar):
    """A fixed shunt reactor: one step of -mvar (MVAr at 1 pu), always in."""
    return {"name": f"R{bus}", "bus": bus, "stepMvar": -mvar, "minSteps": 1, "maxSteps": 1, "initialSteps": 1,
            "control": "voltage", "onPu": 0.01, "offPu": 9.0}


# Volt-var on every demand bus (about 1,900 coupled units) runs at the engine's default settle settings (continuous
# tolerance 1e-7 of each unit's rating, 200 continuous rounds, 25 discrete rounds). The per-unit damping alone does not
# settle this many coupled units; the engine then switches to its accelerated iteration over all units together
# (technical ch. 37), and the run must settle like every other run of the study (fail loud otherwise).
VOLT_VAR_SETTINGS = {}


def write_study(path, pv, reactors, extra=None):
    s = {"format": "gridsim-qsts/1", "stepSeconds": 1800, "steps": 1, "recordStates": True,
         "limits": {"perBusVoltageLimits": True}, "pv": pv, "capacitors": reactors}
    s.update(extra or {})
    with open(path, "w", encoding="utf-8") as f:
        json.dump(s, f, indent=1)
    return path


class Runner:
    def __init__(self, gridsim, work):
        self.gridsim = os.path.abspath(gridsim)
        self.work = work
        self.runs = 0

    def qsts(self, case_dir, pv, reactors, tag, extra=None, allow_unsettled=False):
        study = write_study(os.path.join(self.work, tag + ".study.json"), pv, reactors, extra)
        out = os.path.join(self.work, tag + ".out.json")
        args = [self.gridsim, "qsts", case_dir, "--study", study, "--json", out]
        if allow_unsettled:
            args.append("--allow-unsettled")
        p = subprocess.run(args, capture_output=True, text=True)
        self.runs += 1
        if p.returncode != 0:
            die(f"gridsim qsts exit {p.returncode} on {case_dir} / {study}:\n{p.stdout[-3000:]}\n{p.stderr[-2000:]}")
        with open(out, encoding="utf-8") as f:
            r = json.load(f)
        if not r["summary"]["completed"] or (r["summary"]["unsettled_steps"] != 0 and not allow_unsettled):
            die(f"{tag}: run not complete or not settled")
        return r

    def pf(self, case_dir, tag):
        out = os.path.join(self.work, tag + ".pf.json")
        p = subprocess.run([self.gridsim, "pf", case_dir, "--json", out], capture_output=True, text=True)
        self.runs += 1
        if p.returncode != 0:
            die(f"gridsim pf exit {p.returncode} on {case_dir}:\n{p.stdout[-3000:]}")
        with open(out, encoding="utf-8") as f:
            return json.load(f)


def held_buses(case_dir):
    with open(os.path.join(case_dir, "generation.json"), encoding="utf-8") as f:
        held = {g["bus"] for g in json.load(f) if g.get("role") in ("pv", "slack")}
    with open(os.path.join(case_dir, "buses.json"), encoding="utf-8") as f:
        held |= {b["id"] for b in json.load(f) if b["type"] != "pq"}
    return frozenset(held)


def bus_table(case_dir):
    with open(os.path.join(case_dir, "buses.json"), encoding="utf-8") as f:
        return {b["id"]: b for b in json.load(f)}


def voltages(r):
    return {x["bus_id"]: x["vm_pu"] for x in r["tables"]["voltages"]}


def over(vm, buses):
    """Buses above their own upper limit, worst first: (excess pu, bus id, vm)."""
    o = [(v - buses[b]["vmaxPu"], b, v) for b, v in vm.items() if v > buses[b]["vmaxPu"]]
    return sorted(o, reverse=True)


def level_summary(vm, buses):
    by = collections.defaultdict(list)
    for b, v in vm.items():
        by[buses[b]["baseKv"]].append((v, b))
    rows = []
    for kv in sorted(by, reverse=True):
        vs = by[kv]
        top = max(vs)
        rows.append({"kv": kv, "buses": len(vs), "max_pu": top[0], "max_bus": top[1],
                     "mean_pu": sum(v for v, _ in vs) / len(vs), "over": sum(1 for v, b in vs if v > buses[b]["vmaxPu"])})
    return rows


def reactive_balance(pfres, case_dir):
    """Generator reactive output per bus from the pf result: Qg = q_inj + Qd (Bs is 0 in every variant)."""
    with open(os.path.join(case_dir, "demand.json"), encoding="utf-8") as f:
        qd = collections.Counter()
        for d in json.load(f):
            qd[d["bus"]] += d["qMvar"]
    with open(os.path.join(case_dir, "generation.json"), encoding="utf-8") as f:
        gens = json.load(f)
    slack_bus = next(g["bus"] for g in gens if g.get("role") == "slack")
    gen_bus = {g["bus"] for g in gens}
    qmin = collections.Counter()
    for g in gens:
        if g.get("role") == "pv":
            qmin[g["bus"]] += g["qMinMvar"]
    with open(os.path.join(case_dir, "buses.json"), encoding="utf-8") as f:
        bmeta = {b["id"]: b for b in json.load(f)}
    absorb = inject = at_limit_mvar = 0.0
    at_limit = 0
    slack_q = None
    per_bus = []
    by_kv = collections.Counter()
    for row in pfres["tables"]["buses"]:
        b = row["bus"]
        if b not in gen_bus:
            continue
        q = row["q_inj_mvar"] + qd[b]
        if b == slack_bus:
            slack_q = q
        elif b in qmin and qmin[b] < 0 and q <= qmin[b] + 1e-3:
            at_limit += 1
            at_limit_mvar += -q
        if q < 0:
            absorb += -q
            by_kv[bmeta[b]["baseKv"]] += -q
            per_bus.append((q, b))
        else:
            inject += q
    per_bus.sort()
    top = [{"bus": b, "name": bmeta[b]["name"], "kv": bmeta[b]["baseKv"], "q_mvar": q} for q, b in per_bus[:10]]
    return {"loss_mvar": pfres["summary"]["loss_mvar"], "load_mvar": sum(qd.values()), "gen_absorb_mvar": absorb,
            "gen_inject_mvar": inject, "slack_mvar": slack_q, "slack_bus": slack_bus, "vmax_pu": pfres["summary"]["vmax_pu"],
            "buses_at_qmin": at_limit, "mvar_at_qmin": at_limit_mvar, "absorb_by_kv": dict(by_kv), "top_absorbers": top}


def size_reactor(run, case_dir, pv, reactors, bus, target, tag):
    """Smallest reactor (MVAr at 1 pu) at bus that brings its voltage to target, by the secant method on the settled
    voltage (taps re-settle in every solve). Returns (mvar, vm at bus, run result)."""
    q0 = 0.0
    r = run.qsts(case_dir, pv, reactors, tag + "-0")
    v0 = voltages(r)[bus]
    q1 = 50.0
    for it in range(1, 30):
        r = run.qsts(case_dir, pv, reactors + [reactor(bus, q1)], f"{tag}-{it}")
        v1 = voltages(r)[bus]
        if target - 2e-4 <= v1 <= target + 2e-4:
            return q1, v1, r
        if v1 == v0:
            die(f"{tag}: reactor at {bus} does not move its voltage")
        q2 = q1 + (target - v1) * (q1 - q0) / (v1 - v0)
        q0, v0 = q1, v1
        q1 = max(1.0, min(q2, 4000.0))
    die(f"{tag}: reactor sizing at bus {bus} did not converge")


def greedy_reactors(run, case_dir, pv, buses, tag, cap=40):
    """Place a reactor at the worst over-voltage bus, sized to bring it to 0.001 pu under its limit, re-solve, repeat."""
    plan = []
    reactors = []
    r = run.qsts(case_dir, pv, reactors, tag + "-start")
    for n in range(cap):
        o = over(voltages(r), buses)
        if not o:
            return plan, reactors, r
        _, bus, v = o[0]
        target = buses[bus]["vmaxPu"] - 0.001
        mvar, vafter, r = size_reactor(run, case_dir, pv, reactors, bus, target, f"{tag}-r{n}")
        reactors.append(reactor(bus, mvar))
        plan.append({"bus": bus, "name": buses[bus]["name"], "kv": buses[bus]["baseKv"], "v_before": v,
                     "mvar": mvar, "v_after": vafter, "over_left": len(over(voltages(r), buses))})
        print(f"  reactor {n + 1}: bus {bus} {buses[bus]['name']} {buses[bus]['baseKv']} kV, {v:.4f} -> {vafter:.4f} pu"
              f" with {mvar:.1f} MVAr; {plan[-1]['over_left']} over left", flush=True)
    die(f"{tag}: still over-voltage after {cap} reactors")


def volt_var_bound(units, vm):
    """What Category B volt-var would absorb at the unity-power-factor voltages: Q(V) x rating summed over the units on
    a PQ bus. Absorbing lowers the voltage, so the settled absorption is at most this (the curve falls with V)."""
    q = 0.0
    n_active = 0
    cap = 0.0
    for u in units:
        if "control" not in u:
            continue
        v = vm[u["bus"]]
        frac = 0.0 if v <= 1.02 else (-0.44 if v >= 1.08 else -0.44 * (v - 1.02) / 0.06)
        q += frac * u["ratedMva"]
        cap += 0.44 * u["ratedMva"]
        if frac < 0:
            n_active += 1
    return {"units": sum(1 for u in units if "control" in u), "units_above_1_02": n_active, "q_mvar_at_unity_v": q,
            "absorb_capability_mvar": cap}


def describe(r, buses, label):
    vm = voltages(r)
    o = over(vm, buses)
    s = r["summary"]
    pvq = sum(x["q_mvar"] for x in r["tables"]["pv"])
    print(f"== {label}: vmin {s['vmin_pu']:.4f} vmax {s['vmax_pu']:.4f}, over {len(o)}, taps {s['tap_operations']},"
          f" PV Q {pvq:.1f} MVAr, settled {s['unsettled_steps'] == 0}", flush=True)
    for e, b, v in o[:12]:
        print(f"   {b} {buses[b]['name']} {buses[b]['baseKv']} kV {v:.4f} (+{e:.4f})")
    return {"vmin_pu": s["vmin_pu"], "vmax_pu": s["vmax_pu"], "settled": s["unsettled_steps"] == 0,
            "under": sum(1 for b, v in vm.items() if v < buses[b]["vminPu"]), "solves": r["tables"]["steps"][0]["solves"],
            "over": [{"bus": b, "name": buses[b]["name"], "kv": buses[b]["baseKv"], "vm": v} for e, b, v in o],
            "levels": level_summary(vm, buses), "taps": s["tap_operations"], "pv_q_mvar": pvq, "digest": s["digest"]}


def absorption_sweep(run, a, out, base_p):
    """The worst snapshot with every unit's absorption capability scaled down: where over-voltage appears, and the
    reactors (greedy) and the condensers that restore the limits at the lowest scale that still solves."""
    worst = out["worst"]
    df, pvs, com = SCENARIOS[worst]
    pv = pv_units(pvs * base_p, "unity")
    out["absorption_sweep"] = {}
    deepest = None
    for f in (0.75, 0.5, 0.25):
        name = f"{worst}-absorb{int(f * 100)}"
        v = build_variant(a.work, name, df, pvs, com, absorb_scale=f)
        b = bus_table(v["dir"])
        try:
            r = run.qsts(v["dir"], pv, [], name)
        except SystemExit as e:
            out["absorption_sweep"][str(f)] = {"failed": str(e)[-600:]}
            print(f"== {name}: did not solve", flush=True)
            break
        d = describe(r, b, name)
        vf = build_variant(a.work, name + "-pf", df, pvs, com, fold_pv=True, absorb_scale=f)
        try:
            d["reactive"] = reactive_balance(run.pf(vf["dir"], name), vf["dir"])
        except SystemExit as e:
            d["reactive_failed"] = str(e)[-600:]
        out["absorption_sweep"][str(f)] = d
        # A solution with buses under their lower limit at minimum demand is not a credible operating point (the
        # solver has landed on a low-voltage branch); the plan is made at the lowest scale without one.
        if d["under"] == 0:
            deepest = (f, name, v["dir"], b)
    if deepest is None:
        return
    f, name, vdir, b = deepest
    print(f"== reactor plan at absorption scale {f}", flush=True)
    plan, reactors, rfinal = greedy_reactors(run, vdir, pv, b, name + "-greedy", cap=60)
    sw = out["absorption_sweep"][str(f)]
    sw["reactor_plan"] = plan
    sw["reactor_total_mvar"] = sum(p["mvar"] for p in plan)
    sw["reactor_final"] = describe(rfinal, b, name + " with its reactor plan")
    # Condensers absorbing what each reactor absorbs at its final voltage (rating = MVAr x V^2), holding 1.0 pu.
    vm = voltages(rfinal)
    cons = [(p["bus"], p["mvar"] * vm[p["bus"]] ** 2) for p in plan]
    cv = build_variant(a.work, name + "-condensers", df, pvs, com, condensers=cons, absorb_scale=f)
    r = run.qsts(cv["dir"], pv, [], name + "-condensers")
    sw["condensers"] = describe(r, bus_table(cv["dir"]), name + " with condensers rated as the reactors absorb")
    sw["condenser_total_mva"] = sum(c[1] for c in cons)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gridsim", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--summary", required=True)
    ap.add_argument("--sweep-only", action="store_true", help="run only the absorption sweep, adding it to --summary")
    a = ap.parse_args()
    if a.sweep_only:
        with open(a.summary, encoding="utf-8") as f:
            out = json.load(f)
        run = Runner(a.gridsim, a.work)
        absorption_sweep(run, a, out, sum(d["pMw"] for d in load("demand.json")))
        out["runs"] = out.get("runs", 0) + run.runs
        with open(a.summary, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=1)
        print(f"sweep done: {run.runs} gridsim runs")
        return
    os.makedirs(a.work, exist_ok=True)
    run = Runner(a.gridsim, a.work)
    out = {"scenarios": {}}
    base_p = sum(d["pMw"] for d in load("demand.json"))

    # 1. The four snapshots, unity PV, no mitigation; pf equivalents for the reactive balance.
    for name, (df, pvs, com) in SCENARIOS.items():
        v = build_variant(a.work, name, df, pvs, com)
        buses = bus_table(v["dir"])
        r = run.qsts(v["dir"], pv_units(pvs * base_p, "unity"), [], name)
        d = describe(r, buses, name)
        vf = build_variant(a.work, name + "-pf", df, pvs, com, fold_pv=True)
        d["reactive"] = reactive_balance(run.pf(vf["dir"], name), vf["dir"])
        d["variant"] = {k: v[k] for k in v if k != "dir"}
        print("   reactive", {k: round(x, 1) if isinstance(x, float) else x for k, x in d["reactive"].items()})
        print("   variant", d["variant"])
        out["scenarios"][name] = d

    # 2. Mitigation on the worst snapshot (most buses over; ties by the larger excess).
    worst = max(out["scenarios"], key=lambda n: (len(out["scenarios"][n]["over"]), out["scenarios"][n]["vmax_pu"]))
    df, pvs, com = SCENARIOS[worst]
    out["worst"] = worst
    wdir = os.path.join(a.work, worst)
    buses = bus_table(wdir)
    print(f"== mitigation on {worst}", flush=True)
    plan, reactors, rfinal = greedy_reactors(run, wdir, pv_units(pvs * base_p, "unity"), buses, worst + "-greedy")
    out["reactor_plan"] = plan
    out["reactor_total_mvar"] = sum(p["mvar"] for p in plan)
    out["reactor_final"] = describe(rfinal, buses, worst + " with the reactor plan")
    # The plan applied to every other snapshot.
    out["plan_elsewhere"] = {}
    for name, (df2, pvs2, com2) in SCENARIOS.items():
        if name == worst:
            continue
        r = run.qsts(os.path.join(a.work, name), pv_units(pvs2 * base_p, "unity"), reactors, name + "-plan")
        out["plan_elsewhere"][name] = describe(r, bus_table(os.path.join(a.work, name)), name + " with the plan")
    # Volt-var embedded PV instead of reactors (midday snapshots only; the night has no PV).
    out["volt_var"] = {}
    for name, (df2, pvs2, com2) in SCENARIOS.items():
        if pvs2 > 0:
            vdir = os.path.join(a.work, name)
            units = pv_units(pvs2 * base_p, "volt-var", held_buses(vdir))
            base_r = json.load(open(os.path.join(a.work, name + ".out.json"), encoding="utf-8"))
            out["volt_var"][name] = {"bound": volt_var_bound(units, voltages(base_r))}
            print("   volt-var bound", name, out["volt_var"][name]["bound"], flush=True)
            if name == worst:
                r = run.qsts(vdir, units, [], name + "-voltvar", VOLT_VAR_SETTINGS)
                out["volt_var"][name]["run"] = describe(r, bus_table(vdir), name + " with volt-var PV")
    # Condensers at the reactor sites (same MVA as each reactor's MVAr, holding 1.0 pu).
    cv = build_variant(a.work, worst + "-condensers", df, pvs, com, condensers=[(p["bus"], p["mvar"]) for p in plan])
    r = run.qsts(cv["dir"], pv_units(pvs * base_p, "unity"), [], worst + "-condensers")
    out["condensers"] = describe(r, bus_table(cv["dir"]), worst + " with condensers at the reactor sites")
    # Sensitivity: the 132 kV branches whose charging is beyond any cable capped at the typical cable ratio.
    sv = build_variant(a.work, worst + "-capped", df, pvs, com, cap_charging=True)
    r = run.qsts(sv["dir"], pv_units(pvs * base_p, "unity"), [], worst + "-capped")
    out["capped"] = describe(r, bus_table(sv["dir"]), worst + " with suspect 132 kV charging capped")
    out["capped"]["capped_branches"] = sv["capped_branches"]
    sp = build_variant(a.work, worst + "-capped-pf", df, pvs, com, fold_pv=True, cap_charging=True)
    out["capped"]["reactive"] = reactive_balance(run.pf(sp["dir"], worst + "-capped"), sp["dir"])
    cplan, creactors, crfinal = greedy_reactors(run, sv["dir"], pv_units(pvs * base_p, "unity"), bus_table(sv["dir"]),
                                                worst + "-capped-greedy")
    out["capped"]["reactor_plan"] = cplan
    out["capped"]["reactor_total_mvar"] = sum(p["mvar"] for p in cplan)
    with open(a.summary, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)
    try:
        stress(run, a, out, worst, df, pvs, com, base_p)
    except SystemExit as e:
        out["noabsorb_failed"] = str(e)
        print("stress run stopped: " + str(e)[:2000], flush=True)
    absorption_sweep(run, a, out, base_p)
    out["runs"] = run.runs
    with open(a.summary, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)
    print(f"done: {run.runs} gridsim runs; summary {a.summary}")


def stress(run, a, out, worst, df, pvs, com, base_p):
    """No generator absorbs reactive power (lower limits 0; the slack still absorbs), on the worst snapshot."""
    nv = build_variant(a.work, worst + "-noabsorb", df, pvs, com, no_absorb=True)
    nb = bus_table(nv["dir"])
    r = run.qsts(nv["dir"], pv_units(pvs * base_p, "unity"), [], worst + "-noabsorb")
    out["noabsorb"] = describe(r, nb, worst + " with no generator absorption")
    npf = build_variant(a.work, worst + "-noabsorb-pf", df, pvs, com, fold_pv=True, no_absorb=True)
    out["noabsorb"]["reactive"] = reactive_balance(run.pf(npf["dir"], worst + "-noabsorb"), npf["dir"])
    nunits = pv_units(pvs * base_p, "volt-var", held_buses(nv["dir"]))
    out["noabsorb"]["volt_var_bound"] = volt_var_bound(nunits, voltages(r))
    print("   volt-var bound (no absorption)", out["noabsorb"]["volt_var_bound"], flush=True)
    nplan, nreactors, nfinal = greedy_reactors(run, nv["dir"], pv_units(pvs * base_p, "unity"), nb, worst + "-noabsorb-greedy", cap=120)
    out["noabsorb"]["reactor_plan"] = nplan
    out["noabsorb"]["reactor_total_mvar"] = sum(p["mvar"] for p in nplan)
    out["noabsorb"]["reactor_final"] = describe(nfinal, nb, worst + " with no generator absorption and its reactor plan")


if __name__ == "__main__":
    main()
