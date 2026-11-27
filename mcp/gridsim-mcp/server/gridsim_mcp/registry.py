"""The tool registry and the one dispatch path every call takes.

A tool is a plain function ``fn(ctx, **arguments) -> Outcome``. ``dispatch`` runs it, applies the guards, computes the
case and result hashes, appends the call to the session's study record and shapes the answer: GridSim's own JSON under
``document`` (structured content) plus an ``evidence`` block, and the same as text. ``record_replay`` re-runs recorded
calls through this same path, so a replay is checked exactly as the original was made.
"""

import importlib
import inspect
import json
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

from . import build_status, record
from .allowlist import Refused
from .host import McpError

#: Every answer says this: GridSim's core is verified, the system and any study built on it are not validated.
EVIDENCE_NOTE = ("GridSim numbers, verified solver; the case and study are not validated against measurements. "
                 "Cite the build, result hash and case hash with any claim.")


@dataclass
class Outcome:
    document: Any = None                 # GridSim's own JSON (a result document or an rpc result), as returned
    summary: str = ""                    # one human line
    case: Any = None                     # the CaseState the call worked on, if any
    exit_code: Optional[int] = None
    extra: dict = field(default_factory=dict)   # MCP-side fields beside the document (verdicts, ids)
    store: bool = True                   # keep a gridsim-cli-result/1 document as a result file (r1, r2, ...)
    status: str = "ok"
    tuning: Any = None                   # tuning.Applied of a tuned run (TUNED marker, off-standard flags), or None


@dataclass
class ToolSpec:
    name: str
    title: str
    description: str
    fn: Callable
    read_only: bool
    destructive: bool
    idempotent: bool
    group: str = "core"       # "compare" / "caselab" / "rebuild": maintainer tools, registered only when enabled


TOOLS = {}


def tool(name, title, read_only, destructive=False, idempotent=False, group="core"):
    if len(name) > 64:
        raise ValueError(f"tool name {name!r} is longer than 64 characters")

    def deco(fn):
        TOOLS[name] = ToolSpec(name, title, inspect.cleandoc(fn.__doc__ or title), fn, read_only, destructive,
                               idempotent, group)
        return fn
    return deco


#: The maintainer modules, by tool group, and the function that says whether the group is on. They are optional: the
#: public plugin is the same server without these files, and every group whose module is absent is simply off.
MAINTAINER_MODULES = {"compare": ("tools_compare", lambda m, env: m.settings(env) is not None),
                      "caselab": ("tools_caselab", lambda m, env: m.settings(env) is not None),
                      "rebuild": ("tools_rebuild", lambda m, env: m.enabled(env))}


def maintainer_module(group):
    """The module of a maintainer tool group (imported, so its tools are registered), or None when it is not shipped."""
    name = f"{__package__}.{MAINTAINER_MODULES[group][0]}"
    try:
        return importlib.import_module(name)
    except ModuleNotFoundError as ex:
        if ex.name != name:
            raise
        return None


def load_maintainer_modules():
    """Import every maintainer module that is present. Returns the groups found."""
    return [g for g in MAINTAINER_MODULES if maintainer_module(g) is not None]


def enabled_tools(environ=None):
    """The tools this server offers: every core tool, the compare group only when it is enabled and configured
    (tools_compare.settings), the case lab group only when it is also enabled (tools_caselab.settings), and the
    rebuild group only when its maintainer option is on (tools_rebuild.enabled). A group whose module is not shipped is
    off."""
    on = {"core": True}
    for group, (_, is_on) in MAINTAINER_MODULES.items():
        module = maintainer_module(group)
        on[group] = module is not None and bool(is_on(module, environ))
    return {n: s for n, s in TOOLS.items() if on.get(s.group, False)}


class Ctx:
    """What a tool sees: the pool, its session (opened on first use) and, during a replay, the record being replayed."""

    def __init__(self, pool, session_name, replaying=False):
        self.pool = pool
        self.session_name = session_name
        self.replaying = replaying
        self._session = None

    @property
    def session(self):
        if self._session is None:
            self._session = self.pool.get(self.session_name)
        return self._session

    @property
    def has_session(self):
        return self._session is not None or self.session_name in self.pool.sessions


def dispatch(pool, name, arguments, session_name="default", reasoning=None, replaying=False):
    """Run tool ``name``. Returns (structured, text, is_error, entry_or_None)."""
    spec = TOOLS[name]
    arguments = {k: v for k, v in (arguments or {}).items() if v is not None}
    ctx = Ctx(pool, session_name, replaying)
    outcome = None
    status, message = "ok", None
    try:
        if spec.group != "core" and name not in enabled_tools():
            raise Refused(f"REFUSED: '{name}' is not available through the GridSim MCP server.")
        outcome = spec.fn(ctx, **arguments)
        status = outcome.status
    except Refused as ex:
        status, message = "refused", str(ex)
    except (McpError, ValueError, KeyError, TypeError) as ex:
        status, message = "error", str(ex) if not isinstance(ex, KeyError) else f"not found: {ex}"
    except Exception as ex:  # noqa: BLE001 - every failure is reported, none escapes
        status, message = "error", f"{type(ex).__name__}: {ex}"

    session = ctx._session if ctx._session is not None else pool.sessions.get(session_name)
    identity = None
    try:
        identity = pool.identity
    except Exception:  # noqa: BLE001 - no GridSim configured: the error message says so
        identity = None
    build = None if identity is None else {k: identity[k] for k in ("version", "sha256", "build_status")}

    case_id = case_hash = result_hash = result_id = None
    document = None
    if outcome is not None:
        document = outcome.document
        if outcome.case is not None and session is not None:
            case_id = outcome.case.id
            try:
                case_hash = session.case_hash(outcome.case)
            except McpError:
                case_hash = None
        if document is not None:
            result_hash = record.sha256_obj(document)
            if (outcome.store and session is not None and isinstance(document, dict)
                    and document.get("schema") == "gridsim-cli-result/1"):
                result_id, _ = session.store_result(document)

    tuned = outcome.tuning if outcome is not None and outcome.tuning is not None and outcome.tuning.is_tuned else None
    entry = None
    if session is not None and name not in record.UNRECORDED:
        entry = session.record.append(
            name, _recordable(arguments), reasoning, status,
            exit_code=None if outcome is None else outcome.exit_code,
            case_id=case_id, case_hash=case_hash, result_hash=result_hash, result_id=result_id, build=build,
            message=message, tuned=tuned.marker() if tuned is not None else None,
            off_standard=tuned.off_standard if tuned is not None else None)

    evidence = {
        "tool": name,
        "status": status,
        "session": session_name,
        "build": build,
        "build_tag": None if build is None else f"[{build['build_status']}]",
        "case": case_id,
        "case_hash": case_hash,
        "result": result_id,
        "result_hash": result_hash,
        "record_seq": None if entry is None else entry["seq"],
        "record_hash": None if entry is None else entry["hash"],
        "note": EVIDENCE_NOTE,
    }
    # Cross-check: a result document carries GridSim's own build status. When it disagrees with what GridSim reported at
    # the start of the server (the program changed underneath it, say), this answer is UNSUPPORTED BUILD and says why.
    doc_status = build_status.document_status(document)
    if build is not None and doc_status is not None and doc_status != identity.get("gridsim_reported"):
        evidence["build_tag"] = f"[{build_status.UNSUPPORTED}]"
        evidence["build_note"] = (f"the result document reports {doc_status}, but GridSim reported "
                                  f"{identity.get('gridsim_reported')} when the server started")
    # A tuned run is the user's own: the build tag as reported plus TUNED, the tuned values and any standard left.
    if tuned is not None:
        evidence["build_tag"] = f"{evidence['build_tag'] or '[build unknown]'} TUNED"
        evidence.update(tuned.evidence())
    if status in ("refused", "error"):
        structured = {"evidence": evidence, "message": message}
        text = f"{status.upper()}: {message}" if not message.startswith("REFUSED") else message
        return structured, text, True, entry

    structured = {"document": document, "evidence": evidence}
    structured.update(outcome.extra)
    head = outcome.summary or f"{name}: ok"
    tag = evidence["build_tag"] or ""
    text = (f"{head} {tag}".strip() + "\n" + json.dumps(structured, ensure_ascii=False, separators=(",", ":")))
    return structured, text, False, entry


def _recordable(arguments):
    """The arguments as recorded: everything the tool was given (session and reasoning are kept apart)."""
    return json.loads(json.dumps(arguments, ensure_ascii=False))
