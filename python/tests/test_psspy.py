"""Tests for gridsim.psspy, the psspy-style layer over the object model. Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The mapping-table test needs nothing but Python; the others drive the real CLI and skip, saying so, when none is built.
Oracles: the pf verb (GridSim.run), a ModelSession on the same file, hand arithmetic for the fault admittance, and one
dynamics-study row run in one go.
"""

import importlib
import math
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
RAW33 = ROOT / "tests" / "GridSim.Tests" / "fixtures" / "psse" / "case14.raw"
DYR = ROOT / "tests" / "GridSim.Tests" / "fixtures" / "dynamics-study" / "case14-study.dyr"


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


class Mapping(unittest.TestCase):

    def test_psspy_is_not_imported_with_the_package(self):
        self.assertNotIn("psspy", gridsim.__all__)

    def test_every_mapped_name_imports_and_every_refused_name_raises(self):
        from gridsim import psspy
        for name in psspy.SUPPORTED:
            with self.subTest(name=name):
                self.assertTrue(callable(getattr(psspy, name)))
        for name in list(psspy.NOT_SUPPORTED) + ["accc_parallel_2", "ascc_4", "opf_lp", "some_unknown_call"]:
            with self.subTest(name=name):
                with self.assertRaises(NotImplementedError) as ctx:
                    getattr(psspy, name)()
                self.assertIn(name, str(ctx.exception))
        self.assertEqual(psspy.progress_output(6, "", [0, 0]), 0)
        self.assertEqual((psspy.getdefaultint(), psspy.getdefaultreal(), psspy.getdefaultchar()), (psspy._i, psspy._f, psspy._s))


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class RealHost(unittest.TestCase):

    def setUp(self):
        from gridsim import psspy
        self.psspy = importlib.reload(psspy)
        self.tmp = tempfile.TemporaryDirectory()
        self.assertEqual(self.psspy.psseinit(150, cwd=self.tmp.name), 0)
        self.assertEqual(self.psspy.read(0, str(RAW33)), 0)

    def tearDown(self):
        self.psspy.close()
        self.tmp.cleanup()

    def test_read_fnsl_busdat_equal_the_model_session_and_pf(self):
        p = self.psspy
        self.assertEqual(p.solved(), 3)
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.solved(), 0)
        ierr, vm = p.busdat(1, "PU")
        self.assertEqual(ierr, 0)
        with gridsim.ModelSession(cwd=self.tmp.name) as ms:
            self.assertEqual(ms.open(RAW33).solve().bus(1).vm, vm)
        rows = gridsim.GridSim(cwd=self.tmp.name).pf(str(RAW33)).table("buses")
        self.assertEqual(next(r["vm_pu"] for r in rows if r["bus"] == 1), vm)
        self.assertEqual(p.busdat(99999, "PU")[0], 1)
        self.assertEqual(p.busdat(1, "NOPE")[0], 2)
        self.assertEqual(p.fnsl([1, 0, 0, 0, 0, 0, 0, 0]), 9)   # a non-default option is never ignored silently

    def test_machine_chng_2_with_defaults_changes_only_pg(self):
        p = self.psspy
        key = p._gen_key(2, "1")
        self.assertIsNotNone(key)
        before = p.S.case.generators[key].values()
        self.assertEqual(p.machine_chng_2(2, "1", [p._i] * 6, [55.5] + [p._f] * 16), 0)
        after = p.S.case.generators[key].values()
        self.assertEqual(after["pg"], 55.5)
        self.assertEqual({k: v for k, v in after.items() if k != "pg"}, {k: v for k, v in before.items() if k != "pg"})
        self.assertEqual(p.solved(), 3)
        self.assertEqual(p.machine_chng_2(2, "1", [p._i, 5], None), 9)   # an untested slot
        self.assertEqual(p.S.case.edits()[-1]["attribute"], "pg")

    def test_dist_bus_fault_maps_the_admittance(self):
        p = self.psspy
        self.assertEqual(p.dyre_new([1, 1, 1, 1], str(DYR)), 0)
        self.assertEqual(p.dist_bus_fault(5, 1, 0.0, [0.0, -2.0e9]), 0)
        self.assertEqual(p.events()[-1], {"kind": "busFault", "at": 0.0, "bus": 5})
        self.assertEqual(p.dist_bus_fault(5, 1, 0.0, [0.0, -1000.0]), 0)
        e = p.events()[-1]
        self.assertEqual((e["kind"], e["type"], e["rPu"], e["xPu"]), ("sequenceFault", "threePhase", 0.0, 0.1))   # Y = -10j pu
        self.assertEqual(p.dist_clear_fault(2), 0)
        self.assertEqual(p.events()[-1], {"kind": "faultClear", "at": 0.0, "bus": 5})

    def _oracle(self, events, duration):
        """The dynamics-study row for ``events`` written here by hand (the unedited RAW 33 file and the DYR, named by SHA-256,
        the channels machine 2 angle and bus 4 voltage), run by the gridsim CLI in a subprocess: no psspy code involved."""
        import hashlib
        import json
        import shutil
        import subprocess
        folder = tempfile.mkdtemp(dir=self.tmp.name)
        shutil.copyfile(RAW33, os.path.join(folder, "c.raw"))
        shutil.copyfile(DYR, os.path.join(folder, "c.dyr"))

        def sha(name):
            with open(os.path.join(folder, name), "rb") as f:
                return hashlib.sha256(f.read()).hexdigest()
        study = {
            "schemaVersion": 1,
            "raw": {"path": "c.raw", "sha256": sha("c.raw")},
            "dyr": {"path": "c.dyr", "sha256": sha("c.dyr")},
            "initialisation": {"powerFlow": {"tolerancePu": 1e-8, "maxIterations": 100}, "unmodelled": "refuse"},
            "integration": {"stepSeconds": 0.005, "durationSeconds": duration},
            "criteria": {"slipLimitRad": 2.0 * math.pi, "maxAngleSpreadDeg": 180.0},
            "angleReference": {"bus": 1, "id": "1"},
            "channels": [{"kind": "rotorAngle", "machine": {"bus": 2, "id": "1"}}, {"kind": "busVoltage", "bus": 4}],
            "cases": [{"name": "oracle", "kind": "events", "events": events}],
        }
        with open(os.path.join(folder, "s.json"), "w", encoding="utf-8") as f:
            json.dump(study, f)
        out = subprocess.run(gridsim.find_cli() + ["dynamics-study", "s.json"], capture_output=True, text=True, cwd=folder)
        return json.loads(out.stdout)["cases"][0]["run"]

    def test_a_run_in_steps_equals_a_study_written_by_hand_and_earlier_samples_never_change(self):
        p = self.psspy
        self.assertEqual(p.dyre_new([1, 1, 1, 1], str(DYR)), 0)
        self.assertEqual(p.machine_array_channel([1, 1, 2], "1"), 0)
        self.assertEqual(p.voltage_channel([-1, -1, -1, 4]), 0)
        self.assertEqual(p.machine_array_channel([3, 2, 2], "1"), 9)   # PELEC is not traced
        self.assertEqual(p.strt(0, ""), 0)                              # strt(option, outfile)
        self.assertEqual(p.run(0, 0.1), 0)
        quiet = p.channels()
        # No event yet: only the time series, t = 0 .. 0.1, and nothing about the stand-in event the study needed.
        self.assertEqual(quiet["kind"], "no events")
        self.assertNotIn("disposition", quiet)
        self.assertEqual(len(quiet["run"]["timeSeconds"]), 21)
        self.assertEqual(quiet["run"]["timeSeconds"][-1], 0.1)
        p.dist_bus_fault(4, 1, 0.0, [0.0, -2.0e11])
        self.assertEqual(p.run(0, 0.2), 0)
        faulted = p.channels()["run"]
        p.dist_clear_fault(1)
        self.assertEqual(p.run(0, 1.0), 0)
        final = p.channels()["run"]
        self.assertEqual([e["kind"] for e in p.events()], ["busFault", "faultClear"])
        self.assertEqual(len(final["timeSeconds"]), 201)

        # 1. The final row is the study written by hand with the same two events, bit for bit.
        oracle = self._oracle([{"kind": "busFault", "at": 0.1, "bus": 4}, {"kind": "faultClear", "at": 0.2, "bus": 4}], 1.0)
        self.assertEqual(final["timeSeconds"], oracle["timeSeconds"])
        self.assertEqual([c["values"] for c in final["channels"]], [c["values"] for c in oracle["channels"]])

        # 2. A later event never changes an earlier sample: every sample before 0.2 s of the faulted run, and every
        #    sample before 0.1 s of the quiet window, is the final run's.
        for earlier, before in ((faulted, 40), (quiet["run"], 20)):
            self.assertEqual(earlier["timeSeconds"][:before], final["timeSeconds"][:before])
            for a, b in zip(earlier["channels"], final["channels"]):
                self.assertEqual(a["values"][:before], b["values"][:before])

        # 3. The quiet window is undisturbed to its last sample: a hand-written study whose only event lies far beyond.
        far = self._oracle([{"kind": "branchTrip", "at": 0.5, "branch": {"from": 1, "to": 2, "circuit": "1"}}], 0.5)
        self.assertEqual([c["values"] for c in quiet["run"]["channels"]], [c["values"][:21] for c in far["channels"]])

    def test_dist_bus_fault_uses_the_case_base(self):
        p = self.psspy
        raw = os.path.join(self.tmp.name, "base50.raw")
        with open(RAW33, encoding="utf-8") as f:
            text = f.read()
        self.assertTrue(text.startswith("0, 100, 33,"))
        with open(raw, "w", encoding="utf-8") as f:
            f.write("0, 50, 33," + text[len("0, 100, 33,"):])
        self.assertEqual(p.read(0, raw), 0)
        self.assertEqual(p.dyre_new([1, 1, 1, 1], str(DYR)), 0)
        self.assertEqual(p.dist_bus_fault(5, 1, 0.0, [0.0, -1000.0]), 0)
        e = p.events()[-1]
        # Y = -1000j MVA / 50 MVA = -20j pu, so Zf = 1/Y = 0.05j pu (hand arithmetic; 0.05 is the exact quotient 20/400).
        self.assertEqual((e["rPu"], e["xPu"]), (0.0, 0.05))
        self.assertEqual(p.dist_bus_fault(5, 2, 0.0, [0.0, -1.0]), 9)   # mhos: not mapped

    def test_load_calls_follow_the_psspy_strings_and_refuse_a_bus_with_two_loads(self):
        p = self.psspy
        # case14.raw bus 2 holds one load, id '1', PL 21.7, QL 12.7, constant power (the file's own record).
        self.assertEqual(p.loddt2(2, "1", "TOTAL", "ACT"), (3, None))   # ACT needs a solve
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.loddt2(2, "1", "TOTAL", "ACT"), (0, complex(21.7, 12.7)))
        self.assertEqual(p.loddt2(2, "1", "MVA", "NOM"), (0, complex(21.7, 12.7)))
        self.assertEqual(p.loddt2(2, "1", "IL", "NOM"), (0, complex(0.0, 0.0)))
        self.assertEqual(p.loddt2(2, "1", "MVA", "O_ACT"), (9, None))
        self.assertEqual(p.loddt2(2, "1", "MVA", "TOTAL"), (2, None))     # the old, wrong pair
        self.assertEqual(p.loddt2(2, "2", "MVA", "NOM"), (1, None))
        # load_chng_5 with the fifth lodtyp argument: accepted blank, refused otherwise.
        self.assertEqual(p.load_chng_5(3, "1", [p._i] * 7, [90.0] + [p._f] * 7, ""), 0)
        self.assertEqual(p.S.case.loads["3"].pd, 90.0)
        self.assertEqual(p.load_chng_5(3, "1", [p._i] * 7, [91.0] + [p._f] * 7, "INDUSTRIAL"), 9)

        # A second load record at bus 2: id '1' no longer names the bus demand.
        raw = os.path.join(self.tmp.name, "twoloads.raw")
        with open(RAW33, encoding="utf-8") as f:
            text = f.read()
        line = "2, '1', 1, 1, 1, 21.7, 12.7, 0, 0, 0, 0, 1, 1, 0\n"
        self.assertIn(line, text)
        text = text.replace(line, line + "2, '2', 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 0\n")
        with open(raw, "w", encoding="utf-8") as f:
            f.write(text)
        self.assertEqual(p.read(0, raw), 0)
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.loddt2(2, "1", "MVA", "NOM"), (9, None))
        self.assertEqual(p.load_chng_5(2, "1", [p._i] * 7, [20.0] + [p._f] * 7), 9)
        self.assertEqual(p.S.case.edits(), [])

    def test_machine_values_are_the_solved_output(self):
        p = self.psspy
        self.assertEqual(p.macdat(1, "1", "P"), (3, None))
        self.assertEqual(p.fnsl(), 0)
        rows = gridsim.GridSim(cwd=self.tmp.name).pf(str(RAW33)).table("buses")
        inj = {r["bus"]: (r["p_inj_mw"], r["q_inj_mvar"]) for r in rows}
        # Bus 1 has no load: the swing unit's P and Q are the bus injection exactly. Bus 2 carries 21.7 + j12.7 MW/MVAr
        # of constant-power load: its unit gives the injection plus that load.
        self.assertEqual(p.macdat(1, "1", "P"), (0, inj[1][0]))
        self.assertEqual(p.macdat(1, "1", "Q"), (0, inj[1][1]))
        self.assertEqual(p.macdat(2, "1", "P"), (0, inj[2][0] + 21.7))
        self.assertEqual(p.macdat(2, "1", "Q"), (0, inj[2][1] + 12.7))
        self.assertEqual(p.macdat(1, "1", "PMAX"), (0, 332.4))         # the file's PT
        ierr, (pgen,) = p.amachreal(-1, 1, "PGEN")
        self.assertEqual(ierr, 0)
        self.assertEqual(pgen[:2], [inj[1][0], inj[2][0] + 21.7])
        self.assertNotEqual(pgen[0], 232.4)                            # not the case's PG set-point

    def test_fnsl_returns_0_when_the_solve_does_not_converge(self):
        p = self.psspy
        self.assertEqual(p.load_chng_5(3, "1", [p._i] * 7, [1.0e5] + [p._f] * 7), 0)
        self.assertEqual(p.fnsl(), 0)
        self.assertEqual(p.solved(), 1)

    def test_writing_and_unconfirmed_layouts(self):
        p = self.psspy
        out = os.path.join(self.tmp.name, "w.raw")
        self.assertEqual(p.writerawversion("33", 1, out), 9)          # out 1 is the report window
        self.assertEqual(p.writerawversion("33", 0, out), 0)
        self.assertTrue(os.path.isfile(out))
        self.assertEqual(p.rawd_2(0, 1, [1, 1, 1, 0, 0, 0, 0], 0, out), 9)   # positional status/opt: unconfirmed
        self.assertEqual(p.rawd_2(0, 1, out=0, ofile=os.path.join(self.tmp.name, "r.raw")), 0)
        self.assertEqual(p.readrawx(out), 9)
        self.assertEqual(p.fnsl(), 0)
        ierr, flow = p.brnflo(1, 2, "1")
        self.assertEqual(p.brnmsc(1, 2, "1", "P"), (0, flow.real))
        self.assertEqual(p.brnmsc(1, 2, "1", "PCTRTA"), (9, None))
        self.assertEqual(p.brnmsc(1, 2, "1", "MW"), (2, None))
        self.assertEqual(p.abrnint(-1, 1, 1, 1, 1, "FROMNUMBER")[0], 9)   # lines only: GridSim cannot tell them apart
        ierr, (frm,) = p.abrnint(-1, 1, 1, 3, 1, "FROMNUMBER")
        self.assertEqual((ierr, frm[0]), (0, 1))
        self.assertEqual(p.abusint(-1, 2, "NUMBER")[1][0][:3], [1, 2, 3])

    def test_an_edited_case_is_checked_through_raw_33_before_a_dynamic_run(self):
        p = self.psspy
        self.assertEqual(p.dyre_new([1, 1, 1, 1], str(DYR)), 0)
        self.assertEqual(p.machine_chng_2(2, "1", None, [45.0]), 0)
        self.assertEqual(p.strt_2([0, 0], ""), 0)     # the edited case survives the RAW 33 round trip
        self.assertIsNone(p.last_error())
        self.assertEqual(p.load_chng_5(3, "1", [0]), 0)   # an out-of-service load: its kept set-point is not in the RAW
        self.assertEqual(p.strt_2([0, 0], ""), 9)
        self.assertIn("does not survive a RAW 33 write and read unchanged", p.last_error())

if __name__ == "__main__":
    unittest.main()
