"""Seeded case families for the case lab: small, plausible networks of the kind a student or a small distribution or
local study would build, and a few larger transmission-like ones.

Families:
- ``radial``: an 11 kV feeder behind a 33/11 kV primary transformer (OHL and cable sections, aggregate loads, an
  optional embedded generator, an optional capacitor, an optional 11/0.4 kV LV tail).
- ``meshed``: a small 33 kV network fed from one or two 132 kV grid supply buses through two or three grid
  transformers (a ring plus ties, an optional embedded generator, an optional capacitor).
- ``transmission``: an IEEE-like 5-14 bus network at 132 or 275 kV (sometimes 15-30 buses), PV/PQ mix with Q limits
  from a 0.85 lagging / 0.95 leading capability, an optional lower voltage level behind tapped transformers, shunts.

Every value is drawn from a ranged typical value (per-km line and cable constants by class, transformer impedance on
rating and X/R by rating, demand power factor, tap step) and the case as a whole is tagged Synthetic: chosen for a
generated network, not taken from a real one. The ranges are the maintainer's working table (not yet reviewed); they
are used here only to keep generated cases plausible and are not offered as reference data. Nothing is fitted to the
solver: a case that does not pass GridSim's gates is recorded as a generator reject with its reason, never adjusted.

The same family and seed always give the same case (and so the same bytes).
"""

import math

from .model import SCHEMA, sig
from .rng import Rng

FAMILIES = ("radial", "meshed", "transmission")

#: Per-km positive-sequence ranges by voltage class: R1 ohm/km, X1 ohm/km, B1 uS/km (OHL) or C1 nF/km (cable).
OHL = {11: ((0.2, 1.2), (0.30, 0.40), (3.0, 3.6)), 33: ((0.15, 0.6), (0.32, 0.42), (2.8, 3.5)),
       132: ((0.06, 0.25), (0.37, 0.43), (2.6, 3.0)), 275: ((0.03, 0.07), (0.29, 0.36), (3.2, 4.0)),
       400: ((0.015, 0.035), (0.25, 0.33), (3.5, 4.5))}
CABLE = {11: ((0.06, 0.32), (0.08, 0.14), (250, 500)), 33: ((0.05, 0.25), (0.09, 0.16), (150, 350)),
         132: ((0.015, 0.06), (0.10, 0.22), (130, 280))}
#: LV cable: no row in the typical-value table; order-of-magnitude values for a 0.4 kV distributor (Synthetic).
LV_CABLE = ((0.1, 0.6), (0.07, 0.09))
#: Continuous OHL rating per circuit by class, MVA (order-of-magnitude placeholders). Cables carry no rating here
#: (cable ratings come from a paid standard): rate 0, unlimited.
OHL_RATING = {11: (3, 8), 33: (15, 40), 132: (90, 200), 275: (700, 1200), 400: (2000, 3500)}
#: Transformer impedance on rating (%) by rated power (MVA upper bound): the typical range above the IEC 60076-5 minimum.
TX_Z = ((0.63, (4.0, 6.0)), (1.25, (5.0, 6.0)), (2.5, (6.0, 7.0)), (6.3, (7.0, 9.0)), (25.0, (8.0, 15.0)),
        (40.0, (10.0, 18.0)), (63.0, (11.0, 20.0)), (100.0, (12.5, 20.0)), (1e9, (13.0, 25.0)))
TX_XR = ((1.0, (2.0, 6.0)), (10.0, (6.0, 15.0)), (100.0, (15.0, 40.0)), (1e9, (30.0, 60.0)))
#: Off-nominal tap positions drawn for transformers (whole steps of 0.0125 pu within 0.9-1.1), nominal twice as likely.
TAPS = (1.0, 1.0, 0.9875, 1.0125, 0.975, 1.025)
DEMAND_PF = (0.95, 0.99)
F_HZ = 50.0
BASE = 100.0

NOTES = ["Synthetic: generated network, not a real one; every value drawn from a ranged typical value",
         "lines and cables: per-km R1, X1, B1 (C1) by voltage class; transformers: impedance on rating and X/R by "
         "rating; demand power factor 0.95-0.99 lagging; taps in steps of 0.0125 pu within 0.9-1.1",
         "cables carry no thermal rating (rate 0): cable ratings come from a paid standard and are not reproduced",
         "generator costs are Synthetic (quadratic), for the OPF modes only"]


def _vlim(kv):
    """Voltage limits by class: +/-10 % at 132 kV and above, +/-6 % below, +10/-6 % at LV."""
    if kv >= 132:
        return 1.10, 0.90
    if kv < 1:
        return 1.10, 0.94
    return 1.06, 0.94


def _tx_ranges(rating):
    z = next(r for lim, r in TX_Z if rating <= lim)
    xr = next(r for lim, r in TX_XR if rating <= lim)
    return z, xr


class _Builder:
    def __init__(self, family, seed, rng):
        self.rng = rng
        self.case = {"schema": SCHEMA, "name": f"cl_{family[:5]}_{seed & 0xFFFFFFFF:08x}", "family": family,
                     "seed": seed, "baseMVA": BASE, "frequencyHz": F_HZ, "buses": [], "gens": [], "branches": [],
                     "provenance": {"tag": "Synthetic", "notes": list(NOTES)}}
        self._br = 0
        self._gen = 0

    def bus(self, kv, role, btype=1, vm=1.0):
        bid = len(self.case["buses"]) + 1
        vmax, vmin = _vlim(kv)
        self.case["buses"].append({"id": bid, "type": btype, "pd": 0.0, "qd": 0.0, "gs": 0.0, "bs": 0.0, "vm": vm,
                                   "va": 0.0, "kv": float(kv), "vmax": vmax, "vmin": vmin, "role": role})
        return bid

    def get(self, bid):
        return self.case["buses"][bid - 1]

    def load(self, bid, p_lo, p_hi):
        r = self.rng
        p = r.uniform(p_lo, p_hi, 4)
        pf = r.uniform(*DEMAND_PF, 4)
        b = self.get(bid)
        b["pd"] = sig(b["pd"] + p)
        b["qd"] = sig(b["qd"] + p * math.tan(math.acos(pf)))

    def line(self, f, t, kv, kind, km):
        r = self.rng
        zb = kv * kv / BASE
        if kind == "ohl":
            (r1, x1, b1), rate = OHL[kv], r.uniform(*OHL_RATING[kv], 1)
            R, X, B = r.uniform(*r1), r.uniform(*x1), r.uniform(*b1)
        elif kv < 1:
            (r1, x1), rate = LV_CABLE, 0.0
            R, X, B = r.uniform(*r1), r.uniform(*x1), 0.0
        else:
            (r1, x1, c1), rate = CABLE[kv], 0.0
            R, X = r.uniform(*r1), r.uniform(*x1)
            B = 2 * math.pi * F_HZ * r.uniform(*c1) * 1e-3     # nF/km -> uS/km
        return self._add(f, t, sig(R * km / zb), sig(X * km / zb), sig(B * 1e-6 * km * zb), rate, 0.0, kind,
                         round(km, 3))

    def transformer(self, f, t, rating, taps=TAPS):
        r = self.rng
        (zlo, zhi), (xlo, xhi) = _tx_ranges(rating)
        z = r.uniform(zlo, zhi, 4) / 100.0 * BASE / rating
        xr = r.uniform(xlo, xhi, 3)
        rr = z / math.sqrt(1 + xr * xr)
        return self._add(f, t, sig(rr), sig(rr * xr), 0.0, float(rating), r.choice(taps), "tx", None)

    def _add(self, f, t, rr, x, b, rate, tap, kind, km):
        self._br += 1
        bid = f"b{self._br}"
        row = {"id": bid, "from": f, "to": t, "r": rr, "x": x, "b": b, "rate": rate, "tap": tap if kind == "tx" else 1.0,
               "shift": 0.0, "kind": kind}
        if km is not None:
            row["lengthKm"] = km
        self.case["branches"].append(row)
        return bid

    def gen(self, bid, pmax, pg, qmax, qmin, vg, btype=2):
        r = self.rng
        self._gen += 1
        self.case["gens"].append({"id": f"g{self._gen}", "bus": bid, "pg": sig(pg), "qg": 0.0, "qmax": sig(qmax),
                                  "qmin": sig(qmin), "vg": vg, "mbase": sig(max(pmax / 0.85, 1.0)),
                                  "pmax": sig(pmax), "pmin": 0.0,
                                  "cost": [r.uniform(0.001, 0.05, 4), r.uniform(10.0, 50.0, 2), 0.0]})
        b = self.get(bid)
        if b["type"] != 3:
            b["type"] = btype
        b["vm"] = vg

    def total_load(self):
        return sum(b["pd"] for b in self.case["buses"])

    def scale_loads(self, factor):
        for b in self.case["buses"]:
            b["pd"], b["qd"] = sig(b["pd"] * factor), sig(b["qd"] * factor)

    def slack_gen(self, bid, vm):
        total = self.total_load()
        cap = max(2.0 * total, 5.0)
        self.gen(bid, cap, 0.0, cap, -cap, vm, btype=3)


def _radial(b):
    r = b.rng
    vm = r.uniform(1.0, 1.03, 3)
    src = b.bus(33, "source 33 kV", 3, vm)
    rating = r.choice([7.5, 10.0, 12.0, 15.0, 20.0, 24.0])
    bb = b.bus(11, "primary 11 kV busbar")
    b.transformer(src, bb, rating)
    feeder = [bb]
    n = r.randint(3, 10)
    parent_of = {}
    for _ in range(n):
        parent = feeder[-1] if r.chance(0.6) else r.choice(feeder)
        k = b.bus(11, "feeder 11 kV")
        kind = "ohl" if r.chance(0.5) else "cable"
        b.line(parent, k, 11, kind, r.uniform(0.3, 2.0, 3))
        parent_of[k] = parent
        feeder.append(k)
        b.load(k, 0.1, 0.8)
    if r.chance(0.3):                                   # an LV tail behind a distribution transformer
        at = r.choice(feeder[1:])
        lv_rating = r.choice([0.315, 0.5, 0.8])
        lv = b.bus(0.4, "LV 0.4 kV board")
        b.transformer(at, lv, lv_rating, taps=(1.0, 1.0, 0.975, 1.025))
        prev = lv
        for _ in range(r.randint(1, 3)):
            k = b.bus(0.4, "LV 0.4 kV")
            b.line(prev, k, 0.4, "cable", r.uniform(0.03, 0.15, 3))
            b.load(k, 0.01, 0.05)
            prev = k
    # Design rules (before any solve): the primary transformer carries at most half its rating, and every OHL section
    # at most 80 % of its rating counting the demand downstream of it (cables carry no rating here).
    factor = min(1.0, 0.5 * rating / max(b.total_load(), 1e-9))
    for br in b.case["branches"]:
        if br["kind"] == "ohl" and br["rate"] > 0 and br["to"] in parent_of:
            down, todo = 0.0, [br["to"]]
            while todo:
                k = todo.pop()
                down += b.get(k)["pd"]
                todo += [c for c, p in parent_of.items() if p == k]
            if down > 0:
                factor = min(factor, 0.8 * br["rate"] / down)
    if factor < 1.0:
        b.scale_loads(factor)
    if r.chance(0.4):                                   # an embedded generator on the feeder
        at = r.choice(feeder[1:])
        pmax = r.uniform(0.5, 2.0, 3)
        b.gen(at, pmax, pmax * r.uniform(0.5, 0.9, 3), 0.33 * pmax, -0.33 * pmax, r.uniform(1.0, 1.02, 3))
    if r.chance(0.3):                                   # a capacitor at a remote feeder bus
        at = feeder[-1]
        b.get(at)["bs"] = r.uniform(0.3, 1.0, 3)
    b.slack_gen(src, vm)


def _meshed(b):
    r = b.rng
    vm = r.uniform(1.0, 1.03, 3)
    gsp = [b.bus(132, "grid supply 132 kV", 3, vm)]
    if r.chance(0.5):
        gsp.append(b.bus(132, "grid supply 132 kV"))
        b.line(gsp[0], gsp[1], 132, "ohl", r.uniform(5.0, 30.0, 2))
    ntx = r.randint(2, 3)
    m = r.randint(max(4, ntx), 9)
    mv = [b.bus(33, "primary 33 kV") for _ in range(m)]
    ratings = 0.0
    for i in range(ntx):
        rating = r.choice([45.0, 60.0, 90.0])
        ratings += rating
        b.transformer(gsp[i % len(gsp)], mv[i], rating)
    pairs = set()
    for i in range(m):                                  # a ring through the 33 kV buses
        a, c = mv[i], mv[(i + 1) % m]
        if m == 2 and i == 1:
            break
        pairs.add(frozenset((a, c)))
        kind = "ohl" if r.chance(0.6) else "cable"
        b.line(a, c, 33, kind, r.uniform(2.0, 12.0, 2) if kind == "ohl" else r.uniform(1.0, 6.0, 2))
    for _ in range(r.randint(0, 2)):                    # ties across the ring
        a, c = r.sample(mv, 2)
        if frozenset((a, c)) in pairs:
            continue
        pairs.add(frozenset((a, c)))
        b.line(a, c, 33, "ohl", r.uniform(3.0, 15.0, 2))
    for k in mv:
        b.load(k, 2.0, 10.0)
    if b.total_load() > 0.5 * ratings:
        b.scale_loads(0.5 * ratings / b.total_load())
    if r.chance(0.6):
        at = r.choice(mv)
        pmax = r.uniform(5.0, 20.0, 2)
        b.gen(at, pmax, pmax * r.uniform(0.5, 0.9, 3), 0.33 * pmax, -0.33 * pmax, r.uniform(1.0, 1.03, 3))
    if r.chance(0.4):
        b.get(r.choice(mv))["bs"] = r.uniform(2.0, 5.0, 2)
    b.slack_gen(gsp[0], vm)


def _transmission(b):
    r = b.rng
    kv = r.choice([132, 275])
    n = r.randint(15, 30) if r.chance(0.15) else r.randint(5, 14)
    n_low = r.randint(1, max(1, n // 4)) if (r.chance(0.5) and n >= 6) else 0
    n_high = n - n_low
    vm = r.uniform(1.0, 1.04, 3)
    hv = [b.bus(kv, f"transmission {kv} kV", 3 if i == 0 else 1, vm if i == 0 else 1.0) for i in range(n_high)]
    km = (20.0, 100.0) if kv == 275 else (10.0, 50.0)
    pairs = set()
    for i in range(1, n_high):                          # a random spanning tree ...
        j = r.randint(0, i - 1)
        pairs.add(frozenset((hv[i], hv[j])))
        b.line(hv[j], hv[i], kv, "ohl", r.uniform(*km, 2))
    for _ in range(round(0.4 * n_high)):               # ... plus meshing lines
        a, c = r.sample(hv, 2)
        if frozenset((a, c)) in pairs:
            continue
        pairs.add(frozenset((a, c)))
        b.line(a, c, kv, "ohl", r.uniform(*km, 2))
    low_kv = 132 if kv == 275 else 33
    low = []
    for _ in range(n_low):                              # a lower level behind tapped transformers
        k = b.bus(low_kv, f"sub-transmission {low_kv} kV")
        b.transformer(r.choice(hv), k, r.choice([120.0, 180.0, 240.0]) if kv == 275 else r.choice([45.0, 60.0, 90.0]))
        low.append(k)
    p_load = (50.0, 300.0) if kv == 275 else (10.0, 80.0)
    p_low = (10.0, 80.0) if kv == 275 else (2.0, 10.0)
    n_gen = max(1, round(0.3 * n_high))
    gen_at = r.sample(hv[1:], min(n_gen, n_high - 1)) if n_high > 1 else []
    for k in hv[1:]:
        if k not in gen_at:
            b.load(k, *p_load)
    for k in low:
        b.load(k, *p_low)
    if not any(x["pd"] for x in b.case["buses"]):
        b.load(hv[-1], *p_load)
    total = b.total_load()
    pmaxes = [r.uniform(0.6, 1.4, 3) * 1.4 * total / (len(gen_at) + 1) for _ in gen_at]
    share = 0.8 * total / (sum(pmaxes) + 1.4 * total / (len(gen_at) + 1))
    for k, pmax in zip(gen_at, pmaxes):
        q_lag = pmax * math.tan(math.acos(0.85))
        q_lead = pmax * math.tan(math.acos(0.95))
        b.gen(k, pmax, pmax * share, q_lag, -q_lead, r.uniform(1.0, 1.04, 3))
    for _ in range(r.randint(0, 2)):
        k = r.choice(hv + low)
        b.get(k)["bs"] = (r.uniform(20.0, 80.0, 1) if r.chance(0.6) else -r.uniform(20.0, 60.0, 1)) * (
            1.0 if b.get(k)["kv"] >= 132 else 0.1)
    b.slack_gen(hv[0], vm)


_BUILD = {"radial": _radial, "meshed": _meshed, "transmission": _transmission}


def generate(family, seed):
    """One case of a family from a 64-bit seed."""
    if family not in _BUILD:
        raise ValueError(f"family is one of {', '.join(FAMILIES)}")
    b = _Builder(family, seed, Rng(seed))
    _BUILD[family](b)
    return b.case
