"""RC5 WP15: adding and deleting elements from Python (Case.add, Case.delete) and the psspy calls mapped onto them.
Standard library unittest; the host tests drive the real CLI and skip, saying so, when none is built.

Oracles: the closed-form two-bus solution (V2^2 = [(1 - 2QX) + sqrt((1 - 2QX)^2 - 4X^2(P^2 + Q^2))]/2 with V1 = 1,
P = 0.5, Q = 0.2, X = 0.1 pu, evaluated with mpmath at 40 digits: V2 = 0.97824823061862617640), and the case as loaded.
"""

import importlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

V2 = 0.9782482306186262

ONE_BUS = """function mpc = onebus
mpc.version = '2';
mpc.baseMVA = 100;
mpc.bus = [
	1	3	0	0	0	0	1	1	0	132	1	1.1	0.9;
];
mpc.gen = [
	1	0	0	300	-300	1	100	1	250	10	0	0	0	0	0	0	0	0	0	0	0;
];
mpc.branch = zeros(0, 13);
"""


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


class Mapping(unittest.TestCase):

    def test_the_new_names_are_supported_and_the_unconfirmed_ones_refused(self):
        from gridsim import psspy
        for name in ("bus_data_4", "load_data_6", "machine_data_4", "plant_data_4", "branch_data_3", "two_winding_data_6",
                     "shunt_data", "purgbrn", "purgmac", "purgload", "purgshunt", "abuscplx", "amachcplx", "abrncplx",
                     "inibus", "nxtbus", "inimac", "nxtmac", "inibrn", "nxtbrn"):
            with self.subTest(name=name):
                self.assertIn(name, psspy.SUPPORTED)
                self.assertNotIn(name, psspy.NOT_SUPPORTED)
                self.assertTrue(callable(getattr(psspy, name)))
        for name in ("load_data_7", "extr"):
            with self.subTest(name=name):
                self.assertNotIn(name, psspy.SUPPORTED)
                with self.assertRaises(NotImplementedError):
                    getattr(psspy, name)()
        for name in ("fdns", "fncl", "nsol"):
            self.assertIn(name, psspy.NOT_SUPPORTED)


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class RealHost(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.psspy = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        self.case = Path(self.tmp.name) / "onebus.m"
        self.case.write_text(ONE_BUS, encoding="utf-8")
        self.assertEqual(self.psspy.psseinit(150, cwd=self.tmp.name), 0)
        self.assertEqual(self.psspy.read(0, str(self.case)), 0)

    def tearDown(self):
        self.psspy.close()
        self.tmp.cleanup()

    def _build(self):
        p, _i, _f = self.psspy, self.psspy._i, self.psspy._f
        self.assertEqual(p.bus_data_4(2, 0, [1, 1, 1, 1], [132.0, 1.0, 0.0, 1.1, 0.9, _f, _f], ""), 0)
        self.assertEqual(p.branch_data_3(1, 2, "1", [1, 1, 1, _i, _i, _i], [0.0, 0.1, _f] + [_f] * 9, [_f] * 12, ""), 0)
        self.assertEqual(p.load_data_6(2, "1", [1, 1, 1, 1, 1, 0, 0], [50.0, 20.0, 0, 0, 0, 0, 0, 0], ""), 0)

    def test_built_by_psspy_solves_to_the_closed_form(self):
        p = self.psspy
        self._build()
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.solved(), 0)
        ierr, v = p.busdat(2, "PU")
        self.assertEqual(ierr, 0)
        self.assertAlmostEqual(v, V2, delta=1e-9)
        ierr, x = p.abuscplx(-1, 1, "VOLTAGE")
        self.assertEqual(ierr, 0)
        self.assertAlmostEqual(abs(x[0][1]), V2, delta=1e-9)
        ierr, rx = p.abrncplx(-1, 1, 1, 3, 1, "RX")
        self.assertEqual((ierr, rx), (0, [[complex(0.0, 0.1)]]))
        # One load per bus: a second id is ierr 9 and changes nothing.
        self.assertEqual(p.load_data_6(2, "2", [1], [10.0]), 9)
        self.assertEqual(p.S.case.loads["2"].pd, 50.0)
        # Changing the same load.
        self.assertEqual(p.load_data_6(2, "1", [p._i], [60.0]), 0)
        self.assertEqual(p.S.case.loads["2"].pd, 60.0)

    def test_purgbrn_leaves_the_island_that_the_solve_refuses(self):
        p = self.psspy
        self._build()
        self.assertEqual(p.purgbrn(1, 2, "1"), 0)
        self.assertEqual(p.purgbrn(1, 2, "1"), 2)   # PSS/E's "branch not found"
        # fnsl runs (ierr 0, as in PSS/E) and the solve does not converge: bus 2 is an island with no slack, so it has no
        # voltage to read.
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.solved(), 1)
        self.assertEqual(p.busdat(2, "PU"), (3, None))
        # A study of the case as it stands is refused, naming the island.
        with self.assertRaises(gridsim.GridSimError) as ctx:
            p.S.case.study("pf")
        self.assertIn("island", str(ctx.exception).lower())

    def test_purge_back_to_the_case_as_loaded(self):
        p = self.psspy
        self._build()
        self.assertEqual(p.purgload(2, "1"), 0)
        self.assertEqual(p.purgbrn(1, 2, "1"), 0)
        case = p.S.case
        case.delete("bus", "2")
        self.assertEqual(case.buses.keys(), ["1"])
        self.assertEqual(case.branches.keys(), [])
        self.assertEqual(case.loads.keys(), [])

    def test_case_delete_with_cascade_and_machine_plant_shunt_calls(self):
        p = self.psspy
        self._build()
        self.assertEqual(p.plant_data_4(2, 0, [0, 0], [1.01, 100.0]), 0)
        self.assertEqual(p.machine_data_4(2, "1", [1], [5.0]), 0)
        case = p.S.case
        self.assertIn("2:1", case.generators)
        self.assertEqual(case.generators["2:1"].vg, 1.01)
        self.assertEqual(p.busint(2, "TYPE"), (0, 2))
        self.assertEqual(p.inimac(2), 0)
        self.assertEqual(p.nxtmac(2), (0, "1"))
        self.assertEqual(p.nxtmac(2), (1, None))
        self.assertEqual(p.shunt_data(2, "1", [1], [0.0, 10.0]), 0)
        self.assertEqual(p.shunt_data(2, "2", [1], [0.0, 1.0]), 9)
        self.assertEqual(p.inibus(0), 0)
        self.assertEqual([p.nxtbus()[1], p.nxtbus()[1], p.nxtbus()[0]], [1, 2, 1])
        self.assertEqual(p.inibrn(2, 2), 0)
        self.assertEqual(p.nxtbrn(2), (0, 1, "1"))
        self.assertEqual(p.nxtbrn(2), (1, 0, "  "))
        self.assertEqual(p.purgmac(1, "1"), 9)   # the last unit at the slack bus
        before = case.revision
        with self.assertRaises(gridsim.GridSimError):
            case.delete("bus", "2")
        self.assertEqual(case.revision, before)
        case.delete("bus", "2", cascade=True)
        kinds = [e["kind"] for e in case.edits() if e["revision"] == case.revision]
        self.assertEqual(kinds, ["branch", "generator", "load", "shunt", "bus"])
        self.assertEqual(case.buses.keys(), ["1"])

    def test_refused_layouts(self):
        p, _f = self.psspy, self.psspy._f
        self.assertEqual(p.bus_data_4(3, 0, [1, 2], [132.0]), 9)                    # area 2: no areas held
        self.assertEqual(p.bus_data_4(3, 0, [1], [132.0, 1.0, 0.0, _f, _f, 1.2]), 9)  # emergency limit
        self.assertEqual(p.bus_data_4(3, 0, [1], [_f]), 2)                           # a new bus needs BASKV
        self.assertEqual(p.bus_data_4(3, 0, [1], [132.0]), 0)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1] + [p._i] * 12 + [2, 1, 1], [0.0, 0.1]), 9)  # CW 2
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1], [0.0, 0.1, _f, 1.05, _f, 3.0, 1.0]), 0)
        case = p.S.case
        row = [r for r in case.branches.rows() if r["key"] == "1-3:1"][0]
        self.assertEqual((row["tap"], row["shift_deg"], row["x"]), (1.05, 3.0, 0.1))
        self.assertEqual(p.load_data_6(3, "1", [1], [10.0, 0.0, 5.0]), 9)            # constant current: not held


if __name__ == "__main__":
    unittest.main()
