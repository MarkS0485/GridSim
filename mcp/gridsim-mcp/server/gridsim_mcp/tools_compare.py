"""Validation tools (Mark-only): GridSim against the rival tools through the Lab Mode harness, GridSim's own
cross-validation sidecars, divergence triage and the breaking-change regression check.

These tools exist only when the plugin option ``enable_compare`` is true and the harness path is set (environment
GRIDSIM_MCP_ENABLE_COMPARE and GRIDSIM_MCP_HARNESS); otherwise they are not registered at all. They need the full rival
runtime set the harness describes (pandapower, MATPOWER under Octave, PowerModels under Julia), which planners and
students will not have.

- compare: one correctness-only Lab cell (one warm attempt per tool, no timing, an element diff between every pair of
  passing tools), through the harness's ``lab serve`` daemon, which keeps the rival workers warm between calls (stdio
  pipes, no network). When the daemon cannot start, the harness command line is run instead.
- crossval_single: one of GridSim's sidecars in tools/crossval on a case. Exit 77 means not run (a dependency is
  missing): nothing was validated.
- divergence_triage: sorts each tool of a compare bundle into GridSim gate fail, rival gate fail, documented model
  difference, genuine disagreement or agreement, with the independent residual and physics evidence of both sides. A
  disagreement is evidence, not proof.
- build_regression: ``gridsim breaking-change validate`` over the session's run journal.

Licence: cases above 1,500 buses run through GridSim's CLI paths, which read the operator licence the plugin
configuration names (GRIDSIM_LICENCE_FILE in the environment). The licence is never read, printed or recorded here.
"""

import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Literal, Optional

from . import docs_index, paths
from .allowlist import Refused
from .host import McpError, _unset_config
from .registry import Outcome, tool

ENV_ENABLE = "GRIDSIM_MCP_ENABLE_COMPARE"
ENV_HARNESS = "GRIDSIM_MCP_HARNESS"
ENV_LAB_PYTHON = "GRIDSIM_MCP_LAB_PYTHON"
ENV_CROSSVAL = "GRIDSIM_MCP_CROSSVAL_DIR"

#: Every answer of these tools says this.
EVIDENCE_NOT_PROOF = ("A disagreement between tools is evidence, not proof: it says where to look, not which side is "
                      "wrong. Each side is first judged against physics by its own gate.")

LabMode = Literal["pf", "pfq", "dcpf", "dcopf", "acopf", "cpf", "cpf0", "sc", "se", "nminus1"]
Sidecar = Literal["crossval", "residual_check", "physics_audit", "crossval_smallsignal", "crossval_classical"]

#: How long one Lab request may take (a first call starts Octave and Julia, about four minutes on a small laptop).
LAB_TIMEOUT_S = 1800


def _truthy(value):
    return not _unset_config(value) and value.strip().lower() in ("1", "true", "yes", "on")


def settings(environ=None):
    """The compare settings, or None when the tools are off: {harness, python, crossval_dir}."""
    env = os.environ if environ is None else environ
    if not _truthy(env.get(ENV_ENABLE)):
        return None
    harness = env.get(ENV_HARNESS)
    if _unset_config(harness):
        return None
    h = Path(harness).expanduser()
    if not ((h / "run.py").is_file() and (h / "lab" / "serve.py").is_file()):
        return None
    python = env.get(ENV_LAB_PYTHON)
    if _unset_config(python) or not Path(python).is_file():
        python = None
        local = h / "lab" / "local.json"     # the harness's own, machine-local runtime file
        try:
            candidate = json.loads(local.read_text(encoding="utf-8")).get("python")
            if candidate and Path(candidate).is_file():
                python = candidate
        except (OSError, ValueError):
            pass
    crossval = env.get(ENV_CROSSVAL)
    return {"harness": h.resolve(), "python": python or sys.executable,
            "crossval_dir": None if _unset_config(crossval) else Path(crossval).resolve()}


def _settings_or_refuse():
    s = settings()
    if s is None:
        raise Refused("REFUSED: the validation tools are not available through the GridSim MCP server.")
    return s


def _rel(ws, p):
    """A path as shown: relative to the workspace (with '/'), never the machine path."""
    try:
        return Path(p).resolve().relative_to(ws).as_posix()
    except ValueError:
        return Path(p).name


def _program(config):
    """The GridSim program file of the configured command (for the harness's GRIDSIM_CLI), or None."""
    cmd = config.command
    if len(cmd) == 1:
        return cmd[0]
    return None


def _neutral(text, config):
    ws = str(config.workspace)
    for form in {ws, ws.replace("\\", "/")}:
        text = text.replace(form, "<workspace>")
    return text


#: Absolute machine paths left after the named ones are replaced: a drive path, a UNC path, or a POSIX path under a
#: system or user root. Each becomes <path>.
_ABS_PATH = re.compile(r"(?<![A-Za-z0-9<])[A-Za-z]:[\\/][^\s\"'<>|,;)]*"
                       r"|\\\\[^\s\"'<>|,;)]+"
                       r"|(?<![\w.<>/])/(?:home|Users|usr|opt|tmp|var|mnt|root|private|Library|nix|snap|srv|media|"
                       r"Applications|Volumes|proc)/[^\s\"'<>|,;)]*")


def _scrub_text(text, names):
    for value, mark in names:
        text = text.replace(value, mark)
    return _ABS_PATH.sub("<path>", text)


def scrub(obj, config, st=None):
    """``obj`` with every machine path replaced by a placeholder (F13): the interpreter (<python>), the GridSim program
    (<gridsim>), the sidecar folder (<crossval>), the harness (<harness>), the workspace (<workspace>) and the home
    folder (<home>) by name, then any other absolute path as <path>. Strings in lists and maps are scrubbed too."""
    names = []
    def add(value, mark):
        if not value:
            return
        v = str(value)
        for form in {v, v.replace("\\", "/"), v.replace("/", "\\")}:
            if len(form) > 3:
                names.append((form, mark))
    st = st or {}
    add(st.get("python"), "<python>")
    for part in (config.command or []):
        if Path(str(part)).is_absolute():
            add(part, "<gridsim>")
    add(st.get("crossval_dir"), "<crossval>")
    add(st.get("harness"), "<harness>")
    add(config.workspace, "<workspace>")
    add(Path.home(), "<home>")
    names.sort(key=lambda n: -len(n[0]))

    def walk(o):
        if isinstance(o, str):
            return _scrub_text(o, names)
        if isinstance(o, list):
            return [walk(x) for x in o]
        if isinstance(o, dict):
            return {k: walk(v) for k, v in o.items()}
        return o
    return walk(obj)


# -- The Lab daemon -----------------------------------------------------------------------------------------------------

class LabDaemon:
    """The harness's ``lab serve`` process: JSON lines over its stdin and stdout."""

    def __init__(self, s, config, root):
        root.mkdir(parents=True, exist_ok=True)
        env = dict(config.env)
        prog = _program(config)
        if prog:
            env["GRIDSIM_CLI"] = prog
        self.log = open(root / "serve.log", "ab")
        self.proc = subprocess.Popen([s["python"], "-I", str(s["harness"] / "run.py"), "--lab", "serve",
                                      "--output-root", str(root)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=self.log, env=env, cwd=str(s["harness"]), text=True, encoding="utf-8",
                                     errors="replace", bufsize=1)
        self.replies = queue.Queue()
        self.n = 0
        self.lock = threading.Lock()
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.proc.stdout:
            line = line.strip()
            if line:
                try:
                    self.replies.put(json.loads(line))
                except ValueError:
                    pass
        self.replies.put(None)

    def alive(self):
        return self.proc.poll() is None

    def request(self, op, timeout=LAB_TIMEOUT_S, **fields):
        with self.lock:
            self.n += 1
            rid = self.n
            self.proc.stdin.write(json.dumps({"id": rid, "op": op, **fields}) + "\n")
            self.proc.stdin.flush()
            deadline = time.monotonic() + timeout
            while True:
                try:
                    reply = self.replies.get(timeout=max(0.1, deadline - time.monotonic()))
                except queue.Empty:
                    self.kill()
                    raise McpError(f"the Lab daemon gave no answer within {timeout} s")
                if reply is None:
                    raise McpError(f"the Lab daemon ended (exit {self.proc.poll()})")
                if reply.get("id") == rid:
                    if not reply.get("ok"):
                        raise McpError(f"Lab: {reply.get('error')}")
                    return reply["result"]

    def kill(self):
        if self.proc.poll() is None:
            self.proc.kill()

    def close(self):
        try:
            if self.alive():
                with self.lock:
                    self.proc.stdin.write(json.dumps({"id": 0, "op": "shutdown"}) + "\n")
                    self.proc.stdin.flush()
                self.proc.wait(timeout=60)
        except (OSError, subprocess.TimeoutExpired, ValueError):
            self.kill()
        finally:
            self.log.close()


def _daemon(ctx, s):
    pool = ctx.pool
    with pool.lock:
        d = getattr(pool, "lab_daemon", None)
        if d is not None and d.alive():
            return d
        root = pool.config.workspace / ".gridsim-mcp" / "compare"
        d = LabDaemon(s, pool.config, root)
        hello = d.request("hello", timeout=120)
        if hello.get("schema") != "gridsim-lab-serve/1":
            d.close()
            raise McpError("the harness's lab serve answered with an unknown schema")
        pool.lab_daemon = d
        pool.closers.append(d.close)
        return d


# -- Cases --------------------------------------------------------------------------------------------------------------

def _lab_case(ctx, case):
    """(kind, value, note) of the case the harness runs: ('name', 'case14.m') for a MATPOWER 8.1 case the harness looks
    up in its case folder, or ('path', <absolute .m path>) for a MATPOWER file in the workspace (a case id is converted
    to MATPOWER first, as explain_solve does)."""
    from .tools_teach import _bare_case_file
    config = ctx.pool.config
    text = str(case)
    if re.fullmatch(r"c\d+", text):
        sess = ctx.session
        c = sess.case(text)
        src, notes = _bare_case_file(sess, c)
        if not src.lower().endswith(".m"):
            return "name", src + ".m", ("the harness runs MATPOWER 8.1's file of this name, not GridSim's built-in "
                                        "copy"), c
        return "path", str((config.workspace / src).resolve()), ("converted to MATPOWER for the rival tools: "
                                                                 + "; ".join(notes) if notes else None), c
    if text.lower().endswith(".m") and ("/" in text or "\\" in text or (config.workspace / text).is_file()):
        p = paths.resolve(config, text)
        if not p.is_file():
            raise McpError(f"case file '{text}' not found")
        return "path", str(p), None, None
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", text):
        raise McpError(f"'{text}' is neither a case id, a MATPOWER case name nor a .m file in the workspace")
    return "name", text if text.endswith(".m") else text + ".m", None, None


def _summary(record, ws, bundle, only=None):
    tools = {}
    for name, e in record["tools"].items():
        if only and name not in only:
            continue
        row = {k: e[k] for k in ("status", "reason", "crossCheck", "modelDifference") if k in e}
        if "warmGate" in e:
            row["gateMetrics"] = e["warmGate"].get("firstMetrics")
        tools[name] = row
    folder = Path(bundle) / record["threadPolicy"] / record["mode"] / Path(record["case"]).stem
    return {"mode": record["mode"], "case": record["case"], "buses": record["buses"],
            "caseSha256": record["caseSha256"], "tools": tools, "elementDiff": record.get("elementDiff"),
            "cell": _rel(ws, folder)}


def _cli_compare(s, config, mode, kind, value, out):
    cmd = [s["python"], "-I", str(s["harness"] / "run.py"), "--lab", "--correctness-only", "--mode", mode,
           "--output", str(out)]
    if kind == "path":
        cmd += ["--case", Path(value).name, "--case-dir", str(Path(value).parent)]
    else:
        cmd += ["--case", value]
    env = dict(config.env)
    prog = _program(config)
    if prog:
        env["GRIDSIM_CLI"] = prog
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env,
                       cwd=str(s["harness"]), timeout=LAB_TIMEOUT_S, stdin=subprocess.DEVNULL)
    manifest = out / "lab-manifest.json"
    if not manifest.is_file():
        raise McpError("the Lab run wrote no bundle: " + _neutral((p.stderr or p.stdout)[-400:], config))
    return json.loads(manifest.read_text(encoding="utf-8"))


@tool("compare", "Compare GridSim with rival tools on one case (Lab Mode)", read_only=False, idempotent=True,
      group="compare")
def compare(ctx, mode: LabMode, case: str, tools: Optional[list[str]] = None):
    """Run one correctness-only Lab Mode cell: GridSim and every rival tool that supports the mode (pandapower,
    lightsim2grid, MATPOWER under Octave, PowerModels under Julia), each judged first by its own physics gate, then the
    cross-check and an element diff between every pair of passing tools (per bus |dVm|, |dVa|; per branch |dP|,
    |dQ|; per bus dPg for dcopf). case: a case id (converted to MATPOWER), a MATPOWER 8.1 case name (case14) or a .m
    file in the workspace. tools: limit the run to these tools (default all). The bundle (manifest, report, each
    tool's first result) is written under .gridsim-mcp/compare. Uses the harness's lab serve daemon, which keeps the
    rival workers warm between calls; the first call starts them (minutes). Statuses: pass, gate-fail, n/a,
    unavailable, each with its reason. A disagreement is evidence, not proof: follow it with divergence_triage."""
    st = _settings_or_refuse()
    config = ctx.pool.config
    kind, value, note, c = _lab_case(ctx, case)
    ws = config.workspace
    started = time.perf_counter()
    via, pool_events = "daemon", None
    try:
        d = _daemon(ctx, st)
        result = d.request("compare", mode=mode, case=value, **({"tools": list(tools)} if tools else {}))
        bundle = Path(result["bundle"])
        manifest = json.loads((bundle / "lab-manifest.json").read_text(encoding="utf-8"))
        pool_events = result.get("workerPool")
    except (OSError, McpError) as ex:
        if isinstance(ex, McpError) and str(ex).startswith("Lab: "):
            raise
        via = f"command line (the daemon was not available: {_neutral(str(ex), config)})"
        root = ws / ".gridsim-mcp" / "compare"
        root.mkdir(parents=True, exist_ok=True)
        k = len(list(root.glob("cli-*"))) + 1
        bundle = root / f"cli-{k:04d}-{mode}-{Path(value).stem}"
        manifest = _cli_compare(st, config, mode, kind, value, bundle)
    record = manifest["cells"][0]["record"]
    doc = _summary(record, ws, bundle, set(tools) if tools else None)
    doc["runType"] = manifest.get("runType")
    doc["note"] = EVIDENCE_NOT_PROOF
    if note:
        doc["caseNote"] = note
    seconds = round(time.perf_counter() - started, 3)
    head = f"compare {mode} {record['case']}: " + ", ".join(
        f"{t}={e.get('status')}" for t, e in doc["tools"].items() if e.get("status") != "n/a")
    return Outcome(document=doc, case=c, store=False, summary=head,
                   extra={"bundle": _rel(ws, bundle), "via": via, "seconds": seconds, "workerPool": pool_events})


# -- crossval sidecars --------------------------------------------------------------------------------------------------

def _crossval_dir(st, config):
    if st["crossval_dir"] is not None:
        d = st["crossval_dir"]
    else:
        docs = docs_index.find_docs_root(config.command)
        d = None if docs is None else docs.parent / "tools" / "crossval"
    if d is None or not (d / "crossval.py").is_file():
        raise McpError("GridSim's cross-validation sidecars (tools/crossval) were not found beside the GridSim "
                       "program; set GRIDSIM_MCP_CROSSVAL_DIR to the folder")
    return d


def _cli_case(ctx, case):
    """The case argument for a GridSim CLI run in the workspace: a built-in name, or a workspace-relative file."""
    from .tools_teach import _bare_case_file
    text = str(case)
    if re.fullmatch(r"c\d+", text):
        src, _ = _bare_case_file(ctx.session, ctx.session.case(text))
        return src, ctx.session.case(text)
    if "/" in text or "\\" in text or "." in text:
        return paths.host_arg(ctx.pool.config, text), None
    return text, None


def _run(cmd, config, env_extra=None, timeout=900):
    env = dict(config.env)
    env.update(env_extra or {})
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env,
                       cwd=str(config.workspace), timeout=timeout, stdin=subprocess.DEVNULL)
    return p.returncode, _neutral(p.stdout, config), _neutral(p.stderr, config)


def _verdict(code):
    return {0: "within tolerance", 1: "mismatch", 3: "setup error", 4: "setup error",
            77: "not run (exit 77: a dependency is missing; nothing was validated)"}.get(code, f"exit {code}")


def _json_or_tail(text):
    try:
        return json.loads(text)
    except ValueError:
        lines = [ln for ln in text.splitlines() if ln.strip()]
        return {"output": lines[-12:]}


def _sidecar(st, config, script, export, extra=()):
    d = _crossval_dir(st, config)
    # Not -I: a sidecar imports its sibling sidecar_common.py from its own folder (GridSim's own scripts).
    code, out, err = _run([st["python"], str(d / script), str(export), *extra], config)
    verdict = _verdict(code)
    if code not in (0, 77) and "Traceback" in err:
        verdict = "setup error (the sidecar raised; see stderr)"
    return {"script": f"tools/crossval/{script}", "exitCode": code, "verdict": verdict,
            "report": _json_or_tail(out), **({"stderr": err.strip().splitlines()[-3:]} if err.strip() else {})}


def _gridsim_crossval(st, config, case_arg, out_rel):
    """`gridsim crossval <case> --out <dir>`: GridSim's tight-tolerance solve export plus the pandapower sidecar."""
    code, out, err = _run(config.command + ["crossval", case_arg, "--out", out_rel], config,
                          {"GRIDSIM_PYTHON": st["python"]})
    export = config.workspace / out_rel / "solve-export.json"
    return code, out, err, export


@tool("crossval_single", "Run one of GridSim's cross-validation sidecars on a case", read_only=False, idempotent=True,
      group="compare")
def crossval_single(ctx, case: str, sidecar: Sidecar = "crossval"):
    """Run one of GridSim's own sidecars (tools/crossval) on a case: crossval (GridSim's power flow solved at the
    tightest tolerance against pandapower, bus by bus, through `gridsim crossval`), residual_check (does GridSim's
    answer satisfy the network equations, from an independently built admittance matrix), physics_audit (the laws
    recomputed in NumPy), crossval_smallsignal (modal eigenvalues against SciPy) or crossval_classical (the classical
    swing against SciPy). Verdicts: within tolerance, mismatch, setup error, or not run (exit 77: a dependency is
    missing, nothing was validated; never a pass). Outputs go to .gridsim-mcp/crossval."""
    st = _settings_or_refuse()
    config = ctx.pool.config
    case_arg, c = _cli_case(ctx, case)
    root = config.workspace / ".gridsim-mcp" / "crossval"
    root.mkdir(parents=True, exist_ok=True)
    k = len([p for p in root.iterdir() if p.is_dir()]) + 1
    out = root / f"{k:04d}-{sidecar}-{Path(str(case_arg)).stem}"
    out_rel = _rel(config.workspace, out)
    doc = {"sidecar": sidecar, "case": str(case_arg)}
    if sidecar in ("crossval", "residual_check", "physics_audit"):
        code, stdout, stderr, export = _gridsim_crossval(st, config, case_arg, out_rel)
        doc["producer"] = {"command": f"gridsim crossval {case_arg} --out {out_rel}", "exitCode": code,
                           "output": [ln for ln in (stdout + stderr).splitlines() if ln.strip()][-12:]}
        if sidecar == "crossval":
            diff = out / "crossval-diff.json"
            doc.update(exitCode=code, verdict={0: "within tolerance", 3: "mismatch or sidecar failure",
                                               77: _verdict(77), 1: "setup error"}.get(code, f"exit {code}"))
            if diff.is_file():
                d = json.loads(diff.read_text(encoding="utf-8"))
                doc["report"] = {k2: d[k2] for k2 in d if k2 not in ("buses", "rows")}
        elif not export.is_file():
            doc.update(exitCode=None, verdict="setup error", reason="GridSim wrote no solve export")
        else:
            res = _sidecar(st, config, "residual_check.py" if sidecar == "residual_check" else "physics_audit.py",
                           export, () if sidecar == "residual_check" else ("--json",))
            doc.update(res)
    else:
        verb, name, script = (("modal", "smallsignal.json", "crossval_smallsignal.py") if sidecar ==
                              "crossval_smallsignal" else ("stability", "classical.json", "crossval_classical.py"))
        out.mkdir(parents=True, exist_ok=True)
        code, stdout, stderr = _run(config.command + [verb, case_arg, "--export", f"{out_rel}/{name}"], config)
        doc["producer"] = {"command": f"gridsim {verb} {case_arg} --export {out_rel}/{name}", "exitCode": code}
        if not (out / name).is_file():
            doc.update(exitCode=None, verdict="setup error",
                       reason=(stderr or stdout).strip().splitlines()[-1:] or ["no export written"])
        else:
            doc.update(_sidecar(st, config, script, out / name))
    doc["note"] = "GridSim's sidecars recompute from GridSim's recorded inputs with independent code."
    doc = scrub(doc, config, st)
    return Outcome(document=doc, case=c, store=False, exit_code=doc.get("exitCode"),
                   summary=scrub(f"crossval_single {sidecar} {case_arg}: {doc.get('verdict')}", config, st),
                   extra={"output": out_rel})


# -- Divergence triage --------------------------------------------------------------------------------------------------

#: Element-diff thresholds above which two passing answers are a disagreement (pu, degrees, MW, MVAr). Two exact
#: solutions of the same equations agree far closer than these (the gates hold residuals to 1e-8 pu). dcopf: the
#: optimal dispatch per bus (dPg, MW); interior-point and simplex solvers agree to about 1e-6 MW on a unique optimum.
DISAGREE = {"pf": {"dVm": 1e-6, "dVa": 1e-5, "dP": 1e-4, "dQ": 1e-4},
            "pfq": {"dVm": 1e-6, "dVa": 1e-5, "dP": 1e-4, "dQ": 1e-4},
            "dcpf": {"dVa": 1e-6, "dP": 1e-4},
            "dcopf": {"dPg": 1e-3},
            "se": {"dVm": 1e-6, "dVa": 1e-5}}

#: Documented model differences: (id, tool, modes, test on (record, entry, case text), text).
KNOWN_DIFFERENCES = (
    ("powermodels-dcpf-taps", "powermodels", ("dcpf",),
     "PowerModels' compute_dc_pf ignores branch tap ratios and phase shifts (harness lab/settings.md); a case with "
     "an off-nominal tap or a phase shift gives other angles by construction"),
    ("matpower-cpf-slack-q", "matpower", ("cpf",),
     "MATPOWER's continuation power flow limits the slack generator's Q and GridSim does not (CPF-INVESTIGATION); "
     "the harness widens MATPOWER's slack Q range to +/-9999 to align them, so check this first when the nose falls "
     "outside GridSim's bracket"),
    ("gridsim-refuses-exdc2", "andes", ("dynamics",),
     "GridSim refuses the EXDC2 exciter that ANDES runs: such a system cannot be matched, the cell is not compared"),
)


def _branch_rows(text):
    """The rows of mpc.branch in a MATPOWER file (floats), or []."""
    m = re.search(r"mpc\.branch\s*=\s*\[(.*?)\];", text, re.S)
    if not m:
        return []
    rows = []
    for line in m.group(1).split(";"):
        line = line.split("%")[0].strip()
        if line:
            try:
                rows.append([float(x) for x in line.split()])
            except ValueError:
                continue
    return rows


def has_taps_or_shifts(case_text):
    """True when an in-service branch has an off-nominal tap or a phase shift (MATPOWER columns TAP, SHIFT, STATUS)."""
    for r in _branch_rows(case_text):
        if len(r) >= 11 and r[10] > 0 and ((r[8] not in (0.0, 1.0)) or r[9] != 0.0):
            return True
    return False


def known_difference(mode, tool, entry, gridsim_entry, case_text):
    """The documented model difference that explains this tool's result, as (id, text), or None."""
    for kid, ktool, modes, text in KNOWN_DIFFERENCES:
        if mode not in modes:
            continue
        if kid == "powermodels-dcpf-taps" and tool == ktool and (
                entry.get("modelDifference") == "documented" or has_taps_or_shifts(case_text or "")):
            return kid, text
        if kid == "matpower-cpf-slack-q" and tool == ktool and not (
                (gridsim_entry.get("crossCheck") or {}).get("passed", True)):
            return kid, text
        if kid == "gridsim-refuses-exdc2" and "EXDC2" in (str(entry.get("reason")) + str(gridsim_entry.get("reason"))):
            return kid, text
    return None


def _pair_max(record, a, b):
    for p in (record.get("elementDiff") or {}).get("pairs", []):
        if {p["a"], p["b"]} == {a, b}:
            return p
    return None


def classify(record, tool, case_text=None):
    """The triage class of one rival tool in a Lab cell record, with the reasons. Classes: 'GridSim gate fail',
    'rival gate fail', 'documented model difference', 'genuine disagreement', 'agreement', 'not compared'."""
    mode = record["mode"]
    g = record["tools"].get("gridsim", {})
    t = record["tools"].get(tool, {})
    gs, ts = g.get("status"), t.get("status")
    known = known_difference(mode, tool, t, g, case_text)
    reasons = []
    if gs == "gate-fail":
        if known and known[0] == "gridsim-refuses-exdc2":
            return "documented model difference", [known[1]], known[0]
        return "GridSim gate fail", [f"GridSim: {g.get('reason')}"], None
    if ts == "gate-fail":
        if known:
            return "documented model difference", [known[1], f"{tool}: {t.get('reason')}"], known[0]
        return "rival gate fail", [f"{tool}: {t.get('reason')}"], None
    if gs != "pass" or ts != "pass":
        if known:
            return "documented model difference", [known[1]], known[0]
        return "not compared", [f"gridsim {gs}: {g.get('reason', '')}".strip(), f"{tool} {ts}: {t.get('reason', '')}"
                                .strip()], None
    for side, e in (("gridsim", g), (tool, t)):
        check = e.get("crossCheck")
        if check and check.get("passed") is False:
            reasons.append(f"cross-check ({side} against {check.get('reference')}): {check.get('reason')}")
    pair = _pair_max(record, "gridsim", tool)
    limits = DISAGREE.get(mode, {})
    if pair and "max" in pair:
        for key, limit in limits.items():
            m = pair["max"].get(key)
            if m and m.get("max") is not None and m["max"] > limit:
                where = f"branch {m['at']} ({m['from']}-{m['to']})" if "from" in m else f"bus {m['at']}"
                reasons.append(f"max |{key}| {m['max']:.3e} at {where} exceeds {limit:g}")
    if reasons:
        if known:
            return "documented model difference", [known[1]] + reasons, known[0]
        return "genuine disagreement", reasons, None
    return "agreement", ["both passed their gates; the cross-check and the element diff agree"], None


def _substituted_export(export, payload):
    """GridSim's solve export with the bus state replaced by another tool's answer (bus [id, vm, va]) and GridSim's own
    physics block removed, so the sidecars audit that answer against the same recorded model."""
    doc = json.loads(json.dumps(export))
    state = {int(r[0]): (float(r[1]), float(r[2])) for r in payload.get("bus") or []}
    for b in doc["result"]["buses"]:
        if int(b["id"]) in state:
            b["vm"], b["vaDeg"] = state[int(b["id"])]
    doc.pop("physics", None)
    return doc


def _evidence(ctx, st, config, cell, record, tool):
    """Independent evidence of both sides: each harness gate, GridSim's physics-check of the case, and the residual
    check and physics audit sidecars on GridSim's tight-tolerance export and on the same export carrying the rival's
    answer (AC power-flow modes)."""
    ws = config.workspace
    g, t = record["tools"].get("gridsim", {}), record["tools"].get(tool, {})
    case_file = cell / "input" / record["case"]
    out = {"gridsim": {"harness_gate": (g.get("warmGate") or {}).get("firstMetrics")},
           tool: {"harness_gate": (t.get("warmGate") or {}).get("firstMetrics")}}
    rel_case = _rel(ws, case_file)
    code, stdout, _ = _run(config.command + ["physics-check", rel_case], config)
    summary = next((ln.strip() for ln in stdout.splitlines() if "VERDICT" in ln.upper() or "gate" in ln.lower()), None)
    out["gridsim"]["physics_check"] = {"command": f"gridsim physics-check {rel_case}", "exitCode": code,
                                       "verdict": {0: "PASS", 1: "FAIL (physically impossible)",
                                                   3: "not converged"}.get(code, f"exit {code}"), "line": summary}
    out[tool]["physics_check"] = ("GridSim's physics-check solves the case itself; for this tool's answer the harness "
                                  "gate above and the physics audit below are the independent checks")
    if record["mode"] not in ("pf", "pfq"):
        note = "the residual check and physics audit sidecars apply to AC power-flow states (pf, pfq)"
        out["gridsim"]["residual_check"] = out[tool]["residual_check"] = note
        return out
    tri = cell / "triage"
    code, stdout, stderr, export = _gridsim_crossval(st, config, rel_case, _rel(ws, tri / "gridsim"))
    if not export.is_file():
        out["gridsim"]["residual_check"] = {"verdict": "setup error", "reason": "GridSim wrote no solve export",
                                            "exitCode": code}
        return out
    out["gridsim"]["crossval_pandapower"] = {"exitCode": code, "verdict": {0: "within tolerance", 77: _verdict(77)}
                                             .get(code, "mismatch or sidecar failure" if code == 3 else f"exit {code}")}
    out["gridsim"]["residual_check"] = _sidecar(st, config, "residual_check.py", export)
    out["gridsim"]["physics_audit"] = _sidecar(st, config, "physics_audit.py", export, ("--json",))
    first = cell / tool / "first-result.json"
    if first.is_file():
        rival = tri / tool / "solve-export.json"
        rival.parent.mkdir(parents=True, exist_ok=True)
        payload = json.loads(first.read_text(encoding="utf-8"))
        rival.write_text(json.dumps(_substituted_export(json.loads(export.read_text(encoding="utf-8")), payload)),
                         encoding="utf-8")
        out[tool]["residual_check"] = _sidecar(st, config, "residual_check.py", rival)
        out[tool]["physics_audit"] = _sidecar(st, config, "physics_audit.py", rival, ("--json",))
        out[tool]["note"] = ("the sidecars ran on GridSim's export of the same case with this tool's bus state in "
                             "place of GridSim's (GridSim's own physics block removed)")
    return out


def _bundle_folder(config, bundle):
    p = paths.resolve(config, bundle)
    if (p / "lab-manifest.json").is_file():
        return p
    raise McpError(f"'{bundle}' is not a Lab bundle (no lab-manifest.json)")


@tool("divergence_triage", "Sort a compare result into gate failure, model difference or disagreement",
      read_only=False, idempotent=True, group="compare")
def divergence_triage(ctx, bundle: str, tool: Optional[str] = None, evidence: bool = True,
                      record_finding: bool = True):
    """Triage a compare bundle (the 'bundle' path compare returned): for each rival tool (or the one named), the class
    GridSim gate fail, rival gate fail, documented model difference (PowerModels dcpf ignores taps; MATPOWER CPF
    limits slack Q; GridSim refuses EXDC2), genuine disagreement (both passed their gates but the cross-check or the
    element diff disagrees) or agreement, with the reasons. evidence=true attaches the independent checks of both
    sides: each harness gate, GridSim's physics-check, and the residual check and physics audit sidecars on GridSim's
    answer and on the rival's (AC power flow). A disagreement is evidence, not proof: it does not say which side is
    wrong, and no core or determinism change follows from it without Mark. record_finding=true appends the result to
    the private findings store .gridsim-mcp/findings/findings.jsonl in the workspace."""
    st = _settings_or_refuse()
    config = ctx.pool.config
    folder = _bundle_folder(config, bundle)
    manifest = json.loads((folder / "lab-manifest.json").read_text(encoding="utf-8"))
    rows = []
    for cell in manifest["cells"]:
        record = cell["record"]
        cell_folder = folder / cell["policy"] / record["mode"] / Path(record["case"]).stem
        case_file = cell_folder / "input" / record["case"]
        case_text = case_file.read_text(encoding="utf-8", errors="replace") if case_file.is_file() else None
        names = [tool] if tool else [t for t in record["tools"] if t != "gridsim"]
        for name in names:
            if name not in record["tools"]:
                raise McpError(f"no tool '{name}' in the bundle (tools: {', '.join(record['tools'])})")
            entry = record["tools"][name]
            if not tool and entry.get("status") == "n/a" and record["tools"].get("gridsim", {}).get("status") != \
                    "gate-fail":
                continue
            cls, reasons, known = classify(record, name, case_text)
            row = {"mode": record["mode"], "case": record["case"], "tool": name, "class": cls, "reasons": reasons,
                   "documented": known, "status": {"gridsim": record["tools"].get("gridsim", {}).get("status"),
                                                   name: entry.get("status")}}
            if evidence and cls != "not compared" and name != "andes":
                row["evidence"] = _evidence(ctx, st, config, cell_folder, record, name)
            rows.append(row)
    doc = {"bundle": _rel(config.workspace, folder), "runType": manifest.get("runType"),
           "gridSimCommit": manifest.get("gridSimCommit"), "triage": rows, "note": EVIDENCE_NOT_PROOF,
           "rules": ["no core or determinism change is proposed without Mark",
                     "a test change needs a mathematical proof that the old expectation was wrong"]}
    finding_file = None
    if record_finding:
        store = config.workspace / ".gridsim-mcp" / "findings"
        store.mkdir(parents=True, exist_ok=True)
        finding_file = store / "findings.jsonl"
        with finding_file.open("a", encoding="utf-8") as f:
            for row in rows:
                f.write(json.dumps({"bundle": doc["bundle"], "gridSimCommit": doc["gridSimCommit"],
                                    **{k: row[k] for k in ("mode", "case", "tool", "class", "reasons", "documented")}},
                                   ensure_ascii=False) + "\n")
    head = "triage: " + ", ".join(f"{r['mode']}/{r['case']}/{r['tool']}={r['class']}" for r in rows)
    return Outcome(document=doc, store=False, summary=head,
                   extra={"findings": None if finding_file is None else _rel(config.workspace, finding_file)})


# -- Breaking-change regression -----------------------------------------------------------------------------------------

@tool("build_regression", "Re-run the session's journal on a build and explain what changed", read_only=False,
      idempotent=True, group="compare")
def build_regression(ctx, program: Optional[str] = None, since: Optional[str] = None, verb: Optional[str] = None):
    """Run `gridsim breaking-change validate --json` over this session's run journal: every recorded run is re-run
    on the build under test and compared bit for bit, and each changed field is checked against the documented
    breaking changes (EXPLAINED, EXCEEDS-CLAIM, AMBIGUOUS, UNEXPLAINED, UNBOUNDED). program: another GridSim program
    to test (a new or tuned build; default the configured one, whose own runs are SKIPPED-SAME-BUILD). since and verb
    narrow the entries. The report is written to .gridsim-mcp/regression."""
    _settings_or_refuse()
    s = ctx.session
    config = ctx.pool.config
    journal = s.journal
    if not journal.is_file() or not journal.read_text(encoding="utf-8", errors="replace").strip():
        raise McpError("the session's run journal is empty: run some studies first")
    if program:
        prog = Path(program)
        if str(program).startswith(("\\\\", "//")) or not prog.is_file():
            raise McpError(f"program '{program}' is not a local file")
        cmd = [str(prog.resolve())]
    else:
        cmd = list(config.command)
    root = config.workspace / ".gridsim-mcp" / "regression"
    root.mkdir(parents=True, exist_ok=True)
    k = len(list(root.glob("*.json"))) + 1
    out = root / f"{k:04d}-validate.json"
    args = ["breaking-change", "validate", "--journal", _rel(config.workspace, journal), "--json",
            _rel(config.workspace, out)]
    if since:
        args += ["--since", str(since)]
    if verb:
        args += ["--verb", str(verb)]
    code, stdout, stderr = _run(cmd + args, config, timeout=3600)
    doc = json.loads(out.read_text(encoding="utf-8")) if out.is_file() else {
        "exitCode": code, "output": [ln for ln in (stdout + stderr).splitlines() if ln.strip()][-12:]}
    line = next((ln.strip() for ln in stdout.splitlines() if ln.startswith("breaking-change validate")), None)
    return Outcome(document=doc, store=False, exit_code=code,
                   summary=line or f"breaking-change validate: exit {code}",
                   extra={"report": _rel(config.workspace, out), "program": "configured" if not program else "given"})
