"""Tests for psspy.aareaint and psspy.aareareal (RC5 WP13). Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The case is a three-bus RAW 33 file: bus 1 slack (area 1), bus 2 PV (area 2) with 40 MW, bus 3 PQ (area 2) with
100 MW; line 1-2 (R 0.01, X 0.1) metered at bus 2 (MET 2), line 2-3 lossless. Oracle: hand arithmetic. Area 2's tie is
metered inside it, so its export is the power at bus 2 into the tie: 40 - 100 = -60 MW (the 2-3 line is lossless). Area
1 is metered outside, so its export is +60 MW and the tie loss falls on area 1.
"""

import importlib
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

RAW = """0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, WP13 area check
GridSim WP13 check
Three buses, two areas
1, 'SRC', 132, 3, 1, 1, 1, 1, 0
2, 'GEN', 132, 2, 2, 1, 1, 1, 0
3, 'LOAD', 132, 1, 2, 1, 1, 1, 0
0 / END OF BUS DATA, BEGIN LOAD DATA
3, '1', 1, 2, 1, 100, 0, 0, 0, 0, 0, 1, 1, 0
0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
2, '1', 40, 0, 999, -999, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
1, 2, '1', 0.01, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0, 1, 1
2, 3, '1', 0, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
1, 1, 0, 10, 'WEST'
2, 2, -30, 0.1, 'EAST'
7, 0, 5, 2, 'EMPTY'
0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
0 / END OF INDUCTION MACHINE DATA
Q
"""


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


class AreaFile(unittest.TestCase):

    def test_the_raw_areas_are_read_from_the_file(self):
        from gridsim import psspy
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "areas.raw")
            with open(path, "w", encoding="utf-8") as f:
                f.write(RAW)
            info = psspy._raw_areas(path)
        self.assertEqual(info.bus_area, {1: 1, 2: 2, 3: 2})
        self.assertEqual(info.areas, {1: (1, 0.0, 10.0), 2: (2, -30.0, 0.1), 7: (0, 5.0, 2.0)})
        self.assertEqual(info.metered[(1, 2, "1")], 2)
        self.assertEqual(info.metered[(2, 3, "1")], 2)
        self.assertFalse(info.three_winding)


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class AreaArrays(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.psspy = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        path = os.path.join(self.tmp.name, "areas.raw")
        with open(path, "w", encoding="utf-8") as f:
            f.write(RAW)
        self.assertEqual(self.psspy.psseinit(10, cwd=self.tmp.name), 0)
        self.assertEqual(self.psspy.read(0, path), 0)

    def tearDown(self):
        self.psspy.close()
        self.tmp.cleanup()

    def test_numbers_and_the_refused_unconfirmed_parts(self):
        p = self.psspy
        self.assertEqual(p.aareaint(-1, 1, "NUMBER"), (0, [[1, 2]]))
        # No public source confirms flag 2 or the strings SWING, PDES and PTOL, so each is ierr 9, never guessed.
        self.assertEqual(p.aareaint(-1, 2, "NUMBER")[0], 9)
        self.assertEqual(p.aareaint(-1, 1, ["NUMBER", "SWING"])[0], 9)
        self.assertEqual(p.aareareal(-1, 1, "PDES")[0], 9)
        self.assertEqual(p.aareareal(-1, 1, "PTOL")[0], 9)
        self.assertEqual(p.aareaint(0, 1, "NUMBER")[0], 9)     # a subsystem other than the whole case
        self.assertEqual(p.aareaint(-1, 3, "NUMBER")[0], 9)
        self.assertEqual(p.aareaint(-1, 1, "NOPE")[0], 2)

    def test_pint_is_the_solved_export_at_the_metered_end(self):
        p = self.psspy
        self.assertEqual(p.aareareal(-1, 1, "PINT")[0], 3)     # no solve yet
        self.assertEqual(p.fnsl(), 0)
        ierr, (pint,) = p.aareareal(-1, 1, "PINT")
        self.assertEqual(ierr, 0)
        self.assertAlmostEqual(pint[1], -60.0, delta=1e-6)    # area 2: 40 - 100 at bus 2
        self.assertAlmostEqual(pint[0], 60.0, delta=1e-6)     # area 1: metered outside, so the loss is area 1's


if __name__ == "__main__":
    unittest.main()
