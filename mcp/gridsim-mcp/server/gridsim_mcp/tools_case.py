"""Session and case tools: open and close a session, start a case, load, inspect, edit, history, save, validate."""

import re
from typing import Literal, Optional

from . import paths
from .allowlist import Refused, check_verb, forbidden_path, refusal
from .host import KINDS, McpError
from .registry import Outcome, tool

Kind = Literal["bus", "generator", "load", "branch", "shunt"]


@tool("session_open", "Open a GridSim session", read_only=False, destructive=False, idempotent=True)
def session_open(ctx):
    """Open a session: one resident GridSim host (gridsim rpc, a pipe, no network) with its own cases, results and
    study record. Every other tool takes the session name (default "default") and opens it on first use; call this to
    see the build identity and build status first."""
    s, created = ctx.pool.open(ctx.session_name)
    ctx._session = s
    ident = ctx.pool.identity
    doc = {"session": s.name, "created": created, "tool": s.host.tool, "operations": list(s.host.operations),
           "build": {k: ident.get(k) for k in ("version", "commit", "commit_listed", "sha256", "program_listed", "build_status",
                                                "gridsim_reported", "server_check",
                                                "compliance_exit", "reasons")},
           "licensed": "yes" if s.config.licensed else "no"}
    return Outcome(document=doc, summary=f"session '{s.name}' open on {ident['version']}", store=False)


@tool("session_close", "Close a GridSim session", read_only=False, destructive=True, idempotent=True)
def session_close(ctx):
    """Close a session and end its GridSim host. Its cases are gone; its record stays exportable until the server
    ends only if exported first (record_export)."""
    s = ctx.pool.sessions.get(ctx.session_name)
    if s is None:
        return Outcome(document={"session": ctx.session_name, "closed": False}, summary="no such session", store=False)
    ctx._session = s
    n = len(s.record.entries)
    ctx.pool.close(ctx.session_name)
    return Outcome(document={"session": ctx.session_name, "closed": True, "record_entries": n},
                   summary=f"session '{ctx.session_name}' closed", store=False)


_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,40}$")


def _seed_text(name, base_mva, base_kv, slack_bus, vm):
    return (f"function mpc = {name}\n"
            "% A one-bus seed written by the GridSim MCP: a slack bus and its generator. Build the network on it with\n"
            "% case_edit add (buses, branches, loads, generators).\n"
            "mpc.version = '2';\n"
            f"mpc.baseMVA = {base_mva!r};\n"
            "%% bus_i type Pd Qd Gs Bs area Vm Va baseKV zone Vmax Vmin\n"
            f"mpc.bus = [\n\t{slack_bus}\t3\t0\t0\t0\t0\t1\t{vm!r}\t0\t{base_kv!r}\t1\t1.1\t0.9;\n];\n"
            "%% bus Pg Qg Qmax Qmin Vg mBase status Pmax Pmin\n"
            f"mpc.gen = [\n\t{slack_bus}\t0\t0\t9999\t-9999\t{vm!r}\t{base_mva!r}\t1\t9999\t0;\n];\n"
            "%% fbus tbus r x b rateA rateB rateC ratio angle status angmin angmax\n"
            "mpc.branch = zeros(0, 13);\n")


@tool("case_new", "Start a new case from a slack bus", read_only=False, destructive=False, idempotent=True)
def case_new(ctx, name: str, base_kv: float = 132.0, base_mva: float = 100.0, slack_bus: int = 1,
             vm_pu: float = 1.0):
    """Start a network from scratch: writes a one-bus seed case (a slack bus with its generator) as
    cases/<name>.m in the workspace and loads it. Then add buses, branches, loads and generators with case_edit
    (op "add"). Every step is checked by GridSim."""
    if not _NAME.match(name):
        raise McpError("name must start with a letter and use letters, digits and underscores (at most 41)")
    if not (base_kv > 0 and base_mva > 0 and vm_pu > 0 and slack_bus > 0):
        raise McpError("base_kv, base_mva, vm_pu and slack_bus must be above 0")
    s = ctx.session
    rel = f"cases/{name}.m"
    target = paths.resolve(s.config, rel, write=True)
    text = _seed_text(name, float(base_mva), float(base_kv), int(slack_bus), float(vm_pu))
    if target.is_file():
        if target.read_text(encoding="utf-8") != text:
            raise McpError(f"{rel} already exists with other content; pick another name")
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\n")
    c = s.load(rel, rel)
    return Outcome(document=c.load_response["result"], case=c,
                   summary=f"{c.id}: new case '{name}' (slack bus {slack_bus}, {base_kv} kV)", store=False,
                   extra={"case_id": c.id, "file": rel})


@tool("case_load", "Load a case", read_only=False, destructive=False, idempotent=False)
def case_load(ctx, case: str):
    """Load a case into the session: a built-in name (case9, case14, case30, ...), or a file in the workspace or the
    project folder (MATPOWER .m, PSS/E .raw, a JSON v1 folder or a v2 manifest). Returns the case id (c1, c2, ...) to use
    with the other tools, its size and its SHA-256. Shipped GB cases load, but are offered to static verbs only."""
    s = ctx.session
    token = str(case).strip()
    if forbidden_path(token):
        raise Refused(refusal(f"the location '{token}'"))
    if paths.looks_like_path(token) or (s.config.workspace / token).exists():
        arg = paths.host_arg(s.config, token)
        source = arg
    else:
        arg = source = token
    c = s.load(source, arg)
    r = c.load_response["result"]
    return Outcome(document=r, case=c, store=False, extra={"case_id": c.id},
                   summary=f"{c.id}: {r.get('case')} ({r.get('buses')} buses, {r.get('branches')} branches)")


@tool("case_inspect", "Inspect a case", read_only=True, idempotent=True)
def case_inspect(ctx, case_id: str, kind: Optional[Kind] = None, key: Optional[str] = None,
                 attributes: Optional[list[str]] = None):
    """Read a case as it stands. With no kind: the element count of every kind. With a kind: every element of that kind
    with every attribute. With a kind and key: that element's attributes (on a branch, p_from_mw, q_from_mvar,
    p_to_mw, q_to_mvar and loading_pct read the solved flows; on a generator, p_solved_mw and q_solved_mvar)."""
    s = ctx.session
    c = s.case(case_id)
    if kind is None:
        counts = {}
        for k in KINDS:
            counts[k] = len(s.call("elements", handle=c.handle, kind=k)["result"]["rows"])
        doc = {"case": c.load_response["result"].get("case"), "revision": s.edits(c)["revision"], "counts": counts}
        return Outcome(document=doc, case=c, store=False, summary=f"{c.id}: " + ", ".join(f"{v} {k}" for k, v in counts.items()))
    if key is None:
        r = s.call("elements", handle=c.handle, kind=kind)["result"]
        return Outcome(document=r, case=c, store=False, summary=f"{c.id}: {len(r['rows'])} {kind} element(s)")
    fields = {"handle": c.handle, "kind": kind, "key": str(key)}
    if attributes:
        fields["attributes"] = [str(a) for a in attributes]
    r = s.call("get", **fields)["result"]
    return Outcome(document=r, case=c, store=False, summary=f"{c.id}: {kind} {key}")


@tool("case_edit", "Edit a case (set, add, delete)", read_only=False, destructive=True, idempotent=False)
def case_edit(ctx, case_id: str, op: Literal["set", "add", "delete"], kind: Kind, key: str,
              values: Optional[dict[str, float]] = None, cascade: bool = False):
    """Change the case held in the session (never a file). op "set" writes attributes; "add" adds an element (a bus
    needs base_kv; a branch key is "from-to:circuit" and needs x; a load or shunt key is its bus id; a generator key is
    "bus:id"); "delete" removes one (cascade deletes what refers to it). GridSim checks every edit before applying it:
    a refused edit changes nothing. Returns the new revision."""
    s = ctx.session
    c = s.case(case_id)
    key = str(key)
    if op == "set":
        if not values:
            raise McpError("set needs values (attribute names and numbers)")
        r = s.call("set", handle=c.handle, kind=kind, key=key, values=dict(values))
    elif op == "add":
        r = s.call("add", handle=c.handle, kind=kind, key=key, values=dict(values or {}))
    elif op == "delete":
        r = s.call("delete", handle=c.handle, kind=kind, key=key, cascade=bool(cascade))
    else:
        raise McpError("op is set, add or delete")
    res = r["result"]
    c.revision = res.get("revision", c.revision)
    return Outcome(document=res, case=c, store=False, summary=f"{c.id}: {op} {kind} {key} -> revision {c.revision}")


@tool("case_history", "Show a case's edit history", read_only=True, idempotent=True)
def case_history(ctx, case_id: str):
    """The edit log of a case: revision, kind, key, attribute, before and after, in order."""
    s = ctx.session
    c = s.case(case_id)
    r = s.edits(c)
    return Outcome(document=r, case=c, store=False, summary=f"{c.id}: {len(r['edits'])} edit(s), revision {r['revision']}")


@tool("case_save", "Save a case to a file", read_only=False, destructive=False, idempotent=True)
def case_save(ctx, case_id: str, path: str, format: Literal["v2", "raw33", "raw34", "raw35", "raw36", "rawx"] = "v2"):
    """Write the case as it stands to a file in the workspace (JSON v2 manifest, or PSS/E RAW 33-36 / RAWX). GridSim
    never overwrites a file with different content. Returns the files written with their SHA-256 and the writer's
    notes."""
    s = ctx.session
    c = s.case(case_id)
    arg = paths.host_arg(s.config, path, write=True)
    paths.resolve(s.config, path, write=True).parent.mkdir(parents=True, exist_ok=True)
    r = s.call("save", handle=c.handle, path=arg, format=format)["result"]
    return Outcome(document=r, case=c, store=False, extra={"path": arg},
                   summary=f"{c.id}: saved as {format} to {arg} ({len(r.get('files', []))} file(s))")


@tool("case_validate", "Validate a case against the v2 schema", read_only=True, idempotent=True)
def case_validate(ctx, case_id: Optional[str] = None, path: Optional[str] = None):
    """Check a case with `gridsim validate`: the JSON v2 schema plus GridSim's semantic checks. Give a case id (the case
    as it stands is written as JSON v2 first) or a v2 file or folder in the workspace. Exit 0 valid, 3 not valid (each
    failure named by JSON pointer)."""
    s = ctx.session
    if (case_id is None) == (path is None):
        raise McpError("give exactly one of case_id or path")
    c = None
    if case_id is not None:
        c = s.case(case_id)
        target = s.materialise(c, force_v2=True)
    else:
        target = paths.host_arg(s.config, path)
    check_verb("validate", [target])
    return run_document(s, "validate", [target], case=c)


def run_document(s, verb, args, case=None, timeout_s=None, summary=None):
    """Run a verb on the session's host (rpc run-verb) and shape its answer."""
    fields = {"verb": verb, "args": [str(a) for a in args]}
    if timeout_s:
        fields["timeout_s"] = float(timeout_s)
    r = s.call("run-verb", **fields)
    res = r["result"]
    code = r["exitCode"]
    if res.get("document") is not None:
        doc = res["document"]
        head = summary or f"gridsim {verb}: exit {code}"
        return Outcome(document=doc, case=case, exit_code=code, summary=head,
                       extra={"stderr": res.get("stderr", "")} if res.get("stderr") else {})
    doc = {"verb": verb, "exitCode": code, "stdout": res.get("stdout", ""), "stderr": res.get("stderr", "")}
    return Outcome(document=doc, case=case, exit_code=code, summary=summary or f"gridsim {verb}: exit {code} (printed output)")
