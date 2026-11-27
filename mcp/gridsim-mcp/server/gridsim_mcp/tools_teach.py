"""Student tools: look inside one solve (explain_solve) and break a network on purpose (break_it). Both show GridSim's
own words: the solve export, the fail-loud message and the Troubleshooting entry that explains it."""

import json
import math
from typing import Literal, Optional

from . import docs_index, record
from .allowlist import check_verb
from .host import McpError
from .registry import Outcome, tool

#: The Jacobian is shown only for cases of up to this many buses (the dense engine's own export limit).
JACOBIAN_MAX_BUSES = 300

#: At most this many iterations of mismatch history are listed per pass (first ones and the last).
HISTORY_MAX = 25


def _docs_root(ctx):
    return docs_index.find_docs_root(ctx.pool.config.command)


def _bare_case_file(s, c):
    """A case file the bare solve form reads (a built-in name or a MATPOWER .m), and the convert notes when the case had
    to be converted (JSON v2 -> .m, round trip proved by convert)."""
    edited = s.edits(c)["revision"] > 0
    src = str(c.source)
    if not edited and (src.lower().endswith(".m") or ("/" not in src and "\\" not in src and "." not in src)):
        return src, []
    v2 = s.materialise(c, force_v2=True)
    h = v2.split("/")[-2]
    m = f".gridsim-mcp/exports/{h}.m"
    if not (s.config.workspace / m).is_file():
        (s.config.workspace / m).parent.mkdir(parents=True, exist_ok=True)
        check_verb("convert", [v2, m])
        r = s.run_raw("convert", [v2, m])
        if r["exit_code"] != 0:
            raise McpError(r["error"] or f"convert exit {r['exit_code']}")
        notes = [ln.strip() for ln in (r["stdout"] + "\n" + r["stderr"]).splitlines()
                 if ln.strip().startswith(("note:", "round trip:"))]
        (s.config.workspace / (m + ".notes.json")).write_text(json.dumps(notes, indent=1), encoding="utf-8")
    notes_file = s.config.workspace / (m + ".notes.json")
    notes = json.loads(notes_file.read_text(encoding="utf-8")) if notes_file.is_file() else []
    return m, notes


def _fail_line(text):
    lines = [ln.strip() for ln in (text or "").splitlines() if ln.strip()]
    for ln in lines:
        if ln.lower().startswith("error:"):
            return ln
    return lines[-1] if lines else ""


def _orders(history):
    """Estimated order of convergence p from three consecutive mismatches: log(e2/e1) / log(e1/e0)."""
    out = []
    for e0, e1, e2 in zip(history, history[1:], history[2:]):
        try:
            if e0 > 0 and e1 > 0 and e2 > 0 and e1 != e0:
                out.append(round(math.log(e2 / e1) / math.log(e1 / e0), 3))
        except (ValueError, ZeroDivisionError):
            pass
    return out


def summarise_export(export, jacobian=False):
    """The teaching summary of a gridsim-solve-export/1 document (the export's meta, which holds the clock and the
    machine name, is left out so the summary is deterministic)."""
    solve = export.get("solve", {})
    spec = {b["Index"]: b for b in solve.get("busSpec", [])}
    types = {}
    for b in spec.values():
        types[b["Type"]] = types.get(b["Type"], 0) + 1
    passes = []
    worst = None
    for ps in solve.get("passes", []):
        its = ps.get("iterations", [])
        hist = [it.get("maxMismatch") for it in its]
        shown = hist if len(hist) <= HISTORY_MAX else hist[:HISTORY_MAX - 1] + [hist[-1]]
        passes.append({"pass": ps.get("pass"), "iterations": len(its), "max_mismatch_history": shown,
                       "history_truncated": len(hist) > HISTORY_MAX,
                       "order_estimates": _orders(hist[:HISTORY_MAX]),
                       "pv_pq_equations": len(ps.get("pvpqIndices", [])), "pq_equations": len(ps.get("pqIndices", []))})
        if its:
            last = its[-1].get("mismatch") or []
            pvpq, pq = ps.get("pvpqIndices", []), ps.get("pqIndices", [])
            if last:
                i = max(range(len(last)), key=lambda k: abs(last[k]))
                if i < len(pvpq):
                    idx, eq = pvpq[i], "P"
                else:
                    idx, eq = pq[i - len(pvpq)] if i - len(pvpq) < len(pq) else None, "Q"
                worst = {"pass": ps.get("pass"), "iteration": its[-1].get("iteration"), "equation": eq,
                         "bus_index": idx, "bus_id": spec.get(idx, {}).get("Id"), "mismatch_pu": last[i]}
    doc = {
        "schema": "gridsim-mcp-explain-solve/1",
        "export_schema": export.get("schema"),
        "case": export.get("meta", {}).get("Case"),
        "buses": solve.get("n"),
        "solver": solve.get("solver"),
        "status": solve.get("status"),
        "converged": solve.get("converged"),
        "iterations": solve.get("iterations"),
        "max_mismatch_pu": solve.get("maxMismatch"),
        "bus_types": types,
        "passes": passes,
        "pv_pq_switches": [{"bus_id": e.get("BusId"), "pinned_at": e.get("PinnedAt"), "pass": e.get("Pass")}
                           for e in solve.get("qLimitEvents", [])],
        "worst_bus_last_iteration": worst,
        "slack_q_note": solve.get("slackQNote"),
        "physics_gate_pass": export.get("physics", {}).get("gatePass"),
        "losses_mw": export.get("result", {}).get("totalLossMw"),
    }
    if jacobian:
        ps = (solve.get("passes") or [{}])[-1]
        its = ps.get("iterations") or [{}]
        jac = its[0].get("jacobian")
        doc["jacobian"] = None if jac is None else {
            "pass": ps.get("pass"), "iteration": its[0].get("iteration"), "rows": len(jac),
            "ordering": "P equations of pvpqIndices, then Q equations of pqIndices (zero-based bus indices)",
            "pvpqIndices": ps.get("pvpqIndices"), "pqIndices": ps.get("pqIndices"), "matrix": jac}
        if jac is None:
            doc["jacobian_note"] = ("no Jacobian in the export (the sparse or sweep engine records none, or the case "
                                    "is above the dense export limit)")
    return doc


@tool("explain_solve", "Explain one power-flow solve step by step", read_only=True, idempotent=True)
def explain_solve(ctx, case_id: str, jacobian: bool = False):
    """Re-solve a case as it stands with GridSim's solve export (`gridsim <case> --once --enableExport`) and summarise
    the solver's own working: iterations, how the largest mismatch falls (with the estimated order of convergence:
    about 2 near the answer for Newton-Raphson), PV->PQ switches in the reactive-limit loop, the bus with the largest
    mismatch at the last iteration, the bus types and the physics gate. jacobian=true adds the Jacobian of the first
    iteration (cases of up to 300 buses). A solve that does not converge still writes the export: its message and the
    matching Troubleshooting entry are returned. An edited case is converted to MATPOWER first (convert proves the
    round trip)."""
    s = ctx.session
    c = s.case(case_id)
    buses = c.load_response["result"].get("buses")
    if jacobian and isinstance(buses, int) and buses > JACOBIAN_MAX_BUSES:
        raise McpError(f"the Jacobian is shown for cases of up to {JACOBIAN_MAX_BUSES} buses; this one has {buses}. "
                       "Run explain_solve without jacobian.")
    case_file, notes = _bare_case_file(s, c)
    h = s.case_hash(c)[:16]
    exp = f".gridsim-mcp/exports/{h}-solve.json"
    target = s.config.workspace / exp
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        target.unlink()
    args = ["--once", f"--enableExport={exp}"]
    check_verb("pf", args, case_tokens=[c.source])   # the bare solve form: a power flow of the case
    r = s.run_raw(case_file, args)
    if not target.is_file():
        raise McpError(r["error"] or f"the solve export was not written (exit {r['exit_code']})")
    export = json.loads(target.read_text(encoding="utf-8"))
    doc = summarise_export(export, jacobian=jacobian)
    doc["convert_notes"] = notes
    doc["exitCode"] = r["exit_code"]
    if r["exit_code"] != 0:
        msg = _fail_line(r["stderr"]) or _fail_line(r["stdout"])
        doc["gridsim_message"] = msg
        doc["troubleshooting"] = docs_index.match_troubleshooting(_docs_root(ctx), msg + "\n" + (r["stdout"] or ""))
    head = (f"{c.id}: {doc['solver']} {doc['status']} in {doc['iterations']} iteration(s), max mismatch "
            f"{doc['max_mismatch_pu']} pu, {len(doc['pv_pq_switches'])} PV->PQ switch(es)")
    return Outcome(document=doc, case=c, exit_code=r["exit_code"], store=False, extra={"export": exp}, summary=head)


BreakAction = Literal["trip_branch", "trip_generator", "overload", "remove_slack", "island_bus", "fault"]


def _copy(s, c):
    """A fresh copy of the case as it stands, so the break never touches the student's case."""
    if s.edits(c)["revision"] == 0:
        return s.load(c.source, c.source)
    v2 = s.materialise(c, force_v2=True)
    return s.load(v2, v2)


def _rows(s, cc, kind):
    return s.call("elements", handle=cc.handle, kind=kind)["result"]["rows"]


@tool("break_it", "Break a network on purpose and read GridSim's answer", read_only=False, destructive=False,
      idempotent=False)
def break_it(ctx, case_id: str, action: BreakAction, target: Optional[str] = None, factor: float = 3.0):
    """A guided failure on a COPY of the case (the case itself is not changed): trip_branch (target: branch key),
    trip_generator (target: generator key), overload (every load times factor, default 3), remove_slack (the slack bus
    made a PQ bus), island_bus (target: bus id; every branch at it opened) or fault (a bolted three-phase fault at bus
    target). GridSim fails loud: an edit that would make the model invalid is refused with the reason, a solve that
    does not converge says so with exit 3. The answer returns GridSim's own message, what happened, and the matching
    entry of manual ch. 30 Troubleshooting. Explain the physics from those words."""
    s = ctx.session
    c = s.case(case_id)
    cc = _copy(s, c)
    applied, refused, message = [], False, None

    def edit(kind, key, values):
        nonlocal refused, message
        try:
            s.call("set", handle=cc.handle, kind=kind, key=str(key), values=values)
            applied.append({"kind": kind, "key": str(key), "values": values})
            return True
        except McpError as ex:
            refused, message = True, str(ex)
            return False

    outcome = None
    fault_doc = None
    if action == "trip_branch":
        if not target:
            raise McpError("trip_branch needs target: a branch key (case_inspect kind branch lists them)")
        edit("branch", target, {"status": 0})
    elif action == "trip_generator":
        if not target:
            raise McpError("trip_generator needs target: a generator key (case_inspect kind generator)")
        edit("generator", target, {"status": 0})
    elif action == "overload":
        if not (factor > 0 and math.isfinite(factor)):
            raise McpError("factor must be a finite number above 0")
        for row in _rows(s, cc, "load"):
            if not edit("load", row["key"], {"pd": row["pd"] * factor, "qd": row["qd"] * factor}):
                break
    elif action == "remove_slack":
        slack = [b for b in _rows(s, cc, "bus") if b.get("type") == 3]
        if not slack:
            raise McpError("the case has no slack bus to remove")
        edit("bus", slack[0]["key"], {"type": 1})
    elif action == "island_bus":
        if not target:
            raise McpError("island_bus needs target: a bus id")
        for row in _rows(s, cc, "branch"):
            ends = row["key"].split(":")[0].split("-")
            if str(target) in ends and row.get("status", 1):
                if not edit("branch", row["key"], {"status": 0}):
                    break
    elif action == "fault":
        if not target:
            raise McpError("fault needs target: a bus id")
        check_verb("fault", ["--bus", str(target)], case_tokens=[cc.source])
        r = s.call("study", handle=cc.handle, verb="fault", args=["--bus", str(target)])
        fault_doc = r["result"].get("document")
        outcome = "fault result"
        if r["exitCode"] != 0:
            message = _fail_line(r["result"].get("stderr", "")) or f"fault exit {r['exitCode']}"

    solve = None
    exit_code = None
    if refused:
        outcome = ("edit refused by GridSim: the refused edit changed nothing"
                   + (f" ({len(applied)} earlier edit(s) of this break were applied to the copy)" if applied else ""))
    elif action != "fault":
        check_verb("pf", [], case_tokens=[cc.source])
        res = s.call("solve", handle=cc.handle)["result"]
        solve = {k: res.get(k) for k in ("converged", "iterations", "max_mismatch", "loss_mw") if k in res}
        if res.get("converged"):
            outcome = "converged: the network still solves after the change"
            exit_code = 0
        else:
            outcome = "did not converge"
            case_file, _ = _bare_case_file(s, cc)
            exp = f".gridsim-mcp/exports/{s.case_hash(cc)[:16]}-solve.json"
            r = s.run_raw(case_file, ["--once", f"--enableExport={exp}"])
            exit_code = r["exit_code"]
            message = _fail_line(r["stderr"]) or _fail_line(r["stdout"])
            ef = s.config.workspace / exp
            if ef.is_file():
                solve["explain"] = summarise_export(json.loads(ef.read_text(encoding="utf-8")))
    doc = {
        "schema": "gridsim-mcp-break-it/1",
        "action": action,
        "target": target,
        "factor": factor if action == "overload" else None,
        "copy_of": c.id,
        "edits_applied": applied,
        "outcome": outcome,
        "gridsim_message": message,
        "solve": solve,
        "fault": None if fault_doc is None else {"summary": fault_doc.get("summary"),
                                                 "result_hash": record.sha256_obj(fault_doc)},
        "troubleshooting": docs_index.match_troubleshooting(_docs_root(ctx), message) if message else None,
    }
    return Outcome(document=doc, case=cc, exit_code=exit_code, store=False, extra={"broken_case_id": cc.id},
                   summary=f"break_it {action}" + (f" {target}" if target else "") + f" on a copy of {c.id} "
                   f"({cc.id}): {outcome}")
