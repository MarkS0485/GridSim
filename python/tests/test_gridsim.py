"""Tests for the gridsim Python package (standard library unittest, no network).

Run from the repository root:  python -m unittest discover -s python/tests
The tests that run the real CLI skip, saying so, when no built CLI is found (see gridsim.find_cli); the rest drive a
stand-in CLI written here, so they need nothing but Python.
"""

import json
import math
import os
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gridsim  # noqa: E402

DOCUMENT = {
    "schema": "gridsim-cli-result/1",
    "tool": "gridsim 2.0.0+test",
    "verb": "pf",
    "args": ["pf", "case14"],
    "exitCode": 0,
    "summary": {"converged": True, "iterations": 3, "losses_mw": 13.393103479857866, "worst": "NaN"},
    "tables": {"buses": [{"id": 1, "vm_pu": 1.06, "va_deg": 0.0, "margin": "Infinity"},
                         {"id": 2, "vm_pu": 1.045, "va_deg": -4.982589778363714, "margin": "-Infinity"}]},
}

# A stand-in for the gridsim CLI: writes DOCUMENT (with the verb and arguments it was given) to the --json path, or
# writes nothing and exits 3 when the first argument after the verb is "diverge".
FAKE = textwrap.dedent("""
    import json, sys
    doc = json.loads(%r)
    argv = sys.argv[1:]
    out = argv[argv.index("--json") + 1]
    if len(argv) > 1 and argv[1] == "diverge":
        print("DID NOT CONVERGE")
        print("error: the solve did not converge", file=sys.stderr)
        sys.exit(3)
    verb = {"n-1": "nminus1", "n-2": "nminus2", "nminus1minus1": "n-1-1"}.get(argv[0], argv[0])
    doc["verb"] = verb
    doc["args"] = argv[:argv.index("--json")]
    doc["exitCode"] = 1 if "--fail" in argv else 0
    with open(out, "w", encoding="utf-8") as f:
        json.dump(doc, f)
    print("report on stdout")
    sys.exit(doc["exitCode"])
""") % json.dumps(DOCUMENT)


class StandIn(unittest.TestCase):
    """The wrapper against a stand-in CLI: argument passing, the document, failures."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.fake = os.path.join(cls.tmp.name, "fake_gridsim.py")
        with open(cls.fake, "w", encoding="utf-8") as f:
            f.write(FAKE)
        cls.gs = gridsim.GridSim(cli=[sys.executable, cls.fake])

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_run_passes_the_arguments_in_order_and_reads_the_document(self):
        r = self.gs.run("pf", "case14", "--top", 5)
        self.assertEqual(r.verb, "pf")
        self.assertEqual(r.args, ["pf", "case14", "--top", "5"])
        self.assertEqual(r.exit_code, 0)
        self.assertTrue(r.ok)
        self.assertEqual(r.tool, "gridsim 2.0.0+test")
        self.assertEqual(r.stdout.strip(), "report on stdout")
        self.assertIs(r.summary["converged"], True)
        self.assertEqual(r.summary["iterations"], 3)

    def test_numbers_come_back_as_the_same_bits(self):
        r = self.gs.pf("case14")
        self.assertEqual(r.summary["losses_mw"].hex(), (13.393103479857866).hex())
        self.assertEqual(r.table("buses")[1]["va_deg"].hex(), (-4.982589778363714).hex())

    def test_named_floats_become_floats(self):
        r = self.gs.pf("case14")
        self.assertTrue(math.isnan(r.summary["worst"]))
        self.assertEqual(r.table("buses")[0]["margin"], math.inf)
        self.assertEqual(r.table("buses")[1]["margin"], -math.inf)
        self.assertEqual(r.document["summary"]["worst"], "NaN")   # the document itself is kept as read

    def test_every_writer_verb_has_a_method_and_aliases_resolve(self):
        for verb in gridsim.WRITER_VERBS:
            self.assertTrue(callable(getattr(self.gs, verb.replace("-", "_"))), verb)
        self.assertEqual(self.gs.n_1_1("case14").verb, "n-1-1")
        self.assertEqual(self.gs.run("n-1", "case14").verb, "nminus1")
        self.assertEqual(self.gs.run("NMINUS1MINUS1", "case14").verb, "n-1-1")

    def test_a_finished_result_with_a_failing_verdict_is_returned_with_its_exit_code(self):
        r = self.gs.nminus1("case14", "--fail")
        self.assertEqual(r.exit_code, 1)
        self.assertFalse(r.ok)

    def test_a_run_with_no_result_raises_with_the_exit_code_and_last_stderr_line(self):
        with self.assertRaises(gridsim.GridSimError) as ctx:
            self.gs.pf("diverge")
        self.assertEqual(ctx.exception.exit_code, 3)
        self.assertIn("wrote no result (exit 3): error: the solve did not converge", str(ctx.exception))
        self.assertIn("DID NOT CONVERGE", ctx.exception.stdout)

    def test_result_flags_and_non_writer_verbs_are_refused(self):
        for flag in ("--json", "--csv", "--out"):
            with self.assertRaises(ValueError):
                self.gs.run("pf", "case14", flag, "x.json")
        with self.assertRaises(ValueError) as ctx:
            self.gs.run("trip", "case14")
        self.assertIn("does not write a result document", str(ctx.exception))

    def test_table_names_the_tables_there_are(self):
        with self.assertRaises(KeyError) as ctx:
            self.gs.pf("case14").table("branches")
        self.assertIn("tables: buses", str(ctx.exception))


class Documents(unittest.TestCase):
    """The document checks and CLI lookup, with no process at all."""

    def test_a_document_of_another_schema_or_missing_a_key_is_refused(self):
        with self.assertRaises(gridsim.GridSimError):
            gridsim.Result({**DOCUMENT, "schema": "gridsim-cli-result/2"})
        broken = dict(DOCUMENT)
        del broken["tables"]
        with self.assertRaises(gridsim.GridSimError):
            gridsim.Result(broken)

    def test_a_cli_that_does_not_exist_is_refused(self):
        with self.assertRaises(gridsim.GridSimError):
            gridsim.find_cli(os.path.join(tempfile.gettempdir(), "no-such-gridsim-binary"))
        old = os.environ.get("GRIDSIM_CLI")
        os.environ["GRIDSIM_CLI"] = os.path.join(tempfile.gettempdir(), "no-such-gridsim-binary")
        try:
            with self.assertRaises(gridsim.GridSimError):
                gridsim.find_cli()
        finally:
            if old is None:
                del os.environ["GRIDSIM_CLI"]
            else:
                os.environ["GRIDSIM_CLI"] = old

    def test_canonical_names(self):
        self.assertEqual(gridsim.canonical("N-2"), "nminus2")
        self.assertEqual(gridsim.canonical("physics-check"), "physics-check")
        with self.assertRaises(ValueError):
            gridsim.canonical("serve")


def _real_cli():
    try:
        return gridsim.find_cli()
    except gridsim.GridSimError:
        return None


@unittest.skipIf(_real_cli() is None, "no built gridsim CLI found (pass none: set GRIDSIM_CLI or build src/GridSim.Cli)")
class RealCli(unittest.TestCase):
    """The wrapper against the real CLI on built-in cases."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.gs = gridsim.GridSim(cwd=cls.tmp.name)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_dcpf_on_case14_gives_its_result_document(self):
        r = self.gs.dcpf("case14")
        self.assertEqual((r.verb, r.exit_code), ("dcpf", 0))
        self.assertTrue(r.tool.startswith("gridsim "))
        self.assertTrue(r.tables, "dcpf writes its branch flows as a table")
        self.assertEqual(len(r.tables[next(iter(r.tables))]), 20)   # case14 has 20 branches

    def test_two_runs_give_the_same_bits(self):
        a, b = self.gs.pf("case14"), self.gs.pf("case14")
        self.assertEqual(json.dumps(a.document["summary"]), json.dumps(b.document["summary"]))
        self.assertEqual(json.dumps(a.document["tables"]), json.dumps(b.document["tables"]))

    def test_an_unknown_case_raises_with_exit_2(self):
        with self.assertRaises(gridsim.GridSimError) as ctx:
            self.gs.pf("no-such-case")
        self.assertEqual(ctx.exception.exit_code, 2)


if __name__ == "__main__":
    unittest.main()
