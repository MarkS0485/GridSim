"""Planner tools: bring a model in (import_model), derive line parameters from geometry (line_parameters), run a
declarative study case (study_case_run) and render a hash-bound report (report_render)."""

import hashlib
import json
import re
from pathlib import Path
from typing import Any, Literal, Optional

from . import paths
from .allowlist import check_verb
from .host import McpError
from .registry import Outcome, tool

_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,40}$")

ImportFormat = Literal["auto", "matpower", "raw", "rawx", "ucte", "cgmes", "json", "dgs", "scl"]

_EXT = {".m": "matpower", ".raw": "raw", ".rawx": "rawx", ".uct": "ucte", ".xml": "cgmes", ".zip": "cgmes",
        ".json": "json", ".dgs": "dgs", ".scd": "scl", ".ssd": "scl", ".icd": "scl", ".cid": "scl", ".iid": "scl"}


def _detect(path):
    p = Path(path)
    if p.is_dir():
        return "cgmes" if not (p / "system.json").is_file() and any(p.glob("*.xml")) else "json"
    return _EXT.get(p.suffix.lower())


def _lines(text, prefix):
    return [ln.strip() for ln in (text or "").splitlines() if ln.strip().lower().startswith(prefix)]


def _file_sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _outputs(ws, folder):
    """Every file written under a workspace folder, with its SHA-256, in name order."""
    base = ws / folder
    return [{"path": p.relative_to(ws).as_posix(), "sha256": _file_sha(p)}
            for p in sorted(base.rglob("*")) if p.is_file()]


@tool("import_model", "Import a network model into the workspace", read_only=False, destructive=False,
      idempotent=False)
def import_model(ctx, source: str, name: str, format: ImportFormat = "auto",
                 target: Literal["v2", "matpower"] = "v2", params: Optional[str] = None, assume_closed: bool = False,
                 accept_unconfirmed: bool = False, load: bool = True):
    """Bring a model into the session workspace, under imports/<name>/. MATPOWER, PSS/E RAW 33-36, RAWX, UCTE-DEF,
    CGMES (.xml, .zip or a folder of profiles) and JSON cases go through `gridsim convert`, which re-reads what it wrote
    and proves the round trip (the written case solves bit-identically); the 'round trip:' statement is returned. A
    built-in case name (case9, case14, ...) is converted the same way. A PowerFactory DGS file goes through `gridsim dgs-import` (node-breaker projection, its import report returned; a
    one-way import, no round trip). An IEC 61850 SCL file goes through `gridsim scl-import` with a params file
    (impedances and switch states come from it); its node-breaker file is written, not loaded. Every 'note:' line
    (what the target format cannot hold, or what the importer dropped) is returned: report them. With load=true the
    imported case is loaded and its case id returned."""
    s = ctx.session
    if not _NAME.match(name):
        raise McpError("name must start with a letter and use letters, digits, '-' and '_' (at most 41)")
    token = str(source).strip()
    builtin = not paths.looks_like_path(token) and not (s.config.workspace / token).exists()
    if builtin:
        src, fmt = token, ("builtin" if format == "auto" else format)
        if fmt in ("dgs", "scl"):
            raise McpError(f"'{token}' is not a file; a {fmt} import reads a file in the workspace")
    else:
        src = paths.host_arg(s.config, token)
        src_abs = paths.resolve(s.config, token)
        if not src_abs.exists():
            raise McpError(f"'{source}' does not exist")
        fmt = _detect(src_abs) if format == "auto" else format
    if fmt is None:
        raise McpError(f"cannot tell the format of '{source}' from its name; give format")
    folder = f"imports/{name}"
    out_dir = paths.resolve(s.config, folder, write=True)
    if out_dir.exists() and any(out_dir.iterdir()):
        raise McpError(f"{folder} already holds files; pick another name (GridSim never overwrites)")
    out_dir.mkdir(parents=True, exist_ok=True)

    case_file = None
    if fmt == "dgs":
        args = [src, "--out", folder] + (["--accept-unconfirmed"] if accept_unconfirmed else [])
        verb = "dgs-import"
        case_file = f"{folder}/case.m"
    elif fmt == "scl":
        if not params:
            raise McpError("an SCL import needs params: a gridsim-scl-params/1 file in the workspace (impedances, "
                           "set points and switch states keyed by SCL path)")
        args = [src, "--params", paths.host_arg(s.config, params), "--out", f"{folder}/{name}.substations.json"]
        args += ["--assume-closed"] if assume_closed else []
        verb = "scl-import"
    else:
        verb = "convert"
        case_file = f"{folder}/{name}.case.json" if target == "v2" else f"{folder}/{name}.m"
        args = [src, case_file]
    check_verb(verb, args)
    run = s.run_raw(verb, args)
    printed = (run["stdout"] or "") + "\n" + (run["stderr"] or "")
    notes = _lines(printed, "note:")
    round_trip = _lines(printed, "round trip:")
    written = _outputs(s.config.workspace, folder)
    doc = {
        "importer": verb,
        "format": fmt,
        "source": src,
        "exitCode": run["exit_code"],
        "notes": notes,
        "round_trip": round_trip,
        "files": written,
    }
    report = None
    if fmt == "dgs":
        rp = out_dir / "dgs-import-report.json"
        if rp.is_file():
            report = json.loads(rp.read_text(encoding="utf-8"))
            doc["import_report"] = {k: report.get(k) for k in ("schema", "provenance", "complete", "summary",
                                                               "unsupportedClasses", "unconfirmedMappingsUsed")
                                    if k in report}
        doc["round_trip"] = ["no round trip: dgs-import is a one-way import; the projected case.m is the model"]
    if fmt == "scl":
        doc["round_trip"] = ["no round trip: scl-import writes a node-breaker file (use it with substation-state)"]
    if run["exit_code"] not in (0, None) or run["error"]:
        doc["message"] = _error_line(printed) or run["error"] or _last(printed)
        doc["printed"] = printed.strip()[-4000:]
        return Outcome(document=doc, exit_code=run["exit_code"], store=False, status="ok",
                       summary=f"import of {source} via {verb}: exit {run['exit_code']} ({doc['message']})")
    extra = {"files": [w["path"] for w in written]}
    c = None
    if load and case_file and (s.config.workspace / case_file).exists():
        c = s.load(case_file, case_file)
        extra["case_id"] = c.id
        r = c.load_response["result"]
        doc["loaded"] = {"case_id": c.id, "buses": r.get("buses"), "branches": r.get("branches")}
    summ = (f"imported {source} via {verb} into {folder}: {len(notes)} note(s)"
            + (f"; {round_trip[0]}" if round_trip else "") + (f"; loaded as {c.id}" if c else ""))
    return Outcome(document=doc, case=c, exit_code=run["exit_code"], store=False, extra=extra, summary=summ)


def _error_line(text):
    return next((ln.strip() for ln in (text or "").splitlines() if ln.strip().lower().startswith("error:")), "")


def _last(text):
    return next((ln.strip() for ln in reversed((text or "").splitlines()) if ln.strip()), "")


@tool("line_parameters", "Derive line parameters from conductor geometry", read_only=True, idempotent=True)
def line_parameters(ctx, geometry: Optional[dict[str, Any]] = None, file: Optional[str] = None, cable: bool = False,
                    length_km: Optional[float] = None, base_kv: Optional[float] = None,
                    base_mva: Optional[float] = None):
    """Series impedance and shunt capacitance from geometry with `gridsim line-constants`, so a line's R, X and B are
    derived rather than invented. Overhead: give the gridsim-lineconstants/1 object as geometry (or a file); cables:
    cable=true and a gridsim-cableconstants/1 file. With length_km, base_kv and base_mva (all three) the positive-
    sequence R1, X1, B1 are also given in per unit. The inputs (conductor resistance, GMR, radius, spacing) are your
    assumptions: tag them in the case (Synthetic or CuratedPlaceholder with [VERIFY]) unless they come from a cited
    datasheet."""
    s = ctx.session
    pu = [length_km, base_kv, base_mva]
    if any(v is not None for v in pu) and not all(v is not None for v in pu):
        raise McpError("give all three of length_km, base_kv and base_mva, or none")
    if cable or file is not None:
        if file is None:
            raise McpError("a cable study reads a gridsim-cableconstants/1 file: give file")
        args = (["--cable"] if cable else []) + [paths.host_arg(s.config, file)]
        if all(v is not None for v in pu):
            args += ["--length-km", repr(float(length_km)), "--base-kv", repr(float(base_kv)),
                     "--base-mva", repr(float(base_mva))]
        check_verb("line-constants", args)
        r = s.run_raw("line-constants", args)
        doc = r["document"]
        if doc is None:
            raise McpError(r["error"] or f"line-constants exit {r['exit_code']}: {_last(r['stderr'] or r['stdout'])}")
        return Outcome(document=doc, exit_code=r["exit_code"], summary=_lc_summary(doc))
    if geometry is None:
        raise McpError("give geometry (a gridsim-lineconstants/1 object) or file")
    check_verb("line-constants", ["<rpc>"])
    fields = {"geometry": geometry}
    if all(v is not None for v in pu):
        fields["per_unit"] = {"length_km": float(length_km), "base_kv": float(base_kv), "base_mva": float(base_mva)}
    r = s.call("line-constants", **fields)
    doc = r["result"]["document"]
    return Outcome(document=doc, exit_code=r["exitCode"], summary=_lc_summary(doc))


def _lc_summary(doc):
    sm = (doc or {}).get("summary", {})
    keys = [k for k in ("r1_ohm_per_km", "x1_ohm_per_km", "b1_us_per_km", "c1_nf_per_km", "r1_pu", "x1_pu", "b1_pu")
            if k in sm]
    return "line-constants: " + (", ".join(f"{k} {sm[k]}" for k in keys) if keys else f"{len(sm)} summary values")


@tool("study_case_run", "Run a study case or planning study", read_only=False, destructive=False, idempotent=True)
def study_case_run(ctx, file: str, out: str, kind: Literal["study-case", "planning-study"] = "study-case",
                   max_jobs: Optional[int] = None, timeout_s: Optional[float] = None):
    """Run a declarative study: `gridsim study-case` (gridsim-studycase/1: one hashed master case, dated variations,
    scenarios, groups and studies) or `gridsim planning-study` (gridsim-planning/1: dated snapshots), into a new folder
    in the workspace. Returns the run manifest with each job's result SHA-256: cite those hashes. Write the study as
    JSON rather than looping over calls; the expansion is deterministic and the folder is byte-identical on a re-run
    (except evidence.json)."""
    s = ctx.session
    f = paths.host_arg(s.config, file)
    o = paths.host_arg(s.config, out, write=True)
    target = paths.resolve(s.config, out, write=True)
    if target.exists() and any(target.iterdir()):
        raise McpError(f"{out} already holds files; give a new folder")
    args = [f, "--out", o] + (["--max-jobs", str(int(max_jobs))] if max_jobs else [])
    check_verb(kind, args)
    r = s.run_raw(kind, args, timeout_s=timeout_s)
    manifest = None
    for cand in ("run-manifest.json", "result-manifest.json"):
        mp = target / cand
        if mp.is_file():
            manifest = {"file": f"{o}/{cand}", "sha256": _file_sha(mp),
                        "content": json.loads(mp.read_text(encoding="utf-8"))}
            break
    doc = {"verb": kind, "exitCode": r["exit_code"], "out": o, "manifest": manifest,
           "files": _outputs(s.config.workspace, o) if target.exists() else [],
           "document": r["document"]}
    if r["error"] and manifest is None:
        doc["message"] = _error_line((r["stdout"] or "") + "\n" + (r["stderr"] or "")) or r["error"]
        doc["printed"] = ((r["stdout"] or "") + (r["stderr"] or "")).strip()[-4000:]
    n = len(doc["files"])
    return Outcome(document=doc, exit_code=r["exit_code"], store=False,
                   summary=f"{kind} {file}: exit {r['exit_code']}, {n} file(s) in {o}"
                   + ("" if manifest else " (no run manifest written)"))


@tool("report_render", "Render a hash-bound report of a result", read_only=False, destructive=False, idempotent=True)
def report_render(ctx, result: str, out: str, template: Optional[str] = None,
                  format: Literal["md", "html", "csv"] = "md"):
    """Render a result document as a report with `gridsim report`: deterministic and bound to the result by its
    SHA-256 (the report records the template and result hashes; no timestamp). result is a result id (r1, ...) or a
    result file in the workspace. With no template, `gridsim report --template-for` first writes the default template
    (a summary, then a section per table) beside the report. GridSim never overwrites: out must be a new file. Narrate
    the report; do not retype its numbers."""
    s = ctx.session
    res = s.result_path(result) if re.fullmatch(r"r\d+", str(result)) else paths.host_arg(s.config, result)
    o = paths.host_arg(s.config, out, write=True)
    out_abs = paths.resolve(s.config, out, write=True)
    if out_abs.exists():
        raise McpError(f"{out} exists; GridSim never overwrites a report")
    out_abs.parent.mkdir(parents=True, exist_ok=True)
    steps = []
    if template is None:
        tpl = Path(o).with_suffix("").as_posix() + ".template.json"
        if paths.resolve(s.config, tpl, write=True).exists():
            raise McpError(f"{tpl} exists; give template, or another out")
        a = ["--template-for", res, "--out", tpl]
        check_verb("report", a, case_tokens=[res])
        r = s.run_raw("report", a)
        steps.append({"step": "template-for", "exitCode": r["exit_code"], "error": r["error"]})
        if r["exit_code"] != 0:
            raise McpError(r["error"] or f"report --template-for exit {r['exit_code']}")
        template_arg = tpl
    else:
        template_arg = paths.host_arg(s.config, template)
    a = [template_arg, res, "--out", o, "--format", format]
    check_verb("report", a, case_tokens=[res])
    r = s.run_raw("report", a)
    steps.append({"step": "report", "exitCode": r["exit_code"], "error": r["error"]})
    if r["exit_code"] != 0:
        raise McpError(r["error"] or f"report exit {r['exit_code']}: {_last(r['stderr'] or r['stdout'])}")
    ws = s.config.workspace
    doc = {"report": o, "report_sha256": _file_sha(ws / o), "template": template_arg,
           "template_sha256": _file_sha(ws / template_arg), "result": res, "result_sha256": _file_sha(ws / res),
           "format": format, "steps": steps}
    return Outcome(document=doc, exit_code=0, store=False, extra={"report": o},
                   summary=f"report of {result} written to {o} ({format})")

