"""Tests for gridsim.ModelSession (gridsim rpc --protocol 2, the object model). Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The tests skip, saying so, when no built CLI is found. Oracles: the pf verb (GridSim.run) on the same file, edited the
same way, and the level 1 Session.
"""

import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
CASE9 = ROOT / "data" / "matpower" / "lib" / "case9.m"


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class ObjectModel(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.gs = gridsim.GridSim(cwd=cls.tmp.name)
        cls.ms = gridsim.ModelSession(cwd=cls.tmp.name)

    @classmethod
    def tearDownClass(cls):
        cls.ms.close()
        cls.tmp.cleanup()

    def test_the_host_speaks_level_2(self):
        self.assertEqual(gridsim.PROTOCOL2, "gridsim-rpc/2")
        self.assertEqual(self.ms.operations[-8:], ("elements", "get", "set", "edits", "study", "save", "add", "delete"))

    def test_elements_are_keyed_in_model_order(self):
        case = self.ms.open(CASE9)
        self.assertEqual(list(case.buses), [str(i) for i in range(1, 10)])
        self.assertEqual(list(case.generators), ["G1", "G2", "G3"])
        self.assertEqual(list(case.loads), ["5", "7", "9"])
        self.assertEqual(case.generators["G2"].pg, 163.0)
        self.assertEqual(case.buses["5"].base_kv, 345.0)
        with self.assertRaises(KeyError):
            case.loads["4"]

    def test_an_edit_then_solve_equals_pf_on_the_file_edited_the_same_way(self):
        edited = os.path.join(self.tmp.name, "case9-bus9-off.m")
        with open(CASE9, encoding="utf-8") as f, open(edited, "w", encoding="utf-8") as g:
            g.write(f.read().replace("\t9\t1\t125\t50\t", "\t9\t1\t0\t0\t"))
        oracle = self.gs.pf(edited).table("buses")
        case = self.ms.open(CASE9)
        load = case.loads["9"]
        load.pd = 0.0
        load.qd = 0.0
        r = case.solve()
        self.assertTrue(r.converged)
        for row in oracle:
            self.assertEqual(r.bus(row["bus"]).vm, row["vm_pu"])
            self.assertEqual(r.bus(row["bus"]).va, row["va_deg"])
        self.assertEqual(case.revision, 2)
        self.assertEqual([e["attribute"] for e in case.edits()], ["pd", "qd"])
        load.set(pd=125.0, qd=50.0)
        again = case.solve()
        original = self.gs.pf(str(CASE9)).table("buses")
        for row in original:
            self.assertEqual(again.bus(row["bus"]).vm, row["vm_pu"])

    def test_a_refused_write_changes_nothing(self):
        case = self.ms.open(CASE9)
        with self.assertRaises(gridsim.GridSimError):
            case.generators["G2"].status = 2
        with self.assertRaises(gridsim.GridSimError):
            case.buses["5"].base_kv = 1.0
        self.assertEqual(case.edits(), [])
        self.assertEqual(case.revision, 0)

    def test_branch_flows_and_studies_run_on_the_case_as_edited(self):
        case = self.ms.open(CASE9)
        r = case.solve()
        f = r.branch("1-4")
        self.assertAlmostEqual(f.p_from, r.bus(1).p_inj_mw, delta=1e-9 * abs(f.p_from))
        n1 = case.contingency()
        self.assertEqual(n1.verb, "nminus1")
        sc = case.short_circuit("--bus", 5)
        self.assertEqual(sc.verb, "iec60909")
        self.assertEqual(sc.summary["ikpp_ka"], self.gs.iec60909(str(CASE9), "--bus", "5").summary["ikpp_ka"])


if __name__ == "__main__":
    unittest.main()
