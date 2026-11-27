"""The case lab tools (maintainer only): caselab_run, caselab_status, caselab_findings, caselab_reproduce and
caselab_propose.

They exist only when the plugin option ``enable_caselab`` is on and the validation tools are configured (they run
through the same Lab Mode daemon as compare): environment GRIDSIM_MCP_ENABLE_CASELAB plus GRIDSIM_MCP_ENABLE_COMPARE and
GRIDSIM_MCP_HARNESS. Otherwise they are not registered.

The lab generates small, plausible cases from a seed (or takes cases an AI designed through caselab_propose), gates
each one in GridSim, compares it with the rival tools mode by mode, triages every non-agreement, searches around each
new finding and shrinks it to a minimal reproducer. Findings go to caselab/<campaign-id>/ in the workspace. A
disagreement is evidence, not proof; every proposal is text only.
"""

import json
import os
from pathlib import Path
from typing import Any, Literal, Optional

from . import tools_compare
from .allowlist import Refused
from .caselab import campaign as camp
from .caselab import generator, model, propose, triage
from .host import McpError
from .registry import Ctx, Outcome, tool

ENV_ENABLE = "GRIDSIM_MCP_ENABLE_CASELAB"

Family = Literal["radial", "meshed", "transmission"]
Mode = Literal["pf", "pfq", "dcpf", "dcopf", "acopf", "nminus1"]
FindingClass = Literal["GridSim gate fail", "rival gate fail", "documented model difference", "genuine disagreement"]


def settings(environ=None):
    """The compare settings when the case lab is on, else None."""
    env = os.environ if environ is None else environ
    if not tools_compare._truthy(env.get(ENV_ENABLE)):
        return None
    return tools_compare.settings(env)


def _settings_or_refuse():
    s = settings()
    if s is None:
        raise Refused("REFUSED: the case lab is not available through the GridSim MCP server.")
    return s


def _campaign_folder(config, cid):
    if not cid or not all(ch.isalnum() or ch in "-_" for ch in str(cid)):
        raise McpError("campaign is a campaign id (cl-<seed>-<hash>[-n]) as caselab_run returned")
    f = camp.root(config) / str(cid)
    if not (f / "campaign.json").is_file():
        raise McpError(f"no campaign '{cid}' (caselab_status lists them)")
    return f


def _findings(folder):
    p = folder / "findings.jsonl"
    if not p.is_file():
        return []
    return [json.loads(ln) for ln in p.read_text(encoding="utf-8").splitlines() if ln.strip()]


def _brief(f):
    rep = f.get("reproducer") or {}
    return {"id": f["id"], "class": f["class"], "mode": f["modes"][0], "tool": f["tools"][1],
            "signature": f["signature"], "occurrences": len(f.get("occurrences", [])),
            "firstCase": f["case"]["label"], "documented": f["triage"].get("documented"),
            "reproducer": None if not rep else {k: rep.get(k) for k in ("status", "buses", "branches",
                                                                        "offNominalTaps", "steps")},
            "proposal": f["proposal"]}


def _load_proposed(config, pid):
    if not all(ch.isalnum() or ch in "-_" for ch in str(pid)):
        raise McpError(f"'{pid}' is not a proposed case id")
    folder = camp.root(config) / "proposed" / str(pid)
    gates = folder / "gates.json"
    if not gates.is_file():
        raise McpError(f"no proposed case '{pid}' (caselab_propose stores them)")
    if not json.loads(gates.read_text(encoding="utf-8")).get("accepted"):
        raise McpError(f"proposed case '{pid}' did not pass the gates")
    return json.loads((folder / "model.json").read_text(encoding="utf-8"))


@tool("caselab_run", "Run a case lab campaign (generate, compare, triage, refine)", read_only=False, idempotent=False,
      group="caselab")
def caselab_run(ctx, seed: int, n: int = 12, families: Optional[list[Family]] = None,
                modes: Optional[list[Mode]] = None, budget_minutes: float = 60.0, refine: bool = True,
                refine_budget: int = 30, evidence: bool = True, tools: Optional[list[str]] = None,
                proposed: Optional[list[str]] = None, wait: bool = True):
    """Run a case lab campaign: generate n small, plausible cases from seed (families radial, meshed, transmission,
    taken in turn; the same seed gives byte-identical cases), plus any proposed case ids from caselab_propose. Each
    case passes GridSim's gates first (convert to JSON v2, validate, physics-check, plausible voltages) or is
    recorded as a generator reject with its reason. Then, mode by mode (default pf, pfq, dcpf, dcopf; acopf and
    nminus1 when named), one correctness-only Lab cell per case through the Lab daemon, and every non-agreement is
    triaged (GridSim gate fail, rival gate fail, documented model difference, genuine disagreement) and deduplicated by
    (class, mode, tool, signature). refine=true searches around each new finding (seeded perturbations) and shrinks
    it to a minimal reproducer (at most refine_budget evaluations each). budget_minutes stops the campaign cleanly.
    evidence=true attaches both sides' independent checks to each finding. wait=false starts it in the background
    (follow with caselab_status). The store is caselab/<campaign-id>/ in the workspace. A disagreement is evidence,
    not proof; proposals are text only."""
    st = _settings_or_refuse()
    config = ctx.pool.config
    proposed_cases = [_load_proposed(config, pid) for pid in (proposed or [])]
    params = camp.params_of(seed, n, families, modes, refine, evidence, refine_budget, tools, proposed)
    if not params["n"] and not proposed_cases:
        raise McpError("nothing to run: n is 0 and no proposed case is named")
    budget_s = None if budget_minutes is None or budget_minutes <= 0 else float(budget_minutes) * 60.0
    run_ctx = ctx if wait else Ctx(ctx.pool, f"caselab-{seed}")
    cid, doc = camp.start(run_ctx, st, params, budget_s, proposed_cases, wait=wait)
    folder = camp.root(config) / cid
    rel = folder.relative_to(config.workspace).as_posix()
    if not wait:
        return Outcome(document={"campaign": cid, "folder": rel, "status": "running"}, store=False,
                       summary=f"case lab campaign {cid} started in the background (caselab_status)")
    findings = [_brief(f) for f in _findings(folder)]
    doc = dict(doc, folder=rel, findingsBrief=findings)
    counts = {}
    for mode_counts in doc["countsByClass"].values():
        for cls, k in mode_counts.items():
            counts[cls] = counts.get(cls, 0) + k
    head = (f"case lab {cid}: {doc['status']}; {doc['cases']['accepted']} case(s), {doc['cases']['rejected']} "
            f"reject(s), {doc['cells']} cell(s); " + ", ".join(f"{k} {v}" for k, v in sorted(counts.items()))
            + f"; {len(findings)} finding(s)")
    return Outcome(document=doc, store=False, summary=head, extra={"summary_md": f"{rel}/summary.md"})


@tool("caselab_status", "Show case lab campaigns and their progress", read_only=True, idempotent=True,
      group="caselab")
def caselab_status(ctx, campaign: Optional[str] = None):
    """With no campaign: every campaign in the workspace (id, status, cases, cells, findings), newest first. With a
    campaign id: its campaign.json (parameters, build, progress, counts by class, generator rejects) and whether it is
    still running."""
    _settings_or_refuse()
    config = ctx.pool.config
    if campaign is None:
        r = camp.root(config)
        rows = []
        for f in sorted(r.glob("cl-*/campaign.json"), key=lambda p: p.stat().st_mtime, reverse=True) if r.is_dir() \
                else []:
            d = json.loads(f.read_text(encoding="utf-8"))
            rows.append({"campaign": d["id"], "status": d["status"], "seed": d["params"]["seed"],
                         "cases": d["cases"], "cells": d["cells"], "findings": d["findings"],
                         "running": d["id"] in camp.RUNNING and camp.RUNNING[d["id"]].is_alive()})
        return Outcome(document={"campaigns": rows}, store=False, summary=f"{len(rows)} campaign(s)")
    folder = _campaign_folder(config, campaign)
    d = json.loads((folder / "campaign.json").read_text(encoding="utf-8"))
    t = camp.RUNNING.get(d["id"])
    d["running"] = bool(t and t.is_alive())
    return Outcome(document=d, store=False,
                   summary=f"{d['id']}: {d['status']}, {d['cells']} cell(s), {d['findings']} finding(s)")


@tool("caselab_findings", "List the findings of a case lab campaign", read_only=True, idempotent=True,
      group="caselab")
def caselab_findings(ctx, campaign: str, finding_class: Optional[FindingClass] = None, mode: Optional[Mode] = None,
                     tool_name: Optional[str] = None, finding: Optional[str] = None, full: bool = False):
    """The findings of a campaign (gridsim-mcp-caselab-finding/1), filtered by class, mode or rival tool. Each: id,
    class, mode, tool, signature, occurrences, the first case, the reproducer (size, steps, minimality) and the
    proposal (text only). finding=<id> or full=true returns the whole record, with the triage evidence of both sides
    and the reproducer's file hashes."""
    _settings_or_refuse()
    folder = _campaign_folder(ctx.pool.config, campaign)
    rows = _findings(folder)
    if finding:
        rows = [f for f in rows if f["id"] == finding]
        if not rows:
            raise McpError(f"no finding '{finding}' in {campaign}")
    rows = [f for f in rows if (not finding_class or f["class"] == finding_class)
            and (not mode or f["modes"][0] == mode) and (not tool_name or f["tools"][1] == tool_name)]
    out = rows if (full or finding) else [_brief(f) for f in rows]
    return Outcome(document={"campaign": campaign, "findings": out, "note": tools_compare.EVIDENCE_NOT_PROOF},
                   store=False, summary=f"{campaign}: {len(out)} finding(s)")


def _rebuild(folder, f):
    """The finding's reproducer rebuilt from its recipe: the family and seed (or the stored proposed model), then the
    steps. Returns (case, base check)."""
    rep = f.get("reproducer") or {}
    src = rep.get("from") or {"case": f["case"]["label"], "family": f["case"]["family"], "seed": f["case"]["seed"],
                              "model": f"cases/{f['case']['label']}/model.json",
                              "modelSha256": f["case"]["sha256"].get("model")}
    if src.get("family") in generator.FAMILIES:
        base = generator.generate(src["family"], int(src["seed"]))
        base_ok = camp._dump(base).encode("utf-8") == (folder / src["model"]).read_bytes() if (
            folder / src["model"]).is_file() else None
    else:
        base = json.loads((folder / src["model"]).read_text(encoding="utf-8"))
        base_ok = camp.sha256_file(folder / src["model"]) == src.get("modelSha256")
    return model.apply_steps(base, rep.get("steps") or []), base_ok


@tool("caselab_reproduce", "Re-run a case lab finding's reproducer and check its hashes", read_only=False,
      idempotent=True, group="caselab")
def caselab_reproduce(ctx, campaign: str, finding: str):
    """Rebuild a finding's minimal reproducer from its recipe (family and seed, or the proposed model, then the
    recorded steps), gate it, check that every file hash (case-lab model, MATPOWER source, JSON v2, the Lab's MATPOWER
    file) equals the recorded one, and re-run its Lab cell: REPRODUCED when the hashes match and the same finding
    (class and signature) is shown again, DIFFERS otherwise, with what differed. Files go to
    caselab/<campaign>/reproduce/."""
    st = _settings_or_refuse()
    config = ctx.pool.config
    folder = _campaign_folder(config, campaign)
    rows = [f for f in _findings(folder) if f["id"] == finding]
    if not rows:
        raise McpError(f"no finding '{finding}' in {campaign}")
    f = rows[0]
    rep = f.get("reproducer") or {}
    expected = rep.get("sha256") or f["case"]["sha256"]
    tools = rep.get("tools") or f["tools"]
    case, base_ok = _rebuild(folder, f)
    k = len(list((folder / "reproduce").glob(f"{finding}-*"))) + 1 if (folder / "reproduce").is_dir() else 1
    out = folder / "reproduce" / f"{finding}-{k:02d}"
    gates = camp.prepare(ctx.session, case, out)
    hashes = {name: {"recorded": expected.get(name), "now": gates["sha256"].get(name),
                     "same": expected.get(name) == gates["sha256"].get(name)} for name in sorted(expected)}
    doc = {"campaign": campaign, "finding": finding, "class": f["class"], "signature": f["signature"],
           "baseCase": "identical" if base_ok else ("differs" if base_ok is False else "not checked"),
           "gates": gates["gates"], "accepted": gates["accepted"], "hashes": hashes}
    same_finding = False
    if gates["accepted"]:
        record, cell, _ = camp.compare_cell(ctx, st, f["modes"][0], gates["lab"],
                                            folder / "bundles" / "reproduce" / f"{finding}-{k:02d}", tools)
        text = Path(gates["lab"]).read_text(encoding="utf-8")
        row = next((r for r in camp.triage_cell(record, text, {f["tools"][1]})), None)
        if row:
            _, cls, reasons, known = row
            sig = triage.signature(cls, reasons, known)
            same_finding = cls == f["class"] and sig == f["signature"]
            doc["now"] = {"class": cls, "signature": sig, "reasons": reasons,
                          "maxDiffs": triage.max_diffs(record, f["tools"][1])}
        doc["bundle"] = Path(cell).parents[2].relative_to(folder).as_posix()
    all_same = all(h["same"] for h in hashes.values())
    doc["verdict"] = "REPRODUCED" if (all_same and same_finding and base_ok is not False) else "DIFFERS"
    doc["note"] = tools_compare.EVIDENCE_NOT_PROOF
    return Outcome(document=doc, store=False,
                   summary=f"{finding}: {doc['verdict']} (hashes {'match' if all_same else 'differ'}, "
                           f"{'same finding' if same_finding else 'finding not shown'})")


@tool("caselab_propose", "Get the case lab template, or check a case an AI designed", read_only=False,
      idempotent=True, group="caselab")
def caselab_propose(ctx, case: Optional[dict[str, Any]] = None):
    """Without a case: the template (gridsim-mcp-caselab-case/1, a filled 3-bus example), the instructions and the
    class ranges for designing a case yourself. The server calls no model: you design, it checks. With a case: its
    structure is checked, it is written, and it passes the same gates as a generated case (convert to JSON v2,
    validate, physics-check, plausible voltages). An accepted case is stored as caselab/proposed/<id>/ and its id can
    be given to caselab_run(proposed=[...]). A rejected case returns the reason: fix the modelling error, never fit
    values to the solver."""
    _settings_or_refuse()
    if case is None:
        return Outcome(document={"schema": model.SCHEMA, "template": propose.template(),
                                 "instructions": propose.INSTRUCTIONS, "ranges": propose.ranges()},
                       store=False, summary="case lab template: fill it and call caselab_propose(case=...)")
    c = propose.normalise(case)
    errors = model.check_structure(c)
    if errors:
        return Outcome(document={"accepted": False, "errors": errors}, store=False, status="ok",
                       summary=f"proposed case not accepted: {errors[0]}")
    pid = f"{c['name']}-{propose.digest(c)[:8]}"
    folder = camp.root(ctx.pool.config) / "proposed" / pid
    gates = camp.prepare(ctx.session, c, folder)
    doc = {"id": pid, "accepted": gates["accepted"], "reason": gates["reason"], "gates": gates["gates"],
           "sha256": gates["sha256"], "folder": folder.relative_to(ctx.pool.config.workspace).as_posix(),
           "intent": c.get("intent")}
    return Outcome(document=doc, store=False,
                   summary=(f"proposed case {pid} accepted: use caselab_run(proposed=['{pid}'])" if gates["accepted"]
                            else f"proposed case {pid} rejected: {gates['reason']}"))

