"""GridSim from Python.

A pure-Python wrapper over the gridsim command-line program: it runs a verb with ``--json`` and returns the result
document as Python values. No in-process interop and no network service, so the numbers are the CLI's own, bit for
bit. Standard library only.

    import gridsim
    gs = gridsim.GridSim()                 # finds the CLI (see find_cli)
    r = gs.pf("case14")                    # or gs.run("pf", "case14")
    r.summary["converged"], r.table("buses")[0]

See python/README.md and manual chapter 19 ("Python").
"""

from ._cli import (ALIASES, SCHEMA, WRITER_VERBS, GridSim, GridSimError, Result, canonical, find_cli, run)

__all__ = ["ALIASES", "SCHEMA", "WRITER_VERBS", "GridSim", "GridSimError", "Result", "canonical", "find_cli", "run"]
