"""AI-designed cases: the template caselab_propose hands the calling AI, and the check of what comes back.

The server never calls a model. The AI fills the template (a gridsim-mcp-caselab-case/1 JSON), the server checks its
structure, writes it, and runs the same generator gates as a generated case (convert to JSON v2, validate,
physics-check, plausible voltages). An accepted case is stored under caselab/proposed/ and can be named in caselab_run.
"""

import copy
import hashlib
import json

from . import generator, model

INSTRUCTIONS = [
    "Design one small, plausible network of the kind a student or a small distribution or local study would build "
    "(a radial MV feeder, a small meshed 33/132 kV network, an IEEE-like 5-30 bus system). Fill the template and send "
    "it back as caselab_propose(case=...).",
    "Aim at a place where tools might differ for a stated reason (an off-nominal tap, a phase shift, a PV bus near its "
    "Q limit, a cable with large charging, a loop with mixed X/R), and say the reason in 'intent'.",
    "Units: baseMVA 100; pd, qd, pg, pmax, qmax, qmin in MW and MVAr; gs, bs in MW and MVAr at 1 pu; r, x, b in pu on "
    "100 MVA and the bus base kV; rate in MVA (0 = unlimited); tap is the off-nominal ratio of a transformer (kind "
    "'tx'; 1.0 nominal), shift in degrees; cost [c2, c1, c0] per MW^2 h, per MWh, per h.",
    "One slack bus (type 3) with a generator; PV buses (type 2) need a generator; the network must be connected.",
    "Keep values inside the ranges of their class (per-km constants, transformer impedance on rating, demand power "
    "factor 0.95-0.99). Tag the case Synthetic: it is a designed network, not a real one. Never name, locate or "
    "attribute a real facility.",
    "No fitting: do not change values until a solve converges. If the gates reject the case, the reason is returned; "
    "fix the modelling error the reason names, or design another case.",
]


def template():
    """A filled 3-bus example of the case schema (a 33/11 kV transformer with an off-nominal tap and one feeder
    section): the AI replaces it with its own design."""
    return {
        "schema": model.SCHEMA, "name": "proposed_example", "family": "proposed", "intent": "why this case",
        "baseMVA": 100.0, "frequencyHz": 50.0,
        "buses": [
            {"id": 1, "type": 3, "pd": 0.0, "qd": 0.0, "gs": 0.0, "bs": 0.0, "vm": 1.02, "va": 0.0, "kv": 33.0,
             "vmax": 1.06, "vmin": 0.94, "role": "source 33 kV"},
            {"id": 2, "type": 1, "pd": 0.0, "qd": 0.0, "gs": 0.0, "bs": 0.0, "vm": 1.0, "va": 0.0, "kv": 11.0,
             "vmax": 1.06, "vmin": 0.94, "role": "primary 11 kV busbar"},
            {"id": 3, "type": 1, "pd": 2.0, "qd": 0.5, "gs": 0.0, "bs": 0.0, "vm": 1.0, "va": 0.0, "kv": 11.0,
             "vmax": 1.06, "vmin": 0.94, "role": "feeder 11 kV"}],
        "gens": [{"id": "g1", "bus": 1, "pg": 0.0, "qg": 0.0, "qmax": 10.0, "qmin": -10.0, "vg": 1.02, "mbase": 100.0,
                  "pmax": 10.0, "pmin": 0.0, "cost": [0.01, 20.0, 0.0]}],
        "branches": [
            {"id": "b1", "from": 1, "to": 2, "r": 0.066, "x": 0.66, "b": 0.0, "rate": 15.0, "tap": 0.9875,
             "shift": 0.0, "kind": "tx"},
            {"id": "b2", "from": 2, "to": 3, "r": 0.545, "x": 0.347, "b": 4.8e-06, "rate": 6.0, "tap": 1.0, "shift": 0.0,
             "kind": "ohl", "lengthKm": 1.2}],
        "provenance": {"tag": "Synthetic", "notes": ["designed by the AI for the case lab"]},
    }


def ranges():
    """The class ranges the generator draws from, for the AI to stay inside."""
    return {"ohlPerKm": {str(k): {"r1": v[0], "x1": v[1], "b1_uS": v[2]} for k, v in generator.OHL.items()},
            "cablePerKm": {str(k): {"r1": v[0], "x1": v[1], "c1_nF": v[2]} for k, v in generator.CABLE.items()},
            "ohlRatingMva": {str(k): v for k, v in generator.OHL_RATING.items()},
            "transformerZPercentOnRating": [{"uptoMva": lim, "range": r} for lim, r in generator.TX_Z],
            "transformerXR": [{"uptoMva": lim, "range": r} for lim, r in generator.TX_XR],
            "demandPowerFactor": generator.DEMAND_PF, "tapStep": model.TAP_STEP,
            "tapRange": [model.TAP_MIN, model.TAP_MAX],
            "tag": "Synthetic: working ranges, not reference data"}


def normalise(case):
    """The case as stored: defaults filled, family 'proposed', numbers as floats rounded to 6 significant digits."""
    c = copy.deepcopy(case)
    c.setdefault("schema", model.SCHEMA)
    c["family"] = "proposed"
    c.setdefault("baseMVA", 100.0)
    c.setdefault("frequencyHz", 50.0)
    c.setdefault("provenance", {"tag": "Synthetic", "notes": ["designed by the AI for the case lab"]})
    for b in c.get("buses") or []:
        for k in ("pd", "qd", "gs", "bs", "va"):
            b.setdefault(k, 0.0)
        b.setdefault("vm", 1.0)
        if isinstance(b.get("kv"), (int, float)):
            vmax, vmin = generator._vlim(b["kv"])
            b.setdefault("vmax", vmax)
            b.setdefault("vmin", vmin)
    for g in c.get("gens") or []:
        for k, v in (("qg", 0.0), ("pmin", 0.0), ("vg", 1.0), ("mbase", 100.0)):
            g.setdefault(k, v)
    for br in c.get("branches") or []:
        for k, v in (("b", 0.0), ("rate", 0.0), ("tap", 1.0), ("shift", 0.0)):
            br.setdefault(k, v)
    for group in ("buses", "gens", "branches"):
        for row in c.get(group) or []:
            for k, v in list(row.items()):
                if isinstance(v, float) or (isinstance(v, int) and not isinstance(v, bool) and k not in
                                            ("id", "type", "bus", "from", "to")):
                    row[k] = model.sig(v)
            if "cost" in row and isinstance(row["cost"], list):
                row["cost"] = [model.sig(x) if isinstance(x, (int, float)) else x for x in row["cost"]]
    return c


def digest(case):
    return hashlib.sha256(json.dumps(case, sort_keys=True).encode("utf-8")).hexdigest()
