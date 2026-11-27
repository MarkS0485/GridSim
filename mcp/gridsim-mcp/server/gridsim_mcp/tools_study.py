"""Study tools: the power flow, the level 2 studies and any allowlisted verb."""

from typing import Literal, Optional, Union

from . import paths, tuning as tuning_mod
from .allowlist import check_verb
from .host import STUDY_VERBS, McpError
from .registry import TOOLS, Outcome, tool
from .tools_case import run_document

#: A tuning map: tunable key (see tunables_list) to value.
Tuning = Optional[dict[str, Union[bool, int, float, str]]]

TUNING_DOC = (" tuning: optional map of tunable values (tunables_list names them, with their class, default and safe "
              "range); an unknown key or an out-of-range value is refused and nothing runs. A tuned result is marked "
              "TUNED with its values, and a STANDARD value also says which standard is no longer complied with.")

StudyVerb = Literal["pf", "nminus1", "nminus2", "iec60909", "fault", "acopf-full", "dcopf", "scopf", "stability"]


def _verdict(verb, code):
    if code == 0:
        return "ok"
    if code == 3:
        return "verdict failed or not converged (exit 3): read the document, it is still a result"
    return f"exit {code}"


@tool("solve_pf", "Solve the power flow", read_only=True, idempotent=True)
def solve_pf(ctx, case_id: str, full: bool = False, timeout_s: Optional[float] = None, tuning: Tuning = None):
    """Solve the AC power flow of a case as it stands (edits included). Default: the host's resident solve (converged,
    iterations, max mismatch, losses, and per bus vm_pu, va_deg, p_inj_mw, q_inj_mvar). full=true runs `gridsim pf` on
    the case and returns its complete result document (bus and branch tables), the same bits as the command line.
    With tuning the full pf runs (the resident solve has fixed options)."""
    applied = tuning_mod.apply("pf", tuning)
    s = ctx.session
    c = s.case(case_id)
    check_verb("pf", applied.flags, case_tokens=[c.source])
    if full or applied.flags:
        return _study(s, c, "pf", applied.flags, timeout_s, applied)
    r = s.call("solve", handle=c.handle)
    res = r["result"]
    code = r["exitCode"]
    summ = (f"{c.id}: pf {'converged' if res.get('converged') else 'DID NOT CONVERGE'} in {res.get('iterations')} "
            f"iteration(s), losses {res.get('loss_mw')} MW")
    return Outcome(document=res, case=c, exit_code=code, store=False, summary=summ)


def _study(s, c, verb, args, timeout_s, applied=None):
    fields = {"handle": c.handle, "verb": verb, "args": [str(a) for a in args]}
    if timeout_s:
        fields["timeout_s"] = float(timeout_s)
    r = s.call("study", **fields)
    res = r["result"]
    code = r["exitCode"]
    doc = res.get("document")
    if doc is None:
        doc = {k: v for k, v in res.items() if k not in ("stdout", "stderr")} or None
        if doc is None:
            doc = {"verb": verb, "exitCode": code, "stdout": res.get("stdout", "")}
    extra = {"stderr": res["stderr"]} if res.get("stderr") else {}
    return Outcome(document=doc, case=c, exit_code=code, extra=extra, tuning=applied,
                   summary=f"{c.id}: {verb} {_verdict(verb, code)}")


@tool("study", "Run a study on a held case", read_only=True, idempotent=True)
def study(ctx, case_id: str, verb: StudyVerb, args: Optional[list[str]] = None, timeout_s: Optional[float] = None,
          tuning: Tuning = None):
    """Run a study on a case as it stands, edits included: pf, nminus1, nminus2, iec60909, fault, acopf-full, dcopf,
    scopf or stability. Returns GridSim's own result document. An edited case is written as JSON v2 and checked
    bit for bit before the study runs, so the study is of exactly this model. Exit 3 is a verdict (NOT SECURE,
    DIVERGED, ...) and still a result."""
    s = ctx.session
    c = s.case(case_id)
    if verb not in STUDY_VERBS:
        raise McpError(f"study runs {', '.join(STUDY_VERBS)}; use run_verb for other verbs")
    argv = [str(a) for a in (args or [])]
    applied = tuning_mod.apply(verb, tuning, argv)
    check_verb(verb, argv, case_tokens=[c.source])
    for a in argv:
        if not a.startswith("-") and paths.looks_like_path(a):
            paths.resolve(s.config, a)
    return _study(s, c, verb, argv + applied.flags, timeout_s, applied)


@tool("run_verb", "Run an allowlisted GridSim verb", read_only=False, destructive=False, idempotent=True)
def run_verb(ctx, verb: str, args: Optional[list[str]] = None, case_id: Optional[str] = None,
             timeout_s: Optional[float] = None, tuning: Tuning = None):
    """Run one GridSim verb, as `gridsim <verb> <args>` on the command line, and return its result document (or its
    printed output for verbs without one). Only allowlisted verbs run; any other verb or flag is not available
    (see the resource gridsim://plugin/allowlist). With case_id, the case as it stands is passed as the first
    argument. Files named in args must lie in the workspace; --out (for verbs that name their own output) must
    point inside the workspace."""
    s = ctx.session
    argv = [str(a) for a in (args or [])]
    tokens = []
    c = None
    if case_id is not None:
        c = s.case(case_id)
        tokens.append(c.source)
    v = check_verb(verb, argv, case_tokens=tokens)
    applied = tuning_mod.apply(v, tuning, argv)
    checked = []
    for i, a in enumerate(argv):
        prev = argv[i - 1] if i else ""
        if not a.startswith("-") and paths.looks_like_path(a):
            checked.append(paths.host_arg(s.config, a, write=(prev == "--out")))
        else:
            checked.append(a)
    if c is not None:
        checked = [s.materialise(c)] + checked
    out = run_document(s, v, checked + applied.flags, case=c, timeout_s=timeout_s,
                       summary=f"gridsim {v}" + (f" on {c.id}" if c else ""))
    out.tuning = applied
    return out


@tool("tunables_list", "List the values a user may tune", read_only=True, idempotent=True)
def tunables_list(ctx, verb: Optional[str] = None):
    """The values a user may tune at run time (gridsim-mcp-tunables/1), all of them or those of one verb: each key's
    class (STANDARD: the result is flagged off-standard; NUMERICS: free within the safe range), the GridSim flag it
    sets, its default, unit and safe range. PHYSICS is never tunable. Settings that protect GridSim's use limits are
    not tunable and not listed. Pass the keys as `tuning` to solve_pf, study, run_verb or reproduce_check."""
    doc = tuning_mod.listing(verb)
    n = len(doc["tunables"])
    return Outcome(document=doc, store=False,
                   summary=f"tunables: {n} value(s)" + (f" for {doc['verb']}" if verb else ""))


for _name in ("solve_pf", "study", "run_verb"):
    TOOLS[_name].description += TUNING_DOC
