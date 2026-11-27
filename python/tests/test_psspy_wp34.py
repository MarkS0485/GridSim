"""RC5 WP34: psspy writerawversion(36) and a RAWX save. Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The mapping test needs nothing but Python; the others drive the real CLI and skip, saying so, when none is built.
Oracle: the pf verb (GridSim.run) on the revision 35 file written from the same case; the revision 36 and RAWX files must
give the same bus voltages and angles to the bit (the writers express the same records, and the readers rebuild the same
revision 35 records from them).
"""

import importlib
import os
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


class Mapping(unittest.TestCase):

    def test_the_docstring_no_longer_limits_writerawversion_to_35(self):
        from gridsim import psspy
        doc = psspy.writerawversion.__doc__
        self.assertNotIn("as RAW 33, 34 or 35 to", doc)
        self.assertIn("33, 34, 35 or 36", doc)


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class RealHost(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.psspy = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        self.gs = gridsim.GridSim(cwd=self.tmp.name)
        self.assertEqual(self.psspy.psseinit(150, cwd=self.tmp.name), 0)
        self.assertEqual(self.psspy.read(0, str(RAW33)), 0)

    def tearDown(self):
        # Stop the rpc host first: it runs with the temporary folder as its working directory, and Windows
        # refuses to delete a folder a live process is in (test_psspy.RealHost does the same).
        self.psspy.close()
        self.tmp.cleanup()

    def _buses(self, path):
        return [(row["bus"], row["vm_pu"], row["va_deg"]) for row in self.gs.pf(path).table("buses")]

    def test_writerawversion_36_is_allowed_and_reads_as_the_35_file(self):
        p = self.psspy
        out35 = os.path.join(self.tmp.name, "w35.raw")
        out36 = os.path.join(self.tmp.name, "w36.raw")
        self.assertEqual(p.writerawversion("35", 0, out35), 0)
        self.assertEqual(p.writerawversion("36", 0, out36), 0)
        with open(out36, encoding="utf-8") as f:
            first = f.readline()
        self.assertIn(", 36, ", first)
        self.assertEqual(self._buses(out36), self._buses(out35))

    def test_other_versions_stay_refused(self):
        p = self.psspy
        out = os.path.join(self.tmp.name, "w.raw")
        self.assertEqual(p.writerawversion("37", 0, out), 9)
        self.assertEqual(p.writerawversion("32", 0, out), 9)
        self.assertEqual(p.writerawversion("36", 1, out), 9)    # out 1 is the report window
        self.assertFalse(os.path.exists(out))

    def test_a_rawx_save_reads_as_the_35_file(self):
        p = self.psspy
        out35 = os.path.join(self.tmp.name, "w35.raw")
        self.assertEqual(p.writerawversion("35", 0, out35), 0)
        outx = os.path.join(self.tmp.name, "w.rawx")
        p.S.case.save(outx, "rawx")
        with open(outx, encoding="utf-8") as f:
            self.assertTrue(f.read().lstrip().startswith("{"))
        self.assertEqual(self._buses(outx), self._buses(out35))


if __name__ == "__main__":
    unittest.main()
