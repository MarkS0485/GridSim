"""Tuning: the values a user may change at run time, through GridSim's own command-line options only.

The list is ``tunables.json`` (``gridsim-mcp-tunables/1``), shipped with the plugin. A ``tuning`` map given to solve_pf,
study, run_verb or reproduce_check is checked against it strictly before anything runs: an unknown key, a key of another
verb, a value of the wrong type or outside its safe range, or a missing companion key is refused. A valid map becomes the
verb's own flags. Any value that differs from the default marks the result TUNED, and a STANDARD key marks it
off-standard as well. A flag that is on the list may not be given in the plain arguments too: it goes through tuning, so
it is always marked.

Nothing here can reach a guard. The list holds no guard settings, and a key, flag or value naming one is refused
whatever the list says (``_GUARD_WORDS``), so a mistaken edit of the list cannot open one either.
"""

import json
import math
import re
from functools import lru_cache
from pathlib import Path

from .allowlist import Refused, canonical, check_flag

SCHEMA = "gridsim-mcp-tunables/1"
TUNABLES_FILE = Path(__file__).resolve().parent / "tunables.json"
CLASSES = ("PHYSICS", "STANDARD", "NUMERICS")

#: Words that name a guard: never a tuning key, flag or value, whatever the list says.
_GUARD_WORDS = re.compile(r"horizon|169|utc|now|clock|lake|gda|licen[cs]e|bus[-_ ]?limit|bus[-_ ]?cap|telemetry|"
                          r"build[-_ ]?status|release|sbom|audit|journal", re.IGNORECASE)


@lru_cache(maxsize=1)
def load():
    """The tunables document, checked: schema, classes, unique keys, no guard words."""
    doc = json.loads(TUNABLES_FILE.read_text(encoding="utf-8"))
    if doc.get("schema") != SCHEMA:
        raise ValueError(f"tunables.json is not {SCHEMA}")
    seen = set()
    for t in doc["tunables"]:
        if t["class"] not in ("STANDARD", "NUMERICS"):
            raise ValueError(f"tunable {t['key']}: class {t['class']} is never tunable")
        if t["key"] in seen:
            raise ValueError(f"tunable {t['key']} is listed twice")
        seen.add(t["key"])
        if _GUARD_WORDS.search(t["key"]) or _GUARD_WORDS.search(t["flag"]):
            raise ValueError(f"tunable {t['key']} names a guard")
        if t["class"] == "STANDARD" and not t.get("standard"):
            raise ValueError(f"tunable {t['key']} is STANDARD but names no standard")
    for r in doc.get("rebuild_allowlist", []):
        if r["class"] != "NUMERICS":
            raise ValueError(f"rebuild entry {r['symbol']}: only NUMERICS constants may be rebuilt")
        if _GUARD_WORDS.search(r["symbol"]) or _GUARD_WORDS.search(r["file"]):
            raise ValueError(f"rebuild entry {r['symbol']} names a guard")
    return doc


def by_key():
    return {t["key"]: t for t in load()["tunables"]}


def for_verb(verb):
    v = canonical(verb)
    return {k: t for k, t in by_key().items() if v in t["verbs"]}


def _refuse(msg):
    raise Refused(f"REFUSED (tuning): {msg}. Nothing was run. The tunable values are listed by tunables_list.")


def _number(key, t, value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        _refuse(f"{key} takes a number, not {value!r}")
    x = float(value)
    if not math.isfinite(x):
        _refuse(f"{key} must be a finite number")
    return x


def _check_range(key, t, x, given):
    lo, hi = t.get("min"), t.get("max")
    if "exclusive_min" in t and not x > t["exclusive_min"]:
        _refuse(f"{key} = {x!r} is outside its safe range (above {t['exclusive_min']})")
    if "exclusive_max" in t and not x < t["exclusive_max"]:
        _refuse(f"{key} = {x!r} is outside its safe range (below {t['exclusive_max']})")
    if lo is not None and x < lo:
        _refuse(f"{key} = {x!r} is outside its safe range ({lo} to {hi})")
    if hi is not None and x > hi:
        _refuse(f"{key} = {x!r} is outside its safe range ({lo} to {hi})")
    if "max_key" in t:
        other = given.get(t["max_key"], by_key()[t["max_key"]]["default"])
        if other is not None and x > float(other):
            _refuse(f"{key} = {x!r} must be at most {t['max_key']} ({other!r})")
    if "min_key" in t:
        other = given.get(t["min_key"], by_key()[t["min_key"]]["default"])
        if other is not None and not x > float(other):
            _refuse(f"{key} = {x!r} must be above {t['min_key']} ({other!r})")


def _value(key, t, value, given):
    """The checked value (the type the list says) and its flag tokens."""
    kind = t["type"]
    if kind == "boolean":
        if not isinstance(value, bool):
            _refuse(f"{key} takes true or false, not {value!r}")
        return value, ([t["flag"]] if value else [])
    if kind == "enum":
        if not isinstance(value, str) or value not in t["values"]:
            _refuse(f"{key} takes one of {', '.join(t['values'])}, not {value!r}")
        return value, [t["flag"], value]
    if kind == "number_or_off" and value == "off":
        return "off", [t["flag"], "off"]
    if kind == "integer":
        if isinstance(value, bool) or not isinstance(value, int):
            if isinstance(value, float) and value.is_integer():
                value = int(value)
            else:
                _refuse(f"{key} takes a whole number, not {value!r}")
        _check_range(key, t, float(value), given)
        return value, [t["flag"], str(value)]
    x = _number(key, t, value)
    _check_range(key, t, x, given)
    return x, [t["flag"], repr(x)]


def default_of(key, given):
    t = by_key()[key]
    for alt in t.get("default_if", []):
        if all(given.get(k, by_key()[k]["default"]) == v for k, v in alt["when"].items()):
            return alt["default"]
    return t["default"]


class Applied:
    """A checked tuning map: the flags to add, the non-default values and the standards no longer complied with."""

    def __init__(self, flags=(), tuned=None, off_standard=()):
        self.flags = list(flags)
        self.tuned = dict(tuned or {})
        self.off_standard = list(off_standard)

    @property
    def is_tuned(self):
        return bool(self.tuned)

    def marker(self):
        """'tuned: k=v, ...' (sorted keys), or None."""
        if not self.tuned:
            return None
        return "tuned: " + ", ".join(f"{k}={_show(v)}" for k, v in sorted(self.tuned.items()))

    def evidence(self):
        if not self.tuned:
            return {}
        out = {"tuned": self.marker(), "tuning": dict(sorted(self.tuned.items()))}
        if self.off_standard:
            out["off_standard"] = list(self.off_standard)
        return out


def _same(value, default):
    """True when a checked value is the default (so the run is not tuned)."""
    if isinstance(value, bool) or isinstance(default, bool):
        return value is default
    if default is None or isinstance(value, str) or isinstance(default, str):
        return value == default
    return float(value) == float(default)


def _show(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    return str(v) if not isinstance(v, float) else repr(v)


def apply(verb, tuning, args=()):
    """Check ``tuning`` for ``verb`` (and that ``args`` do not carry a tunable flag of the verb). Returns Applied.
    Raises Refused, before anything runs, on any problem."""
    v = canonical(verb)
    own = for_verb(v)
    flags_of_verb = {t["flag"] for t in own.values()}
    for a in args or ():
        name = str(a).split("=", 1)[0]
        if name in flags_of_verb:
            key = next(k for k, t in own.items() if t["flag"] == name)
            _refuse(f"{name} is a tunable value of {v}; pass it as tuning {{\"{key}\": ...}} so the result is marked")
    if not tuning:
        return Applied()
    if not isinstance(tuning, dict):
        _refuse("tuning is a map of key to value")
    for key, value in tuning.items():
        if _GUARD_WORDS.search(str(key)) or (isinstance(value, str) and _GUARD_WORDS.search(value)):
            raise Refused("REFUSED (tuning): guards are not tunable and are not available through the GridSim MCP "
                          "server. Nothing was run.")
        if key not in by_key():
            _refuse(f"'{key}' is not a tunable value")
        if key not in own:
            _refuse(f"'{key}' belongs to {', '.join(by_key()[key]['verbs'])}, not {v}")
    given = dict(tuning)
    flags, tuned, off = [], {}, []
    for key in sorted(given):
        t = own[key]
        for need, want in t.get("requires", {}).items():
            if given.get(need, by_key()[need]["default"]) != want:
                _refuse(f"{key} needs {need} = {want!r}")
        if t.get("requires_also") and t["requires_also"] not in given:
            _refuse(f"{key} must be given together with {t['requires_also']}")
        checked, tokens = _value(key, t, given[key], given)
        for tok in tokens:
            if tok.startswith("--"):
                check_flag(tok)
        flags += tokens
        if not _same(checked, default_of(key, given)):
            tuned[key] = checked
            if t["class"] == "STANDARD":
                note = f"off-standard: {t['standard']} no longer complied with"
                if note not in off:
                    off.append(note)
    return Applied(flags, tuned, off)


def listing(verb=None):
    """The list as served by tunables_list: everything, or the keys of one verb."""
    doc = load()
    if verb is None:
        return doc
    v = canonical(verb)
    return {"schema": doc["schema"], "classes": doc["classes"], "verb": v,
            "tunables": [t for t in doc["tunables"] if v in t["verbs"]]}
