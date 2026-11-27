"""Checking tools: physics check, case and result diffs, reproducibility, the study record, and the preflight."""

import re
from typing import Optional, Union

from . import paths, record, tuning as tuning_mod
from .allowlist import check_verb
from .host import STUDY_VERBS, McpError
from .registry import Outcome, dispatch, tool
from .tools_case import run_document


@tool("physics_check", "Check a solved case against the laws of physics", read_only=True, idempotent=True)
def physics_check(ctx, case_id: str, tol_mva: Optional[float] = None):
    """Run `gridsim physics-check` on a case as it stands: the solved state against the law registry (conservation of
    energy, Ohm's law, Joule heating, KCL, complex power, ...). Exit 0 PASS; 1 physically impossible (the worst law and
    residual are named); 3 not converged. Use it after every build or edit."""
    s = ctx.session
    c = s.case(case_id)
    args = [s.materialise(c)]
    if tol_mva is not None:
        args += ["--tol", repr(float(tol_mva))]
    check_verb("physics-check", args, case_tokens=[c.source])
    out = run_document(s, "physics-check", args, case=c)
    summ = (out.document or {}).get("summary", {})
    out.summary = (f"{c.id}: physics-check {'PASS' if summ.get('gate_pass') else 'FAIL'}"
                   + (f" (worst: {summ.get('worst')})" if summ.get("worst") else ""))
    return out


def _case_file(s, ref):
    """A case id (written as JSON v2, one folder per state) or a workspace path."""
    if re.fullmatch(r"c\d+", str(ref)) and str(ref) in s.cases:
        c = s.cases[str(ref)]
        return s.materialise(c, force_v2=True), c
    return s.materialise_path(paths.host_arg(s.config, ref)), None


@tool("diff_cases", "Compare two cases", read_only=True, idempotent=True)
def diff_cases(ctx, a: str, b: str):
    """Compare two cases with `gridsim case-diff`: identity-matched records with units (CHANGED, ADDED, REMOVED,
    REORDERED). Each side is a case id (the case as it stands) or a case file in the workspace (any format GridSim
    loads); both are written as JSON v2 under one naming first, so only the network differs. Exit 0 equal, 3
    different. To see what an edit changed: case_save before the edit, then diff_cases(saved file, case id)."""
    s = ctx.session
    fa, ca = _case_file(s, a)
    fb, cb = _case_file(s, b)
    check_verb("case-diff", [fa, fb])
    out = run_document(s, "case-diff", [fa, fb], case=cb or ca)
    summ = (out.document or {}).get("summary", {})
    out.summary = f"case-diff {a} vs {b}: {'equal' if summ.get('equal') else str(summ.get('differences')) + ' difference(s)'}"
    return out


@tool("diff_results", "Compare two results", read_only=True, idempotent=True)
def diff_results(ctx, a: str, b: str):
    """Compare two result documents with `gridsim result-diff`: every number by ULP distance and absolute difference,
    the first difference marked. Each side is a result id (r1, r2, ... from earlier calls) or a result file in the
    workspace. Exit 0 identical, 3 different."""
    s = ctx.session
    fa = s.result_path(a) if re.fullmatch(r"r\d+", str(a)) else paths.host_arg(s.config, a)
    fb = s.result_path(b) if re.fullmatch(r"r\d+", str(b)) else paths.host_arg(s.config, b)
    check_verb("result-diff", [fa, fb])
    out = run_document(s, "result-diff", [fa, fb])
    out.store = False
    out.summary = f"result-diff {a} vs {b}: exit {out.exit_code} ({'identical' if out.exit_code == 0 else 'different'})"
    return out


@tool("reproduce_check", "Check a study reproduces bit for bit", read_only=True, idempotent=True)
def reproduce_check(ctx, case_id: str, verb: str = "pf", args: Optional[list[str]] = None, runs: int = 2,
                    tuning: Optional[dict[str, Union[bool, int, float, str]]] = None):
    """Run the same study on the same case several times (each run is a fresh GridSim process) and compare the SHA-256
    of the result documents: MATCH when every run gives the same bits, DIFFER otherwise. GridSim's determinism
    contract says MATCH for the same build, case and options. tuning: as for study (checked the same way, and the
    answer is marked TUNED); every run uses it."""
    s = ctx.session
    c = s.case(case_id)
    if verb not in STUDY_VERBS:
        raise McpError(f"reproduce_check runs a study verb: {', '.join(STUDY_VERBS)}")
    argv = [str(a) for a in (args or [])]
    applied = tuning_mod.apply(verb, tuning, argv)
    argv += applied.flags
    check_verb(verb, argv, case_tokens=[c.source])
    runs = max(2, min(int(runs), 5))
    hashes, first = [], None
    for _ in range(runs):
        r = s.call("study", handle=c.handle, verb=verb, args=argv)
        doc = r["result"].get("document")
        if doc is None:
            doc = {k: v for k, v in r["result"].items() if k not in ("stdout", "stderr")}
        hashes.append(record.sha256_obj(doc))
        first = first or doc
    verdict = "MATCH" if len(set(hashes)) == 1 else "DIFFER"
    return Outcome(document=first, case=c, extra={"reproduce": {"verdict": verdict, "runs": runs, "hashes": hashes}},
                   summary=f"{c.id}: {verb} x{runs}: {verdict}", tuning=applied)


# -- the study record ---------------------------------------------------------------------------------------------------

@tool("record_export", "Export the study record", read_only=False, destructive=False, idempotent=True)
def record_export(ctx, name: Optional[str] = None):
    """Write the session's study record (gridsim-mcp-record/1, hash-chained JSON Lines) to records/<name>.jsonl in the
    workspace. Every call is in it with its arguments, the AI's stated reasoning, the case hash, the result hash and
    the build identity. record_replay re-runs it."""
    s = ctx.session
    stem = name or f"{s.name}-record"
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,80}", stem):
        raise McpError("name uses letters, digits, '.', '_' and '-'")
    rel = f"records/{stem}.jsonl"
    path = paths.resolve(s.config, rel, write=True)
    file_sha = s.record.write(path)
    doc = {"path": rel, "file_sha256": file_sha, "entries": len(s.record.entries), "head": s.record.head,
           "header": s.record.header}
    return Outcome(document=doc, store=False, summary=f"record written to {rel}: {len(s.record.entries)} entries")


@tool("record_replay", "Replay a study record and compare hashes", read_only=False, destructive=False, idempotent=True)
def record_replay(ctx, path: str):
    """Re-run every call of an exported study record in a fresh session on this build and compare each call's case
    hash, result hash and entry hash with the record. First the hash chain is checked (TAMPERED names the first broken
    entry). REPRODUCED when every hash matches."""
    s = ctx.session
    p = paths.resolve(s.config, path)
    header, entries = record.read(p)
    ok, bad, msg = record.verify(header, entries)
    if not ok:
        return Outcome(document={"chain": "TAMPERED", "first_bad_entry": bad, "message": msg}, store=False,
                       status="ok", summary=f"record chain TAMPERED: {msg}")
    n = 1
    while f"replay-{n}" in ctx.pool.sessions:
        n += 1
    rname = f"replay-{n}"
    rows, all_match = [], True
    try:
        ctx.pool.open(rname)
        for e in entries:
            if e["tool"] in record.NOT_REPLAYED:
                rows.append({"seq": e["seq"], "tool": e["tool"], "verdict": "SKIPPED"})
                replayed = ctx.pool.sessions[rname].record.append(
                    e["tool"], e["arguments"], e["reasoning"], e["status"], exit_code=e["exitCode"],
                    case_id=e["case"], case_hash=e["caseHash"], result_hash=e["resultHash"], result_id=e["result"],
                    build=e["build"], message=e["message"])
            else:
                _, _, _, replayed = dispatch(ctx.pool, e["tool"], e["arguments"], rname, e["reasoning"], replaying=True)
            same = {
                "status": replayed["status"] == e["status"],
                "caseHash": replayed["caseHash"] == e["caseHash"],
                "resultHash": replayed["resultHash"] == e["resultHash"],
                "hash": replayed["hash"] == e["hash"],
            }
            match = all(same.values())
            all_match &= match
            if e["tool"] not in record.NOT_REPLAYED:
                rows.append({"seq": e["seq"], "tool": e["tool"], "verdict": "MATCH" if match else "DIFFER",
                             "differs": [k for k, v in same.items() if not v],
                             "result_hash": replayed["resultHash"], "recorded_result_hash": e["resultHash"]})
        head = ctx.pool.sessions[rname].record.head
        header_same = ctx.pool.sessions[rname].record.header["hash"] == header["hash"]
    finally:
        ctx.pool.close(rname)
    recorded_head = entries[-1]["hash"] if entries else header["hash"]
    verdict = "REPRODUCED" if all_match and header_same and head == recorded_head else "DIFFERS"
    doc = {"chain": "INTACT", "verdict": verdict, "entries": len(entries), "header_match": header_same,
           "head": head, "recorded_head": recorded_head, "calls": rows}
    return Outcome(document=doc, store=False, summary=f"replay of {path}: {verdict} ({len(entries)} entries)")


# -- preflight ----------------------------------------------------------------------------------------------------------

@tool("preflight", "Check the GridSim build before a study", read_only=True, idempotent=True)
def preflight(ctx):
    """Describe and check the GridSim build: version, commit, program SHA-256, build status (RELEASE only for a CI
    release build whose commit is a published release commit and whose SHA-256 is a published release binary; everything else is UNSUPPORTED BUILD), the separate
    `compliance verify` verdict, `simdcheck` (CPU tier), and a physics check and pf of case9 with their result hashes.
    Fully offline."""
    s = ctx.session
    ident = ctx.pool.identity
    simd = s.call("run-verb", verb="simdcheck", args=[])["result"].get("stdout", "")
    tier = next((ln.strip() for ln in simd.splitlines() if ln.strip().startswith("Active vector tier")), None)
    pc = s.call("run-verb", verb="physics-check", args=["case9"])
    pf = s.call("run-verb", verb="pf", args=["case9"])
    doc = {
        "build": ident,
        "licensed": "yes" if s.config.licensed else "no",
        "simd_tier": tier,
        "simdcheck": simd,
        "physics_check_case9": {"exitCode": pc["exitCode"],
                                "gate_pass": (pc["result"].get("document") or {}).get("summary", {}).get("gate_pass"),
                                "result_hash": record.sha256_obj(pc["result"].get("document"))},
        "pf_case9": {"exitCode": pf["exitCode"], "result_hash": record.sha256_obj(pf["result"].get("document")),
                     "build_status": (pf["result"].get("document") or {}).get("buildStatus")},
    }
    return Outcome(document=doc, store=False,
                   summary=f"preflight: {ident['version']}")

