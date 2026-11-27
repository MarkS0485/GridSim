"""The case lab's case model, its MATPOWER writer, and the edits that refinement applies (steps).

A case is plain JSON (schema ``gridsim-mcp-caselab-case/1``): buses, generators (each with a quadratic cost for the OPF
modes) and branches (lines, cables and transformers), plus the family, the seed and the provenance tag. The same model
is what caselab_propose asks an AI to fill in.

A step is a small JSON edit (``{"op": "merge_leaf", "bus": 7}``). A case is fully described by where it came from (a
family and a seed, or a proposed JSON) and the steps applied to it, so a reproducer can be rebuilt byte for byte.
"""

import copy
import math

SCHEMA = "gridsim-mcp-caselab-case/1"

#: Tap step and range for transformer edits (GridSim's documented OLTC fallback, technical ch. 13).
TAP_STEP = 0.0125
TAP_MIN, TAP_MAX = 0.9, 1.1


def sig(x, n=6):
    """x rounded to n significant digits (so a written value is the same bytes everywhere)."""
    x = float(x)
    if x == 0 or not math.isfinite(x):
        return 0.0 if x == 0 else x
    return float(f"{x:.{n}g}")


def fmt(x):
    x = float(x)
    if x == int(x) and abs(x) < 1e15:
        return str(int(x))
    return format(x, ".12g")


# -- structure ----------------------------------------------------------------------------------------------------------

def slack_bus(case):
    return next(b["id"] for b in case["buses"] if b["type"] == 3)


def neighbours(case):
    adj = {b["id"]: set() for b in case["buses"]}
    for br in case["branches"]:
        adj[br["from"]].add(br["to"])
        adj[br["to"]].add(br["from"])
    return adj


def connected(case, branches=None):
    branches = case["branches"] if branches is None else branches
    ids = [b["id"] for b in case["buses"]]
    if not ids:
        return False
    adj = {i: set() for i in ids}
    for br in branches:
        adj[br["from"]].add(br["to"])
        adj[br["to"]].add(br["from"])
    seen, todo = {ids[0]}, [ids[0]]
    while todo:
        for n in adj[todo.pop()]:
            if n not in seen:
                seen.add(n)
                todo.append(n)
    return len(seen) == len(ids)


def degree(case):
    d = {b["id"]: 0 for b in case["buses"]}
    for br in case["branches"]:
        d[br["from"]] += 1
        d[br["to"]] += 1
    return d


def is_transformer(br):
    return br.get("kind") == "tx"


def has_off_nominal_tap(case):
    return any(is_transformer(br) and (br["tap"] != 1.0 or br.get("shift", 0.0) != 0.0) for br in case["branches"])


def size(case):
    """What 'smaller' means for a reproducer: buses, then branches, then generators, then non-default details."""
    detail = (sum(1 for b in case["buses"] if b["gs"] or b["bs"])
              + sum(1 for br in case["branches"] if is_transformer(br) and br["tap"] != 1.0))
    return (len(case["buses"]), len(case["branches"]), len(case["gens"]), detail)


def check_structure(case):
    """Problems with a case's structure (an empty list when it is well formed). Physics is GridSim's job: this only
    checks that the JSON can be written as a MATPOWER case."""
    errors = []
    if case.get("schema") != SCHEMA:
        errors.append(f"schema must be {SCHEMA}")
    name = str(case.get("name", ""))
    if not (name[:1].isalpha() and name.replace("_", "").isalnum() and len(name) <= 40):
        errors.append("name: a letter, then letters, digits and '_' (at most 40)")
    buses = case.get("buses") or []
    ids = [b.get("id") for b in buses]
    if not buses:
        errors.append("buses: at least one")
    if len(set(ids)) != len(ids) or not all(isinstance(i, int) and i > 0 for i in ids):
        errors.append("buses: ids must be distinct positive integers")
    if sum(1 for b in buses if b.get("type") == 3) != 1:
        errors.append("buses: exactly one slack bus (type 3)")
    for b in buses:
        if b.get("type") not in (1, 2, 3):
            errors.append(f"bus {b.get('id')}: type 1 (PQ), 2 (PV) or 3 (slack)")
        for k in ("pd", "qd", "gs", "bs", "vm", "va", "kv", "vmax", "vmin"):
            if not isinstance(b.get(k), (int, float)) or not math.isfinite(float(b.get(k))):
                errors.append(f"bus {b.get('id')}: {k} must be a finite number")
        if isinstance(b.get("kv"), (int, float)) and b["kv"] <= 0:
            errors.append(f"bus {b.get('id')}: kv must be above 0")
    idset = set(ids)
    gens = case.get("gens") or []
    for g in gens:
        if g.get("bus") not in idset:
            errors.append(f"generator {g.get('id')}: bus {g.get('bus')} is not a bus")
        for k in ("pg", "qg", "qmax", "qmin", "vg", "mbase", "pmax", "pmin"):
            if not isinstance(g.get(k), (int, float)):
                errors.append(f"generator {g.get('id')}: {k} must be a number")
        cost = g.get("cost")
        if not (isinstance(cost, list) and len(cost) == 3 and all(isinstance(c, (int, float)) for c in cost)):
            errors.append(f"generator {g.get('id')}: cost is [c2, c1, c0] (per MW^2 h, per MWh, per h)")
    if len({g.get("id") for g in gens}) != len(gens):
        errors.append("generators: ids must be distinct")
    for b in buses:
        if b.get("type") in (2, 3) and not any(g.get("bus") == b.get("id") for g in gens):
            errors.append(f"bus {b.get('id')}: a PV or slack bus needs a generator")
    brs = case.get("branches") or []
    if len({br.get("id") for br in brs}) != len(brs):
        errors.append("branches: ids must be distinct")
    for br in brs:
        if br.get("from") not in idset or br.get("to") not in idset or br.get("from") == br.get("to"):
            errors.append(f"branch {br.get('id')}: from and to must be two different buses")
        for k in ("r", "x", "b", "rate", "tap", "shift"):
            if not isinstance(br.get(k), (int, float)):
                errors.append(f"branch {br.get('id')}: {k} must be a number")
        if br.get("kind") not in ("ohl", "cable", "tx"):
            errors.append(f"branch {br.get('id')}: kind is ohl, cable or tx")
        if isinstance(br.get("x"), (int, float)) and br["x"] <= 0:
            errors.append(f"branch {br.get('id')}: x must be above 0")
    if not errors and not connected(case):
        errors.append("the network is not connected (one island, one slack)")
    return errors


# -- MATPOWER writer ----------------------------------------------------------------------------------------------------

def to_matpower(case):
    """The case as MATPOWER version 2 text, with a gencost block (polynomial, model 2). Same case, same bytes."""
    lines = [f"function mpc = {case['name']}",
             f"% Case lab ({case.get('family', 'proposed')}), {case.get('provenance', {}).get('tag', 'Synthetic')}: "
             "a generated case, not a real network.",
             "mpc.version = '2';",
             f"mpc.baseMVA = {fmt(case['baseMVA'])};",
             "%% bus_i type Pd Qd Gs Bs area Vm Va baseKV zone Vmax Vmin",
             "mpc.bus = ["]
    for b in case["buses"]:
        lines.append("\t" + "\t".join(fmt(v) for v in (b["id"], b["type"], b["pd"], b["qd"], b["gs"], b["bs"], 1,
                                                         b["vm"], b["va"], b["kv"], 1, b["vmax"], b["vmin"])) + ";")
    lines += ["];", "%% bus Pg Qg Qmax Qmin Vg mBase status Pmax Pmin", "mpc.gen = ["]
    for g in case["gens"]:
        lines.append("\t" + "\t".join(fmt(v) for v in (g["bus"], g["pg"], g["qg"], g["qmax"], g["qmin"], g["vg"],
                                                         g["mbase"], 1, g["pmax"], g["pmin"])) + ";")
    lines += ["];", "%% fbus tbus r x b rateA rateB rateC ratio angle status angmin angmax", "mpc.branch = ["]
    for br in case["branches"]:
        ratio = br["tap"] if is_transformer(br) else 0
        lines.append("\t" + "\t".join(fmt(v) for v in (br["from"], br["to"], br["r"], br["x"], br["b"], br["rate"],
                                                         br["rate"], br["rate"], ratio, br["shift"], 1, -360, 360))
                     + ";")
    lines += ["];", gencost_block(case)]
    return "\n".join(lines) + "\n"


def gencost_block(case):
    rows = ["%% gencost: model startup shutdown n c2 c1 c0", "mpc.gencost = ["]
    for g in case["gens"]:
        c2, c1, c0 = g["cost"]
        rows.append("\t" + "\t".join(fmt(v) for v in (2, 0, 0, 3, c2, c1, c0)) + ";")
    rows.append("];")
    return "\n".join(rows)


# -- steps --------------------------------------------------------------------------------------------------------------

class StepRefused(ValueError):
    """The step does not apply to this case (the shrink search skips it)."""


def _bus(case, bid):
    for b in case["buses"]:
        if b["id"] == bid:
            return b
    raise StepRefused(f"no bus {bid}")


def _branch(case, brid):
    for br in case["branches"]:
        if br["id"] == brid:
            return br
    raise StepRefused(f"no branch {brid}")


def _gen(case, gid):
    for g in case["gens"]:
        if g["id"] == gid:
            return g
    raise StepRefused(f"no generator {gid}")


def apply_step(case, step):
    """A new case with one step applied. Raises StepRefused when the step does not apply."""
    c = copy.deepcopy(case)
    op = step.get("op")
    if op == "merge_leaf":
        b = _bus(c, step["bus"])
        if b["type"] == 3:
            raise StepRefused("the slack bus is kept")
        if any(g["bus"] == b["id"] for g in c["gens"]):
            raise StepRefused("a bus with a generator is not merged (drop the generator first)")
        links = [br for br in c["branches"] if b["id"] in (br["from"], br["to"])]
        if len(links) != 1:
            raise StepRefused("not a leaf bus")
        br = links[0]
        parent = _bus(c, br["to"] if br["from"] == b["id"] else br["from"])
        for k in ("pd", "qd", "gs", "bs"):
            parent[k] = sig(parent[k] + b[k])
        c["buses"] = [x for x in c["buses"] if x["id"] != b["id"]]
        c["branches"] = [x for x in c["branches"] if x["id"] != br["id"]]
    elif op == "drop_branch":
        br = _branch(c, step["branch"])
        rest = [x for x in c["branches"] if x["id"] != br["id"]]
        if not connected(c, rest):
            raise StepRefused("removing it would split the network")
        c["branches"] = rest
    elif op == "nominal_tap":
        br = _branch(c, step["branch"])
        if not is_transformer(br) or (br["tap"] == 1.0 and br["shift"] == 0.0):
            raise StepRefused("not an off-nominal transformer")
        br["tap"], br["shift"] = 1.0, 0.0
    elif op == "drop_gen":
        g = _gen(c, step["gen"])
        bus = _bus(c, g["bus"])
        if bus["type"] == 3 and sum(1 for x in c["gens"] if x["bus"] == bus["id"]) == 1:
            raise StepRefused("the slack generator is kept")
        c["gens"] = [x for x in c["gens"] if x["id"] != g["id"]]
        if bus["type"] == 2 and not any(x["bus"] == bus["id"] for x in c["gens"]):
            bus["type"] = 1
    elif op == "drop_shunt":
        b = _bus(c, step["bus"])
        if not (b["gs"] or b["bs"]):
            raise StepRefused("no shunt at this bus")
        b["gs"] = b["bs"] = 0.0
    elif op == "scale_load":
        f = float(step["factor"])
        if not 0 < f <= 3:
            raise StepRefused("factor in (0, 3]")
        for b in c["buses"]:
            b["pd"], b["qd"] = sig(b["pd"] * f), sig(b["qd"] * f)
    elif op == "move_tap":
        br = _branch(c, step["branch"])
        if not is_transformer(br):
            raise StepRefused("not a transformer")
        t = round(br["tap"] + int(step["steps"]) * TAP_STEP, 6)
        if not TAP_MIN - 1e-9 <= t <= TAP_MAX + 1e-9:
            raise StepRefused("tap outside 0.9-1.1")
        br["tap"] = t
    elif op == "tighten_q":
        g = _gen(c, step["gen"])
        f = float(step["factor"])
        if not 0 < f < 1:
            raise StepRefused("factor in (0, 1)")
        g["qmax"], g["qmin"] = sig(g["qmax"] * f), sig(g["qmin"] * f)
    else:
        raise StepRefused(f"unknown step {op!r}")
    c.setdefault("steps", []).append(dict(step))
    return c


def apply_steps(case, steps):
    for s in steps:
        case = apply_step(case, s)
    return case


def reductions(case):
    """Every step that makes the case smaller, in a fixed order: merge a leaf bus (highest id first), drop a
    generator, drop a branch that closes a loop, set a transformer to nominal tap, drop a shunt."""
    out = []
    deg = degree(case)
    gen_buses = {g["bus"] for g in case["gens"]}
    for b in sorted(case["buses"], key=lambda x: -x["id"]):
        if b["type"] != 3 and deg[b["id"]] == 1 and b["id"] not in gen_buses:
            out.append({"op": "merge_leaf", "bus": b["id"]})
    slack = slack_bus(case)
    for g in sorted(case["gens"], key=lambda x: x["id"], reverse=True):
        if g["bus"] != slack:
            out.append({"op": "drop_gen", "gen": g["id"]})
    for br in sorted(case["branches"], key=lambda x: x["id"], reverse=True):
        rest = [x for x in case["branches"] if x["id"] != br["id"]]
        if connected(case, rest):
            out.append({"op": "drop_branch", "branch": br["id"]})
    for br in case["branches"]:
        if is_transformer(br) and (br["tap"] != 1.0 or br["shift"] != 0.0):
            out.append({"op": "nominal_tap", "branch": br["id"]})
    for b in case["buses"]:
        if b["gs"] or b["bs"]:
            out.append({"op": "drop_shunt", "bus": b["id"]})
    return out


def perturbations(case, rng, k=4):
    """Seeded steps around a case (the neighbourhood of a finding): scale the load, move a tap by one step, tighten a
    generator's Q limits, remove a loop branch. At most one of each kind; k caps the total."""
    out = [{"op": "scale_load", "factor": rng.choice([0.8, 1.2])}]
    txs = [br for br in case["branches"] if is_transformer(br)]
    if txs:
        out.append({"op": "move_tap", "branch": rng.choice(txs)["id"], "steps": rng.choice([-1, 1])})
    gens = [g for g in case["gens"] if g["qmax"] > g["qmin"]]
    if gens:
        out.append({"op": "tighten_q", "gen": rng.choice(gens)["id"], "factor": 0.5})
    loops = [br for br in case["branches"]
             if connected(case, [x for x in case["branches"] if x["id"] != br["id"]])]
    if loops:
        out.append({"op": "drop_branch", "branch": rng.choice(loops)["id"]})
    return out[:k]
