"""The validation tools (Mark-only): absent unless enabled and configured; the triage classes; and, with the Lab Mode
harness configured (GRIDSIM_MCP_LAB_TESTS=1 and GRIDSIM_MCP_HARNESS), a real compare through the daemon."""

import json
import os
from pathlib import Path

import pytest

from gridsim_mcp import registry, tools_compare

COMPARE_TOOLS = {"compare", "crossval_single", "divergence_triage", "build_regression"}

CASE_WITH_TAP = """function mpc = t
mpc.branch = [
\t1\t2\t0.01\t0.1\t0\t0\t0\t0\t0\t0\t1\t-360\t360;
\t2\t3\t0.0\t0.2\t0\t0\t0\t0\t0.978\t0\t1\t-360\t360;
];
"""
CASE_NO_TAP = CASE_WITH_TAP.replace("0.978", "0")


def _harness(tmp_path):
    h = tmp_path / "harness"
    (h / "lab").mkdir(parents=True)
    (h / "run.py").write_text("", encoding="utf-8")
    (h / "lab" / "serve.py").write_text("", encoding="utf-8")
    return h


def test_tools_are_absent_unless_enabled_and_configured(tmp_path, monkeypatch):
    for k in (tools_compare.ENV_ENABLE, tools_compare.ENV_HARNESS):
        monkeypatch.delenv(k, raising=False)
    assert COMPARE_TOOLS <= set(registry.TOOLS)
    assert not COMPARE_TOOLS & set(registry.enabled_tools())
    h = _harness(tmp_path)
    assert tools_compare.settings({tools_compare.ENV_ENABLE: "true"}) is None                 # no harness path
    assert tools_compare.settings({tools_compare.ENV_HARNESS: str(h)}) is None                # not enabled
    assert tools_compare.settings({tools_compare.ENV_ENABLE: "${user_config.enable_compare}",
                                   tools_compare.ENV_HARNESS: str(h)}) is None                # placeholder
    assert tools_compare.settings({tools_compare.ENV_ENABLE: "true",
                                   tools_compare.ENV_HARNESS: str(tmp_path / "none")}) is None  # not a harness
    on = {tools_compare.ENV_ENABLE: "true", tools_compare.ENV_HARNESS: str(h)}
    assert tools_compare.settings(on)["harness"] == h.resolve()
    assert COMPARE_TOOLS <= set(registry.enabled_tools(on))
    for name in COMPARE_TOOLS:
        spec = registry.TOOLS[name]
        assert spec.group == "compare" and len(name) <= 64 and spec.title


def test_a_disabled_tool_is_refused_through_dispatch(pool, monkeypatch):
    monkeypatch.delenv(tools_compare.ENV_ENABLE, raising=False)
    structured, text, is_error, _ = registry.dispatch(pool, "compare", {"mode": "pf", "case": "case9"}, "t")
    assert is_error and text.startswith("REFUSED") and "not available through the GridSim MCP server" in text


def _record(mode, **tools):
    return {"mode": mode, "case": "case14.m", "tools": tools}


def test_triage_classes():
    # The known PowerModels dcpf case: a rival gate fail explained by the documented tap model difference.
    rec = _record("dcpf", gridsim={"status": "pass"},
                  powermodels={"status": "gate-fail", "reason": "angle differs from reference by 3.132e-01 deg"})
    cls, reasons, known = tools_compare.classify(rec, "powermodels", CASE_WITH_TAP)
    assert (cls, known) == ("documented model difference", "powermodels-dcpf-taps")
    assert "ignores branch tap ratios" in reasons[0]
    # The harness's own mark is enough (no case text).
    rec["tools"]["powermodels"]["modelDifference"] = "documented"
    assert tools_compare.classify(rec, "powermodels")[0] == "documented model difference"
    # Without a tap or a shift, the same failure is a rival gate fail.
    rec["tools"]["powermodels"].pop("modelDifference")
    assert tools_compare.classify(rec, "powermodels", CASE_NO_TAP)[0] == "rival gate fail"
    # GridSim failing its gate comes first.
    rec = _record("pf", gridsim={"status": "gate-fail", "reason": "residual 1e-3"}, matpower={"status": "pass"})
    assert tools_compare.classify(rec, "matpower")[0] == "GridSim gate fail"
    # Both pass and agree.
    pair = {"a": "gridsim", "b": "matpower", "max": {"dVm": {"max": 1e-15, "at": 3}, "dVa": {"max": 1e-13, "at": 4},
                                                       "dP": {"max": 1e-12, "at": 2, "from": 1, "to": 2}}}
    rec = _record("pf", gridsim={"status": "pass"}, matpower={"status": "pass"})
    rec["elementDiff"] = {"pairs": [pair]}
    assert tools_compare.classify(rec, "matpower")[0] == "agreement"
    # Both pass but the voltages differ: a genuine disagreement, with where.
    pair["max"]["dVm"] = {"max": 2e-4, "at": 9}
    cls, reasons, _ = tools_compare.classify(rec, "matpower")
    assert cls == "genuine disagreement" and "bus 9" in reasons[0]
    # A failed cross-check (OPF objective) is a disagreement too.
    rec = _record("dcopf", gridsim={"status": "pass"},
                  pandapower={"status": "pass", "crossCheck": {"passed": False, "reference": "matpower",
                                                               "reason": "objectiveRecomputed differs"}})
    assert tools_compare.classify(rec, "pandapower")[0] == "genuine disagreement"
    # MATPOWER CPF outside GridSim's bracket: the documented slack-Q difference is named.
    rec = _record("cpf", gridsim={"status": "pass", "crossCheck": {"passed": False, "reference": "matpower",
                                                                   "reason": "nose outside bracket"}},
                  matpower={"status": "pass", "crossCheck": {"passed": True}})
    assert tools_compare.classify(rec, "matpower")[2] == "matpower-cpf-slack-q"
    # EXDC2: GridSim refuses it, ANDES runs it.
    rec = _record("dynamics", gridsim={"status": "unavailable", "reason": "GridSim refuses EXDC2"},
                  andes={"status": "pass"})
    assert tools_compare.classify(rec, "andes")[0] == "documented model difference"
    # Not compared when a side did not run.
    rec = _record("pf", gridsim={"status": "pass"}, matpower={"status": "unavailable", "reason": "no Octave"})
    assert tools_compare.classify(rec, "matpower")[0] == "not compared"


def test_taps_are_read_from_the_matpower_branch_table():
    assert tools_compare.has_taps_or_shifts(CASE_WITH_TAP)
    assert not tools_compare.has_taps_or_shifts(CASE_NO_TAP)
    assert tools_compare.has_taps_or_shifts(CASE_NO_TAP.replace("\t0\t1\t-360\t360;\n];", "\t-30\t1\t-360\t360;\n];"))


def test_substituted_export_carries_the_rival_state():
    export = {"model": {}, "result": {"buses": [{"id": 1, "vm": 1.0, "vaDeg": 0.0}, {"id": 2, "vm": 0.98,
                                                                                    "vaDeg": -3.0}]},
              "physics": {"gate_pass": True}}
    doc = tools_compare._substituted_export(export, {"bus": [[2, 0.97, -3.5], [1, 1.0, 0.0]]})
    assert doc["result"]["buses"][1] == {"id": 2, "vm": 0.97, "vaDeg": -3.5} and "physics" not in doc
    assert export["result"]["buses"][1]["vm"] == 0.98   # the original is untouched


LAB = os.environ.get("GRIDSIM_MCP_LAB_TESTS") == "1" and os.environ.get(tools_compare.ENV_HARNESS)
needs_lab = pytest.mark.skipif(not LAB, reason="set GRIDSIM_MCP_LAB_TESTS=1 and GRIDSIM_MCP_HARNESS for Lab runs")


@needs_lab
def test_compare_through_the_daemon_and_triage(pool, monkeypatch):
    monkeypatch.setenv(tools_compare.ENV_ENABLE, "true")

    def call(name, **args):
        return registry.dispatch(pool, name, args, "lab")
    structured, text, is_error, _ = call("compare", mode="dcpf", case="case14", tools=["gridsim", "powermodels"])
    assert not is_error, text
    doc = structured["document"]
    assert doc["tools"]["gridsim"]["status"] == "pass" and doc["runType"] == "lab-correctness"
    assert doc["tools"]["powermodels"]["status"] == "gate-fail"
    tri, text, is_error, _ = call("divergence_triage", bundle=structured["bundle"], tool="powermodels",
                                  record_finding=False)
    assert not is_error, text
    row = tri["document"]["triage"][0]
    assert row["class"] == "documented model difference" and row["documented"] == "powermodels-dcpf-taps"
    assert "evidence, not proof" in tri["document"]["note"]
    assert json.dumps(tri).find("LICENCE") < 0
