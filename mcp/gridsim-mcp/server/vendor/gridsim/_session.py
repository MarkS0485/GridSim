"""A resident gridsim session: ``gridsim rpc`` driven over a pipe (protocol ``gridsim-rpc/1``).

``GridSim.run`` starts one process per call. A Session starts ``gridsim rpc`` once and keeps it, so a case is loaded once
and solved many times, and a geometry or a verb is a request, not a process start. Nothing is computed here: the host
runs the same solver, the same Core code and (for ``run_verb``) the same verb as the command line, and the numbers come
back as the same bits. The protocol is documented in manual chapter 19 ("rpc").

Only the Python standard library is needed; numpy is used for ``SolveResult.array`` when it is installed.
"""

import itertools
import json
import subprocess
import threading

from ._cli import GridSimError, Result, _check_result_flags, _floats, canonical, find_cli

#: The protocol label every response must carry.
PROTOCOL = "gridsim-rpc/1"


class SessionError(GridSimError):
    """The ``gridsim rpc`` host ended, could not be started, or broke the protocol. ``exit_code`` is the host's exit code
    when it had ended (None otherwise) and ``stderr`` what it printed there; the responses already returned are intact."""


class SolveResult:
    """One ``solve`` response: ``converged``, ``iterations``, ``max_mismatch_pu``, ``loss_mw``, ``loss_mvar``,
    ``exit_code`` (3 when it did not converge), ``case_sha256`` and the bus columns ``bus``, ``vm_pu``, ``va_deg``,
    ``p_inj_mw``, ``q_inj_mvar`` as lists (``array(name)`` gives a numpy array when numpy is installed)."""

    COLUMNS = ("bus", "vm_pu", "va_deg", "p_inj_mw", "q_inj_mvar")

    def __init__(self, response):
        r = response["result"]
        self.exit_code = response["exitCode"]
        self.case_sha256 = response["case_sha256"]
        self.case = r["case"]
        self.converged = r["converged"]
        self.iterations = r["iterations"]
        self.max_mismatch_pu = _floats(r["max_mismatch_pu"])
        self.loss_mw = _floats(r["loss_mw"])
        self.loss_mvar = _floats(r["loss_mvar"])
        for name in self.COLUMNS:
            setattr(self, name, [_floats(v) for v in r[name]])

    @property
    def ok(self):
        """True when the solve converged (exit code 0)."""
        return self.exit_code == 0

    def array(self, name):
        """The column ``name`` as a numpy array (ImportError when numpy is not installed)."""
        if name not in self.COLUMNS:
            raise KeyError(f"no column '{name}' (columns: {', '.join(self.COLUMNS)})")
        import numpy
        return numpy.asarray(getattr(self, name), dtype=float)

    def __repr__(self):
        return f"SolveResult(case={self.case!r}, converged={self.converged}, iterations={self.iterations}, buses={len(self.bus)})"


class _Held:
    """A case object issued by one session's host: it remembers that session and the host instance (the process the
    session was running when the case was loaded). A session uses it only when both are its own, so a case of another
    session, or of the same session before its host was restarted, is refused before anything is sent: level 1 handles
    are numbered per host (``case-1`` in every host), so the id alone cannot tell them apart."""

    _session = None
    _host = None
    released = False

    def _owned_by(self, session, op):
        """The id, when ``session`` and its current host issued this case; GridSimError (nothing sent, nothing marked)
        otherwise."""
        if self._session is not session:
            raise GridSimError(f"{op}: case {self.handle!r} belongs to another session; use that session (or load the case "
                               "in this one)", 2)
        if self._host is not session._host:
            raise GridSimError(f"{op}: case {self.handle!r} was loaded before this session's host was restarted, and the new "
                               "host does not hold it; load the case again", 2)
        if self.released and op != "release":
            raise GridSimError(f"{op}: case {self.handle!r} has been released", 2)
        return self.handle


class CaseHandle(_Held):
    """A case held by the host: ``handle`` (its id), ``case`` (the model name), ``buses``, ``branches``, ``sha256`` and
    ``identity`` (``files``, ``file`` or ``built-in-name``: what the SHA-256 is of). ``solve()`` re-solves it without
    reloading; ``release()`` lets the host drop it. It belongs to the session that loaded it and to that session's host
    instance: another session, or the same session after its host restarted, refuses it."""

    def __init__(self, session, response):
        r = response["result"]
        self._session = session
        self._host = session._host
        self.handle = r["handle"]
        self.case = r["case"]
        self.buses = r["buses"]
        self.branches = r["branches"]
        self.identity = r["case_identity"]
        self.sha256 = response["case_sha256"]
        self.released = False

    def solve(self):
        """The ``pf`` solve of this case, as a SolveResult."""
        return self._session.solve(self)

    def release(self):
        """Release the case on the host (a second call does nothing)."""
        if not self.released:
            self._session.release(self)

    def __repr__(self):
        return f"CaseHandle({self.handle!r}, case={self.case!r}, buses={self.buses})"


class Session:
    """A running ``gridsim rpc`` host.

    ``Session(cli=None, cwd=None, env=None)``: ``cli`` as for find_cli, ``cwd`` the folder the host (and so relative case
    paths and any verb it runs) starts in, ``env`` extra environment variables. Use it as a context manager; ``close()``
    ends the host. Requests are sequential: one at a time, in order.
    """

    def __init__(self, cli=None, cwd=None, env=None):
        import os
        self.command = find_cli(cli)
        self.cwd = None if cwd is None else str(cwd)
        self.env = None if env is None else {**os.environ, **{str(k): str(v) for k, v in env.items()}}
        self._ids = itertools.count(1)
        self._lock = threading.Lock()
        self._stderr = []
        self._proc = None
        self._host = None
        self.start()

    # -- life cycle -------------------------------------------------------------------------------------------------

    def start(self):
        """Start the host (done by the constructor)."""
        if self._proc is not None:
            return
        cmd = self.command + ["rpc"]
        try:
            self._proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                          cwd=self.cwd, env=self.env)
        except OSError as ex:
            raise SessionError(f"could not start {' '.join(cmd)}: {ex}", command=cmd) from ex
        self._host = object()   # this host instance: a case loaded from it is refused after a restart
        self._drain = threading.Thread(target=self._drain_stderr, args=(self._proc,), daemon=True)
        self._drain.start()
        hello = self.request("hello")
        if hello["result"]["protocol"] != PROTOCOL:
            self.close()
            raise SessionError(f"the host speaks {hello['result']['protocol']}, this package speaks {PROTOCOL}")
        self.tool = hello["result"]["tool"]

    def _drain_stderr(self, proc):
        for raw in iter(proc.stderr.readline, b""):
            self._stderr.append(raw.decode("utf-8", errors="replace"))

    def close(self):
        """End the host: ask it to shut down, then wait (and stop it if it does not end)."""
        proc, self._proc = self._proc, None
        if proc is None:
            return
        try:
            if proc.poll() is None:
                try:
                    proc.stdin.write(b'{"op":"shutdown"}\n')
                    proc.stdin.flush()
                except OSError:
                    pass
            try:
                proc.stdin.close()
            except OSError:
                pass
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        finally:
            for stream in (proc.stdout, proc.stderr):
                try:
                    stream.close()
                except OSError:
                    pass

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
        return False

    # -- the protocol -----------------------------------------------------------------------------------------------

    def request(self, op, **fields):
        """Send one request and return the whole response dict. An ``error`` status raises GridSimError (``exit_code`` is
        the response's exit-code equivalent); a host that ended raises SessionError."""
        with self._lock:
            proc = self._proc
            if proc is None:
                raise SessionError("the session is closed")
            rid = next(self._ids)
            line = json.dumps({"id": rid, "op": op, **fields}, separators=(",", ":"), ensure_ascii=False) + "\n"
            try:
                proc.stdin.write(line.encode("utf-8"))
                proc.stdin.flush()
                raw = proc.stdout.readline()
            except OSError:
                raw = b""
            if not raw:
                code = proc.wait()
                self._proc = None
                self._drain.join(timeout=2)
                for stream in (proc.stdin, proc.stdout, proc.stderr):
                    try:
                        stream.close()
                    except OSError:
                        pass
                tail = "".join(self._stderr[-20:]).strip()
                raise SessionError(f"the gridsim rpc host ended (exit {code}) during '{op}'" + (f": {tail.splitlines()[-1]}" if tail else ""),
                                   exit_code=code, stderr="".join(self._stderr), command=self.command + ["rpc"])
            try:
                response = json.loads(raw.decode("utf-8"))
            except ValueError as ex:
                raise SessionError(f"the host sent a line that is not JSON: {raw[:80]!r}") from ex
        if response.get("protocol") != PROTOCOL or response.get("id") != rid:
            raise SessionError(f"unexpected response (protocol {response.get('protocol')!r}, id {response.get('id')!r}, wanted {rid})")
        if response["status"] != "ok":
            err = response["error"]
            raise GridSimError(f"{op}: {err['message']}", response["exitCode"], err.get("stdout"), err.get("stderr"))
        return response

    # -- operations -------------------------------------------------------------------------------------------------

    def load(self, case):
        """Load ``case`` (a built-in name, a ``.m`` file or a case folder) once; returns a CaseHandle."""
        return CaseHandle(self, self.request("load", case=str(case)))

    def solve(self, handle):
        """Solve a held case (a CaseHandle or its id string); returns a SolveResult. A CaseHandle of another session, or
        of this session before its host restarted, is refused (GridSimError) before anything is sent."""
        return SolveResult(self.request("solve", handle=self._handle_id(handle, "solve")))

    def release(self, handle):
        """Release a held case on the host. A CaseHandle of another session, or of this session before its host
        restarted, is refused (GridSimError): nothing is sent and nothing is marked released."""
        hid = self._handle_id(handle, "release")
        self.request("release", handle=hid)
        if isinstance(handle, _Held):
            handle.released = True

    def _handle_id(self, handle, op):
        """The id to send for ``handle``: a case object is checked against this session and its host first; a plain
        id string is sent as it is (the host refuses an id it never issued)."""
        return handle._owned_by(self, op) if isinstance(handle, _Held) else str(handle)

    def stats(self):
        """The host's counters: requests, loads, solves, verb_runs and handles (the evidence a handle is reused)."""
        return self.request("stats")["result"]

    def run_verb(self, verb, *args, timeout=None):
        """Run ``gridsim <verb> <args...>`` on the host and return its Result, as ``GridSim.run`` does (same verbs, same
        refusals, same document; its ``args`` lack the closing ``--json <file>`` the subprocess path adds). GridSimError
        when the run writes no result."""
        name = canonical(verb)
        argv = [str(a) for a in args]
        _check_result_flags(name, argv)   # the one check GridSim.run makes (--out passes for OWN_OUT_VERBS)
        fields = {"verb": name, "args": argv}
        if timeout is not None:
            fields["timeout_s"] = timeout
        response = self.request("run-verb", **fields)
        result = response["result"]
        if result["document"] is None:
            raise GridSimError(f"gridsim {name} wrote no result (exit {response['exitCode']})", response["exitCode"],
                               result["stdout"], result["stderr"])
        r = Result(result["document"], result["stdout"], result["stderr"])
        if r.verb != name:
            raise GridSimError(f"asked for {name}, the document is for {r.verb}", response["exitCode"], result["stdout"], result["stderr"])
        return r

    def line_constants(self, geometry, per_unit=None):
        """The ``line-constants`` result for a ``gridsim-lineconstants/1`` geometry (a dict), as a Result. ``per_unit`` is
        an optional dict with ``length_km``, ``base_kv`` and ``base_mva``."""
        fields = {"geometry": geometry}
        if per_unit is not None:
            fields["per_unit"] = per_unit
        response = self.request("line-constants", **fields)
        return Result(response["result"]["document"])

    def __repr__(self):
        return f"Session({self.command!r}, {'open' if self._proc is not None else 'closed'})"

