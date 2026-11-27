"""RC5 WP15 review fixes: the psspy deletes, complex arrays and inimac return PSS/E's own ierr codes, and
two_winding_data_6 honours WN1BUS. Standard library unittest; the tests drive the real CLI and skip, saying so, when none
is built.

Oracle: the API manual's text as excerpted in the PowerMCP repository (Power-Agent/PowerMCP,
PSSE/psspy_command_json/<name>.json), error_codes of purgbrn, purgmac, purgload, purgshunt, abuscplx, amachcplx,
abrncplx and inimac, and INTGAR(9) of two_winding_data_6 ("WN1BUS, winding one side bus number (IBUS or JBUS)").
"""

import importlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

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


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class ApiCodes(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.p = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        case = Path(self.tmp.name) / "onebus.m"
        case.write_text(ONE_BUS, encoding="utf-8")
        self.assertEqual(self.p.psseinit(150, cwd=self.tmp.name), 0)
        self.assertEqual(self.p.read(0, str(case)), 0)
        p, _i, _f = self.p, self.p._i, self.p._f
        # Bus 2 (PQ) joined to bus 1 by a line, with load '1'.
        self.assertEqual(p.bus_data_4(2, 0, [1, 1, 1, 1], [132.0, 1.0, 0.0, 1.1, 0.9, _f, _f], ""), 0)
        self.assertEqual(p.branch_data_3(1, 2, "1", [1, 1, 1, _i, _i, _i], [0.0, 0.1, _f] + [_f] * 9, [_f] * 12, ""), 0)
        self.assertEqual(p.load_data_6(2, "1", [1, 1, 1, 1, 1, 0, 0], [50.0, 20.0, 0, 0, 0, 0, 0, 0], ""), 0)

    def tearDown(self):
        self.p.close()
        self.tmp.cleanup()

    def test_purgbrn(self):
        p = self.p
        self.assertEqual(p.purgbrn(1, 7, "1"), 1)   # bus not found
        self.assertEqual(p.purgbrn(1, 2, "9"), 2)   # branch not found
        self.assertEqual(p.purgbrn(1, 2, "1"), 0)
        self.assertEqual(p.purgbrn(1, 2, "1"), 2)

    def test_purgmac(self):
        p = self.p
        self.assertEqual(p.purgmac(7, "1"), 1)      # bus not found
        self.assertEqual(p.purgmac(2, "1"), 2)      # no machines at bus
        self.assertEqual(p.purgmac(1, "5"), 3)      # machine not found
        self.assertEqual(p.purgmac(1, "1"), 9)      # GridSim: the last unit at the slack bus

    def test_purgload(self):
        p = self.p
        self.assertEqual(p.purgload(7, "1"), 1)     # bus not found
        self.assertEqual(p.purgload(1, "1"), 2)     # no loads at bus
        self.assertEqual(p.purgload(2, "2"), 3)     # load not found
        self.assertEqual(p.purgload(2, "1"), 0)
        self.assertEqual(p.purgload(2, "1"), 2)

    def test_purgshunt(self):
        p = self.p
        self.assertEqual(p.purgshunt(7, "1"), 1)    # bus not found
        self.assertEqual(p.purgshunt(2, "1"), 2)    # no fixed shunts at bus
        self.assertEqual(p.shunt_data(2, "1", [1], [0.0, 10.0]), 0)
        self.assertEqual(p.purgshunt(2, "2"), 3)    # shunt not found
        self.assertEqual(p.purgshunt(2, "1"), 0)
        self.assertEqual(p.purgshunt(2, "1"), 2)

    def test_abuscplx(self):
        p = self.p
        self.assertEqual(p.abuscplx(12, 1, "VOLTAGE"), (2, None))     # invalid SID
        self.assertEqual(p.abuscplx(-1, 3, "VOLTAGE"), (3, None))     # invalid FLAG
        self.assertEqual(p.abuscplx(-1, 1, "NOPE"), (6, None))        # invalid STRING
        self.assertEqual(p.abuscplx(-1, 1, "SHUNTACT"), (9, None))    # GridSim: a valid string it does not map
        self.assertEqual(p.abuscplx(0, 1, "VOLTAGE"), (9, None))      # GridSim: a subsystem
        self.assertEqual(p.abuscplx(-1, 1, "VOLTAGE"), (9, None))     # GridSim: not solved
        self.assertEqual(p.fnsl(), 0)
        ierr, x = p.abuscplx(-1, 1, "VOLTAGE")
        self.assertEqual((ierr, len(x[0])), (0, 2))

    def test_amachcplx(self):
        p = self.p
        self.assertEqual(p.amachcplx(12, 1, "PQGEN"), (2, None))      # invalid SID
        self.assertEqual(p.amachcplx(-1, 5, "PQGEN"), (3, None))      # invalid FLAG
        self.assertEqual(p.amachcplx(-1, 1, "NOPE"), (6, None))       # invalid STRING
        self.assertEqual(p.amachcplx(-1, 1, "ZSORCE"), (9, None))     # GridSim: a valid string it does not map
        self.assertEqual(p.amachcplx(-1, 1, "PQGEN"), (9, None))      # GridSim: not solved
        self.assertEqual(p.fnsl(), 0)
        ierr, pq = p.amachcplx(-1, 1, "PQGEN")
        self.assertEqual((ierr, len(pq[0])), (0, 1))

    def test_abrncplx(self):
        p = self.p
        self.assertEqual(p.abrncplx(12, 1, 1, 3, 1, "RX"), (2, None))   # invalid SID
        self.assertEqual(p.abrncplx(-1, 1, 1, 7, 1, "RX"), (5, None))   # invalid FLAG
        self.assertEqual(p.abrncplx(-1, 1, 1, 3, 3, "RX"), (6, None))   # invalid ENTRY
        self.assertEqual(p.abrncplx(-1, 1, 1, 3, 1, "NOPE"), (9, None))  # invalid STRING
        self.assertEqual(p.abrncplx(-1, 1, 1, 3, 1, "PQ"), (11, None))   # GridSim: a valid string it does not map
        self.assertEqual(p.abrncplx(-1, 1, 1, 1, 1, "RX"), (11, None))   # GridSim: flag 1 (lines only)
        self.assertEqual(p.abrncplx(-1, 1, 1, 3, 2, "RX"), (11, None))   # GridSim: entry 2
        self.assertEqual(p.abrncplx(0, 1, 1, 3, 1, "RX"), (11, None))    # GridSim: a subsystem
        self.assertEqual(p.abrncplx(-1, 1, 1, 3, 1, "RX"), (0, [[complex(0.0, 0.1)]]))

    def test_inimac(self):
        p = self.p
        self.assertEqual(p.inimac(7), 1)            # bus not found
        self.assertEqual(p.inimac(2), 2)            # bus 2 is type 1: not a generator bus
        self.assertEqual(p.inimac(1), 0)
        self.assertEqual(p.nxtmac(1), (0, "1"))

    def test_two_winding_data_6_wn1bus(self):
        p, _i, _f = self.p, self.p._i, self.p._f
        self.assertEqual(p.bus_data_4(3, 0, [1], [33.0]), 0)
        real = [0.0, 0.1, _f, 1.05, _f, 3.0, 1.0]

        def ig(wn1):
            return [1] + [_i] * 7 + [wn1]

        self.assertEqual(p.two_winding_data_6(1, 3, "1", ig(2), real), 9)   # neither end
        # WN1BUS = JBUS: winding one, its ratio and its angle, sit at bus 3, so bus 3 is the from bus.
        self.assertEqual(p.two_winding_data_6(1, 3, "1", ig(3), real), 0)
        case = p.S.case
        self.assertNotIn("1-3:1", case.branches)
        row = [r for r in case.branches.rows() if r["key"] == "3-1:1"][0]
        self.assertEqual((row["tap"], row["shift_deg"], row["x"]), (1.05, 3.0, 0.1))
        # The held transformer's winding one is bus 3: a call that puts it at bus 1 is refused and changes nothing.
        self.assertEqual(p.two_winding_data_6(1, 3, "1", ig(1), real), 9)
        row = [r for r in case.branches.rows() if r["key"] == "3-1:1"][0]
        self.assertEqual(row["tap"], 1.05)
        # Without WN1BUS the held transformer keeps its stored winding one (bus 3), whichever order the call names the
        # ends in (a defaulted WN1BUS is "no input" in the PSS/E API, getdefaultint): WINDV1 = 1.08 and ANG1 = 2.0 go to
        # bus 3's side.
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1], [_f, _f, _f, 1.08, _f, 2.0, 1.0]), 0)
        row = [r for r in case.branches.rows() if r["key"] == "3-1:1"][0]
        self.assertEqual((row["tap"], row["shift_deg"]), (1.08, 2.0))
        self.assertNotIn("1-3:1", case.branches)
        # Named from its winding-one side, the same transformer can be changed; R and X alone need no side.
        self.assertEqual(p.two_winding_data_6(3, 1, "1", [1], [_f, _f, _f, 1.1, _f, _f, 1.0]), 0)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1], [0.01, 0.2]), 0)
        row = [r for r in case.branches.rows() if r["key"] == "3-1:1"][0]
        self.assertEqual((row["tap"], row["r"], row["x"]), (1.1, 0.01, 0.2))

    def test_two_winding_data_6_metbus(self):
        # METBUS (INTGAR(2)) must be one of the two ends, as branch_data_3 checks it.
        p, _i = self.p, self.p._i
        self.assertEqual(p.bus_data_4(3, 0, [1], [33.0]), 0)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1, 2], [0.0, 0.1]), 9)   # bus 2 is neither end
        self.assertNotIn("1-3:1", p.S.case.branches)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1, 3], [0.0, 0.1]), 0)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1, 1], [0.0, 0.2]), 0)
        self.assertEqual(p.two_winding_data_6(1, 3, "1", [1, _i], [0.0, 0.2]), 0)


if __name__ == "__main__":
    unittest.main()
