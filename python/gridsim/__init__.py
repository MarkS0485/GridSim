"""GridSim from Python.

A pure-Python wrapper over the gridsim command-line program: it runs a verb with ``--json`` and returns the result
document as Python values, so the numbers are the CLI's own, bit for bit. ``Session`` keeps one ``gridsim rpc`` host
running (a pipe, or loopback only) for many calls on a case held by handle. Standard library only.

    import gridsim
    gs = gridsim.GridSim()                 # finds the CLI (see find_cli)
    r = gs.pf("case14")                    # or gs.run("pf", "case14")
    r.summary["converged"], r.table("buses")[0]

    with gridsim.Session() as sess:        # one resident host: load once, solve many times
        h = sess.load("case9")
        v = h.solve().vm_pu

See python/README.md and manual chapter 19 ("Python").
"""

from ._cli import (ALIASES, SCHEMA, WRITER_VERBS, GridSim, GridSimError, Result, canonical, find_cli, run)
from ._session import PROTOCOL, CaseHandle, Session, SessionError, SolveResult

__all__ = ["ALIASES", "SCHEMA", "WRITER_VERBS", "GridSim", "GridSimError", "Result", "canonical", "find_cli", "run",
           "PROTOCOL", "CaseHandle", "Session", "SessionError", "SolveResult"]
