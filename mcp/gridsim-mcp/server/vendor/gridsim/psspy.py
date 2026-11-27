"""A psspy-style layer over the GridSim object model (``gridsim rpc --protocol 2``).

Import it explicitly: ``from gridsim import psspy``. It keeps one module-level ``ModelSession`` and one case, and maps
the psspy calls listed in manual chapter 19 ("psspy compatibility") onto GridSim's own operations. Only the positional
forms listed there are accepted; any other psspy name raises NotImplementedError naming the function, and a call GridSim
cannot honour exactly returns ierr 9 rather than doing something else silently.

Return conventions follow psspy: ``ierr``, or ``(ierr, value)``. ierr 0 means success. For most calls the non-zero
ierr values are GridSim's own and are NOT PSS/E's codes: 1 element not found, 2 bad argument or string, 3 not solved (or
stale: the case was edited after the last solve), 9 not supported. The deletes and arrays of RC5 WP15 (purgbrn, purgmac,
purgload, purgshunt, abuscplx, amachcplx, abrncplx) and inimac return PSS/E's own codes from the API manual ([APIX]),
each listed in its docstring, for every condition the manual names. A request PSS/E would answer and GridSim cannot
(a subsystem, an unmapped string or flag, an unsolved case) returns GridSim's "not supported" 9, or 11 in abrncplx,
where PSS/E's 9 means an invalid STRING. ``_i``, ``_f`` and ``_s`` are this module's "no change" defaults
(``getdefaultint/real/char()``).

Sources of the argument layouts. Each mapped call names its source in a comment:
- [API34] the PSS/E 34 Application Program Interface manual (March 2015), as publicly posted; the section is given.
- [FORUM] public example calls on the psspy.org help forum (question numbers given).
- [GH] public open-source repositories on GitHub that call the function with the same argument order (owner/repository
  and file given), and [APIX] the API manual's own text as excerpted in the PowerMCP repository
  (Power-Agent/PowerMCP, PSSE/psspy_command_json/<name>.json). The calls that add and delete elements (RC5 WP15) name
  two such sources each.
A layout neither confirms is refused with ierr 9 (or NotImplementedError), never guessed. Every mapping is verified
against GridSim's own verbs, not against PSS/E.

Only the Python standard library is used.
"""

import json
import math
import os
import re
import shutil
import tempfile

from ._cli import GridSimError
from ._model import ModelSession

#: "No change" defaults (GridSim's values; a psspy array slot holding one of these is left as it is).
_i = -100000000
_f = -1.0e20
_s = "\x00"

#: The psspy names this module maps.
SUPPORTED = (
    "psseinit", "read", "readrawx", "writerawversion", "rawd_2", "fnsl", "solved", "busdat", "busint", "gendat", "macdat",
    "loddt2", "brnmsc", "brnflo", "abusint", "abusreal", "abrnint", "abrnreal", "abrnchar", "amachint", "amachreal",
    "amachchar", "machine_chng_2", "load_chng_5", "branch_chng_3", "bus_chng_4", "dyre_new", "strt_2", "strt", "run",
    "dist_bus_fault", "dist_clear_fault", "dist_branch_trip", "dist_machine_trip", "voltage_channel",
    "machine_array_channel", "progress_output", "report_output", "alert_output", "getdefaultint", "getdefaultreal",
    "getdefaultchar", "aareaint", "aareareal",
    # RC5 WP15: adding and deleting elements, complex arrays, iteration.
    "bus_data_4", "load_data_6", "machine_data_4", "plant_data_4", "branch_data_3", "two_winding_data_6", "shunt_data",
    "purgbrn", "purgmac", "purgload", "purgshunt", "abuscplx", "amachcplx", "abrncplx", "inibus", "nxtbus", "inimac",
    "nxtmac", "inibrn", "nxtbrn",
    # RC5 WP25: Gauss-Seidel (GridSim's pf --engine gs).
    "solv",
)

#: The psspy names GridSim refuses, with the reason (NotImplementedError carries it).
NOT_SUPPORTED = {
    "case": "the .sav binary is proprietary; read a RAW or RAWX file",
    "save": "the .sav binary is proprietary; use writerawversion",
    "fdns": "PSS/E's fixed-slope decoupled Newton is not confirmed to match GridSim's fast-decoupled load flow, so neither it nor Newton (fnsl) is substituted silently; for the fast-decoupled solution use Case.study('pf', '--engine', 'fdxb')",
    "fncl": "PSS/E's FNCL method is not confirmed by a public source, so neither the fast-decoupled engine nor Newton (fnsl) is substituted silently; use Case.study('pf', '--engine', 'fdxb') for the fast-decoupled solution",
    "nsol": "PSS/E's decoupled Newton is not confirmed to match GridSim's fast-decoupled load flow, so neither it nor Newton (fnsl) is substituted silently; for the fast-decoupled solution use Case.study('pf', '--engine', 'fdxb')",
    "mslv": "GridSim has no modified Gauss-Seidel engine; Gauss-Seidel (solv) and Newton (fnsl) are never substituted silently",
    "accc": ".sub/.mon/.con files are not read; use Case.contingency()",
    "accc_2": ".sub/.mon/.con files are not read; use Case.contingency()",
    "accc_with_dsp_3": ".sub/.mon/.con files are not read; use Case.contingency()",
    "dfax": ".sub/.mon/.con files are not read; use Case.contingency()",
    "dfax_2": ".sub/.mon/.con files are not read; use Case.contingency()",
    "ascc": "use Case.short_circuit(); the ANSI multipliers await the standard text",
    "ascc_3": "use Case.short_circuit(); the ANSI multipliers await the standard text",
    "scmu": "use Case.short_circuit(); the ANSI multipliers await the standard text",
    "iecs_4": "the iecs option-array layout is not public; use Case.short_circuit()",
    "opf": "the PSS/E OPF data file is not read; use Case.opf()",
    "dist_scmu_fault": "the argument layout is unverified; use Case.rms() with a sequenceFault or lineFault event",
    "dist_spcb_fault": "the argument layout is unverified; use Case.rms() with a sequenceFault or lineFault event",
    "dist_branch_fault": "the argument layout is unverified; use Case.rms() with a sequenceFault or lineFault event",
    "load_data_7": "no public source confirms its argument layout; use load_data_6",
    "extr": "extr(sid, all, status) removes the buses of a subsystem, and subsystems are not mapped (see bsys); use Case.delete('bus', key, cascade=True)",
    "bsys": "subsystem selection is not mapped: the object model has no areas, zones or owners (aareaint and aareareal read the whole case's areas from the RAW file)",
}

_PREFIX_REASONS = (("accc", NOT_SUPPORTED["accc"]), ("dfax", NOT_SUPPORTED["dfax"]), ("ascc", NOT_SUPPORTED["ascc"]),
                   ("scmu", NOT_SUPPORTED["scmu"]), ("opf", NOT_SUPPORTED["opf"]))

#: PSS/E's default PQBRAK (pu): below it PSS/E reduces a constant-MVA load ([API34] 7.90 'ACT'), which GridSim does not.
_PQBRAK = 0.7


class _State:
    session = None
    case = None
    raw_path = None
    info = None            # _CaseInfo of the file read (None when it could not be read)
    solved = None          # None: no solve since the last edit; else the LoadFlowResult
    dyr_path = None
    started = False
    time = 0.0
    events = []            # dynamics-study events, in the order applied
    load_ids = {}          # RC5 WP15: {bus: id} of each load added through load_data_6 (one load per bus)
    shunt_ids = {}         # RC5 WP15: {bus: id} of each shunt added through shunt_data (one shunt per bus)
    plant_vs = {}          # RC5 WP15: {bus: VS} given by plant_data_4, for units added later
    iter_bus = None        # RC5 WP15: the inibus/nxtbus sequence, {bus: [...]} of inimac and inibrn
    iter_mac = {}
    iter_brn = {}
    faults = []            # bus of each applied bus fault, for dist_clear_fault
    channels = []
    last_run = None
    last_error = None      # the reason of the last ierr 9 from a dynamic run
    step_seconds = 0.005


S = _State()


def _refuse(name):
    def f(*args, **kwargs):
        raise NotImplementedError(f"psspy.{name} is not supported by GridSim: {NOT_SUPPORTED[name]}")
    f.__name__ = name
    return f


for _name in NOT_SUPPORTED:
    globals()[_name] = _refuse(_name)
del _name


def __getattr__(name):
    if name.startswith("__"):
        raise AttributeError(name)
    for prefix, reason in _PREFIX_REASONS:
        if name.startswith(prefix):
            raise NotImplementedError(f"psspy.{name} is not supported by GridSim: {reason}")
    raise NotImplementedError(f"psspy.{name} is not mapped by GridSim's psspy layer (manual chapter 19 lists the calls that are)")


# ---- what the file read says (system base, the loads of each bus, dropped buses) ------------------------------------

class _CaseInfo:
    """Read from the RAW or RAWX file itself, because the object model aggregates loads per bus and does not carry the
    system base: ``sbase`` (MVA), ``loads`` {bus: [(id, constant_power_only), ...]} (every load record, in or out of
    service) and ``isolated`` (the number of IDE 4 buses, which GridSim's reader drops)."""

    def __init__(self, sbase, loads, isolated):
        self.sbase, self.loads, self.isolated = sbase, loads, isolated


def _fields(line):
    """The fields of one RAW record: comma separated (blank separated when there is no comma), quotes kept together,
    anything after a '/' outside quotes dropped. Quotes are stripped from a quoted field."""
    out, cur, quote = [], [], None
    for ch in line:
        if quote:
            if ch == quote:
                quote = None
            else:
                cur.append(ch)
            continue
        if ch in "'\"":
            quote = ch
        elif ch == "/":
            break
        elif ch == ",":
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    out.append("".join(cur).strip())
    if len(out) == 1:
        out = out[0].split()
    return out


def _num(text):
    return float(text) if text not in ("", None) else 0.0


def _raw_info(path):
    """_CaseInfo of a RAW file (revisions 30 and 33 to 36), or None when its layout is not the one expected."""
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = [ln.rstrip("\r\n") for ln in f if not ln.lstrip().startswith("@!")]
    head = _fields(lines[0])
    sbase = _num(head[1]) if len(head) > 1 else 100.0
    rev = int(_num(head[2])) if len(head) > 2 and head[2] else 30
    at = 3

    def section():
        nonlocal at
        rows = []
        while at < len(lines):
            ln = lines[at]
            at += 1
            first = _fields(ln)[0] if ln.strip() else ""
            if first == "0" or first.upper() == "Q":
                return rows
            if ln.strip():
                rows.append(_fields(ln))
        return rows

    if rev >= 35:
        section()   # system-wide data
    buses = section()
    isolated = sum(1 for r in buses if len(r) > 3 and int(_num(r[3])) == 4)
    loads = {}
    for r in section():
        bus, ident = int(_num(r[0])), r[1].strip()
        pq_only = all(_num(r[k]) == 0.0 for k in range(7, 11) if k < len(r))
        loads.setdefault(bus, []).append((ident, pq_only))
    return _CaseInfo(sbase, loads, isolated)


def _rawx_info(path):
    """_CaseInfo of a RAWX file, or None."""
    with open(path, encoding="utf-8") as f:
        net = json.load(f)["network"]

    def table(name):
        t = net.get(name)
        if t is None:
            return []
        rows = t["data"]
        if rows and not isinstance(rows[0], list):
            rows = [rows]
        return [dict(zip(t["fields"], r)) for r in rows]

    case = table("caseid")
    sbase = float(case[0].get("sbase", 100.0)) if case else 100.0
    isolated = sum(1 for b in table("bus") if int(b.get("ide", 1)) == 4)
    loads = {}
    for ld in table("load"):
        pq_only = all(float(ld.get(k, 0.0) or 0.0) == 0.0 for k in ("ip", "iq", "yp", "yq"))
        loads.setdefault(int(ld["ibus"]), []).append((str(ld.get("loadid", "1")).strip(), pq_only))
    return _CaseInfo(sbase, loads, isolated)


def _case_info(path):
    try:
        low = path.lower()
        if low.endswith(".rawx"):
            return _rawx_info(path)
        if low.endswith(".raw"):
            return _raw_info(path)
    except (OSError, ValueError, KeyError, IndexError, TypeError):
        return None
    return None


# ---- defaults and session ---------------------------------------------------------------------------------------------

def getdefaultint():
    return _i


def getdefaultreal():
    return _f


def getdefaultchar():
    return _s


def progress_output(*args, **kwargs):
    """Accepted and ignored: GridSim prints nothing here. [API34] 1.203: progress_output(islct, filarg, options)."""
    return 0


report_output = progress_output
alert_output = progress_output


def psseinit(buses=None, cli=None, cwd=None):
    """Start the module's ModelSession (the bus count is ignored: GridSim sizes itself). ``cli`` and ``cwd`` are passed
    to ModelSession. [API34] 12.13: psseinit(buses)."""
    if S.session is None:
        S.session = ModelSession(cli=cli, cwd=cwd)
    return 0


def _reset():
    S.case = None
    S.raw_path = None
    S.info = None
    S.solved = None
    S.dyr_path = None
    S.started = False
    S.time = 0.0
    S.events = []
    S.faults = []
    S.channels = []
    S.last_run = None
    S.last_error = None
    S.load_ids = {}
    S.shunt_ids = {}
    S.plant_vs = {}
    S.iter_bus = None
    S.iter_mac = {}
    S.iter_brn = {}


def _open(path):
    if S.session is None:
        psseinit()
    try:
        case = S.session.open(path)
    except GridSimError:
        return 2
    _reset()
    S.case = case
    S.raw_path = os.path.abspath(str(path))
    S.info = _case_info(S.raw_path)
    return 0


def read(numnam, ifile):
    """Read a RAW file (revisions 30 and 33 to 36). ``numnam`` is ignored (GridSim reads the file's own numbering).
    [FORUM] q151: psspy.read(0, rawfile)."""
    return _open(ifile)


def readrawx(*args, **kwargs):
    """ierr 9: no public source confirms readrawx's argument layout, so it is not guessed. Open a RAWX file with
    ``ModelSession.open`` instead."""
    return 9


def _need_case():
    if S.case is None:
        raise GridSimError("no case: call psspy.read first")
    return S.case


def writerawversion(vernum, out=1, ofile=None):
    """Write the case as it stands as RAW 33, 34, 35 or 36 to ``ofile`` (``out`` 0, to a file). Any other version, or
    ``out`` 1 (the report window): ierr 9. Never over a different file. Revision 36 is the revision 35 records under an
    '@!' heading line per record group (GridSim's RAW 36 reader places fields by those headings). A RAWX file is written
    with ``Case.save(path, "rawx")``. [API34] 1.334: writerawversion(vernum, out, ofile)."""
    case = _need_case()
    v = str(vernum).strip().split(".", 1)[0]
    if v not in ("33", "34", "35", "36") or out != 0 or ofile is None:
        return 9
    try:
        case.save(ofile, "raw" + v)
    except GridSimError:
        return 2
    return 0


def rawd_2(sid=0, all=1, *positional, out=1, ofile=None):
    """Write the whole case (sid 0 or -1, all 1) as RAW 33 to ``ofile`` (``out=0``). Only the keyword form is accepted:
    the positional layout of the status and option slots is not confirmed by a public source, so any positional argument
    after ``all`` is ierr 9. [FORUM] q3777: psspy.rawd_2(0, 1, out=0, ofile=name)."""
    if positional or sid not in (0, -1) or all != 1 or out != 0 or ofile is None:
        return 9
    return writerawversion("33", 0, ofile)


# ---- load flow --------------------------------------------------------------------------------------------------------

def fnsl(options=None):
    """Newton-Raphson with reactive limits on (GridSim's pf). Any option other than the defaults: ierr 9. ierr 0 means
    the solve ran, as in PSS/E, where non-convergence is not an fnsl error; ``solved()`` reports convergence.
    [API34] 1.87: fnsl(options), OPTIONS(8)."""
    case = _need_case()
    if options is not None and any(o != _i and o != 0 for o in options):
        return 9
    S.solved = case.solve()
    return 0


def solv(options=None):
    """Gauss-Seidel (GridSim's ``pf --engine gs``: MATPOWER gausspf order, acceleration 1, reactive limits on, up to 10000
    iterations, Newton's convergence test). The options are read as in fnsl: any option other than the defaults is
    ierr 9. ierr 0 means the solve ran; ``solved()`` reports convergence. Bus results are the Gauss-Seidel solution;
    branch flows and machine outputs (brnflo, macdat and the arrays) are read from the host, which takes them from its
    own Newton solve of the same case, the same solution to the tolerance. After a solve that did not converge they are
    not served (ierr 3, as for any unsolved case). [API34]: solv(options)."""
    case = _need_case()
    if options is not None and any(o != _i and o != 0 for o in options):
        return 9
    S.solved = _EngineSolve(case, case.study("pf", "--engine", "gs"))
    return 0


class _EngineSolve:
    """A pf study run with ``--engine`` read into the shape psspy reads from a LoadFlowResult."""

    def __init__(self, case, result):
        from ._model import BusView
        self._case = case
        self.revision = case.revision
        self.buses = {}
        if isinstance(result, dict):   # no result document: the solve did not converge
            self.exit_code = result.get("exitCode", 3)
            self.converged = False
            self.iterations = None
            return
        self.exit_code = result.exit_code
        self.converged = bool(result.summary.get("converged"))
        self.iterations = result.summary.get("iterations")
        for row in result.tables.get("buses", []):
            self.buses[int(row["bus"])] = BusView(row["vm_pu"], row["va_deg"], row["p_inj_mw"], row["q_inj_mvar"])

    def bus(self, bus_id):
        return self.buses[int(bus_id)]

    def branch(self, key):
        self._need_converged()
        from ._model import LoadFlowResult
        return LoadFlowResult.branch(self, key)

    def generator(self, key):
        self._need_converged()
        from ._model import LoadFlowResult
        return LoadFlowResult.generator(self, key)

    def _need_converged(self):
        # Branch flows and machine outputs come from the host's own Newton solve, which is the engine's solution only
        # when the engine converged; after a failed solve they would describe a state the engine never reached.
        if not self.converged:
            raise GridSimError("the solve did not converge; there are no branch flows or machine outputs to read")


def _edited():
    S.solved = None


def solved():
    """0 converged, 1 not converged, 3 no solve since the last edit. [API34] 7.142: ival = solved()."""
    if S.solved is None or S.case is None or S.solved.revision != S.case.revision:
        return 3
    return 0 if S.solved.converged else 1


def _solution():
    if solved() != 0:
        return None
    return S.solved


def _bus(ibus):
    case = _need_case()
    key = str(int(ibus))
    return case.buses[key] if key in case.buses else None


def busdat(ibus, string):
    """PU, KV, ANGLE (radians), ANGLED, BASE. [API34] 7.17: ierr, rval = busdat(ibus, string)."""
    b = _bus(ibus)
    if b is None:
        return 1, None
    s = str(string).upper()
    if s == "BASE":
        return 0, b.base_kv
    if s not in ("PU", "KV", "ANGLE", "ANGLED"):
        return 2, None
    sol = _solution()
    if sol is None:
        return 3, None
    v = sol.buses.get(int(ibus))
    if v is None:
        return 1, None
    if s == "PU":
        return 0, v.vm
    if s == "KV":
        return 0, v.vm * b.base_kv
    if s == "ANGLED":
        return 0, v.va
    return 0, math.radians(v.va)


def busint(ibus, string):
    """TYPE only. [API34] 7.21: ierr, ival = busint(ibus, string)."""
    b = _bus(ibus)
    if b is None:
        return 1, None
    if str(string).upper() != "TYPE":
        return 2, None
    return 0, int(b.type)


def _load_of_bus(ibus):
    case = _need_case()
    key = str(int(ibus))
    if key in case.loads:
        v = case.loads[key].values("pd", "qd", "status")
        return (v["pd"], v["qd"]) if v["status"] == 1 else (0.0, 0.0)
    return 0.0, 0.0


def gendat(ibus):
    """The in-service generation at the bus in the solved state, P + jQ (MW, MVAr): the bus's net injection plus its
    demand (exact for a constant-power load, the only load a RAW file carries). [API34] 7.58: ierr, cmpval =
    gendat(ibus)."""
    keys = _generator_keys_at(ibus)
    if not keys:
        return 1, None
    sol = _solution()
    if sol is None:
        return 3, None
    v = sol.buses.get(int(ibus))
    if v is None:
        return 1, None
    pd, qd = _load_of_bus(ibus)
    return 0, complex(v.p_inj_mw + pd, v.q_inj_mvar + qd)


def _machine_bus(key):
    """The bus of a generator key (``<bus>:<id>`` or ``G<bus>[#n]``), or None."""
    if ":" in key:
        return int(key.split(":", 1)[0])
    if key.startswith("G") and key[1:].split("#", 1)[0].isdigit():
        return int(key[1:].split("#", 1)[0])
    return None


def _generator_keys_at(ibus):
    return [r["key"] for r in _machine_rows() if _machine_bus(r["key"]) == int(ibus) and r["status"] == 1]


def _gen_key(ibus, ident):
    case = _need_case()
    ident = str(ident).strip()
    for key in (f"{int(ibus)}:{ident}", f"G{int(ibus)}" if ident == "1" else f"G{int(ibus)}#{ident}"):
        if key in case.generators:
            return key
    return None


def _solved_unit(key, rows=None):
    """(ierr, (P, Q)) of one machine in the solved state. ierr 3 when not solved; ierr 9 when the unit is out of service
    or shares its bus with another in-service unit (PSS/E's sharing of a plant's output between its machines is not
    GridSim's, so only a lone unit is answered)."""
    sol = _solution()
    if sol is None:
        return 3, None
    rows = rows if rows is not None else _machine_rows()
    row = next(r for r in rows if r["key"] == key)
    bus = _machine_bus(key)
    if row["status"] != 1 or bus is None or sum(1 for r in rows if r["status"] == 1 and _machine_bus(r["key"]) == bus) != 1:
        return 9, None
    return 0, sol.generator(key)


def macdat(ibus, ident, string):
    """P, Q, MVA: the machine's output in the solved state (ierr 3 when not solved; ierr 9 for a unit that shares its bus
    with another in-service unit). PMAX, PMIN, QMAX, QMIN, MBASE: the unit's data as the case holds them.
    [API34] 7.93: ierr, rval = macdat(ibus, id, string); 'P'/'Q' are the machine loading in MW/Mvar."""
    key = _gen_key(ibus, ident)
    if key is None:
        return 1, None
    s = str(string).upper()
    if s in ("P", "Q", "MVA"):
        ierr, pq = _solved_unit(key)
        if ierr:
            return ierr, None
        return 0, pq[0] if s == "P" else pq[1] if s == "Q" else abs(complex(pq[0], pq[1]))
    attr = {"PMAX": "pmax", "PMIN": "pmin", "QMAX": "qmax", "QMIN": "qmin", "MBASE": "mbase"}.get(s)
    if attr is None:
        return 2, None
    return 0, getattr(S.case.generators[key], attr)


def _one_load(ibus, ident):
    """ierr of addressing load ``ident`` at ``ibus`` as the object model's bus demand: 0 when the file read holds exactly
    one load record at the bus, with that id; 1 when it holds none with that id; 9 when it holds several (the object
    model aggregates per bus, so one of them cannot be addressed) or the file could not be read."""
    if S.info is None:
        return 9
    recs = S.info.loads.get(int(ibus), [])
    if not any(i == str(ident).strip() for i, _ in recs):
        return 1
    return 0 if len(recs) == 1 else 9


def loddt2(ibus, ident, string1, string2):
    """The load's complex power. string1 'MVA', 'IL', 'YL' or 'TOTAL'; string2 'NOM' or 'ACT' (MW and MVAr).
    Answered only for a bus that holds exactly one load, with this id, and only for a constant-power load (no IP, IQ,
    YP, YQ in the file): its 'MVA' and 'TOTAL' are the bus demand, 'IL' and 'YL' are 0. 'ACT' needs a solve and a bus
    voltage at or above 0.7 pu (PSS/E's default PQBRAK, below which PSS/E reduces a constant-MVA load and GridSim does
    not). 'O_NOM', 'O_ACT', 'LDDGN', 'YNEG', 'YZERO': ierr 9. [API34] 7.90: ierr, cmpval = loddt2(ibus, id, string1,
    string2)."""
    s1, s2 = str(string1).upper(), str(string2).upper()
    if s1 not in ("MVA", "IL", "YL", "TOTAL", "LDDGN", "YNEG", "YZERO") or s2 not in ("NOM", "ACT", "O_NOM", "O_ACT"):
        return 2, None
    if s1 in ("LDDGN", "YNEG", "YZERO") or s2 in ("O_NOM", "O_ACT"):
        return 9, None
    case = _need_case()
    if str(int(ibus)) not in case.loads:
        return 1, None
    ierr = _one_load(ibus, ident)
    if ierr:
        return ierr, None
    if not S.info.loads[int(ibus)][0][1]:
        return 9, None
    if s2 == "ACT":
        sol = _solution()
        if sol is None:
            return 3, None
        if sol.bus(int(ibus)).vm < _PQBRAK:
            return 9, None
    if s1 in ("IL", "YL"):
        return 0, complex(0.0, 0.0)
    pd, qd = _load_of_bus(ibus)
    return 0, complex(pd, qd)


def _branch_key(ibus, jbus, ckt):
    """(key, reversed) of the branch ibus-jbus circuit ckt, or (None, False)."""
    case = _need_case()
    i, j, c = int(ibus), int(jbus), str(ckt).strip()
    candidates = [(f"{i}-{j}:{c}", False), (f"{j}-{i}:{c}", True)]
    n = int(c) if c.isdigit() else None
    if n == 1:
        candidates += [(f"{i}-{j}", False), (f"{j}-{i}", True)]
    elif n is not None:
        candidates += [(f"{i}-{j}#{n}", False), (f"{j}-{i}#{n}", True)]
    for key, rev in candidates:
        if key in case.branches:
            return key, rev
    return None, False


def _flows(ibus, jbus, ckt):
    key, rev = _branch_key(ibus, jbus, ckt)
    if key is None:
        return 1, None
    sol = _solution()
    if sol is None:
        return 3, None
    f = sol.branch(key)
    p, q = (f.p_to, f.q_to) if rev else (f.p_from, f.q_from)
    return 0, (p, q)


def brnmsc(ibus, jbus, ckt, string):
    """P, Q, MVA at the ibus end of the solved case. The percent loadings (PCTRTA and the rest) need the end, the rating
    set and the current or MVA basis PSS/E uses: ierr 9. [API34] 7.13: ierr, rval = brnmsc(ibus, jbus, ickt, string)."""
    s = str(string).upper()
    if s.startswith("PCT"):
        return 9, None
    if s not in ("P", "Q", "MVA"):
        return 2, None
    ierr, v = _flows(ibus, jbus, ckt)
    if ierr:
        return ierr, None
    p, q = v
    return 0, p if s == "P" else q if s == "Q" else abs(complex(p, q))


def brnflo(ibus, jbus, ckt):
    """The flow at the ibus end, P + jQ. [API34] 7.11: ierr, cmpval = brnflo(ibus, jbus, ickt)."""
    ierr, v = _flows(ibus, jbus, ckt)
    return (ierr, None) if ierr else (0, complex(v[0], v[1]))


# ---- subsystem arrays (sid negative, the whole case, only) -----------------------------------------------------------
# GridSim's RAW reader drops IDE 4 buses and what is connected to them, so a flag that asks for "all" elements is
# answered only when the file had none.

def _all_present():
    return S.info is not None and S.info.isolated == 0


def _strings(string):
    return [string] if isinstance(string, str) else list(string)


def _bus_rows():
    return _need_case().buses.rows()


def abusint(sid=-1, flag=1, string="NUMBER"):
    """NUMBER, TYPE. flag 1 (in-service buses: every bus GridSim holds) or 2 (all buses). [API34] 8.3.2:
    ierr, iarray = abusint(sid, flag, string)."""
    if sid >= 0 or flag not in (1, 2) or (flag == 2 and not _all_present()):
        return 9, None
    rows = _bus_rows()
    out = []
    for s in _strings(string):
        s = s.upper()
        if s == "NUMBER":
            out.append([int(r["key"]) for r in rows])
        elif s == "TYPE":
            out.append([int(r["type"]) for r in rows])
        else:
            return 2, None
    return 0, out


def abusreal(sid=-1, flag=1, string="PU"):
    """PU, KV, ANGLE, ANGLED, BASE, with abusint's flags. [API34] 8.3.3: ierr, rarray = abusreal(sid, flag, string)."""
    if sid >= 0 or flag not in (1, 2) or (flag == 2 and not _all_present()):
        return 9, None
    out = []
    for s in _strings(string):
        col = []
        for r in _bus_rows():
            ierr, v = busdat(int(r["key"]), s)
            if ierr:
                return ierr, None
            col.append(v)
        out.append(col)
    return 0, out


def _ends(key):
    """(from, to, circuit) of a branch key."""
    ends, _, rest = key.partition(":")
    ends = ends.split("#", 1)[0]
    parts = ends.split("-")
    ckt = rest.split(":", 1)[0] if rest else (key.split("#", 1)[1] if "#" in key else "1")
    try:
        return int(parts[0]), int(parts[1]), ckt
    except (ValueError, IndexError):
        return None, None, ckt


def _branch_rows(flag, entry):
    """The rows for flag 3 (in-service lines and two-winding transformers) or 4 (all of them), entry 1 (each branch
    once), or None. GridSim's object model does not tell a line from a two-winding transformer, so flags 1, 2, 5 and 6
    are not answered; flags 3 and 4 leave out three-winding windings, so a case that has any is not answered either."""
    if flag not in (3, 4) or entry != 1 or (flag == 4 and not _all_present()):
        return None
    rows = _need_case().branches.rows()
    if any(r["key"].partition(":")[0].count("-") >= 2 for r in rows):
        return None
    return [r for r in rows if flag == 4 or r["status"] == 1]


def abrnint(sid=-1, owner=1, ties=1, flag=1, entry=1, string="FROMNUMBER"):
    """FROMNUMBER, TONUMBER, STATUS; flag 3 or 4, entry 1 (see _branch_rows). [API34] 8.12.2:
    ierr, iarray = abrnint(sid, owner, ties, flag, entry, string)."""
    rows = _branch_rows(flag, entry) if sid < 0 else None
    if rows is None:
        return 9, None
    out = []
    for s in _strings(string):
        s = s.upper()
        if s == "STATUS":
            out.append([int(r["status"]) for r in rows])
        elif s in ("FROMNUMBER", "TONUMBER"):
            out.append([_ends(r["key"])[0 if s == "FROMNUMBER" else 1] for r in rows])
        else:
            return 2, None
    return 0, out


def abrnreal(sid=-1, owner=1, ties=1, flag=1, entry=1, string="P"):
    """P, Q, MVA (from end, solved), MXPCTMVARATA (the larger end MVA as a percentage of RATE A, solved), RATEA;
    flag 3 or 4, entry 1. The current-based percentages (PCTRATEA and the rest): ierr 9. [API34] 8.12.3:
    ierr, rarray = abrnreal(sid, owner, ties, flag, entry, string)."""
    rows = _branch_rows(flag, entry) if sid < 0 else None
    if rows is None:
        return 9, None
    out = []
    for s in _strings(string):
        s = s.upper()
        if s == "RATEA":
            out.append([r["rate_a"] for r in rows])
            continue
        if s.startswith("PCT") or s.startswith("MAXPCT") or (s.startswith("MXPCT") and s != "MXPCTMVARATA"):
            return 9, None
        if s not in ("P", "Q", "MVA", "MXPCTMVARATA"):
            return 2, None
        sol = _solution()
        if sol is None:
            return 3, None
        col = []
        for r in rows:
            f = sol.branch(r["key"])
            col.append(f.p_from if s == "P" else f.q_from if s == "Q" else abs(complex(f.p_from, f.q_from)) if s == "MVA" else f.loading_pct)
        out.append(col)
    return 0, out


def abrnchar(sid=-1, owner=1, ties=1, flag=1, entry=1, string="ID"):
    """ID (the circuit); flag 3 or 4, entry 1. [API34] 8.12.5: ierr, carray = abrnchar(sid, owner, ties, flag, entry,
    string)."""
    rows = _branch_rows(flag, entry) if sid < 0 else None
    if rows is None:
        return 9, None
    out = []
    for s in _strings(string):
        if s.upper() != "ID":
            return 2, None
        out.append([_ends(r["key"])[2] for r in rows])
    return 0, out


def _machine_rows():
    return _need_case().generators.rows()


def _machines(flag):
    """The machine rows of flag 1 (in service, at a type 2 or 3 bus), 2 (all, at a type 2 or 3 bus), 3 (in service) or
    4 (all), or None."""
    if flag not in (1, 2, 3, 4) or (flag in (2, 4) and not _all_present()):
        return None
    rows = _machine_rows()
    types = {int(r["key"]): int(r["type"]) for r in _bus_rows()}
    return [r for r in rows if (flag in (2, 4) or r["status"] == 1)
            and (flag in (3, 4) or types.get(_machine_bus(r["key"])) in (2, 3))]


def amachint(sid=-1, flag=1, string="NUMBER"):
    """NUMBER (the bus of each machine), STATUS. [API34] 8.5.2: ierr, iarray = amachint(sid, flag, string)."""
    rows = _machines(flag) if sid < 0 else None
    if rows is None:
        return 9, None
    out = []
    for s in _strings(string):
        s = s.upper()
        if s == "NUMBER":
            out.append([_machine_bus(r["key"]) for r in rows])
        elif s == "STATUS":
            out.append([int(r["status"]) for r in rows])
        else:
            return 2, None
    return 0, out


def amachreal(sid=-1, flag=1, string="PGEN"):
    """PGEN, QGEN (each machine's output in the solved state, as macdat 'P' and 'Q'), MBASE, PMAX, PMIN, QMAX, QMIN.
    [API34] 8.5.3: ierr, rarray = amachreal(sid, flag, string)."""
    rows = _machines(flag) if sid < 0 else None
    if rows is None:
        return 9, None
    attr = {"MBASE": "mbase", "PMAX": "pmax", "PMIN": "pmin", "QMAX": "qmax", "QMIN": "qmin"}
    out = []
    all_rows = _machine_rows()
    for s in _strings(string):
        s = s.upper()
        if s in ("PGEN", "QGEN"):
            col = []
            for r in rows:
                ierr, pq = _solved_unit(r["key"], all_rows)
                if ierr:
                    return ierr, None
                col.append(pq[0] if s == "PGEN" else pq[1])
            out.append(col)
            continue
        a = attr.get(s)
        if a is None:
            return 2, None
        out.append([r[a] for r in rows])
    return 0, out


def amachchar(sid=-1, flag=1, string="ID"):
    """ID (the machine id; '1', '2', ... for a case that carries none). [API34] 8.5.5: ierr, carray = amachchar(sid,
    flag, string)."""
    rows = _machines(flag) if sid < 0 else None
    if rows is None:
        return 9, None
    out = []
    for s in _strings(string):
        if s.upper() != "ID":
            return 2, None
        col = []
        for r in rows:
            k = r["key"]
            col.append(k.split(":", 1)[1] if ":" in k else (k.split("#", 1)[1] if "#" in k else "1"))
        out.append(col)
    return 0, out


# ---- area arrays (sid negative, the whole case, only) -----------------------------------------------------------------
# Read from the RAW file itself, because the object model carries no areas: each bus's area, the AREA records (ISW, PDES,
# PTOL) and each branch's metered end (MET, revision 33 and later, or a negative J; NMETR 1 on a two-winding transformer;
# otherwise bus I). Section order: bus, load, [fixed shunt,] generator, branch, [system switching device,] transformer,
# area, as GridSim's RAW reader reads them.

class _AreaInfo:
    def __init__(self, bus_area, areas, metered, three_winding):
        self.bus_area, self.areas, self.metered, self.three_winding = bus_area, areas, metered, three_winding


def _raw_areas(path):
    """_AreaInfo of a RAW file, or None (a RAWX file, or a layout that is not the one expected)."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            lines = [ln.rstrip("\r\n") for ln in f if not ln.lstrip().startswith("@!")]
        head = _fields(lines[0])
        rev = int(_num(head[2])) if len(head) > 2 and head[2] else 30
        at = 3

        def section():
            nonlocal at
            rows = []
            while at < len(lines):
                ln = lines[at]
                at += 1
                first = _fields(ln)[0] if ln.strip() else ""
                if first == "0" or first.upper() == "Q":
                    return rows
                if ln.strip():
                    rows.append(_fields(ln))
            return rows

        if rev >= 35:
            section()   # system-wide data
        area_at = 6 if rev == 30 else 4
        bus_area = {}
        for r in section():
            if int(_num(r[3])) != 4:
                bus_area[int(_num(r[0]))] = int(_num(r[area_at])) if len(r) > area_at and r[area_at] else 1
        section()   # loads
        if rev >= 33:
            section()   # fixed shunts
        section()   # generators
        metered = {}
        met_at = None if rev == 30 else (14 if rev == 33 else 24)
        for r in section():
            i, j, ckt = int(_num(r[0])), int(_num(r[1])), (r[2].strip() or "1")
            at_j = j < 0 or (met_at is not None and len(r) > met_at and r[met_at] and int(_num(r[met_at])) == 2)
            metered[(i, abs(j), ckt)] = abs(j) if at_j else i
        if rev >= 34:
            section()   # system switching devices (metered at bus I)
        rows = section()
        three = False
        k = 0
        while k < len(rows):
            r = rows[k]
            third = int(_num(r[2])) if len(r) > 2 and r[2] else 0
            if third != 0:
                three = True
                k += 5
                continue
            i, j, ckt = int(_num(r[0])), abs(int(_num(r[1]))), (r[3].strip() or "1")
            metered[(i, j, ckt)] = j if len(r) > 9 and r[9] and int(_num(r[9])) == 1 else i
            k += 4
        areas = {}
        for r in section():
            areas[int(_num(r[0]))] = (int(_num(r[1])) if len(r) > 1 and r[1] else 0,
                                      _num(r[2]) if len(r) > 2 else 0.0,
                                      _num(r[3]) if len(r) > 3 and r[3] else 10.0)
        return _AreaInfo(bus_area, areas, metered, three)
    except (OSError, ValueError, KeyError, IndexError, TypeError):
        return None


def _area_list(sid, flag):
    """(info, the area numbers) for flag 1, or None. Flag 1 is the only flag a public example uses ([FORUM] q5541); GridSim
    reads it as the areas with a bus in the case, in ascending number. No public source gives the flag meanings, so any
    other flag is ierr 9."""
    if sid >= 0 or flag != 1 or not S.raw_path or not S.raw_path.lower().endswith(".raw"):
        return None
    info = _raw_areas(S.raw_path)
    if info is None:
        return None
    with_buses = set(info.bus_area.values())
    nums = sorted(a for a in info.areas if a in with_buses)
    return info, nums


def _interchange(info, area):
    """The area's net export (MW) in the solved case, tie lines only, at each tie's metered end; None when a case with
    three-winding transformers makes the branch keys ambiguous."""
    if info.three_winding:
        return None
    sol = _solution()
    total = 0.0
    for r in _need_case().branches.rows():
        if r["status"] != 1:
            continue
        f, t, ckt = _ends(r["key"])
        if f is None:
            return None
        fa, ta = info.bus_area.get(f), info.bus_area.get(t)
        if (fa == area) == (ta == area):
            continue
        m = info.metered.get((f, t, ckt), f)
        flow = sol.branch(r["key"])
        p = flow.p_from if m == f else flow.p_to
        total += p if info.bus_area.get(m) == area else -p
    return total


#: Area strings PSS/E may have but no public source confirms: refused with ierr 9, never guessed.
_AREA_UNCONFIRMED = ("SWING", "PDES", "PTOL")


def aareaint(sid=-1, flag=1, string="NUMBER"):
    """NUMBER, the area numbers in ascending order, with flag 1 (see _area_list). A RAW case only (the areas are read
    from the file). [FORUM] q5541 gives the (sid, flag, string) layout of the area arrays: ierr, iarray =
    aareaint(sid, flag, string). 'SWING' (ISW): ierr 9, as no public source confirms the string."""
    got = _area_list(sid, flag)
    if got is None:
        return 9, None
    info, nums = got
    out = []
    for s in _strings(string):
        s = s.upper()
        if s == "NUMBER":
            out.append(list(nums))
        elif s in _AREA_UNCONFIRMED:
            return 9, None
        else:
            return 2, None
    return 0, out


def aareareal(sid=-1, flag=1, string="PINT"):
    """PINT, the solved net interchange of each area (MW, positive leaving the area, tie lines only, measured at each
    tie's metered end, as GridSim's pf area interchange counts it), in aareaint's order; flag 1 only. A case with
    three-winding transformers is not answered. [FORUM] q5541 asks for the area interchange with ierr, rarray =
    aareareal(sid, 1, 'PINT'). 'PDES' and 'PTOL': ierr 9, as no public source confirms the strings."""
    got = _area_list(sid, flag)
    if got is None:
        return 9, None
    info, nums = got
    out = []
    for s in _strings(string):
        s = s.upper()
        if s in _AREA_UNCONFIRMED:
            return 9, None
        elif s == "PINT":
            if _solution() is None:
                return 3, None
            col = [_interchange(info, a) for a in nums]
            if any(v is None for v in col):
                return 9, None
            out.append(col)
        else:
            return 2, None
    return 0, out


# ---- changes ----------------------------------------------------------------------------------------------------------

def _given(x, default):
    return x is not None and x != default


def _apply(element, values):
    if not values:
        return 0
    try:
        element.set(**values)
    except GridSimError:
        return 2
    _edited()
    return 0


def machine_chng_2(ibus, ident, intgar=None, realar=None):
    """intgar[0] status; realar[0..6] PG, QG, QT, QB, PT, PB, MBASE. Any other slot given: ierr 9.
    [API34] 2.28 and 2.30 (MACHINE_DATA_2): machine_chng_2(i, id, intgar, realar), INTGAR(6), REALAR(17)."""
    key = _gen_key(ibus, ident)
    if key is None:
        return 1
    intgar = list(intgar or [])
    realar = list(realar or [])
    if any(_given(x, _i) for x in intgar[1:]) or any(_given(x, _f) for x in realar[7:]):
        return 9
    values = {}
    if intgar and _given(intgar[0], _i):
        values["status"] = intgar[0]
    for k, attr in enumerate(("pg", "qg", "qmax", "qmin", "pmax", "pmin", "mbase")):
        if k < len(realar) and _given(realar[k], _f):
            values[attr] = realar[k]
    return _apply(S.case.generators[key], values)


def load_chng_5(ibus, ident, intgar=None, realar=None, lodtyp=None):
    """intgar[0] status; realar[0..1] PL, QL. Answered only for a bus that holds exactly one load, with this id, that is
    constant power (see loddt2): ierr 9 otherwise. Any other slot given: ierr 9. ``lodtyp`` (a later release's load type)
    is accepted so a five-argument call does not fail; no public source confirms its meaning, so a value other than
    blank is ierr 9. [API34] 2.22 and 2.26 (LOAD_DATA_5): load_chng_5(i, id, intgar, realar), INTGAR(7) STATUS, AREA,
    ZONE, OWNER, SCALE, INTRPT, DGENM; REALAR(8) PL, QL, IP, IQ, YP, YQ, DGENP, DGENQ."""
    if lodtyp is not None and lodtyp != _s and str(lodtyp).strip() != "":
        return 9
    case = _need_case()
    key = str(int(ibus))
    if key not in case.loads:
        return 1
    ierr = _one_load(ibus, ident)
    if ierr:
        return ierr
    intgar = list(intgar or [])
    realar = list(realar or [])
    if any(_given(x, _i) for x in intgar[1:]) or any(_given(x, _f) for x in realar[2:]) or not S.info.loads[int(ibus)][0][1]:
        return 9
    values = {}
    if intgar and _given(intgar[0], _i):
        values["status"] = intgar[0]
    for k, attr in enumerate(("pd", "qd")):
        if k < len(realar) and _given(realar[k], _f):
            values[attr] = realar[k]
    return _apply(case.loads[key], values)


def branch_chng_3(ibus, jbus, ckt, intgar=None, realari=None, ratings=None, namear=None):
    """intgar[0] status; realari[0..2] R, X, B; ratings[0] RATE1 (GridSim's rate A). Any other slot given: ierr 9.
    PSS/E refuses a two-winding transformer here (its ierr 3); GridSim's object model does not tell one from a line,
    so this call edits either. [API34] 2.3 and 2.4.x (BRANCH_DATA_3): branch_chng_3(i, j, ckt, intgar, realar, ratings,
    namear), REALAR(12) R, X, B, GI, BI, GJ, BJ, LEN, F1-F4; RATINGS(12) RATE1-RATE12."""
    key, _ = _branch_key(ibus, jbus, ckt)
    if key is None:
        return 1
    intgar = list(intgar or [])
    realari = list(realari or [])
    ratings = list(ratings or [])
    if (any(_given(x, _i) for x in intgar[1:]) or any(_given(x, _f) for x in realari[3:]) or any(_given(x, _f) for x in ratings[1:])
            or _given(namear, _s) and namear != ""):
        return 9
    values = {}
    if intgar and _given(intgar[0], _i):
        values["status"] = intgar[0]
    for k, attr in enumerate(("r", "x", "b")):
        if k < len(realari) and _given(realari[k], _f):
            values[attr] = realari[k]
    if ratings and _given(ratings[0], _f):
        values["rate_a"] = ratings[0]
    return _apply(S.case.branches[key], values)


def bus_chng_4(ibus, inode=0, intgar=None, realar=None, name=None):
    """intgar[0] type; realar[1] VM, realar[2] VA (degrees); realar[0] BASKV is not written. Any other slot given:
    ierr 9. The arrays are those of the bus data API ([API34] 2.6 and 2.9, BUS_CHNG_3 / BUS_DATA_3: INTGAR(4) IDE,
    AREA, ZONE, OWNER; REALAR(7) BASKV, VM, VA, NMAXV, NMINV, EMAXV, EMINV); the node argument after the bus is placed as
    in [FORUM] q9182 (changing a bus type): psspy.bus_chng_4(ibus, 0, [1])."""
    b = _bus(ibus)
    if b is None:
        return 1
    intgar = list(intgar or [])
    realar = list(realar or [])
    if (inode not in (0, None, _i) or any(_given(x, _i) for x in intgar[1:]) or (realar and _given(realar[0], _f))
            or any(_given(x, _f) for x in realar[3:]) or (name is not None and name != _s and name != "")):
        return 9
    values = {}
    if intgar and _given(intgar[0], _i):
        values["type"] = intgar[0]
    if len(realar) > 1 and _given(realar[1], _f):
        values["vm"] = realar[1]
    if len(realar) > 2 and _given(realar[2], _f):
        values["va_deg"] = realar[2]
    return _apply(b, values)


# ---- adding and deleting elements (RC5 WP15) ----------------------------------------------------------------------------
# Each call adds the element when it does not exist and changes it when it does, as PSS/E's *_data calls do. GridSim's object
# model holds one load and one shunt per bus, has no areas, zones or owners and no plant record, so: a second load or
# shunt id at a bus is ierr 9; an area, zone or owner other than 1 (or a second owner other than 0) is ierr 9; a slot the
# model cannot hold is ierr 9 when it is given. A refused add or delete (GridSim's own rules: Case.add, Case.delete) is
# ierr 9 too, and changes nothing.

def _list(x, n, default):
    out = list(x or [])
    return out + [default] * (n - len(out))


def _is(x, default, *allowed):
    """True when slot ``x`` is the no-change default or one of ``allowed``."""
    return x is None or x == default or x in allowed


def _try(fn, *args, **kwargs):
    try:
        fn(*args, **kwargs)
    except GridSimError:
        return 9
    _edited()
    return 0


def _blank(text):
    return text is None or text == _s or str(text).strip() == ""


def bus_data_4(ibus, inode=0, intgar=None, realar=None, name=None):
    """Add bus ``ibus`` or change it. intgar = [IDE, AREA, ZONE, OWNER]; realar = [BASKV, VM, VA (degrees), NVHI, NVLO,
    EVHI, EVLO]. A new bus needs BASKV. AREA, ZONE and OWNER other than 1, the emergency limits EVHI and EVLO, a node,
    a name, or a BASKV that differs from an existing bus's: ierr 9. NVHI and NVLO are the bus's own voltage limits.
    [GH] jaimiosyncrasy/transmission_planning_with_PSSE modify_circuit.py: bus_data_4(num, 0, [type, 1, 1, 1],
    [0.0, 1.0, 0.0, 1.1, 0.9, 1.1, 0.9], ""), with the comment "bus_data_4(ibus, inode, intgar, realar, name)";
    [GH] jbarberia/psse34parser a_python.py writes intgar [IDE, AREA, ZONE, OWNER] and realar [BASKV, VM, VA, NVHI, NVLO,
    EVHI, EVLO]; [GH] TimThacker/perc-studio psse_worker.py the same order."""
    case = _need_case()
    ig = _list(intgar, 4, _i)
    ra = _list(realar, 7, _f)
    if (inode not in (0, None, _i) or not all(_is(x, _i, 1) for x in ig[1:4]) or len(ig) > 4 or len(ra) > 7
            or not all(_is(x, _f) for x in ra[5:7]) or not _blank(name)):
        return 9
    key = str(int(ibus))
    values = {}
    if _given(ig[0], _i):
        values["type"] = ig[0]
    for k, attr in ((1, "vm"), (2, "va_deg"), (3, "vmax"), (4, "vmin")):
        if _given(ra[k], _f):
            values[attr] = ra[k]
    if key in case.buses:
        if _given(ra[0], _f) and ra[0] != case.buses[key].base_kv:
            return 9
        return _apply(case.buses[key], values) if values else 0
    if not _given(ra[0], _f):
        return 2
    values["base_kv"] = ra[0]
    return _try(case.add, "bus", key, **values)


def load_data_6(ibus, ident="1", intgar=None, realar=None, lodtyp=None):
    """Add the load ``ident`` at ``ibus`` or change it. intgar = [STATUS, AREA, ZONE, OWNER, SCALE, INTRPT, DGENM];
    realar = [PL, QL, IP, IQ, YP, YQ, DGENP, DGENQ]. Only constant power (PL, QL) is held: IP to DGENQ other than 0, AREA,
    ZONE, OWNER or SCALE other than 1, INTRPT or DGENM other than 0, a load type, or a second load id at a bus
    (GridSim holds one load per bus): ierr 9. An existing load is changed as load_chng_5 changes it. [GH] jaimiosyncrasy/
    transmission_planning_with_PSSE modify_circuit.py: load_data_6(bus_num, "1", [1,1,1,1,1,0,0], [0.0]*8, "");
    [GH] TimThacker/perc-studio psse_worker.py: load_data_6(bus, lid, [1, area, zone, owner, 1, 0, 0], [p_mw, q_mvar, 0, 0,
    0, 0, 0, 0], "UNDERTEST"); [GH] acep-uaf/WEC-Grid psse.py: load_data_6(ibus=bus, realar=[pl] + [_f] * 7)."""
    if not _blank(lodtyp):
        return 9
    case = _need_case()
    ig = _list(intgar, 7, _i)
    ra = _list(realar, 8, _f)
    if (len(ig) > 7 or len(ra) > 8 or not all(_is(x, _i, 1) for x in ig[1:5]) or not all(_is(x, _i, 0) for x in ig[5:7])
            or not all(_is(x, _f, 0.0) for x in ra[2:8])):
        return 9
    bus = int(ibus)
    key = str(bus)
    ident = str(ident).strip()
    if key in case.loads:
        if bus in S.load_ids:
            if S.load_ids[bus] != ident:
                return 9
            values = {}
            if _given(ig[0], _i):
                values["status"] = ig[0]
            for k, attr in ((0, "pd"), (1, "qd")):
                if _given(ra[k], _f):
                    values[attr] = ra[k]
            return _apply(case.loads[key], values)
        ierr = _one_load(bus, ident)
        if ierr:
            return 9   # another load id at the bus (one load per bus), or the file's loads could not be read
        return load_chng_5(bus, ident, [ig[0]], ra[:2])
    if key not in case.buses:
        return 1
    if bus in S.load_ids or (S.info is not None and S.info.loads.get(bus)):
        return 9
    values = {"pd": ra[0] if _given(ra[0], _f) else 0.0, "qd": ra[1] if _given(ra[1], _f) else 0.0}
    if _given(ig[0], _i):
        values["status"] = ig[0]
    ierr = _try(case.add, "load", key, **values)
    if ierr == 0:
        S.load_ids[bus] = ident
    return ierr


def _units_at(ibus):
    return [r["key"] for r in _machine_rows() if _machine_bus(r["key"]) == int(ibus)]


def machine_data_4(ibus, ident="1", intgar=None, realar=None, name=None):
    """Add machine ``ident`` at ``ibus`` (key ``ibus:ident``) or change it. intgar[0] STAT (OWNER1 1, the rest 0, or
    defaults); realar[0..6] PG, QG, QT, QB, PT, PB, MBASE (the MACHINE_DATA_2 order, [API34] 2.28). Any later realar slot,
    or a droop control name: ierr 9. A new unit takes the plant's VS from plant_data_4 as its voltage set-point; a PQ bus
    with an in-service unit becomes PV (GridSim's rule). [APIX] machine_data_4: ierr = machine_data_4(ibus, id, intgar,
    realar, name), INTGAR(7), REALAR(17); [GH] acep-uaf/WEC-Grid psse.py: machine_data_4(ibus=..., id=..., realar1=PG);
    [GH] IdahoLabResearch/PyRAPSS PyRAPS_SS.py: machine_data_4(ibus, genId, realar1=Pg_new); [GH] TimThacker/perc-studio
    psse_worker.py: machine_data_2(bus, "1", [1, 1, 0, 0, 0, 0], [PG, QG, QT, QB, PT, PB, MBASE, ...])."""
    case = _need_case()
    ig = _list(intgar, 7, _i)
    ra = _list(realar, 17, _f)
    if (len(ig) > 7 or len(ra) > 17 or not _is(ig[1], _i, 1) or not all(_is(x, _i, 0) for x in ig[2:7])
            or not all(_is(x, _f) for x in ra[7:17]) or not _blank(name)):
        return 9
    values = {}
    if _given(ig[0], _i):
        values["status"] = ig[0]
    for k, attr in enumerate(("pg", "qg", "qmax", "qmin", "pmax", "pmin", "mbase")):
        if _given(ra[k], _f):
            values[attr] = ra[k]
    key = _gen_key(ibus, ident)
    if key is not None:
        return _apply(case.generators[key], values)
    if str(int(ibus)) not in case.buses:
        return 1
    vs = S.plant_vs.get(int(ibus))
    if vs is not None:
        values["vg"] = vs
    return _try(case.add, "generator", f"{int(ibus)}:{str(ident).strip()}", **values)


def plant_data_4(ibus, inode=0, intgar=None, realar=None):
    """The plant at ``ibus``: realar[0] VS sets the voltage set-point of every unit at the bus, and of units added there
    later by machine_data_4. intgar = [IREG, NODREG]: remote regulation (IREG other than 0 or the bus itself) is ierr 9;
    realar[1] RMPCT other than 100: ierr 9. The bus type is not changed (PSS/E's plant data does not change IDE; a unit
    added at a PQ bus makes it PV). [APIX] plant_data_4: ierr = plant_data_4(ibus, inode, intgar, realar), INTGAR(2),
    REALAR(2); [GH] TimThacker/perc-studio psse_worker.py: plant_data_4(bus, 0, [0, 0], [v0, 100.0]); [GH]
    jbarberia/PSSE2PowerModels to_psse.jl: plant_data_4(ibus, 0, intgar1=0, realar1=vm)."""
    case = _need_case()
    ig = _list(intgar, 2, _i)
    ra = _list(realar, 2, _f)
    bus = int(ibus)
    if (inode not in (0, None, _i) or len(ig) > 2 or len(ra) > 2 or not _is(ig[0], _i, 0, bus) or not _is(ig[1], _i, 0)
            or not _is(ra[1], _f, 100.0)):
        return 9
    if str(bus) not in case.buses:
        return 1
    if not _given(ra[0], _f):
        return 0
    for key in _units_at(bus):
        ierr = _apply(case.generators[key], {"vg": ra[0]})
        if ierr:
            return ierr
    S.plant_vs[bus] = ra[0]
    return 0


def _branch_values(intgar, realar, ratings, ibus, jbus, n_real, impedance):
    """(values, ierr) of the status, the given real slots ``impedance`` [(slot, attribute)] and RATE1-RATE3 (GridSim's
    rate A, B, C, as its RAW reader reads them); ierr 9 when another slot is given."""
    ig = list(intgar or [])
    ra = _list(realar, n_real, _f)
    rt = _list(ratings, 12, _f)
    if len(ra) > n_real or len(rt) > 12 or not all(_is(x, _f, 0.0) for x in rt[3:]):
        return None, 9
    values = {}
    if ig and _given(ig[0], _i):
        values["status"] = ig[0]
    for k, attr in impedance:
        if _given(ra[k], _f):
            values[attr] = ra[k]
    for k, attr in enumerate(("rate_a", "rate_b", "rate_c")):
        if _given(rt[k], _f):
            values[attr] = rt[k]
    return values, 0


def _add_or_set_branch(ibus, jbus, ckt, values):
    case = _need_case()
    key, _ = _branch_key(ibus, jbus, ckt)
    if key is not None:
        return _apply(case.branches[key], values)
    if str(int(ibus)) not in case.buses or str(int(jbus)) not in case.buses:
        return 1
    if "x" not in values:
        return 2
    return _try(case.add, "branch", f"{int(ibus)}-{int(jbus)}:{str(ckt).strip()}", **values)


def branch_data_3(ibus, jbus, ckt="1", intgar=None, realar=None, ratings=None, namear=None):
    """Add the line ``ibus``-``jbus`` circuit ``ckt`` (key ``ibus-jbus:ckt``) or change it. intgar = [STATUS, METBUS, O1,
    O2, O3, O4] (METBUS one of the ends; O1 1, O2-O4 0, or defaults); realar = [R, X, B, GI, BI, GJ, BJ, LEN, F1-F4]:
    R, X, B are held, the line shunts GI to BJ and LEN must be 0 or defaults; ratings RATE1-RATE3 are rate A, B, C, the
    rest must be 0 or defaults; a name: ierr 9. A new line needs X. [GH] jaimiosyncrasy/transmission_planning_with_PSSE
    modify_circuit.py: branch_data_3(FROM, TO, ID, [1, FROM, 1, _i, _i, _i], [R, X, _f, ...], [MAX_MVA, _f, ...], "");
    [GH] acep-uaf/WEC-Grid psse.py: realar[0] R, realar[1] X, ratings[0] RATEA; [API34] 2.3 REALAR(12) R, X, B, GI, BI,
    GJ, BJ, LEN, F1-F4."""
    ig = _list(intgar, 6, _i)
    ra = _list(realar, 12, _f)
    if (len(ig) > 6 or not _is(ig[1], _i, int(ibus), int(jbus)) or not _is(ig[2], _i, 1) or not all(_is(x, _i, 0) for x in ig[3:6])
            or not all(_is(x, _f, 0.0) for x in ra[3:12]) or not _blank(namear)):
        return 9
    values, ierr = _branch_values(ig, ra, ratings, ibus, jbus, 12, ((0, "r"), (1, "x"), (2, "b")))
    return ierr or _add_or_set_branch(ibus, jbus, ckt, values)


def two_winding_data_6(ibus, jbus, ckt="1", intgar=None, realari=None, ratings=None, namear=None, vgrpar=None):
    """Add the two-winding transformer ``ibus``-``jbus`` circuit ``ckt`` or change it, as a branch with R, X (pu on the
    system base), tap WINDV1/WINDV2 and shift ANG1, as GridSim's RAW reader builds one. intgar (16) = [STAT, METBUS,
    O1-O4, NTP1, TAB1, WN1BUS, CONT1, NODE1, SICOD1, COD1, CW, CZ, CM]: CW, CZ and CM other than 1, a COD1 other than 0
    (tap control), a table TAB1, O1 other than 1 or O2-O4 other than 0: ierr 9. WN1BUS (INTGAR(9), [APIX]
    two_winding_data_6: "winding one side bus number (IBUS or JBUS) (IBUS by default)") names the bus that carries WINDV1
    and ANG1. A new transformer is added with that bus as its from bus (key ``wn1bus-other:ckt``), so the tap and shift
    sit where PSS/E puts them. A WN1BUS that is neither end: ierr 9. METBUS (INTGAR(2)) must be one of the two ends, as
    in branch_data_3: ierr 9 otherwise. For a transformer already held, its from bus is its winding-one side. A call
    without WN1BUS keeps that stored winding one, in whichever order it names the ends. Source: [APIX] getdefaultint
    defines the default as the "no input" value ("Specify this value to indicate that "no input" is intended"), and
    [APIX] two_winding_chng_6 is the call that modifies "the data of an existing two-winding transformer"; so a defaulted
    WN1BUS gives no new winding one, and the stored one stands. A call that gives WN1BUS on the other end with a winding voltage or an angle: ierr 9 (the ends are never
    swapped silently). realari (21) = [R1-2, X1-2, SBASE1-2, WINDV1, NOMV1,
    ANG1, WINDV2, NOMV2, F1-F4, MAG1, MAG2, RMA1, RMI1, VMA1, VMI1, CR1, CX1, CNXA1]: MAG1, MAG2, CR1, CX1, CNXA1 other than
    0 (or defaults): ierr 9; the control limits are not used without COD1. Ratings as branch_data_3; a name or vector
    group: ierr 9. [GH] jbarberia/psse34parser a_python.py writes these intgar and realari orders; [GH]
    pnnl/LL-risk-assessment Step3a_simsetup_loadadd.py: two_winding_data_6(old, new, id, [1, new, _i, 0, 0, 0, 33, 0, new,
    0, 0, 1, 0, 1, 1, 1], [0, x, base, 1.0, 0.0, 0.0, 1.0, 0.0, ...]); [GH] IIT-MAC/HYACDCSIM module_acdc.py:
    realari [Rt, Xt, ...]."""
    ig = _list(intgar, 16, _i)
    ra = _list(realari, 21, _f)
    if (len(ig) > 16 or len(ra) > 21 or not _is(ig[1], _i, int(ibus), int(jbus)) or not _is(ig[2], _i, 1) or not all(_is(x, _i, 0) for x in ig[3:6]) or not _is(ig[7], _i, 0)
            or not _is(ig[12], _i, 0) or not all(_is(x, _i, 1) for x in ig[13:16])
            or not all(_is(ra[k], _f, 0.0) for k in (12, 13, 18, 19, 20)) or not _blank(namear) or not _blank(vgrpar)):
        return 9
    wn1_given = _given(ig[8], _i)
    key, _ = _branch_key(ibus, jbus, ckt)
    if wn1_given:
        wn1 = int(ig[8])
    elif key is not None:
        wn1 = _ends(key)[0]   # a held transformer keeps its stored winding one
    else:
        wn1 = int(ibus)
    if wn1 not in (int(ibus), int(jbus)):
        return 9
    other = int(jbus) if wn1 == int(ibus) else int(ibus)
    values, ierr = _branch_values(ig, ra, ratings, ibus, jbus, 21, ((0, "r"), (1, "x"), (5, "shift_deg")))
    if ierr:
        return ierr
    w1 = ra[3] if _given(ra[3], _f) else None
    w2 = ra[6] if _given(ra[6], _f) else None
    if key is not None and (wn1_given or w1 is not None or w2 is not None or "shift_deg" in values) and _ends(key)[0] != wn1:
        return 9   # the held transformer's tap side is its from bus; PSS/E's winding one would be on the other end
    if w1 is not None or w2 is not None:
        if key is not None and (w1 is None or w2 is None):
            return 9   # one winding of an existing transformer: the held tap is the ratio, not the windings
        values["tap"] = (w1 if w1 is not None else 1.0) / (w2 if w2 is not None else 1.0)
    elif key is None:
        values["tap"] = 1.0
    if key is None and "shift_deg" not in values:
        values["shift_deg"] = 0.0
    return _add_or_set_branch(wn1, other, ckt, values)


def shunt_data(ibus, ident="1", intgar=None, realar=None):
    """Add the fixed shunt ``ident`` at ``ibus`` or change one added here. intgar = [STATUS] (1 only: the bus shunt has no
    status); realar = [GL, BL] (MW and MVAr at 1 pu, added to the bus shunt as GridSim's RAW reader adds them). A second
    shunt id at a bus, or a shunt of the file (its id is not held): ierr 9. [APIX] shunt_data: ierr = shunt_data(ibus, id,
    intgar, realar), INTGAR(1), REALAR(2); [GH] jbarberia/psse34parser a_python.py: shunt_data(ibus, id, [STATUS], [GL,
    BL]); [GH] mike52ten11/psse Dynamic_psse_ver.py: shunt_data(bus, id, intgar, [GL, BL])."""
    case = _need_case()
    ig = _list(intgar, 1, _i)
    ra = _list(realar, 2, _f)
    if len(ig) > 1 or len(ra) > 2 or not _is(ig[0], _i, 1):
        return 9
    bus = int(ibus)
    key = str(bus)
    ident = str(ident).strip()
    values = {a: ra[k] for k, a in ((0, "gs"), (1, "bs")) if _given(ra[k], _f)}
    if key in case.shunts:
        if S.shunt_ids.get(bus) != ident:
            return 9
        return _apply(case.shunts[key], values)
    if key not in case.buses:
        return 1
    if bus in S.shunt_ids:
        return 9
    ierr = _try(case.add, "shunt", key, **values)
    if ierr == 0:
        S.shunt_ids[bus] = ident
    return ierr


def purgbrn(ibus, jbus, ckt="1"):
    """Delete the branch. ierr ([APIX] purgbrn): 1 bus not found, 2 branch not found; GridSim's 9 when a tap changer,
    flow control or mutual record names it. [GH] jaimiosyncrasy/transmission_planning_with_PSSE modify_circuit.py:
    purgbrn(FROM, TO, ID); [GH] Guzman-Dufrechou/Flujo_DC-uy herramientas.py: purgbrn(from, to, id)."""
    case = _need_case()
    if str(int(ibus)) not in case.buses or str(int(jbus)) not in case.buses:
        return 1
    key, _ = _branch_key(ibus, jbus, ckt)
    if key is None:
        return 2
    return _try(case.delete, "branch", key)


def purgmac(ibus, ident="1"):
    """Delete the machine. ierr ([APIX] purgmac): 1 bus not found, 2 no machines at the bus, 3 machine not found;
    GridSim's 9 for the last unit at the slack bus. [GH] mike52ten11/psse Dynamic_psse_ver.py: purgmac(bus, id); [GH]
    Farhan0048/Analysis-of-A-South-Australian-14-BUS-System... qv.py: purgmac(510, "1")."""
    case = _need_case()
    if str(int(ibus)) not in case.buses:
        return 1
    if not _units_at(ibus):
        return 2
    key = _gen_key(ibus, ident)
    if key is None:
        return 3
    return _try(case.delete, "generator", key)


def purgload(ibus, ident="1"):
    """Delete the load ``ident`` at ``ibus`` (the bus demand). ierr ([APIX] purgload): 1 bus not found, 2 no load at the
    bus, 3 load not found (another id); GridSim's 9 for a bus whose file holds several loads. [GH] jmmauricio/pypstools
    psse_simulation.py: purgload(frmbus, str(id)); [GH] pnnl/LL-risk-assessment Step2a_locational_sensitivity.py:
    purgload(bus_num, LOAD_ID)."""
    case = _need_case()
    bus = int(ibus)
    key = str(bus)
    if key not in case.buses:
        return 1
    if key not in case.loads:
        return 2
    ident = str(ident).strip()
    if bus in S.load_ids:
        if S.load_ids[bus] != ident:
            return 3
    else:
        ierr = _one_load(bus, ident)
        if ierr:
            return 3 if ierr == 1 else ierr
    ierr = _try(case.delete, "load", key)
    if ierr == 0:
        S.load_ids.pop(bus, None)
    return ierr


def purgshunt(ibus, ident="1"):
    """Delete the fixed shunt ``ident`` at ``ibus``, one added here by shunt_data. ierr ([APIX] purgshunt): 1 bus not
    found, 2 no fixed shunt at the bus, 3 shunt not found (another id than the one added here); GridSim's 9 for a shunt
    of the file (its id is not held). [APIX] purgshunt: ierr = purgshunt(ibus, id); [GH] sbhowmik7/PSSEcompare
    meta/fxshunt.py (purgshunt with ibus and id)."""
    case = _need_case()
    bus = int(ibus)
    if str(bus) not in case.buses:
        return 1
    if str(bus) not in case.shunts:
        return 2
    held = S.shunt_ids.get(bus)
    if held is None:
        return 9
    if held != str(ident).strip():
        return 3
    ierr = _try(case.delete, "shunt", str(bus))
    if ierr == 0:
        S.shunt_ids.pop(bus, None)
    return ierr


# ---- complex arrays (RC5 WP15) ---------------------------------------------------------------------------------------------

#: The STRING values each complex array call accepts in PSS/E ([APIX] abuscplx, amachcplx, abrncplx). A string in the
#: list that GridSim does not map is "not supported"; one outside it is PSS/E's invalid STRING.
_BUS_CPLX = ("VOLTAGE", "SHUNTACT", "O_SHUNTACT", "SHUNTNOM", "O_SHUNTNOM", "SHUNTN", "SHUNTZ", "MISMATCH", "O_MISMATCH")
_MACH_CPLX = ("ZSORCE", "XTRAN", "ZPOS", "ZNEG", "ZZERO", "ZGRND", "ZGRNDPU", "PQGEN", "O_PQGEN")
_BRN_CPLX = ("RX", "FROMSHNT", "TOSHNT", "RXZERO", "FROMSHNTZERO", "TOSHNTZERO", "PQ", "PQLOSS", "O_PQ", "O_PQLOSS")

#: PSS/E's highest subsystem identifier ([APIX]: valid identifiers range from 0 to 11).
_MAX_SID = 11


def _cplx_strings(string, known, mapped, invalid, unsupported):
    """(ierr, upper-case strings): ``invalid`` for a string PSS/E does not know, ``unsupported`` for one it knows that is
    not ``mapped``, else 0."""
    out = [str(s).upper() for s in _strings(string)]
    for s in out:
        if s not in known:
            return invalid, None
        if s != mapped:
            return unsupported, None
    return 0, out


def abuscplx(sid=-1, flag=1, string="VOLTAGE"):
    """VOLTAGE: each bus's solved voltage, V at angle (pu), with abusint's flags. ierr ([APIX] abuscplx): 2 invalid SID
    (above 11), 3 invalid FLAG (not 1 or 2), 6 invalid STRING (not one of PSS/E's); GridSim's 9 for a subsystem (SID 0 to
    11), flag 2 when the file had buses GridSim dropped, another valid string, or a case not solved.
    [APIX] abuscplx: ierr, xarray = abuscplx(sid, flag, string); [GH] alam51/psse2 f1.py and alam51/auto_psse_v1.0:
    abuscplx(-1, string='VOLTAGE'); [GH] aldahabi27/BSPSSEPy: abuscplx(-1, 2, [string])."""
    if sid > _MAX_SID:
        return 2, None
    if flag not in (1, 2):
        return 3, None
    ierr, strings = _cplx_strings(string, _BUS_CPLX, "VOLTAGE", 6, 9)
    if ierr:
        return ierr, None
    if sid >= 0 or (flag == 2 and not _all_present()):
        return 9, None
    out = []
    for _ in strings:
        sol = _solution()
        if sol is None:
            return 9, None
        col = []
        for r in _bus_rows():
            v = sol.buses.get(int(r["key"]))
            if v is None:
                return 9, None
            a = math.radians(v.va)
            col.append(complex(v.vm * math.cos(a), v.vm * math.sin(a)))
        out.append(col)
    return 0, out


def amachcplx(sid=-1, flag=1, string="PQGEN"):
    """PQGEN: each machine's solved output PGEN + j QGEN (as amachreal), with amachint's flags. ierr ([APIX] amachcplx):
    2 invalid SID (above 11), 3 invalid FLAG (not 1 to 4), 6 invalid STRING (not one of PSS/E's); GridSim's 9 for a
    subsystem (SID 0 to 11), flag 2 or 4 when the file had elements GridSim dropped, another valid string, a case not
    solved, or a unit amachreal does not answer.
    [APIX] amachcplx: ierr, xarray = amachcplx(sid, flag, string); [GH] NatLabRockies/PyPSSE helper_functions.py and
    NatLabRockies/ParaEMT_public Lib_BW.py: amachcplx(-1, 4, 'PQGEN')."""
    if sid > _MAX_SID:
        return 2, None
    if flag not in (1, 2, 3, 4):
        return 3, None
    ierr, strings = _cplx_strings(string, _MACH_CPLX, "PQGEN", 6, 9)
    if ierr:
        return ierr, None
    out = []
    for _ in strings:
        ierr, pq = amachreal(sid, flag, ["PGEN", "QGEN"])
        if ierr:
            return 9, None
        out.append([complex(p, q) for p, q in zip(pq[0], pq[1])])
    return 0, out


def abrncplx(sid=-1, owner=1, ties=1, flag=1, entry=1, string="RX"):
    """RX: each branch's R + jX (pu on the system base); flag 3 or 4, entry 1 (see _branch_rows). ierr ([APIX]
    abrncplx): 2 invalid SID (above 11), 5 invalid FLAG (not 1 to 6), 6 invalid ENTRY (not 1 or 2), 9 invalid STRING
    (not one of PSS/E's); OWNER and TIES are ignored for a negative SID, as in PSS/E. GridSim's "not supported" is 11
    here, because PSS/E's 9 is an invalid STRING: a subsystem (SID 0 to 11), flags 1, 2, 5 and 6 (GridSim's object model
    does not tell a line from a two-winding transformer), entry 2, a case with three-winding windings, flag 4 when the
    file had elements GridSim dropped, or another valid string.
    [APIX] abrncplx: ierr, xarray = abrncplx(sid, owner, ties, flag, entry, string). Both public call sites found,
    [GH] NatLabRockies/ASSET assetlib.py and pnnl/LL-risk-assessment Step2d_UIF.py: abrncplx(-1, 1, 1, 1, 1, 'RX'),
    confirm the argument order only: they ask for flag 1 (in-service lines without transformers), which GridSim refuses
    with 11, so no public source shows flag 3 or 4 in use. The flag 3 and 4 meanings are the API manual's."""
    if sid > _MAX_SID:
        return 2, None
    if flag not in (1, 2, 3, 4, 5, 6):
        return 5, None
    if entry not in (1, 2):
        return 6, None
    ierr, strings = _cplx_strings(string, _BRN_CPLX, "RX", 9, 11)
    if ierr:
        return ierr, None
    rows = _branch_rows(flag, entry) if sid < 0 else None
    if rows is None:
        return 11, None
    return 0, [[complex(r["r"], r["x"]) for r in rows] for _ in strings]


# ---- iteration (RC5 WP15) ---------------------------------------------------------------------------------------------------
# PSS/E returns buses in its ordered sequence (bus number order with the default numeric output option), machines by
# ascending id and branches by ascending to bus and circuit. GridSim answers the same order from the case as it stands.

def inibus(ibus=0):
    """Start the bus sequence at the first bus numbered ``ibus`` or above (0: the first bus). [APIX] inibus: ierr =
    inibus(ibus); [GH] changgang/steps psse_test_ieee39.py (inibus, then nxtbus until ierr is not 0)."""
    buses = sorted(int(k) for k in _need_case().buses.keys())
    S.iter_bus = [b for b in buses if b >= int(ibus)]
    return 0


def nxtbus():
    """(ierr, ibus, name): the next bus; ierr 1 when there are no more (ibus and name None), 2 when inibus was not called.
    The name is GridSim's bus name (blank when the case has none), without PSS/E's base-voltage suffix. [APIX] nxtbus:
    ierr, ibus, cval = nxtbus(); [GH] changgang/steps psse_test_ieee39.py: ierr, ibus, busname = psspy.nxtbus()."""
    if S.iter_bus is None:
        return 2, None, None
    if not S.iter_bus:
        return 1, None, None
    b = S.iter_bus.pop(0)
    name = _need_case().buses[str(b)].name
    return 0, b, name or ""


def _machine_id(key):
    return key.split(":", 1)[1] if ":" in key else (key.split("#", 1)[1] if "#" in key else "1")


def inimac(ibus):
    """Start the machine sequence at bus ``ibus``. ierr ([APIX] inimac): 1 bus not found, 2 not a generator bus (bus
    type other than 2 or 3). [APIX] inimac: ierr = inimac(ibus); [GH] changgang/steps psse_test_ieee39.py."""
    key = str(int(ibus))
    if key not in _need_case().buses:
        return 1
    if not any(r["key"] == key and int(r["type"]) in (2, 3) for r in _bus_rows()):
        return 2
    S.iter_mac[int(ibus)] = sorted(_machine_id(k) for k in _units_at(ibus))
    return 0


def nxtmac(ibus):
    """(ierr, id): the next machine id at ``ibus``, ascending; ierr 1 when there are no more, 2 when inimac was not
    called for the bus. [APIX] nxtmac: ierr, id = nxtmac(ibus); [GH] changgang/steps psse_test_ieee39.py:
    ierr, ickt = psspy.nxtmac(ibus)."""
    seq = S.iter_mac.get(int(ibus))
    if seq is None:
        return 2, None
    if not seq:
        return 1, None
    return 0, seq.pop(0)


def inibrn(ibus, single=2):
    """Start the branch sequence at bus ``ibus``: single 1, only branches to a higher-numbered bus; 2, all branches
    (three-winding windings are left out). ierr 1: no such bus; 2: another ``single``. [APIX] inibrn: ierr =
    inibrn(ibus, single); [GH] changgang/steps psse_test_ieee39.py."""
    case = _need_case()
    bus = int(ibus)
    if str(bus) not in case.buses:
        return 1
    if single not in (1, 2):
        return 2
    seq = []
    for r in case.branches.rows():
        f, t, c = _ends(r["key"])
        if f is None or r["key"].partition(":")[0].count("-") >= 2:
            continue
        other = t if f == bus else f if t == bus else None
        if other is None or (single == 1 and other <= bus):
            continue
        seq.append((other, c))
    S.iter_brn[bus] = sorted(seq)
    return 0


def nxtbrn(ibus):
    """(ierr, jbus, ickt): the next branch at ``ibus``; ierr 1 when there are no more (jbus 0, ickt blank), 2 when inibrn
    was not called for the bus. [APIX] nxtbrn: ierr, jbus, ickt = nxtbrn(ibus); [GH] changgang/steps
    psse_test_ieee39.py: ierr, jbus, ickt = psspy.nxtbrn(ibus)."""
    seq = S.iter_brn.get(int(ibus))
    if seq is None:
        return 2, None, None
    if not seq:
        return 1, 0, "  "
    j, c = seq.pop(0)
    return 0, j, c


# ---- dynamics (dynamics-study, re-run from t = 0) -------------------------------------------------------------------------

def dyre_new(statusarray=None, dyrefile=None, conecfile="", conetfile="", compilefil="", extended=False):
    """Record the DYR file. GridSim reads it with dynamics-study; ``extended`` (the CLI's --dyr-extended) is not taken by
    dynamics-study: ierr 9. [API34] 4.1.25: dyre_new(startindx, dyrefile, conecfile, conetfile, compilfil)."""
    _need_case()
    if extended:
        return 9
    if dyrefile is None or not os.path.isfile(str(dyrefile)):
        return 2
    S.dyr_path = os.path.abspath(str(dyrefile))
    S.started = False
    return 0


def voltage_channel(status=None, ident=""):
    """A bus voltage channel: status[3] (the last slot) is the bus. [API34] 4.2.4.5: voltage_channel(status, ident),
    STATUS(4) channel, VAR, ICON, bus."""
    status = list(status or [])
    if not status:
        return 2
    S.channels.append({"kind": "busVoltage", "bus": int(status[-1])})
    return 0


def machine_array_channel(status=None, ident="1", name=""):
    """status = [channel, quantity, ibus]: quantity 1 (ANGLE) or 7 (SPEED), or the word. PELEC (2) and the others: ierr 9.
    [API34] 4.2.2.2: machine_array_channel(status, id, ident), STATUS(3)."""
    status = list(status or [])
    if len(status) < 3:
        return 2
    q = status[1]
    kind = {1: "rotorAngle", 7: "speed", "ANGLE": "rotorAngle", "SPEED": "speed"}.get(q.upper() if isinstance(q, str) else q)
    if kind is None:
        return 9
    S.channels.append({"kind": kind, "machine": {"bus": int(status[2]), "id": str(ident).strip()}})
    return 0


def _same_rows(a, b):
    """None when every element of case ``b`` carries the same attribute values as case ``a`` (in order, doubles bit for
    bit), else what differs."""
    for kind in ("buses", "generators", "loads", "branches", "shunts"):
        ra, rb = getattr(a, kind).rows(), getattr(b, kind).rows()
        if len(ra) != len(rb):
            return f"{len(ra)} {kind} held, {len(rb)} read back"
        for x, y in zip(ra, rb):
            for name, v in x.items():
                if name == "key":
                    continue
                w = y.get(name)
                same = (v.hex() == w.hex()) if isinstance(v, float) and isinstance(w, float) else v == w
                if not same:
                    return f"{kind} {x['key']} {name}"
    return None


def _study_files(folder):
    """Copy the RAW (or the edited case, written as RAW 33) and the DYR into ``folder``; return their names. An edited
    case is written, read back and compared with the held case (every attribute, doubles bit for bit), then written again
    from the copy read back and the two files compared byte for byte apart from the two title lines (which carry the
    name of the file read), as the level 2 study operation checks its JSON v2 copy; GridSimError when either check
    fails."""
    case = _need_case()
    raw_name = "case.raw"
    raw = os.path.join(folder, raw_name)
    if case.revision > 0 or not S.raw_path or not S.raw_path.lower().endswith(".raw"):
        case.save(raw, "raw33")
        back = S.session.open(raw)
        try:
            what = _same_rows(case, back)
            if what is not None:
                raise GridSimError(f"the edited case does not survive a RAW 33 write and read unchanged ({what} differs)")
            again = os.path.join(folder, "again.raw")
            back.save(again, "raw33")
            with open(raw, "rb") as f1, open(again, "rb") as f2:
                # The two title lines (RAW 33 records 2 and 3) carry the name of the file read, so they are left out.
                first, second = f1.read().splitlines(keepends=True), f2.read().splitlines(keepends=True)
                if first[:1] + first[3:] != second[:1] + second[3:]:
                    raise GridSimError("the edited case's RAW 33 write-read-write is not idempotent")
        finally:
            back.release()
    else:
        shutil.copyfile(S.raw_path, raw)
    shutil.copyfile(S.dyr_path, os.path.join(folder, "case.dyr"))
    return raw_name, "case.dyr"


def _sha256(path):
    import hashlib
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def _machine_ident(key):
    if ":" in key:
        bus, ident = key.split(":", 1)
        return int(bus), ident
    return None, None


def _reference():
    for r in _machine_rows():
        if r["status"] == 1:
            bus, ident = _machine_ident(r["key"])
            if bus is not None:
                return {"bus": bus, "id": ident}
    raise GridSimError("no in-service machine with a PSS/E identity to take as the angle reference")


def _stand_in_event(at):
    """dynamics-study needs at least one event in a row. With none recorded, a trip of the first branch one step AFTER
    the end of the window stands in; the samples after the window are then cut off (_window), so nothing returned has
    seen it."""
    for r in _branch_rows(4, 1) or _need_case().branches.rows():
        f, t, c = _ends(r["key"])
        if f is not None and r["status"] == 1 and ":" in r["key"]:
            return {"kind": "branchTrip", "at": at, "branch": {"from": f, "to": t, "circuit": c}}
    raise GridSimError("no in-service branch with a PSS/E identity to stand in as the end-of-run event")


def _run_study(duration, events, channels):
    """One dynamics-study row from t = 0 with ``events``; returns the parsed result document. With no event the run goes
    one step past ``duration`` with the stand-in event there (the caller cuts the row back with _window)."""
    end = duration
    if not events:
        end = (round(duration / S.step_seconds) + 1) * S.step_seconds
        events = [_stand_in_event(end)]
    with tempfile.TemporaryDirectory(prefix="gridsim-psspy-") as tmp:
        raw, dyr = _study_files(tmp)
        study = {
            "schemaVersion": 1,
            "raw": {"path": raw, "sha256": _sha256(os.path.join(tmp, raw))},
            "dyr": {"path": dyr, "sha256": _sha256(os.path.join(tmp, dyr))},
            "initialisation": {"powerFlow": {"tolerancePu": 1e-8, "maxIterations": 100}, "unmodelled": "refuse"},
            "integration": {"stepSeconds": S.step_seconds, "durationSeconds": end},
            "criteria": {"slipLimitRad": 2.0 * math.pi, "maxAngleSpreadDeg": 180.0},
            "angleReference": _reference(),
            "channels": channels or [{"kind": "busVoltage", "bus": int(_bus_rows()[0]["key"])}],
            "cases": [{"name": "psspy", "kind": "events", "events": events}],
        }
        path = os.path.join(tmp, "study.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(study, f)
        return S.case.rms(path)


def _window(row, duration):
    """A no-event row cut back to t = 0 .. ``duration``: only ``run`` with ``samples``, ``timeSeconds`` and each
    channel's ``values``. Everything dynamics-study reports about the stand-in event (disposition, post-event network,
    angle spread) is left out, because no event was asked for. GridSimError when the stand-in did not land one step after
    the window."""
    n = round(duration / S.step_seconds)
    run_ = row["run"]
    ev = row.get("events") or [{}]
    if len(run_["timeSeconds"]) != n + 2 or ev[0].get("appliedStep") != n + 1:
        raise GridSimError("the stand-in event did not land one step after the window; the run is not reported")
    return {"index": row.get("index"), "name": row.get("name"), "kind": "no events",
            "run": {"samples": n + 1, "timeSeconds": run_["timeSeconds"][:n + 1],
                    "channels": [{**c, "values": c["values"][:n + 1]} for c in run_["channels"]]}}


def _steps(t):
    n = round(t / S.step_seconds)
    if n < 1 or abs(n * S.step_seconds - t) > 1e-9:
        raise ValueError(f"tpause {t} s is not a whole number of {S.step_seconds} s steps")
    return n * S.step_seconds


def strt_2(options=None, outfile=None):
    """Record t = 0 and check the initialisation by a 0.05 s run with no event (ierr 3 when the machines cannot start;
    ierr 9 when the edited case cannot be written for the run, the reason in ``last_error()``). [FORUM] q6775:
    psspy.strt_2([0, 1], outfile)."""
    if S.case is None or S.dyr_path is None:
        return 2
    try:
        doc = _run_study(0.05, [], S.channels)
    except GridSimError as ex:
        S.last_error = str(ex)
        return 9
    row = doc["cases"][0]
    if row.get("disposition") == "NOT RUN":
        S.last_run = row
        return 3
    S.started = True
    S.time = 0.0
    S.events = []
    S.faults = []
    return 0


def strt(option=0, outfile=""):
    """As strt_2. [API34] 4.1.72: strt(option, outfile)."""
    return strt_2(None, outfile)


def run(option=0, tpause=None, nprt=None, nplt=None, crtplt=None):
    """Run from t = 0 to ``tpause`` with every event recorded so far, as one dynamics-study row (a re-run from zero gives
    the same bits as one long run). ierr 0; the row is ``channels()``. With no event yet the row holds only the time
    series of the undisturbed run. ierr 9 when the run cannot be made (``last_error()`` says why).
    [API34] 4.1.44: run(option, tpause, nprt, nplt, crtplt)."""
    if not S.started:
        return 3
    if tpause is None:
        return 2
    t = _steps(float(tpause))
    try:
        doc = _run_study(t, list(S.events), S.channels)
        row = doc["cases"][0]
        S.last_run = row if S.events else _window(row, t)
    except GridSimError as ex:
        S.last_error = str(ex)
        return 9
    S.time = t
    return 0


def channels():
    """The last run's row: ``run`` holds ``timeSeconds`` and ``channels`` (each with ``values``)."""
    return S.last_run


def last_error():
    """Why the last dynamic call returned ierr 9 (None when none did)."""
    return S.last_error


def dist_bus_fault(ibus, units=1, basekv=0.0, values=None):
    """A three-phase fault at ``ibus`` at the current time. ``values`` = [G, B] in MVA (units 1), so Y = (G + jB)/SBASE
    pu with SBASE the case's system base read from the file, and Zf = 1/Y; [G, B] of magnitude 1e9 MVA or more (PSS/E's
    default is -2e11), or none, is a bolted fault. Units 2 and 3, or a resistive fault on a case whose base was not read:
    ierr 9. [API34] 4.13.2 (DIST_3WIND_FAULT, the same UNITS/BASEKV/VALUES layout: UNITS 1 admittance in MVA, VALUES
    default [0.0, -2.0E11]); [FORUM] q8032: psspy.dist_bus_fault(2, 1, 0, [0.0, -0.2E+10])."""
    if units != 1:
        return 9
    g, b = (list(values) + [0.0, -2.0e11])[:2] if values is not None else (0.0, -2.0e11)
    at = S.time
    if abs(complex(g, b)) >= 1.0e9:
        S.events.append({"kind": "busFault", "at": at, "bus": int(ibus)})
    else:
        if S.info is None or not S.info.sbase > 0:
            return 9
        y = complex(g, b) / S.info.sbase
        if y == 0:
            return 2
        z = 1.0 / y
        S.events.append({"kind": "sequenceFault", "at": at, "bus": int(ibus), "type": "threePhase", "rPu": z.real, "xPu": z.imag})
    S.faults.append(int(ibus))
    return 0


def dist_clear_fault(ifault=1):
    """Clear the ifault-th fault applied (1 is the first). [FORUM] q11482: psspy.dist_clear_fault(1)."""
    if not 1 <= int(ifault) <= len(S.faults):
        return 1
    S.events.append({"kind": "faultClear", "at": S.time, "bus": S.faults[int(ifault) - 1]})
    return 0


def dist_branch_trip(ibus, jbus, id="1"):
    """Trip the branch at the current time. [FORUM] q11482: psspy.dist_branch_trip(ibus=..., jbus=..., id='1')."""
    S.events.append({"kind": "branchTrip", "at": S.time, "branch": {"from": int(ibus), "to": int(jbus), "circuit": str(id).strip()}})
    return 0


def dist_machine_trip(ibus, id="1"):
    """Trip the machine at the current time. [FORUM] q2492: psspy.dist_machine_trip(ibus=..., id=...)."""
    S.events.append({"kind": "machineTrip", "at": S.time, "machine": {"bus": int(ibus), "id": str(id).strip()}})
    return 0


def events():
    """The dynamics-study events recorded so far (a copy)."""
    return [dict(e) for e in S.events]


def close():
    """End the module's session."""
    if S.session is not None:
        S.session.close()
    S.session = None
    _reset()
