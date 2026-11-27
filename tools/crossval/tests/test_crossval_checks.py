"""Tests for the pandapower cross-validation sidecar's own checks (tools/crossval/crossval.py). Standard library unittest.

Run from the repository root:  python -m unittest discover -s tools/crossval/tests
These need neither NumPy nor pandapower: they exercise the checks that decide whether a comparison may be made and how a
reactive-limit difference is judged, on hand-written inputs whose right answer is plain from the numbers.
"""

import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import crossval  # noqa: E402


def _export(model_ids, result_ids):
    """A minimal solve export: the buses the model declares and the rows its result holds."""
    return {
        "schema": "gridsim-solve-export/1",
        "model": {"buses": [{"id": i, "type": "pq", "pd": 0.0, "qd": 0.0} for i in model_ids]},
        "result": {"buses": [{"id": i, "vm": 1.0, "vaDeg": 0.0, "pInjMw": 0.0, "qInjMvar": 0.0} for i in result_ids]},
    }


class ResultCoversModel(unittest.TestCase):
    """Every model bus must have exactly one result row (external review, RC5)."""

    def test_a_complete_result_is_accepted_and_counts_every_model_bus(self):
        self.assertEqual(3, crossval.check_result_covers_model(_export([1, 2, 3], [1, 2, 3])))

    def test_an_empty_result_is_refused(self):
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 2, 3], []))
        self.assertIn("3 model bus(es) have no result row (first: [1, 2, 3])", str(cm.exception))

    def test_a_result_missing_one_bus_is_refused(self):
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 2, 3], [1, 3]))
        self.assertIn("1 model bus(es) have no result row (first: [2])", str(cm.exception))

    def test_a_result_with_one_bus_twice_is_refused(self):
        # The same number of rows as model buses, but bus 2 twice and bus 3 never: a count check alone would pass it.
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 2, 3], [1, 2, 2]))
        msg = str(cm.exception)
        self.assertIn("1 result bus id(s) listed more than once (first: [2])", msg)
        self.assertIn("1 model bus(es) have no result row (first: [3])", msg)

    def test_a_duplicated_bus_alone_is_refused(self):
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 2, 3], [1, 2, 3, 2]))
        self.assertIn("1 result bus id(s) listed more than once (first: [2])", str(cm.exception))

    def test_a_result_row_for_an_unknown_bus_is_refused(self):
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 2], [1, 2, 9]))
        self.assertIn("1 result row(s) name a bus the model does not have (first: [9])", str(cm.exception))

    def test_a_duplicated_model_bus_is_refused(self):
        with self.assertRaises(ValueError) as cm:
            crossval.check_result_covers_model(_export([1, 1], [1, 1]))
        self.assertIn("1 model bus id(s) listed more than once (first: [1])", str(cm.exception))

    def test_the_sidecar_exits_3_on_an_empty_result_before_looking_for_pandapower(self):
        # The export is refused as unusable (exit 3, a setup error), never reported as a skip (77) or a pass.
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "empty.json")
            with open(path, "w", encoding="utf-8") as f:
                json.dump(_export([1, 2, 3], []), f)
            argv, err = sys.argv, io.StringIO()
            sys.argv = ["crossval.py", path]
            try:
                with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
                    code = crossval.main()
            finally:
                sys.argv = argv
        self.assertEqual(3, code)
        self.assertIn("no result row", err.getvalue())


class QLimitOutcomes(unittest.TestCase):
    """The reactive-limit comparison keeps which limit each tool pinned (external review, RC5). Limits -10 / +10 MVAr."""

    RANGES = {5: (-10.0, 10.0)}
    TOL = crossval.GRIDSIM_Q_LIMIT_TOL_MVA

    def classify(self, gs, pp_, q_gs, q_pp):
        return crossval.classify_q_limit_outcomes(gs, pp_, self.RANGES, q_gs, q_pp)

    def test_opposite_limits_are_a_disagreement(self):
        # GridSim pinned at +10, pandapower at -10: the old min(|q - qmax|, |q - qmin|) measured 0 from GridSim's +10 to
        # its own limit and called this a boundary case.
        self.assertEqual(([], [5]), self.classify({5: "max"}, {5: "min"}, {5: 10.0}, {5: -10.0}))
        self.assertEqual(([], [5]), self.classify({5: "min"}, {5: "max"}, {5: -10.0}, {5: 10.0}))

    def test_a_pinned_label_against_fixed_is_a_disagreement(self):
        self.assertEqual(([], [5]), self.classify({5: "fixed"}, {5: "max"}, {5: 10.0}, {5: 10.0}))

    def test_the_same_label_is_agreement(self):
        self.assertEqual(([], []), self.classify({5: "max"}, {5: "max"}, {5: 10.0}, {5: 10.0}))

    def test_pinned_against_free_near_the_disputed_limit_is_a_boundary_case(self):
        # pandapower pinned at +10; GridSim kept the unit PV at 10 + 0.5e-3 MVAr, inside its own 1e-3 MVA slack.
        self.assertEqual(([5], []), self.classify({}, {5: "max"}, {5: 10.0 + 0.5 * self.TOL}, {5: 10.0}))
        # And the same at the lower limit.
        self.assertEqual(([5], []), self.classify({}, {5: "min"}, {5: -10.0 - 0.5 * self.TOL}, {5: -10.0}))

    def test_the_distance_is_to_the_disputed_limit_not_the_nearer_one(self):
        # pandapower pinned at +10, GridSim free at -10.0005: within 1e-3 of -10 (the OTHER limit), 20.0005 from the
        # disputed one. The old rule took the nearer limit and called it a boundary case.
        self.assertEqual(([], [5]), self.classify({}, {5: "max"}, {5: -10.0 - 0.5 * self.TOL}, {5: -10.0}))
        self.assertEqual(([], [5]), self.classify({}, {5: "min"}, {5: 10.0 + 0.5 * self.TOL}, {5: 10.0}))

    def test_the_free_tools_output_is_what_is_measured(self):
        # GridSim pinned at +10 (its own output is then exactly the limit). The distance is pandapower's free output to
        # +10: 5 MVAr away is a disagreement, 0.5e-3 MVAr away a boundary case.
        self.assertEqual(([], [5]), self.classify({5: "max"}, {}, {5: 10.0}, {5: 5.0}))
        self.assertEqual(([5], []), self.classify({5: "max"}, {}, {5: 10.0}, {5: 10.0 - 0.5 * self.TOL}))

    def test_just_outside_the_slack_is_a_disagreement(self):
        self.assertEqual(([], [5]), self.classify({}, {5: "max"}, {5: 10.0 + 2 * self.TOL}, {5: 10.0}))

    def test_a_bus_without_a_range_or_an_output_is_a_disagreement(self):
        self.assertEqual(([], [7]), self.classify({}, {7: "max"}, {7: 10.0}, {}))
        self.assertEqual(([], [5]), self.classify({}, {5: "max"}, {}, {}))


if __name__ == "__main__":
    unittest.main()
