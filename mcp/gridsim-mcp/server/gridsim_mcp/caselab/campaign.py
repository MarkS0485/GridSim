"""The case-lab campaign: generate, gate, compare, triage, refine, record.

Folder (in the workspace, never in a repository): caselab/<campaign-id>/
  campaign.json    gridsim-mcp-caselab-campaign/1: parameters (seed, families, modes, ...), build identity, progress,
                   counts by class, generator rejects, status
  cases/<label>/   model.json (the case-lab case), source.m (written from it), case.json + case.network.json (JSON v2,
                   by gridsim convert), <name>.m (the MATPOWER file the Lab runs: gridsim convert of the v2 case, plus
                   the cost block JSON v2 cannot hold yet), gates.json
  bundles/         one Lab bundle per (case, mode), and per refinement candidate
  refine/<id>/     the candidates the search around a finding tried
  reproducers/<id>/ the minimal reproducer of each finding (the same files as a case)
  findings.jsonl   gridsim-mcp-caselab-finding/1, one line per finding, deduplicated by (class, mode, tool, signature)
  summary.md       the same for a reader

What is identical between two campaigns with the same parameters on the same build and tools: every case file, the
counts and findings.jsonl. Bundles differ only in their timing and environment fields.
"""

import hashlib
import json
import shutil
import threading
import time
from pathlib import Path

from .. import tools_compare
from ..allowlist import check_verb
from ..host import McpError
from . import generator, model, shrink, triage
from .rng import Rng, derive

CAMPAIGN_SCHEMA = "gridsim-mcp-caselab-campaign/1"
DEFAULT_MODES = ("pf", "pfq", "dcpf", "dcopf")
OPTIONAL_MODES = ("acopf", "nminus1")
#: Plausibility band for every bus voltage of a generated case (pu), on GridSim's power flow without Q limits.
V_BAND = (0.90, 1.10)

#: Campaigns running in this server (the Lab runs one cell at a time): id -> thread.
RUNNING = {}
_RUN_LOCK = threading.Lock()


def sha256_file(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def _dump(obj):
    return json.dumps(obj, sort_keys=True, indent=1, ensure_ascii=True) + "\n"


def _write(path, text):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def root(config):
    return config.workspace / "caselab"


# -- preparing a case: files and the generator gates --------------------------------------------------------------------

def prepare(session, case, folder):
    """Write the case's files in ``folder`` and run the generator gates. Returns the gates record:
    {accepted, reason, gates: {...}, files: {...}, sha256: {...}, lab (absolute .m path)}."""
    ws = session.config.workspace
    folder = Path(folder)
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir(parents=True)
    rel = folder.relative_to(ws).as_posix()
    name = case["name"]
    out = {"accepted": False, "reason": None, "gates": {}, "files": {}, "sha256": {}}
    errors = model.check_structure(case)
    if errors:
        out["reason"] = "structure: " + "; ".join(errors[:5])
        return out
    _write(folder / "model.json", _dump(case))
    _write(folder / "source.m", model.to_matpower(case))
    files = {"model": "model.json", "source": "source.m", "v2": "case.json", "v2network": "case.network.json",
             "lab": f"{name}.m"}

    def run(verb, args):
        check_verb(verb, args)
        return session.run_raw(verb, args)

    r = run("convert", [f"{rel}/source.m", f"{rel}/case.json"])
    out["gates"]["convert_to_v2"] = r["exit_code"]
    if r["exit_code"] != 0:
        out["reason"] = f"convert to JSON v2: exit {r['exit_code']}: {_last(r)}"
        return _finish(out, folder, files)
    r = run("validate", [f"{rel}/case.json"])
    out["gates"]["validate"] = r["exit_code"]
    if r["exit_code"] != 0:
        out["reason"] = f"validate: exit {r['exit_code']}: {_last(r)}"
        return _finish(out, folder, files)
    r = run("convert", [f"{rel}/case.json", f"{rel}/{name}.m"])
    out["gates"]["convert_to_matpower"] = r["exit_code"]
    if r["exit_code"] != 0:
        out["reason"] = f"convert to MATPOWER: exit {r['exit_code']}: {_last(r)}"
        return _finish(out, folder, files)
    lab = folder / f"{name}.m"
    text = lab.read_text(encoding="utf-8")
    # JSON v2 cannot hold a cost curve yet, so the converted file has none; the OPF modes need one. The block is the
    # generator's own (same generator order as the source).
    _write(lab, text.rstrip("\n") + "\n\n%% Costs: the case lab's own (JSON v2 carries none)\n"
           + model.gencost_block(case) + "\n")
    r = run("physics-check", [f"{rel}/{name}.m"])
    out["gates"]["physics_check"] = {0: "PASS", 1: "FAIL", 3: "not converged"}.get(r["exit_code"],
                                                                                  f"exit {r['exit_code']}")
    if r["exit_code"] != 0:
        out["reason"] = f"physics-check: {out['gates']['physics_check']}"
        return _finish(out, folder, files)
    r = run("pf", [f"{rel}/{name}.m"])
    summ = (r.get("document") or {}).get("summary") or {}
    out["gates"]["pf"] = {k: summ.get(k) for k in ("converged", "iterations", "vmin_pu", "vmax_pu")}
    if r["exit_code"] != 0 or not summ.get("converged"):
        out["reason"] = f"pf: not converged (exit {r['exit_code']})"
        return _finish(out, folder, files)
    if not (V_BAND[0] <= summ["vmin_pu"] and summ["vmax_pu"] <= V_BAND[1]):
        out["reason"] = (f"implausible: bus voltages {summ['vmin_pu']:.4f}-{summ['vmax_pu']:.4f} pu outside "
                         f"{V_BAND[0]}-{V_BAND[1]} pu")
        return _finish(out, folder, files)
    out["accepted"] = True
    return _finish(out, folder, files)


def _last(r):
    text = (r.get("stderr") or "") + "\n" + (r.get("stdout") or "") + "\n" + (r.get("error") or "")
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    err = [ln for ln in lines if ln.lower().startswith("error")]
    return (err or lines or [""])[-1][:300]


def _finish(out, folder, files):
    for k, f in files.items():
        if (folder / f).is_file():
            out["files"][k] = f
            out["sha256"][k] = sha256_file(folder / f)
    out["lab"] = str(folder / files["lab"])
    _write(folder / "gates.json", _dump({k: v for k, v in out.items() if k != "lab"}))
    return out


# -- one Lab cell -------------------------------------------------------------------------------------------------------

def compare_cell(ctx, st, mode, lab_path, out_dir, tools=None):
    """One correctness-only Lab cell through the daemon. Returns (record, cell folder)."""
    out_dir = Path(out_dir)
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.parent.mkdir(parents=True, exist_ok=True)
    d = tools_compare._daemon(ctx, st)
    fields = {"mode": mode, "case": str(lab_path), "output": str(out_dir)}
    if tools:
        fields["tools"] = list(tools)
    d.request("compare", **fields)
    manifest = json.loads((out_dir / "lab-manifest.json").read_text(encoding="utf-8"))
    cell = manifest["cells"][0]
    record = cell["record"]
    folder = out_dir / cell["policy"] / record["mode"] / Path(record["case"]).stem
    return record, folder, manifest


def triage_cell(record, case_text, only=None):
    """[(tool, class, reasons, known)] for every rival tool of a cell (n/a tools skipped, as divergence_triage does)."""
    rows = []
    g = record["tools"].get("gridsim", {})
    for name, entry in record["tools"].items():
        if name == "gridsim" or (only and name not in only):
            continue
        if entry.get("status") == "n/a" and g.get("status") != "gate-fail":
            continue
        cls, reasons, known = tools_compare.classify(record, name, case_text)
        rows.append((name, cls, reasons, known))
    return rows


# -- the campaign -------------------------------------------------------------------------------------------------------

def params_of(seed, n, families, modes, refine=True, evidence=True, refine_budget=30, tools=None, proposed=None):
    fams = list(families or generator.FAMILIES)
    for f in fams:
        if f not in generator.FAMILIES:
            raise McpError(f"unknown family '{f}' (families: {', '.join(generator.FAMILIES)})")
    ms = list(modes or DEFAULT_MODES)
    for m in ms:
        if m not in DEFAULT_MODES + OPTIONAL_MODES:
            raise McpError(f"mode '{m}' is not a case-lab mode ({', '.join(DEFAULT_MODES + OPTIONAL_MODES)})")
    n = int(n)
    if not 0 <= n <= 500:
        raise McpError("n is 0-500 cases")
    return {"seed": int(seed), "n": n, "families": fams, "modes": ms, "refine": bool(refine),
            "evidence": bool(evidence), "refineBudget": max(0, min(int(refine_budget), 200)),
            "tools": sorted(tools) if tools else None, "proposed": list(proposed or [])}


def case_list(params, proposed_cases=()):
    """[(label, case)] of a campaign: case i of family families[i mod k] with seed derive(seed, i, family), then the
    proposed cases."""
    out = []
    fams = params["families"]
    for i in range(params["n"]):
        fam = fams[i % len(fams)]
        out.append((f"c{i + 1:03d}-{fam}", generator.generate(fam, derive(params["seed"], i, fam))))
    for j, c in enumerate(proposed_cases):
        out.append((f"p{j + 1:03d}-{c['name']}", c))
    return out


def new_id(config, params):
    h = hashlib.sha256(json.dumps(params, sort_keys=True).encode()).hexdigest()[:6]
    base = f"cl-{params['seed']}-{h}"
    r = root(config)
    k = 1
    cid = base
    while (r / cid).exists():
        k += 1
        cid = f"{base}-{k}"
    return cid


class Campaign:
    def __init__(self, ctx, st, params, cid, budget_s, proposed_cases=()):
        self.ctx = ctx
        self.st = st
        self.params = params
        self.id = cid
        self.config = ctx.pool.config
        self.folder = root(self.config) / cid
        self.budget_s = budget_s
        self.proposed_cases = list(proposed_cases)
        self.started = time.monotonic()
        self.store = triage.Store()
        self.counts = {}
        self.rejects = []
        self.cells = 0
        self.status = "running"
        self.stop_reason = None
        self.cases = {}          # label -> (case, gates)
        self.log_file = None

    # -- bookkeeping --
    def rel(self, p):
        return Path(p).resolve().relative_to(self.folder.resolve()).as_posix()

    def log(self, text):
        if self.log_file:
            self.log_file.write(text + "\n")
            self.log_file.flush()

    def over_budget(self):
        return self.budget_s is not None and time.monotonic() - self.started > self.budget_s

    def neutral(self, obj):
        """Evidence with the campaign folder named <campaign>, so findings do not depend on the campaign id."""
        ws = self.config.workspace
        forms = set()
        for base in (self.folder, self.folder.resolve()):
            for v in (str(base), str(base).replace("\\", "/")):
                forms.add(v)
            try:
                forms.add(base.resolve().relative_to(ws).as_posix())
            except ValueError:
                pass

        def walk(o):
            if isinstance(o, str):
                for f in sorted(forms, key=len, reverse=True):
                    o = o.replace(f, "<campaign>")
                return o
            if isinstance(o, list):
                return [walk(x) for x in o]
            if isinstance(o, dict):
                return {k: walk(v) for k, v in o.items()}
            return o
        return tools_compare.scrub(walk(obj), self.config, self.st)

    def doc(self):
        ident = self.ctx.pool.identity
        return {"schema": CAMPAIGN_SCHEMA, "id": self.id, "params": self.params,
                "build": {k: ident.get(k) for k in ("version", "commit", "sha256", "build_status")},
                "buildTag": f"[{ident.get('build_status')}]", "status": self.status, "stopReason": self.stop_reason,
                "cases": {"total": len(self.cases) + len(self.rejects), "accepted": len(self.cases),
                          "rejected": len(self.rejects)},
                "generatorRejects": self.rejects, "cells": self.cells, "countsByClass": self.counts,
                "findings": len(self.store.findings),
                "note": tools_compare.EVIDENCE_NOT_PROOF}

    def save(self):
        _write(self.folder / "campaign.json", _dump(self.doc()))
        _write(self.folder / "findings.jsonl", "".join(line + "\n" for line in self.store.lines()))

    # -- the loop --
    def run(self):
        self.folder.mkdir(parents=True, exist_ok=False)
        self.log_file = open(self.folder / "campaign.log", "a", encoding="utf-8")
        try:
            self.save()
            self._run()
            if self.status == "running":
                self.status = "done"
        except Exception as ex:  # noqa: BLE001 - the campaign records its own failure
            self.status = "failed"
            self.stop_reason = f"{type(ex).__name__}: {tools_compare.scrub(str(ex), self.config, self.st)}"
            self.log(self.stop_reason)
        finally:
            self.save()
            _write(self.folder / "summary.md", self.summary_md())
            self.log_file.close()
        return self.doc()

    def _run(self):
        s = self.ctx.session
        for label, case in case_list(self.params, self.proposed_cases):
            gates = prepare(s, case, self.folder / "cases" / label)
            if gates["accepted"]:
                self.cases[label] = (case, gates)
            else:
                self.rejects.append({"case": label, "reason": gates["reason"]})
            self.log(f"{label}: {'accepted' if gates['accepted'] else 'reject: ' + gates['reason']}")
        self.save()
        new = []
        for mode in self.params["modes"]:            # mode-major: the rival workers stay warm within a mode
            for label, (case, gates) in self.cases.items():
                if self.over_budget():
                    self.status, self.stop_reason = "stopped", "budget reached"
                    return
                record, cell, _ = compare_cell(self.ctx, self.st, mode, gates["lab"],
                                               self.folder / "bundles" / f"{label}-{mode}", self.params["tools"])
                self.cells += 1
                text = Path(gates["lab"]).read_text(encoding="utf-8")
                for tool, cls, reasons, known in triage_cell(record, text):
                    self.counts.setdefault(mode, {}).setdefault(cls, 0)
                    self.counts[mode][cls] += 1
                    if cls not in triage.FINDING_CLASSES:
                        continue
                    sig = triage.signature(cls, reasons, known)
                    k = triage.key(cls, mode, tool, sig)
                    occ = {"case": label, "lab": f"cases/{label}/{case['name']}.m",
                           "labSha256": gates["sha256"].get("lab"), "bundle": self.rel(cell.parents[2]),
                           "maxDiffs": triage.max_diffs(record, tool)}
                    f, is_new = self.store.add(k, occ, lambda fid: self._finding(fid, k, label, case, gates, record,
                                                                                 cell, tool, reasons, known))
                    if is_new:
                        new.append((f, label, case, gates))
                self.log(f"{label} {mode}: " + ", ".join(f"{t}={c}" for t, c, _, _ in triage_cell(record, text)))
                self.save()
        if self.params["refine"]:
            for f, label, case, gates in new:
                if self.over_budget():
                    self.status, self.stop_reason = "stopped", "budget reached during refinement"
                    return
                self.refine(f, label, case, gates)
                self.save()

    def _finding(self, fid, k, label, case, gates, record, cell, tool, reasons, known):
        f = {"schema": triage.FINDING_SCHEMA, "id": fid, "class": k["class"], "signature": k["signature"],
             "modes": [k["mode"]], "tools": ["gridsim", tool],
             "maxDiffs": triage.max_diffs(record, tool),
             "gateMetrics": {t: ((record["tools"].get(t) or {}).get("warmGate") or {}).get("firstMetrics")
                             for t in ("gridsim", tool)},
             "status": {t: (record["tools"].get(t) or {}).get("status") for t in ("gridsim", tool)},
             "case": {"label": label, "family": case.get("family"), "seed": case.get("seed"),
                      "buses": len(case["buses"]), "branches": len(case["branches"]),
                      "lab": f"cases/{label}/{case['name']}.m", "sha256": gates["sha256"]},
             "triage": {"reasons": reasons, "documented": known},
             "reproducer": None, "neighbourhood": None,
             "proposal": triage.proposal(k["class"], tool, known, reasons),
             "note": tools_compare.EVIDENCE_NOT_PROOF}
        if self.params["evidence"] and tool != "andes":
            try:
                ev = tools_compare._evidence(self.ctx, self.st, self.config, cell, record, tool)
                f["triage"]["evidence"] = self.neutral(ev)
            except Exception as ex:  # noqa: BLE001 - evidence is best effort, its failure is recorded
                f["triage"]["evidence"] = {"error": f"{type(ex).__name__}: {self.neutral(str(ex))}"}
        return f

    # -- refinement --
    def _predicate(self, f, fid_dir, counter, tools):
        target = {k: f[k] for k in ("class", "signature")}
        mode, tool = f["modes"][0], f["tools"][1]

        def check(cand):
            counter[0] += 1
            n = counter[0]
            gates = prepare(self.ctx.session, cand, fid_dir / f"k{n:03d}")
            if not gates["accepted"]:
                self.log(f"  k{n:03d}: candidate rejected by the gates: {gates['reason']}")
                return False, "rejected: " + gates["reason"]
            record, _, _ = compare_cell(self.ctx, self.st, mode, gates["lab"],
                                        self.folder / "bundles" / "refine" / f"{f['id']}-k{n:03d}", tools)
            self.cells += 1
            text = Path(gates["lab"]).read_text(encoding="utf-8")
            row = next((r for r in triage_cell(record, text, {tool}) if r[0] == tool), None)
            if row is None:
                return False, "not compared"
            _, cls, reasons, known = row
            # The same finding means the same class and signature with both sides in the same state as when it was
            # found: a candidate on which one side no longer runs at all (GridSim refusing a degenerate network, say)
            # does not show the finding, it shows something else.
            states = {t: (record["tools"].get(t) or {}).get("status") for t in ("gridsim", tool)}
            same = (cls == target["class"] and triage.signature(cls, reasons, known) == target["signature"]
                    and states == f["status"])
            if cls == target["class"] and not same and states != f["status"]:
                self.log(f"  k{n:03d}: same class, but statuses {states} differ from {f['status']}")
            return same, cls
        return check

    def refine(self, f, label, case, gates):
        """Search around a finding (seeded perturbations) and shrink it to a minimal reproducer."""
        fid_dir = self.folder / "refine" / f["id"]
        counter = [0]
        tool = f["tools"][1]
        tools = ["gridsim", tool]
        check = self._predicate(f, fid_dir, counter, tools)
        ok, _ = check(case)
        if not ok:          # the pair alone does not show it (a cross-check may need every tool): use them all
            tools = self.params["tools"]
            check = self._predicate(f, fid_dir, counter, tools)
            ok, _ = check(case)
        if not ok:
            f["reproducer"] = {"status": "not reproduced on re-run", "evaluations": counter[0]}
            self.log(f"{f['id']}: not reproduced on re-run")
            return
        rng = Rng(derive(self.params["seed"], "neighbourhood", f["id"]))
        hood = []
        for step in model.perturbations(case, rng):
            try:
                cand = model.apply_step(case, step)
            except model.StepRefused:
                continue
            same, cls = check(cand)
            hood.append({"step": step, "class": cls, "sameFinding": same})
        f["neighbourhood"] = hood
        base_steps = len(case.get("steps", []))

        def pred(cand):
            return check(cand)[0]
        best, trail, evals, minimal = shrink.shrink(
            case, pred, budget=self.params["refineBudget"],
            log=lambda step, ok: self.log(f"  {f['id']} shrink {json.dumps(step, sort_keys=True)}: "
                                          f"{'kept' if ok else 'not kept'}"))
        out = self.folder / "reproducers" / f["id"]
        rg = prepare(self.ctx.session, best, out)
        f["reproducer"] = {
            "status": "minimal" if minimal else "budget reached (smaller than the original, maybe not minimal)",
            "from": {"case": label, "family": case.get("family"), "seed": case.get("seed"),
                     "model": f"cases/{label}/model.json", "modelSha256": gates["sha256"].get("model")},
            "steps": best.get("steps", [])[base_steps:], "tools": tools,
            "buses": len(best["buses"]), "branches": len(best["branches"]), "gens": len(best["gens"]),
            "offNominalTaps": sum(1 for br in best["branches"] if model.is_transformer(br) and br["tap"] != 1.0),
            "files": {k: f"reproducers/{f['id']}/{v}" for k, v in rg["files"].items()},
            "sha256": rg["sha256"], "evaluations": counter[0], "shrinkEvaluations": evals,
            "minimality": ("1-minimal under the reductions: no single merge, drop or nominal-tap step keeps the "
                           "finding" if minimal else "the evaluation budget ran out")}
        self.log(f"{f['id']}: reproducer {len(best['buses'])} buses, {len(best['branches'])} branches, "
                 f"{len(trail)} steps, {counter[0]} evaluations")

    # -- summary --
    def summary_md(self):
        d = self.doc()
        p = self.params
        lines = [f"# Case lab campaign {self.id}", "",
                 f"Build {d['build'].get('version')} {d['buildTag']}. Seed {p['seed']}, {p['n']} generated case(s)"
                 f"{' + ' + str(len(p['proposed'])) + ' proposed' if p['proposed'] else ''}, families "
                 f"{', '.join(p['families'])}, modes {', '.join(p['modes'])}. Status: {d['status']}"
                 f"{' (' + d['stopReason'] + ')' if d['stopReason'] else ''}.", "",
                 "A disagreement between tools is evidence, not proof. GridSim's solver is verified; these generated "
                 "cases are Synthetic and not validated.", "",
                 f"Cases: {d['cases']['accepted']} accepted, {d['cases']['rejected']} rejected by the generator gates. "
                 f"Lab cells run: {d['cells']}.", "", "## Counts by class", "", "| Mode | Class | Count |",
                 "|---|---|---|"]
        for mode in p["modes"]:
            for cls, n in sorted((self.counts.get(mode) or {}).items()):
                lines.append(f"| {mode} | {cls} | {n} |")
        if self.rejects:
            lines += ["", "## Generator rejects", ""] + [f"- {r['case']}: {r['reason']}" for r in self.rejects]
        lines += ["", "## Findings", ""]
        if not self.store.findings:
            lines.append("None.")
        for f in self.store.findings.values():
            rep = f.get("reproducer") or {}
            lines += [f"### {f['id']}: {f['class']} ({f['modes'][0]}, {f['tools'][1]})", "",
                      f"- Signature: {f['signature']}",
                      f"- First seen: {f['case']['label']} ({f['case']['buses']} buses); occurrences: "
                      f"{len(f['occurrences'])} ({', '.join(o['case'] for o in f['occurrences'])})"]
            if rep.get("files"):
                lines.append(f"- Reproducer: {rep['buses']} bus(es), {rep['branches']} branch(es), "
                             f"{rep['offNominalTaps']} off-nominal tap(s); {rep['status']}; file "
                             f"{rep['files'].get('lab')} (SHA-256 {rep['sha256'].get('lab', '')[:16]}...)")
            elif rep:
                lines.append(f"- Reproducer: {rep.get('status')}")
            lines += [f"- Proposal (text only): {f['proposal']}", ""]
        return "\n".join(lines) + "\n"


def start(ctx, st, params, budget_s, proposed_cases=(), wait=True):
    """Run a campaign (wait) or start it in the background. Returns (campaign id, doc or None)."""
    with _RUN_LOCK:
        busy = [cid for cid, t in RUNNING.items() if t.is_alive()]
        if busy:
            raise McpError(f"campaign {busy[0]} is still running (the Lab runs one cell at a time); see caselab_status")
        cid = new_id(ctx.pool.config, params)
        camp = Campaign(ctx, st, params, cid, budget_s, proposed_cases)
        if wait:
            RUNNING[cid] = threading.current_thread()
    if wait:
        try:
            return cid, camp.run()
        finally:
            RUNNING.pop(cid, None)
    t = threading.Thread(target=camp.run, name=f"caselab-{cid}", daemon=True)
    with _RUN_LOCK:
        RUNNING[cid] = t
    t.start()
    return cid, None
