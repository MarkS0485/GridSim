"""Tests for case ownership (Session, ModelSession) and Session.run_verb --out. Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
They drive the real CLI and skip, saying so, when none is built. Oracles: the session a case was loaded by, the host's
own refusal of a handle it never issued, and the one-shot GridSim.run on the same report.
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class Level1Handles(unittest.TestCase):
    """Level 1 handles are case-1, case-2, ... in every host, so the CaseHandle carries its session and host instance."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def test_two_simultaneous_sessions_refuse_each_others_handles(self):
        with gridsim.Session(cwd=self.tmp.name) as a, gridsim.Session(cwd=self.tmp.name) as b:
            ha = a.load("case9")
            hb = b.load("case14")
            self.assertEqual(ha.handle, hb.handle)   # both "case-1": the id alone cannot tell them apart
            with self.assertRaises(gridsim.GridSimError) as ctx:
                b.solve(ha)
            self.assertIn("another session", str(ctx.exception))
            with self.assertRaises(gridsim.GridSimError):
                b.release(ha)
            self.assertFalse(ha.released)              # a refused release marks nothing
            self.assertEqual(b.stats()["handles"], 1)  # and sends nothing: B still holds its own case
            self.assertEqual(len(a.solve(ha).bus), 9)
            self.assertEqual(len(hb.solve().bus), 14)
            ha.release()
            self.assertTrue(ha.released)
            self.assertEqual(a.stats()["handles"], 0)

    def test_a_handle_from_before_a_restart_is_refused(self):
        s = gridsim.Session(cwd=self.tmp.name)
        try:
            old = s.load("case9")
            s.close()
            s.start()
            new = s.load("case14")
            self.assertEqual(old.handle, new.handle)   # the new host numbers from case-1 again
            with self.assertRaises(gridsim.GridSimError) as ctx:
                old.solve()                            # without the check this would solve case14
            self.assertIn("restarted", str(ctx.exception))
            with self.assertRaises(gridsim.GridSimError):
                old.release()
            self.assertFalse(old.released)
            self.assertEqual(len(new.solve().bus), 14)
            self.assertEqual(s.stats()["handles"], 1)
        finally:
            s.close()

    def test_a_plain_id_string_is_still_accepted(self):
        with gridsim.Session(cwd=self.tmp.name) as s:
            h = s.load("case9")
            self.assertEqual(len(s.solve(h.handle).bus), 9)
            s.release(h.handle)
            with self.assertRaises(gridsim.GridSimError):
                s.solve(h.handle)


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class Level2Cases(unittest.TestCase):
    """Level 2 handles carry the host's token; a Case carries its session and host instance too."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def test_two_simultaneous_model_sessions(self):
        with gridsim.ModelSession(cwd=self.tmp.name) as a, gridsim.ModelSession(cwd=self.tmp.name) as b:
            self.assertNotEqual(a.host_token, b.host_token)
            ca, cb = a.open("case9"), b.open("case14")
            self.assertTrue(ca.handle.startswith(f"case-{a.host_token}-"))
            with self.assertRaises(gridsim.GridSimError) as ctx:
                b.solve(ca)
            self.assertIn("another session", str(ctx.exception))
            with self.assertRaises(gridsim.GridSimError):
                b.release(ca)
            self.assertFalse(ca.released)
            # The bare id reaches B's host, which refuses it as not its own.
            with self.assertRaises(gridsim.GridSimError) as ctx:
                b.request("solve", handle=ca.handle)
            self.assertIn("not issued by this host", str(ctx.exception))
            self.assertTrue(ca.solve().converged)
            self.assertTrue(cb.solve().converged)

    def test_a_case_from_before_a_restart_is_refused(self):
        ms = gridsim.ModelSession(cwd=self.tmp.name)
        try:
            case = ms.open("case9")
            ms.close()
            ms.start()
            with self.assertRaises(gridsim.GridSimError):
                case.solve()
            with self.assertRaises(gridsim.GridSimError):
                case.loads["5"].pd
            with self.assertRaises(gridsim.GridSimError):
                case.release()
            self.assertFalse(case.released)
            self.assertTrue(ms.open("case9").solve().converged)
        finally:
            ms.close()


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class RunVerbOut(unittest.TestCase):
    """Session.run_verb takes --out for the verbs whose own output it names (OWN_OUT_VERBS), as GridSim.run does."""

    def test_the_same_report_through_both_entry_points(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = os.path.join(tmp, "pf.json")
            subprocess.run(gridsim.find_cli() + ["pf", "case9", "--json", result], check=True, capture_output=True, cwd=tmp)
            gs = gridsim.GridSim(cwd=tmp)
            # A template written by hand (a gridsim-report-template/1 table bound by an RFC 6901 pointer).
            template = os.path.join(tmp, "template.json")
            with open(template, "w", encoding="utf-8") as f:
                json.dump({"schema": "gridsim-report-template/1", "title": "case9 buses", "sections": [{"heading": "Buses", "blocks": [
                    {"type": "table", "title": "Buses", "path": "/tables/buses"}]}]}, f)
            one = os.path.join(tmp, "one.md")
            two = os.path.join(tmp, "two.md")
            a = gs.run("report", template, result, "--out", one)
            with gridsim.Session(cwd=tmp) as s:
                b = s.run_verb("report", template, result, "--out", two)
                with self.assertRaises(ValueError):
                    s.run_verb("pf", "case9", "--out", os.path.join(tmp, "x"))   # not an OWN_OUT verb: still refused
                with self.assertRaises(ValueError):
                    s.run_verb("report", template, result, "--json", os.path.join(tmp, "x"))
            self.assertEqual((a.verb, a.exit_code), (b.verb, b.exit_code))
            with open(one, "rb") as f1, open(two, "rb") as f2:
                self.assertEqual(f1.read(), f2.read())


if __name__ == "__main__":
    unittest.main()
