"""The verb allowlist and the refusals.

This is an ALLOWLIST: a verb runs only when it is listed in ALLOWED_VERBS. Everything else is refused. The list was built
from the verb index of manual chapter 19 (CLI reference). Verbs that take recorded, live or time-stamped inputs,
resident services and clock overrides are never exposed, whatever the caller asks. Every refusal says only that the
verb, flag or location is not available through the GridSim MCP server.

GB cases shipped with GridSim are offered to static verbs only (GB_STATIC_VERBS) and are never joined with time-stamped
or market data.
"""

import re

#: Verbs the MCP server may run through ``run_verb``. Static, case-in result-out verbs.
ALLOWED_VERBS = frozenset({
    # Contingency and analysis
    "trip", "screen", "nminus1", "nminus2", "n-1-1", "nminus1-sweep", "stress", "corrective-sequence",
    # Network analysis
    "pf", "ptdf", "lodf", "sensitivity", "qv-curve", "pv-contingency", "dc-contingency", "fault-level-n1",
    "line-constants", "dist3ph-mesh", "substation-state", "cgmes-export", "cgmes-conform", "cgmes-shacl", "dgs-import",
    "dcpf", "dcopf", "acopf", "acopf-full", "scuc", "scopf", "acopf-opt", "acscopf", "reconfig", "dist-opt", "reduce",
    "qv-modal", "transmission-security", "transfer-capability", "cgmes-study", "planning-study", "study-case",
    "case-diff", "result-diff", "report", "restoration-study", "reliability-study", "reliability-network",
    "probabilistic", "qsts", "hosting", "cpf", "taps", "restore", "losses", "dist3ph",
    # Physics and protection
    "physics-check", "protection-check", "grade", "devices-check", "fault-line", "sc-trace", "protection-audit",
    "protection-eval", "protection-grade", "fault", "fault-series", "fault-simultaneous", "iec60909", "g74",
    "arc-flash", "cable-rating", "experimental", "ansi-sc", "sc-dc", "sc-61363",
    # Frequency and dynamics (models on a case, no recorded traces)
    "transient", "domains", "stability", "modal", "modal-full", "inertia-placement", "dynamics-study", "grid-code",
    "motor-start", "user-model",
    # Harmonics, impedance and EMT
    "harmonics", "harmonic-assess", "flicker", "filter-design", "impedance-stability", "emt", "emt-line", "emt3",
    "cosim", "emt-partition", "harmonic-pf",
    # Data (static)
    "convert", "validate", "zonal", "scl-import",
    # Preflight and evidence about the build
    "gpucheck", "simdcheck", "batchsolve", "audit", "breaking-change",
    # References and help
    "ref", "help",
})

#: The words every refusal uses: the AI-visible text says only that the thing asked for is not available here.
NOT_AVAILABLE = "is not available through the GridSim MCP server"

#: The neutral pointer added to a verb refusal that has no more specific one.
ALLOWLIST_POINTER = "The available verbs are listed in the resource gridsim://plugin/allowlist."

#: Verbs refused by name. The value is a neutral pointer to an alternative, or empty (then ALLOWLIST_POINTER is shown).
#: A verb listed here is refused with the same words as an unlisted verb; listing it keeps it refused even if it were
#: ever added to ALLOWED_VERBS by mistake (the two sets must not overlap, a test checks it).
REFUSED_VERBS = {
    "gda": "",
    "gda-replay": "",
    "estimate": "",
    "estimate-noise-floor": "",
    "serve": "",
    "interpose": "",
    "pmu-ingest": "",
    "pmu-estimate": "",
    "cycle": "",
    "replay": "",
    "replay-event": "",
    "scenario": "",
    "h0-study": "",
    "fleet": "",
    "evidence": "",
    "freq": "",
    "fit-frequency": "",
    "harmonic-se": "",
    "chain": "",
    "synth": "",
    "export": "",
    "rpc": "",
    "licence": "",
    "bench": "",
    "conformance": "Use the physics_check tool on a case.",
    "crossval": "",
    "compliance": "Use the preflight tool, which runs 'compliance verify' itself.",
    "lake_conformance": "",
    "lake-conformance": "",
}

#: Flags refused on every verb: clock overrides, live, recorded, measured or market inputs, network endpoints, devices
#: and plug-in code. The value is a neutral pointer shown with the refusal, or empty.
REFUSED_FLAGS = {
    "--utc-now": "",
    "--as-of": "",
    "--live": "",
    "--live-frames": "",
    "--live-idcode": "",
    "--live-quarantine": "",
    "--live-seconds": "",
    "--live-udp-any-sender": "",
    "--live-udp-source": "",
    "--measurements": "",
    "--measurements-dir": "",
    "--phaseangle-from-gda": "",
    "--prices": "",
    "--listen": "",
    "--port": "",
    "--serial": "",
    "--allow-client": "",
    "--allow-any-client": "",
    "--model-plugins": "",
    "--audit-log": "The server keeps its own study record.",
    "--json": "The server names the result file itself.",
    "--csv": "The server names the result file itself.",
}

#: The refused flags the allowlist resource names: the ones the server manages itself.
SERVER_MANAGED_FLAGS = ("--audit-log", "--csv", "--json")

#: Any flag whose name contains one of these words is refused, under any spelling.
REFUSED_FLAG_WORDS = ("horizon", "lake", "gda", "utc-now", "live", "now-override")

#: Verbs a shipped GB case may be used with: static studies on the case as it stands.
GB_STATIC_VERBS = frozenset({
    "pf", "dcpf", "nminus1", "nminus2", "n-1-1", "iec60909", "fault", "physics-check", "validate", "case-diff",
    "result-diff", "losses", "ptdf", "lodf", "sensitivity", "dcopf", "acopf", "acopf-full", "scopf", "convert",
    "reduce", "zonal", "domains", "taps", "qv-curve", "cpf", "fault-level-n1", "g74", "screen", "trip",
})

#: Built-in names that stand for a shipped GB case (Program.cs CaseFolderOf / DefaultProvenancePath).
GB_CASE_NAMES = frozenset({"uk", "gb", "gb-reduced", "gb-full", "gb-synthetic", "synthetic", "gb-reduced-29",
                           "gb-network-2224"})

_GB_PATH = re.compile(r"(^|[\\/])(gb-full|gb-synthetic|uk|gb)([\\/]|$)|gbnetwork|gbreducednetwork", re.IGNORECASE)

#: Path pieces that are never accepted in any argument (private data roots and recorded-data folders).
_FORBIDDEN_PATH = re.compile(r"(^|[\\/])(gda[^\\/]*|[^\\/]*lake[^\\/]*|neso|events)([\\/]|$)",
                             re.IGNORECASE)

ALIASES = {"n-1": "nminus1", "n-2": "nminus2", "nminus1minus1": "n-1-1"}

#: Sub-commands refused within an allowed verb (plug-in code, signing keys, journal import).
_REFUSED_SUBCOMMANDS = {
    ("user-model", "check-plugin"): "",
    ("audit", "sign"): "",
    ("audit", "keygen"): "",
    ("breaking-change", "import"): "",
}


class Refused(Exception):
    """A request the guards refuse. The message says what is not available; nothing was run."""


def refusal(what, pointer=""):
    """The refusal text: ``REFUSED: <what> is not available through the GridSim MCP server.`` and an optional pointer."""
    return f"REFUSED: {what} {NOT_AVAILABLE}." + (f" {pointer}" if pointer else "")


def canonical(verb):
    v = str(verb).strip().lower()
    return ALIASES.get(v, v)


def is_gb_case(token):
    t = str(token).strip()
    return t.lower() in GB_CASE_NAMES or bool(_GB_PATH.search(t))


def forbidden_path(token):
    """True when a token names a private data root or a recorded-data folder."""
    return bool(_FORBIDDEN_PATH.search(str(token)))


def check_flag(flag):
    """Refused when the flag is one of REFUSED_FLAGS or contains a refused word (any '=value' form too)."""
    name = flag.split("=", 1)[0].lower()
    if name in REFUSED_FLAGS:
        raise Refused(refusal(f"flag {name}", REFUSED_FLAGS[name]))
    for word in REFUSED_FLAG_WORDS:
        if word in name:
            raise Refused(refusal(f"flag {name}"))


def check_verb(verb, args=(), case_tokens=()):
    """Raise Refused unless ``verb`` with ``args`` is allowed. ``case_tokens`` are extra case names or paths the call
    will use (a held case's source, say). Returns the canonical verb name."""
    v = canonical(verb)
    if not v or v.startswith("-"):
        raise Refused("REFUSED: a verb name is required.")
    if v in REFUSED_VERBS or v not in ALLOWED_VERBS:
        raise Refused(refusal(f"'{v}'", REFUSED_VERBS.get(v) or ALLOWLIST_POINTER))
    argv = [str(a) for a in args]
    for a in argv:
        if a.startswith("--"):
            check_flag(a)
        if forbidden_path(a):
            raise Refused(refusal(f"the location '{a}'"))
    if argv and (v, argv[0].lower()) in _REFUSED_SUBCOMMANDS:
        raise Refused(refusal(f"'{v} {argv[0]}'", _REFUSED_SUBCOMMANDS[(v, argv[0].lower())]))
    positional = [a for a in argv if not a.startswith("-")]
    gb = any(is_gb_case(t) for t in list(positional) + list(case_tokens))
    if v not in GB_STATIC_VERBS:
        if gb:
            raise Refused(f"REFUSED: a shipped GB case is offered to static verbs only, and '{v}' is not one of them "
                          f"(static verbs: {', '.join(sorted(GB_STATIC_VERBS))}).")
        if not (argv and not argv[0].startswith("-")) and not case_tokens and v not in _NO_CASE_VERBS:
            raise Refused(f"REFUSED: name the case for '{v}' explicitly; with no case several verbs default to a "
                          "GB case, which is offered to static verbs only.")
    return v


#: Allowed verbs that take no case at all.
_NO_CASE_VERBS = frozenset({"simdcheck", "gpucheck", "help", "ref", "user-model", "audit", "breaking-change",
                            "experimental"})


def allowlist_text():
    """The allowlist as plain text (served as a resource)."""
    lines = ["GridSim MCP verb allowlist", "", "Allowed verbs (run_verb):", "  " + ", ".join(sorted(ALLOWED_VERBS)), "",
             "Any other verb is not available through the GridSim MCP server.", "",
             "Flags the server manages itself (not accepted in run_verb):"]
    lines += [f"  {f}: {REFUSED_FLAGS[f]}" for f in SERVER_MANAGED_FLAGS]
    lines += ["  Flags that attach live, recorded or market inputs, open network endpoints or devices, load code, or",
              "  override the clock are not available either.", "",
              "GB cases (" + ", ".join(sorted(GB_CASE_NAMES)) + ") with static verbs only:",
              "  " + ", ".join(sorted(GB_STATIC_VERBS))]
    return "\n".join(lines) + "\n"
