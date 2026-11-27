"""psspy ``solv`` (Gauss-Seidel, GridSim's ``pf --engine gs``). Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The real-host tests drive the built CLI and skip, saying so, when none is built.
Oracle: the CLI's own ``pf --engine gs`` result document, run in a subprocess with no psspy code involved; the engine's
agreement with Newton and pandapower is proved in Rc5Wp25FdlfGsQvModalTests (T2, T3).
"""

import importlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
RAW33 = ROOT / "tests" / "GridSim.Tests" / "fixtures" / "psse" / "case14.raw"


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class Solv(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.psspy = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        self.assertEqual(self.psspy.psseinit(150, cwd=self.tmp.name), 0)
        self.assertEqual(self.psspy.read(0, str(RAW33)), 0)

    def tearDown(self):
        self.psspy.close()
        self.tmp.cleanup()

    def _cli_gs_buses(self):
        """The bus table of ``gridsim pf <case> --engine gs --json``, run by hand in a subprocess."""
        out = os.path.join(self.tmp.name, "gs.json")
        run = subprocess.run(gridsim.find_cli() + ["pf", str(RAW33), "--engine", "gs", "--json", out],
                             capture_output=True, text=True, cwd=self.tmp.name)
        self.assertEqual(run.returncode, 0, run.stderr)
        with open(out, encoding="utf-8") as f:
            doc = json.load(f)
        self.assertEqual(doc["summary"]["engine"], "gs")
        return {int(r["bus"]): r for r in doc["tables"]["buses"]}

    def test_solv_gives_the_cli_gauss_seidel_solution(self):
        p = self.psspy
        self.assertEqual(p.solved(), 3)
        self.assertEqual(p.solv(), 0)
        self.assertEqual(p.solved(), 0)
        rows = self._cli_gs_buses()
        for bus, row in rows.items():
            ierr, vm = p.busdat(bus, "PU")
            self.assertEqual(ierr, 0)
            self.assertEqual(vm, row["vm_pu"], f"bus {bus}")   # the same engine on the same file: the same bits
        self.assertEqual(p.solv([1, 0, 0, 0, 0, 0, 0, 0]), 9)   # a non-default option is never ignored silently

    def test_branch_flows_are_not_served_after_a_solve_that_did_not_converge(self):
        p = self.psspy
        self.assertEqual(p.solv(), 0)
        ierr, flow = p.brnflo(1, 2, "1")
        self.assertEqual(ierr, 0)
        self.assertIsNotNone(flow)
        # The shape solv records when pf --engine gs writes no result document (it did not converge).
        p.S.solved = p._EngineSolve(p.S.case, {"exitCode": 3})
        self.assertEqual(p.solved(), 1)
        self.assertEqual(p.brnflo(1, 2, "1"), (3, None))
        with self.assertRaises(gridsim.GridSimError):
            p.S.solved.branch(next(iter(p.S.case.branches.rows()))["key"])


if __name__ == "__main__":
    unittest.main()
