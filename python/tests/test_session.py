"""Tests for gridsim.Session (the gridsim rpc host, protocol gridsim-rpc/1). Standard library unittest.

Run from the repository root:  python -m unittest discover -s python/tests
The tests that drive the real CLI skip, saying so, when no built CLI is found; the stand-in host tests need nothing but
Python. Oracles are the existing subprocess path (GridSim.run) wherever there is one.
"""

import json
import os
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402


class StandInHost(unittest.TestCase):
    """The session against stand-in hosts: a host that crashes, and one that breaks the protocol."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def _host(self, name, body):
        path = os.path.join(self.tmp.name, name)
        with open(path, "w", encoding="utf-8") as f:
            f.write(textwrap.dedent(body))
        return [sys.executable, path]

    def test_a_host_that_crashes_before_answering_raises_session_error_with_its_exit_code(self):
        cli = self._host("crash.py", """
            import sys
            sys.stdin.readline()
            print("boom: host is going down", file=sys.stderr)
            sys.exit(7)
        """)
        with self.assertRaises(gridsim.SessionError) as ctx:
            gridsim.Session(cli=cli)
        self.assertEqual(ctx.exception.exit_code, 7)
        self.assertIn("boom: host is going down", str(ctx.exception))

    def test_earlier_responses_stay_intact_when_the_host_dies_later(self):
        cli = self._host("dies.py", """
            import sys, json
            hello = json.loads(sys.stdin.readline())
            print(json.dumps({"protocol": "gridsim-rpc/1", "id": hello["id"], "op": "hello", "status": "ok", "exitCode": 0,
                              "case_sha256": None, "result": {"protocol": "gridsim-rpc/1", "tool": "gridsim test", "operations": []}}), flush=True)
            first = json.loads(sys.stdin.readline())
            print(json.dumps({"protocol": "gridsim-rpc/1", "id": first["id"], "op": "stats", "status": "ok", "exitCode": 0,
                              "case_sha256": None, "result": {"loads": 3}}), flush=True)
            sys.stdin.readline()
            sys.exit(9)
        """)
        sess = gridsim.Session(cli=cli)
        kept = sess.stats()
        with self.assertRaises(gridsim.SessionError) as ctx:
            sess.stats()
        self.assertEqual(ctx.exception.exit_code, 9)
        self.assertEqual(kept, {"loads": 3})
        with self.assertRaises(gridsim.SessionError):
            sess.stats()   # the session is closed now
        sess.close()

    def test_a_host_of_another_protocol_is_refused(self):
        cli = self._host("other.py", """
            import sys, json
            hello = json.loads(sys.stdin.readline())
            print(json.dumps({"protocol": "gridsim-rpc/2", "id": hello["id"], "op": "hello", "status": "ok", "exitCode": 0,
                              "case_sha256": None, "result": {"protocol": "gridsim-rpc/2", "tool": "x", "operations": []}}), flush=True)
            sys.stdin.readline()
        """)
        with self.assertRaises(gridsim.SessionError):
            gridsim.Session(cli=cli)

    def test_a_missing_cli_is_refused(self):
        with self.assertRaises(gridsim.GridSimError):
            gridsim.Session(cli=os.path.join(self.tmp.name, "no-such-gridsim"))


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


def _without_result_path(args):
    """A subprocess-path document's args without the closing ``--json <file>`` the wrapper adds."""
    return args[:-2] if len(args) >= 2 and args[-2] == "--json" else args


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (set GRIDSIM_CLI or build src/GridSim.Cli)")
class RealHost(unittest.TestCase):
    """The session against the real CLI."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.gs = gridsim.GridSim(cwd=cls.tmp.name)
        cls.sess = gridsim.Session(cwd=cls.tmp.name)

    @classmethod
    def tearDownClass(cls):
        cls.sess.close()
        cls.tmp.cleanup()

    def test_hello_gives_the_tool_and_protocol(self):
        self.assertTrue(self.sess.tool.startswith("gridsim "))
        self.assertEqual(gridsim.PROTOCOL, "gridsim-rpc/1")

    def test_run_verb_gives_the_document_the_subprocess_path_gives(self):
        for verb, args in (("pf", ["case14"]), ("dcpf", ["case14"]), ("ptdf", ["case14", "--bus", "5"]),
                           ("fault", ["case9", "--bus", "5"]), ("nminus1", ["case9"]), ("lodf", ["case14", "--branch", "3"])):
            with self.subTest(verb=verb):
                a = self.gs.run(verb, *args)
                b = self.sess.run_verb(verb, *args)
                self.assertEqual((a.verb, a.exit_code, a.tool), (b.verb, b.exit_code, b.tool))
                self.assertEqual(a.args[:-2], b.args)
                self.assertEqual(json.dumps(a.document["summary"]), json.dumps(b.document["summary"]))
                self.assertEqual(json.dumps(a.document["tables"]), json.dumps(b.document["tables"]))
                self.assertEqual(a.stdout, b.stdout.replace("\r\n", "\n"))  # text mode of subprocess.run

    def test_run_verb_keeps_the_exit_code_of_a_finished_failing_result(self):
        a = self.gs.run("nminus1", "case9")
        b = self.sess.run_verb("nminus1", "case9")
        self.assertEqual(a.exit_code, b.exit_code)

    def test_run_verb_errors_map_like_the_subprocess_path(self):
        with self.assertRaises(gridsim.GridSimError) as ctx:
            self.sess.run_verb("pf", "no-such-case")
        self.assertEqual(ctx.exception.exit_code, 2)
        with self.assertRaises(ValueError):
            self.sess.run_verb("pf", "case9", "--json", "x.json")
        with self.assertRaises(ValueError):
            self.sess.run_verb("trip", "case9")
        self.assertEqual(self.sess.run_verb("n-1", "case9").verb, "nminus1")

    def test_solve_is_bit_identical_to_the_pf_verb(self):
        h = self.sess.load("case9")
        try:
            pf = self.gs.pf("case9")
            s = h.solve()
            self.assertTrue(s.ok and s.converged)
            self.assertEqual(s.iterations, pf.summary["iterations"])
            self.assertEqual(s.loss_mw.hex(), pf.summary["loss_mw"].hex())
            rows = pf.table("buses")
            self.assertEqual(s.bus, [r["bus"] for r in rows])
            for column in ("vm_pu", "va_deg", "p_inj_mw", "q_inj_mvar"):
                self.assertEqual([float(v).hex() for v in getattr(s, column)], [float(r[column]).hex() for r in rows], column)
            self.assertEqual(len(h.sha256), 64)
            self.assertEqual(s.case_sha256, h.sha256)
        finally:
            h.release()

    def test_one_handle_serves_a_thousand_solves_from_one_load(self):
        before = self.sess.stats()
        h = self.sess.load("case9")
        first = h.solve()
        for _ in range(999):
            last = h.solve()
        self.assertEqual([v.hex() for v in last.vm_pu], [v.hex() for v in first.vm_pu])
        self.assertEqual(last.loss_mw.hex(), first.loss_mw.hex())
        after = self.sess.stats()
        self.assertEqual(after["loads"] - before["loads"], 1)
        self.assertEqual(after["solves"] - before["solves"], 1000)
        h.release()
        self.assertEqual(self.sess.stats()["handles"], before["handles"])

    def test_numpy_arrays_when_numpy_is_installed(self):
        try:
            import numpy  # noqa: F401
        except ImportError:
            self.skipTest("numpy is not installed")
        h = self.sess.load("case9")
        s = h.solve()
        self.assertEqual(s.array("vm_pu").shape, (9,))
        with self.assertRaises(KeyError):
            s.array("nonsense")
        h.release()

    def test_bad_handle_and_unknown_case_are_errors_with_the_cli_exit_codes(self):
        with self.assertRaises(gridsim.GridSimError) as ctx:
            self.sess.solve("case-999")
        self.assertEqual(ctx.exception.exit_code, 2)
        with self.assertRaises(gridsim.GridSimError) as ctx:
            self.sess.load("no-such-case")
        self.assertEqual(ctx.exception.exit_code, 2)
        with self.assertRaises(gridsim.GridSimError):
            self.sess.request("frobnicate")
        # the session carries on after errors
        self.assertTrue(self.sess.load("case9").solve().converged)

    def test_a_released_handle_is_gone(self):
        h = self.sess.load("case9")
        h.release()
        h.release()   # a second call does nothing
        with self.assertRaises(gridsim.GridSimError):
            h.solve()

    def test_two_sessions_give_the_same_bytes(self):
        def script(session):
            h = session.load("case14")
            return json.dumps([h.sha256, h.solve().vm_pu, session.run_verb("dcpf", "case14").document["tables"]])
        with gridsim.Session(cwd=self.tmp.name) as other:
            self.assertEqual(script(self.sess), script(other))

    def test_line_constants_matches_the_verb_and_the_closed_form(self):
        geometry = {"schema": "gridsim-lineconstants/1", "method": "perfect-ground", "frequency_hz": 50,
                    "conductors": [{"name": "A", "role": "phase", "x_m": 0.0, "height_m": 10.0, "resistance_ohm_per_km": 0.0,
                                    "gmr_m": 0.01, "radius_m": 0.01}]}
        r = self.sess.line_constants(geometry)
        x = r.table("phase_z")[0]["x_ohm_per_km"]
        import math
        expected = 2 * math.pi * 50 * 2e-7 * math.log(2 * 10.0 / 0.01) * 1000.0
        self.assertAlmostEqual(x, expected, delta=1e-9 * expected)
        path = os.path.join(self.tmp.name, "g.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(geometry, f)
        v = self.gs.line_constants(path)
        self.assertEqual(json.dumps(v.document["summary"]), json.dumps(r.document["summary"]))
        self.assertEqual(json.dumps(v.document["tables"]), json.dumps(r.document["tables"]))
        with self.assertRaises(gridsim.GridSimError):
            self.sess.line_constants({**geometry, "method": "carson-series"})

    def test_closing_ends_the_host_and_a_closed_session_refuses_requests(self):
        s = gridsim.Session(cwd=self.tmp.name)
        s.close()
        s.close()
        with self.assertRaises(gridsim.SessionError):
            s.stats()


if __name__ == "__main__":
    unittest.main()
