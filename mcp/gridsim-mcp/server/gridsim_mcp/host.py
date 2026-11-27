"""The session pool over ``gridsim rpc --protocol 2``.

Each MCP session holds one resident GridSim host (the vendored ``gridsim`` package's ModelSession: a pipe, no network),
its cases (named c1, c2, ... so no host token is ever shown or hashed), its results (r1, r2, ...) and its study record.

The host is given a fixed, minimal environment: private data settings and OpenTelemetry settings are left out. A licence file
named in the plugin configuration is passed to the host as GRIDSIM_LICENCE_FILE; its content is never read here.
"""

import json
import os
import shutil
import sys
import threading
from pathlib import Path

_VENDOR = Path(__file__).resolve().parents[1] / "vendor"
if str(_VENDOR) not in sys.path:
    sys.path.insert(0, str(_VENDOR))

import gridsim  # noqa: E402  (the vendored copy of GridSim's python/gridsim)

from . import build_status, record  # noqa: E402

SERVER_VERSION = "0.1.0"

#: Environment variables the plugin configuration sets (from userConfig in plugin.json, through .mcp.json).
ENV_CLI = "GRIDSIM_MCP_CLI"
ENV_LICENCE = "GRIDSIM_MCP_LICENCE_FILE"
ENV_WORKSPACE = "GRIDSIM_MCP_WORKSPACE"

#: Kinds of element the object model addresses.
KINDS = ("bus", "generator", "load", "branch", "shunt")

#: The level 2 study verbs (rpc ``study``).
STUDY_VERBS = ("pf", "nminus1", "nminus2", "iec60909", "fault", "acopf-full", "dcopf", "scopf", "stability")


class McpError(Exception):
    """A request the MCP cannot serve (bad argument, unknown case, a GridSim error). Nothing was changed."""


def _unset_config(value):
    """A userConfig value that was not filled in arrives as an empty string or as the unexpanded placeholder."""
    return value is None or not value.strip() or value.strip().startswith("${")


def clean_environment(base=None, licence_file=None):
    """The environment the GridSim host runs in: the caller's, minus private data settings and OpenTelemetry settings."""
    env = {}
    for k, v in (os.environ if base is None else base).items():
        ku = k.upper()
        if ku.startswith(("GDA", "OTEL_")) or "LAKE" in ku or ku.startswith("GRIDSIM_MCP_"):
            continue
        env[k] = v
    env["OTEL_SDK_DISABLED"] = "true"
    if licence_file:
        env["GRIDSIM_LICENCE_FILE"] = str(licence_file)
    return env


class Config:
    """Where GridSim is, where the workspace is, whether a licence is configured."""

    def __init__(self, cli=None, workspace=None, licence_file=None, environ=None):
        environ = os.environ if environ is None else environ
        cli = cli if cli is not None else environ.get(ENV_CLI)
        if _unset_config(cli):
            cli = environ.get("GRIDSIM_CLI") or shutil.which("gridsim")
        if _unset_config(cli):
            raise McpError("GridSim was not found: set the plugin option 'gridsim_cli' to the gridsim program, or put "
                           "gridsim on the PATH. GridSim is installed separately from this plugin.")
        self.command = gridsim.find_cli(cli)
        ws = workspace if workspace is not None else environ.get(ENV_WORKSPACE)
        if _unset_config(ws):
            ws = os.path.join(os.getcwd(), "gridsim-workspace")
        self.workspace = Path(ws).resolve()
        lic = licence_file if licence_file is not None else environ.get(ENV_LICENCE)
        self.licence_file = None if _unset_config(lic) else str(Path(lic))
        self.env = clean_environment(environ, self.licence_file)

    @property
    def licensed(self):
        """Whether a licence file is configured and present (its content is never read)."""
        return bool(self.licence_file) and Path(self.licence_file).is_file()


def scrub(obj, token):
    """Replace the level 2 host token wherever it appears, so nothing shown or hashed carries it."""
    if not token:
        return obj
    if isinstance(obj, str):
        return obj.replace(token, "<host>")
    if isinstance(obj, list):
        return [scrub(v, token) for v in obj]
    if isinstance(obj, dict):
        return {k: scrub(v, token) for k, v in obj.items()}
    return obj


class _Host(gridsim.ModelSession):
    """The vendored ModelSession, started with exactly the cleaned environment (the package itself merges ``env`` over
    the caller's environment, which would let a removed variable through)."""

    def __init__(self, cli, cwd, env):
        self._exact_env = dict(env)
        super().__init__(cli=cli, cwd=cwd, env=env)

    def start(self):
        self.env = dict(self._exact_env)
        super().start()


class CaseState:
    """A case held by a session."""

    def __init__(self, cid, handle, source, load_sha, response):
        self.id = cid
        self.handle = handle
        self.source = source            # what was loaded: a built-in name or a workspace path
        self.load_sha = load_sha
        self.load_response = response
        self.revision = 0


class Session:
    """One MCP session: a GridSim host, its cases, results and record."""

    def __init__(self, name, config, identity):
        self.name = name
        self.config = config
        self.identity = identity
        self.cases = {}
        self.results = {}
        self._case_n = 0
        self._result_n = 0
        self.lock = threading.RLock()
        config.workspace.mkdir(parents=True, exist_ok=True)
        self.journal = config.workspace / ".gridsim-mcp" / "journal" / f"{_safe(name)}.jsonl"
        self.journal.parent.mkdir(parents=True, exist_ok=True)
        env = dict(config.env)
        env["GRIDSIM_JOURNAL"] = str(self.journal)
        self.host = _Host(cli=config.command, cwd=str(config.workspace), env=env)
        self.token = getattr(self.host, "host_token", None)
        self.record = record.Record({
            "server": f"gridsim-mcp {SERVER_VERSION}",
            "build": {k: identity[k] for k in ("version", "sha256", "build_status")},
            "licensed": "yes" if config.licensed else "no",
            "protocol": "gridsim-rpc/2",
        })

    # -- rpc ----------------------------------------------------------------------------------------------------------

    def call(self, op, **fields):
        """One rpc request; the response with the host token scrubbed. GridSim errors become McpError."""
        try:
            response = self.host.call(op, **fields)
        except gridsim.SessionError as ex:
            raise McpError(f"the GridSim host ended: {scrub(str(ex), self.token)}") from ex
        except gridsim.GridSimError as ex:
            detail = scrub(str(ex), self.token)
            last = _last_line(scrub(ex.stderr or "", self.token))
            raise McpError(detail + (f" ({last})" if last and last not in detail else "")) from ex
        return scrub(response, self.token)

    def run_raw(self, verb, args, timeout_s=None):
        """rpc run-verb that never raises for a run that wrote no result: {exit_code, document, stdout, stderr, error}.
        A non-converged solve or a refused import is an answer here, not an error (its printed message is the point)."""
        fields = {"verb": str(verb), "args": [str(a) for a in args]}
        if timeout_s:
            fields["timeout_s"] = float(timeout_s)
        try:
            r = self.host.call("run-verb", **fields)
        except gridsim.SessionError as ex:
            raise McpError(f"the GridSim host ended: {scrub(str(ex), self.token)}") from ex
        except gridsim.GridSimError as ex:
            return {"exit_code": ex.exit_code, "document": None, "stdout": self._neutral(ex.stdout or ""),
                    "stderr": self._neutral(ex.stderr or ""), "error": self._neutral(str(ex))}
        r = scrub(r, self.token)
        res = r["result"]
        return {"exit_code": r["exitCode"], "document": res.get("document"), "stdout": self._neutral(res.get("stdout", "")),
                "stderr": self._neutral(res.get("stderr", "")), "error": None}

    def _neutral(self, text):
        """Printed text with the host token and the workspace's absolute path named neutrally (<workspace>), so what
        is shown and hashed does not depend on where the workspace lies."""
        text = scrub(text, self.token)
        ws = str(self.config.workspace)
        for form in {ws, ws.replace("\\", "/")}:
            text = text.replace(form, "<workspace>")
        return text

    # -- cases--------------------------------------------------------------------------------------------------------

    def case(self, cid):
        c = self.cases.get(str(cid))
        if c is None:
            raise McpError(f"no case '{cid}' in session '{self.name}' (cases: {', '.join(self.cases) or 'none'}); "
                           "load one with case_load or case_new")
        return c

    def load(self, source, host_arg):
        try:
            raw = self.host.call("load", case=str(host_arg))
        except gridsim.GridSimError as ex:
            raise McpError(scrub(str(ex), self.token)) from ex
        self._case_n += 1
        cid = f"c{self._case_n}"
        handle = raw["result"]["handle"]
        resp = scrub(raw, self.token)
        resp["result"]["handle"] = cid
        c = CaseState(cid, handle, source, raw["case_sha256"], resp)
        self.cases[cid] = c
        return c

    def edits(self, c):
        r = self.call("edits", handle=c.handle)["result"]
        c.revision = r["revision"]
        return r

    def case_hash(self, c):
        """The hash of the case as it stands: its load SHA-256 plus its edit log (canonical JSON)."""
        log = self.edits(c)["edits"]
        return record.sha256_obj({"load_sha256": c.load_sha, "edits": log})

    def materialise(self, c, force_v2=False):
        """A file path (relative to the workspace) for the case as it stands, for verbs that read a file: the source
        itself when unedited (unless ``force_v2``), else a JSON v2 copy saved under
        .gridsim-mcp/cases/<case hash>/case.json (one folder per state, so case-diff compares like with like)."""
        if not force_v2 and self.edits(c)["revision"] == 0:
            return c.source
        h = self.case_hash(c)
        rel = f".gridsim-mcp/cases/{h[:16]}/case.json"
        target = self.config.workspace / rel
        if not target.is_file():
            target.parent.mkdir(parents=True, exist_ok=True)
            self.call("save", handle=c.handle, path=rel, format="v2")
        return rel

    def materialise_path(self, host_arg):
        """A case file as JSON v2 under the same naming as ``materialise``: loaded on the host, written as
        .gridsim-mcp/cases/f-<load sha>/case.json, released. So a file and a held case compare like with like."""
        try:
            raw = self.host.call("load", case=str(host_arg))
        except gridsim.GridSimError as ex:
            raise McpError(scrub(str(ex), self.token)) from ex
        handle = raw["result"]["handle"]
        try:
            rel = f".gridsim-mcp/cases/f-{raw['case_sha256'][:16]}/case.json"
            target = self.config.workspace / rel
            if not target.is_file():
                target.parent.mkdir(parents=True, exist_ok=True)
                self.call("save", handle=handle, path=rel, format="v2")
        finally:
            try:
                self.host.call("release", handle=handle)
            except gridsim.GridSimError:
                pass
        return rel

    # -- results ------------------------------------------------------------------------------------------------------

    def store_result(self, document):
        """Keep a GridSim result document as a file (content-addressed) and give it the next result id."""
        text = json.dumps(document, ensure_ascii=False, indent=1)
        digest = record.sha256_obj(document)
        rel = f".gridsim-mcp/results/{digest[:16]}.json"
        path = self.config.workspace / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.is_file():
            path.write_text(text, encoding="utf-8", newline="\n")
        self._result_n += 1
        rid = f"r{self._result_n}"
        self.results[rid] = rel
        return rid, digest

    def result_path(self, rid):
        rel = self.results.get(str(rid))
        if rel is None:
            raise McpError(f"no result '{rid}' in session '{self.name}' (results: {', '.join(self.results) or 'none'})")
        return rel

    def close(self):
        try:
            self.host.close()
        except Exception:  # noqa: BLE001 - closing is best effort
            pass


class Pool:
    """The sessions of this MCP server."""

    def __init__(self, config=None):
        self._config = config
        self.sessions = {}
        self._identity = None
        self.lock = threading.RLock()
        self.closers = []       # called by close_all (the Lab daemon of the compare tools registers its shutdown here)

    @property
    def config(self):
        if self._config is None:
            self._config = Config()
        return self._config

    @property
    def identity(self):
        if self._identity is None:
            self._identity = build_status.identify(self.config.command, env=self.config.env)
        return self._identity

    def open(self, name):
        with self.lock:
            if name in self.sessions:
                return self.sessions[name], False
            s = Session(name, self.config, self.identity)
            self.sessions[name] = s
            return s, True

    def get(self, name, auto=True):
        with self.lock:
            s = self.sessions.get(name)
            if s is None:
                if not auto:
                    raise McpError(f"no session '{name}' (open: {', '.join(self.sessions) or 'none'})")
                s, _ = self.open(name)
            return s

    def close(self, name):
        with self.lock:
            s = self.sessions.pop(name, None)
        if s is not None:
            s.close()
        return s

    def close_all(self):
        for name in list(self.sessions):
            self.close(name)
        while self.closers:
            try:
                self.closers.pop()()
            except Exception:  # noqa: BLE001 - shutting down: every closer runs
                pass


def _safe(name):
    return "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in str(name))[:64] or "session"


def _last_line(text):
    return next((ln.strip() for ln in reversed(text.splitlines()) if ln.strip()), "")
