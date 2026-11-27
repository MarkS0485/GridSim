"""The GridSim object model from Python: ``gridsim rpc --protocol 2`` (protocol ``gridsim-rpc/2``).

A ``ModelSession`` starts the resident host at protocol level 2. ``session.open(case)`` loads a case and gives a ``Case``:
its ``buses``, ``generators``, ``loads``, ``branches`` and ``shunts`` are read-only mappings of ``Element`` objects keyed
as the host keys them, attribute reads are ``get`` requests and attribute assignments are ``set`` requests. Studies run on
the case as edited: ``solve()`` gives a ``LoadFlowResult``; ``contingency()``, ``short_circuit()`` and ``opf()`` give the
verb's own ``Result`` (the same class ``GridSim.run`` returns). Nothing is computed here: every number is the host's, read
back in round-trip form, so it is the bits GridSim computed. Manual chapter 19 ("rpc", "Python").

Only the Python standard library is used.
"""

import json

from ._cli import GridSimError, Result, _floats
from ._session import Session, SessionError, _Held

#: The protocol label every level 2 response carries.
PROTOCOL2 = "gridsim-rpc/2"

#: The kinds of element the object model addresses.
KINDS = ("bus", "generator", "load", "branch", "shunt")

#: The branch flow attributes ``get`` reads from the solved case (read only).
FLOW_ATTRIBUTES = ("p_from_mw", "q_from_mvar", "p_to_mw", "q_to_mvar", "loading_pct")

#: The generator attributes ``get`` reads from the solved case (read only): the unit's solved P and Q.
GENERATOR_SOLVED_ATTRIBUTES = ("p_solved_mw", "q_solved_mvar")


class ModelSession(Session):
    """A ``gridsim rpc --protocol 2`` host. Everything a ``Session`` does works here too (``load``, ``solve``,
    ``run_verb``, ``line_constants``, ``stats``); ``open(case)`` adds the object model. A host that does not answer with
    protocol ``gridsim-rpc/2`` is refused (SessionError)."""

    def start(self):
        """Start the host at protocol level 2 (done by the constructor)."""
        import subprocess
        import threading
        if self._proc is not None:
            return
        cmd = self.command + ["rpc", "--protocol", "2"]
        try:
            self._proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                          cwd=self.cwd, env=self.env)
        except OSError as ex:
            raise SessionError(f"could not start {' '.join(cmd)}: {ex}", command=cmd) from ex
        self._host = object()   # this host instance: a case loaded from it is refused after a restart
        self._drain = threading.Thread(target=self._drain_stderr, args=(self._proc,), daemon=True)
        self._drain.start()
        hello = self.call("hello")
        if hello["result"]["protocol"] != PROTOCOL2:
            self.close()
            raise SessionError(f"the host speaks {hello['result']['protocol']}, the object model needs {PROTOCOL2}")
        self.tool = hello["result"]["tool"]
        self.operations = tuple(hello["result"]["operations"])
        #: The host instance's token: every level 2 handle is ``case-<host_token>-N``, so the host itself refuses an id
        #: another host issued.
        self.host_token = hello["result"].get("host")

    def call(self, op, **fields):
        """Send one level 2 request and return the whole response dict (an ``error`` status raises GridSimError; a host
        that ended raises SessionError)."""
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
                tail = "".join(self._stderr[-20:]).strip()
                raise SessionError(f"the gridsim rpc host ended (exit {code}) during '{op}'" + (f": {tail.splitlines()[-1]}" if tail else ""),
                                   exit_code=code, stderr="".join(self._stderr), command=self.command + ["rpc", "--protocol", "2"])
            try:
                response = json.loads(raw.decode("utf-8"))
            except ValueError as ex:
                raise SessionError(f"the host sent a line that is not JSON: {raw[:80]!r}") from ex
        if response.get("protocol") != PROTOCOL2 or response.get("id") != rid:
            raise SessionError(f"unexpected response (protocol {response.get('protocol')!r}, id {response.get('id')!r}, wanted {rid})")
        if response["status"] != "ok":
            err = response["error"]
            raise GridSimError(f"{op}: {err['message']}", response["exitCode"], err.get("stdout"), err.get("stderr"))
        return response

    # Every inherited operation goes through the level 2 request.
    request = call

    def open(self, case):
        """Load ``case`` (a built-in name, a ``.m``, ``.raw`` or ``.json`` file or a case folder) and return its Case."""
        return Case(self, self.call("load", case=str(case)))

    def __repr__(self):
        return f"ModelSession({self.command!r}, {'open' if self._proc is not None else 'closed'})"


class Element:
    """One element of a held case. Reading an attribute asks the host (``get``); assigning one writes it (``set``), and a
    refused write raises GridSimError and changes nothing. ``key`` and ``kind`` say which element it is; ``values()``
    reads every attribute at once; ``set(**values)`` writes several in one revision."""

    __slots__ = ("_case", "kind", "key")

    def __init__(self, case, kind, key):
        object.__setattr__(self, "_case", case)
        object.__setattr__(self, "kind", kind)
        object.__setattr__(self, "key", key)

    def values(self, *names):
        """The attributes named (all of them when none is), as a dict, in the host's order."""
        fields = {"handle": self._case._hid("get"), "kind": self.kind, "key": self.key}
        if names:
            fields["attributes"] = list(names)
        r = self._case.session.call("get", **fields)["result"]["values"]
        return {k: _floats(v) for k, v in r.items()}

    def set(self, **values):
        """Write several attributes in one revision; returns the new revision."""
        return self._case._set(self.kind, self.key, values)

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return self.values(name)[name]

    def __setattr__(self, name, value):
        if name in Element.__slots__:
            raise AttributeError(f"{name} is fixed")
        self._case._set(self.kind, self.key, {name: value})

    def __repr__(self):
        return f"Element({self.kind!r}, {self.key!r})"


class ElementMap:
    """The elements of one kind, read only, keyed as the host keys them, in model order."""

    def __init__(self, case, kind):
        self._case = case
        self.kind = kind
        self._keys = None

    def keys(self):
        if self._keys is None:
            rows = self._case.session.call("elements", handle=self._case._hid("elements"), kind=self.kind)["result"]["rows"]
            self._keys = [row["key"] for row in rows]
        return list(self._keys)

    def rows(self):
        """Every element with every attribute, as a list of dicts (``key`` first), from one ``elements`` request."""
        rows = self._case.session.call("elements", handle=self._case._hid("elements"), kind=self.kind)["result"]["rows"]
        return [{k: _floats(v) for k, v in row.items()} for row in rows]

    def __getitem__(self, key):
        key = str(key)
        if key not in self.keys():
            raise KeyError(f"no {self.kind} '{key}' (keys: {', '.join(self.keys()[:12])}{' ...' if len(self.keys()) > 12 else ''})")
        return Element(self._case, self.kind, key)

    def __contains__(self, key):
        return str(key) in self.keys()

    def __iter__(self):
        return iter(self.keys())

    def __len__(self):
        return len(self.keys())

    def items(self):
        return [(k, Element(self._case, self.kind, k)) for k in self.keys()]

    def __repr__(self):
        return f"ElementMap({self.kind!r}, {len(self)} elements)"


class BusView:
    """One bus of a LoadFlowResult: ``vm`` (pu) and ``va`` (degrees), plus the injections."""

    def __init__(self, vm, va, p_inj_mw, q_inj_mvar):
        self.vm, self.va, self.p_inj_mw, self.q_inj_mvar = vm, va, p_inj_mw, q_inj_mvar

    def __repr__(self):
        return f"BusView(vm={self.vm!r}, va={self.va!r})"


class BranchView:
    """One branch of a LoadFlowResult: ``p_from``, ``q_from``, ``p_to``, ``q_to`` (MW, MVAr) and ``loading_pct``."""

    def __init__(self, values):
        self.p_from = values["p_from_mw"]
        self.q_from = values["q_from_mvar"]
        self.p_to = values["p_to_mw"]
        self.q_to = values["q_to_mvar"]
        self.loading_pct = values["loading_pct"]

    def __repr__(self):
        return f"BranchView(p_from={self.p_from!r}, q_from={self.q_from!r})"


class LoadFlowResult:
    """The pf solve of a case as it stood: ``converged``, ``iterations``, ``loss_mw``, ``bus(id)`` (vm, va) and
    ``branch(key)`` (the flows, read from the host for the same revision). ``exit_code`` is 3 when it did not converge."""

    def __init__(self, case, response):
        r = response["result"]
        self._case = case
        self.revision = case.revision
        self.exit_code = response["exitCode"]
        self.converged = r["converged"]
        self.iterations = r["iterations"]
        self.max_mismatch_pu = _floats(r["max_mismatch_pu"])
        self.loss_mw = _floats(r["loss_mw"])
        self.loss_mvar = _floats(r["loss_mvar"])
        self.buses = {}
        for i, b in enumerate(r["bus"]):
            self.buses[b] = BusView(_floats(r["vm_pu"][i]), _floats(r["va_deg"][i]), _floats(r["p_inj_mw"][i]), _floats(r["q_inj_mvar"][i]))

    def bus(self, bus_id):
        """The solved bus ``bus_id`` (KeyError when it is not in the solved state)."""
        return self.buses[int(bus_id)]

    def branch(self, key):
        """The flows of branch ``key``. The case must not have been edited since this solve."""
        if self._case.revision != self.revision:
            raise GridSimError("the case was edited after this solve; solve again")
        return BranchView(Element(self._case, "branch", str(key)).values(*FLOW_ATTRIBUTES))

    def generator(self, key):
        """The solved output of generator ``key`` as (P in MW, Q in MVAr): the host's ``p_solved_mw`` and ``q_solved_mvar``
        (the bus generation of the solve; a lone unit takes the whole bus). The case must not have been edited since this
        solve; an out-of-service unit raises GridSimError."""
        if self._case.revision != self.revision:
            raise GridSimError("the case was edited after this solve; solve again")
        v = Element(self._case, "generator", str(key)).values(*GENERATOR_SOLVED_ATTRIBUTES)
        return v["p_solved_mw"], v["q_solved_mvar"]

    def __repr__(self):
        return f"LoadFlowResult(converged={self.converged}, iterations={self.iterations}, buses={len(self.buses)})"


class Case(_Held):
    """A case held by a ModelSession: ``handle``, ``name``, ``sha256``, the element mappings (``buses``,
    ``generators``, ``loads``, ``branches``, ``shunts``), ``revision`` and ``edits()``; studies on the case as edited.
    It belongs to the session that opened it and to that session's host instance: every request first checks both, so
    a case of another session, or one opened before the host restarted, is refused before anything is sent."""

    def __init__(self, session, response):
        r = response["result"]
        self.session = session
        self._session = session
        self._host = session._host
        self.handle = r["handle"]
        token = getattr(session, "host_token", None)
        if token is not None and not self.handle.startswith(f"case-{token}-"):
            raise SessionError(f"the host answered with handle {self.handle!r}, which does not carry its token {token!r}")
        self.released = False
        self.name = r["case"]
        self.sha256 = response["case_sha256"]
        self.revision = 0
        self.buses = ElementMap(self, "bus")
        self.generators = ElementMap(self, "generator")
        self.loads = ElementMap(self, "load")
        self.branches = ElementMap(self, "branch")
        self.shunts = ElementMap(self, "shunt")

    def _hid(self, op):
        """This case's id, after checking that this session's current host issued it."""
        return self._owned_by(self.session, op)

    def _set(self, kind, key, values):
        r = self.session.call("set", handle=self._hid("set"), kind=kind, key=str(key), values=dict(values))
        self.revision = r["result"]["revision"]
        return self.revision

    def _forget_keys(self):
        for m in (self.buses, self.generators, self.loads, self.branches, self.shunts):
            m._keys = None

    def add(self, kind, key, **values):
        """Add an element (RC5 WP15): ``kind`` is bus, generator, load, branch or shunt; ``key`` its key (a bus id; a
        generator ``bus:id``; a load or shunt the bus id; a branch ``from-to:circuit``); ``values`` its attributes, as
        numbers (a bus needs ``base_kv``, a branch ``x``). Returns the new revision; a refused add raises GridSimError and
        changes nothing."""
        r = self.session.call("add", handle=self._hid("add"), kind=str(kind), key=str(key), values=dict(values))
        self.revision = r["result"]["revision"]
        self._forget_keys()
        return self.revision

    def delete(self, kind, key, cascade=False):
        """Delete an element (RC5 WP15). A bus with branches, units, a load or a shunt is refused unless ``cascade``, which
        deletes them first; a branch named by a tap changer, flow control or mutual record likewise. Returns the new
        revision; a refused delete raises GridSimError and changes nothing."""
        r = self.session.call("delete", handle=self._hid("delete"), kind=str(kind), key=str(key), cascade=bool(cascade))
        self.revision = r["result"]["revision"]
        self._forget_keys()
        return self.revision

    def edits(self):
        """The edit log: dicts with revision, kind, key, attribute, before and after."""
        r = self.session.call("edits", handle=self._hid("edits"))["result"]
        self.revision = r["revision"]
        return [{k: _floats(v) for k, v in e.items()} for e in r["edits"]]

    def solve(self):
        """The pf solve of the case as it stands (the level 1 ``solve``), as a LoadFlowResult."""
        return LoadFlowResult(self, self.session.call("solve", handle=self._hid("solve")))

    def study(self, verb, *args, timeout=None):
        """Run ``verb`` (pf, nminus1, nminus2, iec60909, fault, acopf-full, dcopf, scopf or stability) on the case as it
        stands. A verb that writes a result document gives its Result; ``stability`` gives the response's result dict."""
        fields = {"handle": self._hid("study"), "verb": str(verb), "args": [str(a) for a in args]}
        if timeout is not None:
            fields["timeout_s"] = timeout
        response = self.session.call("study", **fields)
        result = response["result"]
        if result["document"] is None:
            return result
        return Result(result["document"], result["stdout"], result["stderr"])

    def contingency(self, *args, timeout=None):
        """``nminus1`` on the case as it stands (extra arguments as the verb takes them)."""
        return self.study("nminus1", *args, timeout=timeout)

    def short_circuit(self, *args, timeout=None):
        """``iec60909`` on the case as it stands, e.g. ``short_circuit("--bus", 5)``."""
        return self.study("iec60909", *args, timeout=timeout)

    def opf(self, *args, timeout=None):
        """``acopf-full`` on the case as it stands."""
        return self.study("acopf-full", *args, timeout=timeout)

    def save(self, path, fmt="v2"):
        """Write the case as it stands: ``fmt`` is raw33, raw34, raw35, raw36, rawx (PSS/E RAWX, revision 35) or v2. Never over a file with other content.
        Returns the host's answer (files written, with sizes and SHA-256, and the writer's notes)."""
        return self.session.call("save", handle=self._hid("save"), path=str(path), format=fmt)["result"]

    def rms(self, study_json, *args, timeout=None):
        """Run ``dynamics-study`` on a study file (its RAW and DYR named by SHA-256, so an edited case is ``save``d first)
        and return the parsed result document."""
        fields = {"verb": "dynamics-study", "args": [str(study_json), *[str(a) for a in args]]}
        if timeout is not None:
            fields["timeout_s"] = timeout
        try:
            response = self.session.call("run-verb", **fields)
            stdout = response["result"]["stdout"]
        except GridSimError as ex:
            stdout = ex.stdout
            if not stdout or not stdout.lstrip().startswith("{"):
                raise
        return json.loads(stdout)

    def release(self):
        """Release the case on the host (a second call does nothing)."""
        if not self.released:
            self.session.release(self)

    def __repr__(self):
        return f"Case({self.handle!r}, {self.name!r}, revision {self.revision})"
