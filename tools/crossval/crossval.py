#!/usr/bin/env python3
"""Cross-validate a GridSim solve against pandapower, to machine epsilon.

Reads a GridSim solve-export JSON (schema `gridsim-solve-export/1`), rebuilds the identical network as a
MATPOWER-style ppc, solves it with pandapower's Newton power flow, and diffs pandapower's bus voltage
magnitudes and angles against GridSim's. Branch flows are NOT compared (they follow from the bus state; R103
corrected this docstring, which used to promise them). Writes a diff JSON and exits non-zero if any bus quantity
exceeds the tolerance.

This is the "our solver is validated against a named industry tool" evidence. It is deliberately dumb: it does
NOT trust GridSim's math - it rebuilds from the raw inputs GridSim recorded and re-solves independently.

N243 (October 2026, Mark's machine-epsilon rule: "Machine epsilon to known tools is a must ... it is hard proof the
numerical core is executing the math correctly"). The comparison used to pass at 1e-3 pu. It is now made
like-for-like and judged at machine-epsilon level:

  * The same mathematical problem on both sides (see build_ppc and solve_pandapower):
      - Generators are aggregated per bus before pandapower sees them. GridSim holds ONE voltage-controlled
        injection per bus: P and Q summed over the bus's generators, reactive limits summed (aggregate Qmin / Qmax),
        the setpoint of the LAST generator listed (VoltageSetpoint, MATPOWER's rule). pandapower's from_ppc keeps
        only the FIRST generator on a bus as a voltage-controlling `gen`, turns every other one into a static `sgen`
        with no reactive range, and takes the FIRST generator's setpoint. On uk (five generators at bus 8) that pinned
        bus 8 at the first unit's 2000 MVAr instead of the bus's 7000 MVAr: 6.64e-4 pu at bus 108 (N243).
      - Transformer line-charging B is moved onto bus shunts, exactly as MATPOWER stamps it (b / 2 / tap^2 at the
        from bus, b / 2 at the to bus). from_ppc turns a transformer's B into a magnetising current of the same
        magnitude (i0_percent = |B|), which flips the sign of a capacitive B and moves it across the tap.
      - A transformer written LV -> HV is written the other way round (tap 1 / t, shift -phi, r and x times t^2: the
        same four admittances). MATPOWER's tap is always at the FROM bus; from_ppc puts the ratio on the HV winding,
        so a step-up branch whose from bus is the LV side was solved with its tap on the wrong side (case24: five
        transformers, 3.85e-2 pu at bus 9).
      - pandapower's pi transformer model (MATPOWER's), constant-power loads except the export's own ZIP loads,
        no distributed slack, the same Q-limit enforcement (PV->PQ, one way, at the aggregate limits).
      - A guard proves the translation: the admittance matrix pandapower actually solved is compared with MATPOWER's
        makeYbus on the ppc built here, entry by entry. A difference above 4096 machine epsilons of the largest
        entry stops the run (exit 4): the comparison would not be like-for-like, so no verdict is given.
  * The tightest tolerance on both sides. Newton's state error is about J^-1 times its final mismatch, so a solve
    stopped at the usual 1e-8 pu leaves an error of 1e-12 to 1e-9 pu in the state; that is a convergence setting, not
    an error in the maths. pandapower is run down a ladder of tolerances (1e-8, 1e-9, ... 1e-15 pu) and the
    tightest one that converges is kept (the floor is set by rounding in V conj(Y V), about 1e-15 to 1e-12 pu by
    case). The `gridsim crossval` verb does the same on GridSim's side; an export written by another verb at the
    default 1e-8 pu shows its own convergence residual (`gridsim_max_mismatch_pu` in the report) and will not
    reach machine epsilon for that reason alone.
  * The tolerance: 4096 machine epsilons (2^-40, about 9.09e-13), the same number the impedance, small-signal and
    harmonic sidecars use (N005, N142), applied to |V| in pu and to the angle in RADIANS (the deg figure printed is
    the same number times 180 / pi, about 5.21e-11 deg).
  * A case that cannot reach it for a documented, specific reason is listed in ALLOWANCES with its own measured
    bound and the reason; there is no blanket loose tolerance. A case in the list is still judged against its own
    bound, and the report says it was judged under an allowance.

Usage:
    python crossval.py <solve-export.json> [--tol 9.09e-13] [--out diff.json]

Voltage-dependent (ZIP) loads in the export are given to pandapower as its own ZIP load model (D-DOC-044, October
2026; before, pandapower solved every load as constant power, so a ZIP case compared two different networks).

Requires: pandapower (pip install pandapower). Exit codes: 0 = within tol, 1 = mismatch, 3 = unreadable export,
4 = pandapower solve failed (or the translation guard fired), 77 = NumPy or pandapower absent (nothing validated);
Python itself exits 2 on a usage error.
"""
import argparse
import json
import math
import sys

from sidecar_common import EXIT_SKIPPED, export_case, missing

# MATPOWER ppc columns this sidecar fills that the solve export does not carry (R214). The voltage limits are only
# used by an OPF; the power flow cross-validated here never reads them. They equal GridSim's statutory band
# (Core/Analysis/VoltageLimits.StatutoryLowPu/HighPu, 0.90/1.10 pu).
PPC_VMAX_PU = 1.1
PPC_VMIN_PU = 0.9
# 'No limit' sentinel for a zero branch rating (a 0 rating trips a pandapower from_ppc masking bug). Large enough
# never to bind in a power flow. (N243: an ABSENT generator Q / P limit is no longer this sentinel but GridSim's own
# model default, +/- infinity, see build_ppc: on the 10,000 MVA uk base a 9999 MVAr "no limit" could bind.)
PPC_UNLIMITED = 9999.0

# N243: the default tolerance, in machine epsilons. 2**-52 is the IEEE-754 double machine epsilon
# (sys.float_info.epsilon). 4096 eps = 2**-40 = 9.094947017729282e-13: the number the impedance, small-signal and
# harmonic sidecars use (N005, N142). Applied to |V| in pu and to the voltage angle in radians.
EPS = 2.0 ** -52
DEFAULT_TOL_EPS = 4096
DEFAULT_TOL = DEFAULT_TOL_EPS * EPS

# N243: the translation guard. pandapower's solved admittance matrix must equal MATPOWER's makeYbus on our ppc to this
# many machine epsilons of the largest |Y| entry. The conversion to pandapower's elements (ohms, nF, percent) and
# back rounds each parameter a few times, a few eps relative; a modelling difference (a tap on the wrong side, a sign
# flipped on a charging B) is many orders of magnitude larger.
YBUS_GUARD_EPS = 4096

# N243: pandapower's Newton tolerance ladder (pu; pandapower's `tolerance_mva` is compared with the per-unit mismatch
# vector directly, so it is a per-unit number on net.sn_mva despite its name). Each rung is a fresh flat-start solve;
# the tightest one that converges is kept. Below the case's rounding floor Newton cannot converge, so the ladder
# stops at the first rung that fails after one has succeeded.
PP_TOLERANCE_LADDER_PU = (1e-8, 1e-9, 1e-10, 1e-11, 1e-12, 1e-13, 1e-14, 1e-15)

# N243: an export whose own final mismatch is above this (pu) was solved at an everyday tolerance, not to the case's
# floor (the crossval verb's tightest rung reached 1e-10 pu or better on every bundled case); a mismatch verdict on it
# says so, because its state error alone (about J^-1 times that mismatch) is far above machine epsilon.
LOOSE_EXPORT_MISMATCH_PU = 1e-10

# N243: documented allowances, keyed by the export's case label (meta.Case, see sidecar_common.export_case). Each
# entry is a case that cannot reach DEFAULT_TOL for a specific, stated reason, with its OWN measured bound (|V| pu,
# angle rad), never a blanket loose tolerance. Empty means every covered case is judged at DEFAULT_TOL.
ALLOWANCES = {
    # Measured 2026-10-05 (pandapower 3.5.5, NumPy 2.4.6, SciPy 1.18.1): GridSim vs pandapower |V| 3.850e-13 pu
    # (bus 2195), angle 2.800e-12 rad = 1.604e-10 deg (bus 2195), the Q-limit outcome the same on both sides (102
    # pinned buses; six more pinned by pandapower alone at Qmin = 0 with Q = 0, boundary cases, the same state).
    "gb-network-2224": {
        "dvm_pu": 2e-12,
        "dva_rad": 4e-12,
        "reason": (
            "floating-point floor, not a modelling difference: the admittance matrix spans up to 4.1e5 pu per row "
            "(near-zero-impedance branches), so the power mismatch cannot be driven below about 4e-11 pu on either "
            "side (GridSim's tightest converging tolerance is 1e-10 pu, final mismatch 3.86e-11 pu; pandapower's is "
            "1e-10 pu too). pandapower disagrees with ITSELF by 1.602e-12 pu and 3.557e-12 rad between a flat start "
            "and a DC start on this case; the bound is that self-spread rounded up (2e-12 pu, 4e-12 rad)."),
    },
}


def load_export(path):
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    schema = doc.get("schema", "")
    if not schema.startswith("gridsim-solve-export/"):
        raise ValueError(f"not a gridsim solve-export (schema={schema!r})")
    return doc


def _num(value, default):
    """A numeric JSON field as float; absent or null gives `default`. (Infinity / -Infinity arrive as floats.)"""
    if value is None:
        return default
    return float(value)


def aggregate_generators(model):
    """N243: GridSim's per-bus generator model, as one row per bus.

    NewtonShell.BuildBusSpec sums Pg, Qg, Qmin and Qmax over the generators at a bus and holds the bus at the setpoint
    of the LAST generator listed there (VoltageSetpoint: MATPOWER's rule). One row per bus with those sums gives
    pandapower exactly that model: one voltage-controlling machine whose reactive range is the bus's aggregate range.
    Pmin / Pmax / mBase are summed too (a power flow never reads them). Absent limits take GridSim's model defaults
    (Generator: Qmin = -inf, Qmax = +inf, Pmin = 0, Pmax = +inf, Vg = 1.0). Rows keep the order in which each bus
    first appears."""
    rows = {}
    for g in model.get("generators", []):
        # D-DOC-067: the solve export now lists the whole fleet, each unit with "inService". An out-of-service unit is
        # not solved (GridSim's Generators list leaves it out), so it is not part of the bus's generator model either.
        # Older exports omit the field and list only in-service units.
        if g.get("inService", True) is False:
            continue
        b = g["bus"]
        row = rows.get(b)
        if row is None:
            row = rows[b] = {"bus": b, "pg": 0.0, "qg": 0.0, "qmin": 0.0, "qmax": 0.0, "pmin": 0.0, "pmax": 0.0,
                             "mBase": 0.0, "vg": 1.0, "units": 0}
        row["pg"] += _num(g.get("pg"), 0.0)
        row["qg"] += _num(g.get("qg"), 0.0)
        row["qmin"] += _num(g.get("qmin"), -math.inf)
        row["qmax"] += _num(g.get("qmax"), math.inf)
        row["pmin"] += _num(g.get("pmin"), 0.0)
        row["pmax"] += _num(g.get("pmax"), math.inf)
        row["mBase"] += _num(g.get("mBase"), _num(model.get("baseMva"), 100.0))
        # The last generator at the bus sets the voltage (GridSim's VoltageSetpoint and MATPOWER alike). A 0 or
        # absent Vg falls back to 1.0 pu, as the old translation did.
        row["vg"] = _num(g.get("vg"), 1.0) or 1.0
        row["units"] += 1
    return list(rows.values())


def is_pandapower_trafo(tap, shift_deg):
    """pandapower's from_ppc makes a branch a transformer element when its ratio is neither 0 nor 1 or it has a phase
    shift (converter/pypower/from_ppc._branch_to_which). The same test, so the charging move and the tap side apply
    to exactly the branches that become transformer elements."""
    return (tap != 0.0 and tap != 1.0) or shift_deg != 0.0


def build_ppc(model, result=None):
    """Reconstruct a MATPOWER ppc dict from the recorded per-unit network inputs.

    N243: generators aggregated per bus (aggregate_generators); a slack bus with no generator gets a zero-output row
    holding the bus's own Vm (GridSim's BuildBusSpec does the same); the transformer charging B moved onto bus shunts
    (see the module docstring); the slack angle taken from GridSim's result when given (the slack angle is an input
    of the solve, so the two tools share the reference and no rotation is needed). Returns (ppc, id_to_row, info),
    info counting what the translation did."""
    import numpy as np

    # GridSim serialises BusType.ToString() -> "Pq" / "Pv" / "Slack"; match case-insensitively.
    type_map = {"pq": 1, "pv": 2, "slack": 3}
    buses = model["buses"]
    base_mva = _num(model.get("baseMva"), 100.0)
    id_to_row = {b["id"]: i for i, b in enumerate(buses)}
    slack_va = {}
    if result is not None:
        slack_va = {b["id"]: float(b["vaDeg"]) for b in result.get("buses", [])}

    bus = np.zeros((len(buses), 13))
    for i, b in enumerate(buses):
        bus[i, 0] = b["id"]
        bus[i, 1] = type_map.get(str(b["type"]).lower(), 1)
        bus[i, 2] = b.get("pd", 0.0)
        bus[i, 3] = b.get("qd", 0.0)
        bus[i, 4] = b.get("gs", 0.0)
        bus[i, 5] = b.get("bs", 0.0)
        bus[i, 6] = 1                       # area
        bus[i, 7] = b.get("vm", 1.0) or 1.0
        # va: the slack's reference angle from GridSim's result (an input it held fixed), else 0.
        bus[i, 8] = slack_va.get(b["id"], 0.0) if bus[i, 1] == 3 else 0.0
        bus[i, 9] = b.get("baseKv", 1.0) or 1.0
        bus[i, 10] = 1                      # zone
        bus[i, 11] = PPC_VMAX_PU            # vmax
        bus[i, 12] = PPC_VMIN_PU            # vmin

    rows = aggregate_generators(model)
    units = sum(r["units"] for r in rows)
    with_gen = {r["bus"] for r in rows}
    # A slack bus needs a generator row in a ppc. GridSim holds a generator-less slack at the bus's stored Vm with
    # unlimited reactive output (BuildBusSpec: Vm = Vg only when the bus has a generator); give it that.
    for b in buses:
        if str(b["type"]).lower() == "slack" and b["id"] not in with_gen:
            rows.append({"bus": b["id"], "pg": 0.0, "qg": 0.0, "qmin": -math.inf, "qmax": math.inf, "pmin": 0.0,
                         "pmax": math.inf, "mBase": base_mva, "vg": b.get("vm", 1.0) or 1.0, "units": 0})
    gen = np.zeros((max(1, len(rows)), 21))
    for i, g in enumerate(rows):
        gen[i, 0] = g["bus"]
        gen[i, 1] = g["pg"]
        gen[i, 2] = g["qg"]
        gen[i, 3] = g["qmax"]
        gen[i, 4] = g["qmin"]
        gen[i, 5] = g["vg"]
        gen[i, 6] = g["mBase"] or base_mva
        gen[i, 7] = 1                       # status
        gen[i, 8] = g["pmax"]
        gen[i, 9] = g["pmin"]

    branches = model["branches"]
    branch = np.zeros((len(branches), 13))
    for i, e in enumerate(branches):
        branch[i, 0] = e["from"]
        branch[i, 1] = e["to"]
        branch[i, 2] = e.get("r", 0.0)
        branch[i, 3] = e.get("x", 0.0)
        branch[i, 4] = e.get("b", 0.0) or 0.0
        branch[i, 5] = (e.get("rateAMva", 0.0) or 0.0) or PPC_UNLIMITED  # 0 rating trips a from_ppc masking bug
        branch[i, 8] = e.get("tap", 0.0) or 0.0       # 0 = no transformer (MATPOWER convention)
        branch[i, 9] = e.get("phaseShiftDeg", 0.0) or 0.0
        branch[i, 10] = 0 if e.get("inService", True) is False else 1   # status (older exports omit it: in service)

    # N243: the export as a plain MATPOWER case, kept for the translation guard (ybus_translation_check compares
    # pandapower's solved matrix with makeYbus on THIS, not on the rewritten form below).
    raw = {"version": "2", "baseMVA": base_mva, "bus": bus.copy(), "gen": gen.copy(), "branch": branch.copy()}
    moved, reversed_count = rewrite_transformers_for_from_ppc(bus, branch, id_to_row, base_mva)

    info = {"generator_units": units, "generator_rows": len(rows), "trafo_charging_moved_to_shunts": moved,
            "trafos_written_hv_to_lv": reversed_count, "raw_ppc": raw}
    return {"version": "2", "baseMVA": base_mva, "bus": bus, "gen": gen, "branch": branch}, id_to_row, info


def rewrite_transformers_for_from_ppc(bus, branch, id_to_row, base_mva):
    """N243: rewrite each branch that from_ppc will turn into a transformer element so the element it builds has
    exactly MATPOWER's admittances. Changes `bus` and `branch` in place; returns (charging moves, reversals).

    Two exact rewrites (the translation guard in diff() checks the result against makeYbus on the original rows):

      * Charging. MATPOWER stamps a branch's B as j b/2 / |tau|^2 on Yff and j b/2 on Ytt (tau = tap e^(j shift), tap
        0 read as 1). Those are pure diagonal terms, so bus shunts of the same admittance give the same matrix (bus Bs
        is in MVAr at 1 pu: Ybus diag += j Bs / baseMVA). from_ppc instead turns a transformer's B into a magnetising
        current of the same magnitude (i0_percent = |B|), which flips the sign of a capacitive B and moves it across
        the tap. An out-of-service branch stamps nothing.
      * Orientation. MATPOWER's tap is always at the FROM bus. from_ppc builds the element with its ratio on the
        higher-voltage end; for a branch whose from bus is the LOWER-voltage end (a step-up written LV -> HV, e.g.
        case24's 138 / 230 kV units) the ratio landed on the wrong winding (from_ppc's tap_side "lv" option does not
        change the matrix it builds: checked on pandapower 3.5.5). So the SAME branch is written the other way round:
        a from-side ideal transformer tau with series admittance ys has
            Yff = ys / |tau|^2, Yft = -ys / conj(tau), Ytf = -ys / tau, Ytt = ys,
        and the reversed branch with tau' = 1 / tau (tap 1 / t, shift -phi) and ys' = ys / |tau|^2 (r and x times
        t^2) has exactly those four entries with the ends swapped."""
    moved = reversed_count = 0
    for i in range(branch.shape[0]):
        tap, shift, b_total = float(branch[i, 8]), float(branch[i, 9]), float(branch[i, 4])
        if not is_pandapower_trafo(tap, shift):
            continue
        f_row, t_row = id_to_row[int(branch[i, 0])], id_to_row[int(branch[i, 1])]
        t = tap if tap != 0.0 else 1.0
        if b_total != 0.0:
            branch[i, 4] = 0.0
            if branch[i, 10] != 0:
                bus[f_row, 5] += b_total / 2.0 / (t * t) * base_mva
                bus[t_row, 5] += b_total / 2.0 * base_mva
            moved += 1
        if bus[t_row, 9] > bus[f_row, 9]:
            branch[i, 0], branch[i, 1] = branch[i, 1], branch[i, 0]
            branch[i, 2] = branch[i, 2] * t * t
            branch[i, 3] = branch[i, 3] * t * t
            branch[i, 8] = 1.0 / t
            branch[i, 9] = -shift
            reversed_count += 1
    return moved, reversed_count


def _resolve_from_ppc():
    """from_ppc moved across pandapower versions; find it wherever it lives."""
    try:
        from pandapower.converter.pypower import from_ppc  # pandapower >= 3.x
        return from_ppc
    except ImportError:
        pass
    try:
        from pandapower.converter import from_ppc  # pandapower < 3.x
        return from_ppc
    except ImportError:
        from pandapower.converter import V1_from_ppc  # last-resort alias
        return V1_from_ppc


def _pandapower_bus_ids(net):
    """GridSim bus id -> pandapower bus index (from_ppc keeps the ppc bus number in net.bus["name"])."""
    out = {}
    for idx in net.bus.index:
        name = net.bus.at[idx, "name"]
        try:
            bus_id = int(float(name)) if name is not None else None
        except (ValueError, TypeError):
            bus_id = None
        out[bus_id if bus_id is not None else idx] = idx
    return out


def apply_zip_loads(net, model):
    """D-DOC-044 (October 2026): give pandapower the export's voltage-dependent (ZIP) loads.

    from_ppc builds every load as constant power from the bus Pd / Qd, so before this a correct GridSim solve of a ZIP
    load was compared with a pandapower solve of a different (constant-power) network. GridSim draws
    P(V) = Pd (ap V^2 + bp V + cp); pandapower draws p_mw (z V^2 + i V + (1 - z - i)) with z, i its
    const_z_p_percent / const_i_p_percent over 100 (and the same for Q). The two are the same polynomial when
    p_mw = Pd s, z = ap / s and i = bp / s with s = ap + bp + cp (s = 1 for a ZIP whose triple sums to 1, the normal
    case, so p_mw stays Pd). A triple summing to 0 has no pandapower form and is refused, naming the bus.
    Returns the number of buses changed."""
    buses = [b for b in model.get("buses", []) if b.get("zip")]
    if not buses:
        return 0
    ids = _pandapower_bus_ids(net)
    for b in buses:
        z = b["zip"]
        idx = ids.get(b["id"])
        if idx is None:
            raise ValueError(f"ZIP bus {b['id']} is not in the pandapower network")
        loads = net.load.index[net.load.bus == idx]
        for comp, a_key, b_key, c_key, val_col, z_col, i_col in (
                ("P", "ap", "bp", "cp", "p_mw", "const_z_p_percent", "const_i_p_percent"),
                ("Q", "aq", "bq", "cq", "q_mvar", "const_z_q_percent", "const_i_q_percent")):
            a = float(z.get(a_key, 0.0) or 0.0)
            bb = float(z.get(b_key, 0.0) or 0.0)
            c = float(z.get(c_key, 1.0) if z.get(c_key) is not None else 1.0)
            s = a + bb + c
            if s == 0.0:
                raise ValueError(f"bus {b['id']}: ZIP {comp} coefficients sum to 0, which pandapower cannot represent")
            for li in loads:
                net.load.at[li, val_col] = float(net.load.at[li, val_col]) * s
                net.load.at[li, z_col] = 100.0 * a / s
                net.load.at[li, i_col] = 100.0 * bb / s
    return len(buses)


def _runpp(net, tol_pu, init):
    """One pandapower Newton solve with the like-for-like settings (N243). Raises when it does not converge."""
    import pandapower as pp
    pp.runpp(net, algorithm="nr", calculate_voltage_angles=True, init=init,
             max_iteration=100,
             tolerance_mva=tol_pu,          # compared with the per-unit mismatch (see PP_TOLERANCE_LADDER_PU)
             enforce_q_lims=True,           # GridSim's crossval solve enforces Q limits: PV -> PQ at the aggregate limit
             trafo_model="pi",              # MATPOWER's (and GridSim's) branch model; pandapower's default is "t"
             voltage_depend_loads=True,     # only the export's ZIP loads are voltage dependent (apply_zip_loads)
             distributed_slack=False,       # one slack bus takes the balance, as in GridSim's crossval solve
             numba=False)                   # the plain PYPOWER code path (numba is optional; same maths)


def solve_pandapower(ppc, model=None):
    """Build the pandapower net and solve it to the tightest tolerance on the ladder that converges (N243).

    Each rung is a fresh solve from a flat start (falling back to a DC-power-flow start for a stiff case that will
    not flat-start, as before). Returns (net, tolerance reached, iterations of that solve)."""
    from_ppc = _resolve_from_ppc()
    try:
        net = from_ppc(ppc, f_hz=50.0)
    except TypeError:
        net = from_ppc(ppc)  # some versions drop the f_hz kwarg
    if model is not None:
        apply_zip_loads(net, model)

    reached = None
    iterations = None
    last_error = None
    for tol_pu in PP_TOLERANCE_LADDER_PU:
        solved = False
        for init in ("flat", "dc"):
            try:
                _runpp(net, tol_pu, init)
                solved = True
                break
            except Exception as e:  # noqa: BLE001  (pandapower raises its own LoadflowNotConverged)
                last_error = e
        if not solved:
            break
        reached = tol_pu
        iterations = int(net._ppc.get("iterations", 0) or 0)
        res_bus = net.res_bus.copy()
        res_gen = net.res_gen.copy()
        internal_ybus = net._ppc["internal"]["Ybus"]
    if reached is None:
        raise RuntimeError(f"pandapower did not converge at {PP_TOLERANCE_LADDER_PU[0]:g} pu: {last_error}")
    # A failed rung leaves pandapower's result tables at the failed attempt; restore the converged one.
    net.res_bus = res_bus
    net.res_gen = res_gen
    net._crossval_ybus = internal_ybus
    return net, reached, iterations


def ybus_translation_check(net, ppc):
    """N243 guard: the admittance matrix pandapower solved vs MATPOWER's makeYbus on our ppc, in GridSim bus order.

    Returns (max |dY|, max |Y|). Both include the bus shunts; neither includes loads."""
    import numpy as np
    import scipy.sparse as sp
    from pandapower.pypower.makeYbus import makeYbus
    from pandapower.pypower.idx_brch import branch_cols
    from pandapower.pypower.idx_bus import bus_cols

    n = ppc["bus"].shape[0]
    ids = [int(x) for x in ppc["bus"][:, 0]]
    internal = {bid: k for k, bid in enumerate(ids)}
    bus = np.zeros((n, bus_cols))
    bus[:, :ppc["bus"].shape[1]] = ppc["bus"]
    bus[:, 0] = np.arange(n)
    br = np.zeros((ppc["branch"].shape[0], branch_cols))
    br[:, :ppc["branch"].shape[1]] = ppc["branch"]
    br[:, 0] = [internal[int(x)] for x in ppc["branch"][:, 0]]
    br[:, 1] = [internal[int(x)] for x in ppc["branch"][:, 1]]
    y_mp = sp.csr_matrix(makeYbus(ppc["baseMVA"], bus, br)[0])

    lookup = net._pd2ppc_lookups["bus"]
    pp_ids = _pandapower_bus_ids(net)
    perm = np.array([lookup[pp_ids[bid]] for bid in ids])
    y_pp = sp.csr_matrix(net._crossval_ybus)[perm, :][:, perm]
    d = abs(y_pp - y_mp)
    max_d = float(d.max()) if d.nnz else 0.0
    max_y = float(abs(y_mp).max()) if y_mp.nnz else 0.0
    return max_d, max_y


# GridSim's slack when it compares a PV bus's reactive output with its limits (NewtonShell.QLimitTolMva, MVAr): a bus
# whose output is past a limit by no more than this stays PV. pandapower pins on any excess at all.
GRIDSIM_Q_LIMIT_TOL_MVA = 1e-3


def _limit_label(q, qmin, qmax):
    """'fixed' when Qmin = Qmax (a unit with no reactive range: both tools hold it at that one value), else 'max' /
    'min' for the limit q sits on exactly, else None."""
    if qmin == qmax and q == qmax:
        return "fixed"
    if q == qmax:
        return "max"
    if q == qmin:
        return "min"
    return None


def pandapower_pinned_buses(net):
    """Bus id -> 'max' / 'min' / 'fixed' for the pandapower generators that ended pinned at a reactive limit.

    pandapower writes a pinned generator's Q as exactly its limit (fixedQg); with one row per bus (N243) a pinned
    row is a pinned bus. The slack's ext_grid is never limited (as in GridSim)."""
    out = {}
    ids = {idx: bid for bid, idx in _pandapower_bus_ids(net).items()}
    for gi in net.gen.index:
        label = _limit_label(float(net.res_gen.at[gi, "q_mvar"]), float(net.gen.at[gi, "min_q_mvar"]),
                             float(net.gen.at[gi, "max_q_mvar"]))
        if label is not None:
            out[ids.get(int(net.gen.at[gi, "bus"]))] = label
    return out


def gridsim_pinned_buses(doc):
    """Bus id -> 'max' / 'min' / 'fixed' for the buses GridSim's solve ended pinned at a reactive limit (declared PV,
    solved PQ), from the export's own trace (sidecar_common)."""
    from sidecar_common import pinned_q_limits, solved_bus_types
    solved, _ = solved_bus_types(doc)
    declared = {b["id"]: str(b["type"]).lower() for b in doc["model"]["buses"]}
    limits = pinned_q_limits(doc)
    ranges = {r["bus"]: (r["qmin"], r["qmax"]) for r in aggregate_generators(doc["model"])}
    out = {}
    for bid, t in solved.items():
        if t == "pq" and declared.get(bid) == "pv" and bid in limits:
            qmin, qmax = ranges.get(bid, (None, None))
            out[bid] = "fixed" if qmin is not None and qmin == qmax else limits[bid]
    return out


def q_limit_comparison(doc, net):
    """Compare the two tools' reactive-limit outcomes bus by bus (N243).

    A bus pinned by one tool and not the other is a BOUNDARY case, not a disagreement, when GridSim's own generator
    output there sits within GRIDSIM_Q_LIMIT_TOL_MVA of the limit concerned: GridSim kept it PV with Q a hair past (or
    on) the limit, pandapower pinned it at the limit, and the two states are the same to that tolerance (gb-network-2224:
    six units with Qmin = 0 whose bus solves at Q = 0). Returns (gridsim, pandapower, boundary bus ids, agrees)."""
    from sidecar_common import load_at_voltage
    gs = gridsim_pinned_buses(doc)
    pp_ = pandapower_pinned_buses(net)
    ranges = {r["bus"]: (r["qmin"], r["qmax"]) for r in aggregate_generators(doc["model"])}
    buses = {b["id"]: b for b in doc["model"]["buses"]}
    solved = {b["id"]: b for b in doc["result"]["buses"]}
    boundary, disagree = [], []
    for bid in sorted(set(gs) | set(pp_), key=str):
        if gs.get(bid) == pp_.get(bid):
            continue
        qmin, qmax = ranges.get(bid, (None, None))
        s = solved.get(bid)
        if qmin is None or s is None:
            disagree.append(bid)
            continue
        # The reactive power GridSim's generators produce at the bus: the net injection plus the load drawn at the
        # solved voltage (as NewtonShell.ApplyQLimits measures it).
        q_gen = float(s["qInjMvar"]) + load_at_voltage(buses[bid], float(s["vm"]))[1]
        near = min(abs(q_gen - qmax), abs(q_gen - qmin))
        (boundary if near <= GRIDSIM_Q_LIMIT_TOL_MVA else disagree).append(bid)
    return gs, pp_, boundary, not disagree


def diff(doc, tol):
    model = doc["model"]
    result = doc["result"]
    ppc, id_to_row, info = build_ppc(model, result)
    net, pp_tol, pp_iters = solve_pandapower(ppc, model)

    # N243 guard: the network pandapower solved must be the network GridSim solved.
    ybus_d, ybus_max = ybus_translation_check(net, info["raw_ppc"])   # against the UNREWRITTEN export rows
    ybus_bound = YBUS_GUARD_EPS * EPS * max(ybus_max, 1.0)
    if not ybus_d <= ybus_bound:
        raise RuntimeError(
            f"translation guard: pandapower's admittance matrix differs from MATPOWER's makeYbus on the same ppc by "
            f"{ybus_d:.3e} (bound {ybus_bound:.3e}); the two tools would not be solving the same network, so no "
            f"verdict is given")

    # pandapower keeps the original ppc bus number in net.bus["name"] after from_ppc; fall back to order.
    pp_by_id = _pandapower_bus_ids(net)

    # Bus voltage ANGLE is only defined up to a global reference. build_ppc gives pandapower GridSim's slack angle, so
    # the offset is normally exactly 0; it is still measured and removed, so a reference rotation is never an error.
    slack_ids = [bb["id"] for bb in model["buses"] if str(bb.get("type", "")).lower() == "slack"]
    va_offset = 0.0
    if slack_ids:
        sid = slack_ids[0]
        gs_slack = next((bb["vaDeg"] for bb in result["buses"] if bb["id"] == sid), 0.0)
        sp_idx = pp_by_id.get(sid)
        if sp_idx is not None:
            va_offset = float(net.res_bus.at[sp_idx, "va_degree"]) - gs_slack

    rows = []
    max_dvm = max_dva = 0.0
    worst_dvm_bus = worst_dva_bus = None
    # GR-441: every GridSim bus must be compared, with finite values on both sides. A bus pandapower did not return
    # used to be skipped, and a NaN difference never became the maximum (NaN > x is False), so a partial or NaN
    # result could be reported as agreeing. Either is now a comparison that cannot be made: no verdict (exit 4).
    missing_buses = [b["id"] for b in result["buses"] if pp_by_id.get(b["id"]) is None]
    if missing_buses:
        raise RuntimeError(f"{len(missing_buses)} GridSim bus(es) have no pandapower result (first: {missing_buses[:5]}); "
                           "the comparison would not cover the whole network")
    for b in result["buses"]:
        bid = b["id"]
        pidx = pp_by_id[bid]
        vm_pp = float(net.res_bus.at[pidx, "vm_pu"])
        va_pp = float(net.res_bus.at[pidx, "va_degree"]) - va_offset  # reference-aligned
        dvm = abs(vm_pp - b["vm"])
        dva = abs(va_pp - b["vaDeg"])
        if not (math.isfinite(dvm) and math.isfinite(dva)):
            raise RuntimeError(f"bus {bid}: a voltage difference is not finite (|V| {vm_pp!r} vs {b['vm']!r}, "
                               f"angle {va_pp!r} vs {b['vaDeg']!r} deg); no verdict can be given")
        # The worst bus for each quantity (the first one seen, when every difference is 0).
        if worst_dvm_bus is None or dvm > max_dvm:
            max_dvm, worst_dvm_bus = dvm, bid
        if worst_dva_bus is None or dva > max_dva:
            max_dva, worst_dva_bus = dva, bid
        rows.append({"bus": bid, "vm_gridsim": b["vm"], "vm_pandapower": vm_pp, "dvm": dvm,
                     "va_gridsim": b["vaDeg"], "va_pandapower": va_pp, "dva_deg": dva})
    max_dva_rad = math.radians(max_dva)

    # Q-limit outcome on both sides: the same buses must end pinned at the same limits, or the two tools solved
    # different final problems (named in the report, so the reason for a mismatch is never a mystery).
    gs_pinned, pp_pinned, q_boundary, pinned_agree = q_limit_comparison(doc, net)

    case = export_case(doc)   # D-DOC-126: meta.Case as the export writes it
    allowance = ALLOWANCES.get(case)
    tol_pu = tol_rad = tol
    if allowance is not None:
        tol_pu, tol_rad = max(tol, allowance["dvm_pu"]), max(tol, allowance["dva_rad"])
    ok = max_dvm <= tol_pu and max_dva_rad <= tol_rad
    return {
        "schema": "gridsim-crossval/1",
        "tool": "pandapower",
        "case": case,
        "tolerance": tol,                        # the machine-epsilon default (or --tol): |V| pu and angle rad
        "tolerance_eps": tol / EPS,
        "tolerance_pu": tol_pu,                  # what |V| was judged against (tolerance, or the allowance's bound)
        "tolerance_rad": tol_rad,
        "tolerance_deg": math.degrees(tol_rad),
        "allowance": allowance,                  # None, or {dvm_pu, dva_rad, reason}
        "zip_buses": sum(1 for b in model.get("buses", []) if b.get("zip")),   # D-DOC-044
        "buses_compared": len(rows),
        "max_dvm_pu": max_dvm,
        "max_dva_deg": max_dva,
        "max_dva_rad": max_dva_rad,
        "worst_dvm_bus": worst_dvm_bus,
        "worst_dva_bus": worst_dva_bus,
        "within_tolerance": bool(ok),
        # GR-910: the verdict also requires the same reactive-limit outcome (boundary buses excepted). Two tools that
        # pinned different buses solved different final problems, so agreement of their voltages would be chance.
        "passed": bool(ok and pinned_agree),
        "like_for_like": {
            "generator_units": info["generator_units"],
            "generator_rows_after_per_bus_aggregation": info["generator_rows"],
            "trafo_charging_moved_to_shunts": info["trafo_charging_moved_to_shunts"],
            "trafos_written_hv_to_lv": info["trafos_written_hv_to_lv"],
            "ybus_max_abs_diff": ybus_d,
            "ybus_max_abs": ybus_max,
            "pandapower_tolerance_pu": pp_tol,
            "pandapower_iterations": pp_iters,
            "gridsim_max_mismatch_pu": (doc.get("solve") or {}).get("maxMismatch"),
            "q_limit_pinned_gridsim": {str(k): v for k, v in sorted(gs_pinned.items())},
            "q_limit_pinned_pandapower": {str(k): v for k, v in sorted(pp_pinned.items())},
            "q_limit_boundary_buses": [str(b) for b in q_boundary],   # pinned by one tool only, same state
            "q_limit_outcome_agrees": bool(pinned_agree),
        },
        "rows": rows,
    }


# The conventional "skipped" exit code (automake / CTest): the dependency is absent, so nothing was validated.
EXIT_DEPENDENCY_ABSENT = 77
assert EXIT_DEPENDENCY_ABSENT == EXIT_SKIPPED   # the same code as sidecar_common's (one meaning everywhere)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol", type=float, default=DEFAULT_TOL,
                    help=f"|V| (pu) and angle (rad) tolerance (default {DEFAULT_TOL_EPS} machine epsilons = "
                         f"{DEFAULT_TOL:.3g})")
    ap.add_argument("--out")
    args = ap.parse_args()

    # Distinct exit codes so the caller can tell an ENVIRONMENT skip (77 = pandapower absent) from a genuine
    # validation failure (3 = unreadable export, 4 = pandapower solve crashed). Only 77 is a CI "never ran".
    # R218: the skip used to be exit 2, which Python ALSO uses for an argparse usage error and for "can't open
    # file", so a caller/sidecar argument mismatch was reported as a skip instead of a failure.
    try:
        doc = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval: cannot read export: {e}", file=sys.stderr)
        return 3
    # A missing NumPy or pandapower is a skip (exit 77) with the package and the interpreter named
    # (sidecar_common.missing); the export was read first, so a bad input is never reported as a skip.
    if missing("crossval", "numpy", "scipy", "pandapower") is None:
        return EXIT_DEPENDENCY_ABSENT

    try:
        report = diff(doc, args.tol)
    except Exception as e:  # noqa: BLE001
        print(f"crossval: pandapower solve failed: {e}", file=sys.stderr)
        return 4

    text = json.dumps(report, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    lfl = report["like_for_like"]
    if not report["passed"]:
        # Say WHY in plain words when the reason is visible from the report itself.
        if not lfl["q_limit_outcome_agrees"]:
            print("crossval: the two tools ended with different reactive-limit outcomes (see q_limit_pinned_*): they "
                  "solved different final problems.", file=sys.stderr)
        mm = lfl["gridsim_max_mismatch_pu"]
        if isinstance(mm, (int, float)) and mm > LOOSE_EXPORT_MISMATCH_PU:
            print(f"crossval: this export was converged only to a mismatch of {mm:.3e} pu; Newton's state error is "
                  f"about J^-1 times that, so it cannot agree to machine epsilon. `gridsim crossval <case>` solves to "
                  f"the tightest tolerance the case allows.", file=sys.stderr)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
