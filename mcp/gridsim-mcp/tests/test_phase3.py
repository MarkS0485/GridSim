"""Phase 3 tools against the real CLI: import_model, line_parameters, study_case_run, report_render, explain_solve and
break_it; and the docs resources read from the GridSim checkout."""

import shutil
from pathlib import Path

import pytest

from conftest import CLI, needs_cli
from gridsim_mcp import docs_index, prompts

pytestmark = needs_cli

REPO = Path(__file__).resolve().parents[3]
GEOMETRY = {"schema": "gridsim-lineconstants/1", "method": "perfect-ground", "frequency_hz": 50, "transposed": True,
            "conductors": [{"name": n, "role": "phase", "x_m": x, "height_m": 15.0, "resistance_ohm_per_km": 0.157,
                            "gmr_m": 0.0072, "radius_m": 0.0093} for n, x in (("A", -4.5), ("B", 0.0), ("C", 4.5))]}


def _ok(r):
    structured, text, is_error, entry = r
    assert not is_error, text
    assert structured["evidence"]["build_tag"] in ("[RELEASE]", "[UNSUPPORTED BUILD]")
    assert entry is not None and entry["tool"]
    return structured


def test_explain_solve_case9_with_jacobian(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    d = _ok(call("explain_solve", case_id=c, jacobian=True))["document"]
    assert d["schema"] == "gridsim-mcp-explain-solve/1" and d["converged"] is True and d["status"] == "Converged"
    assert d["bus_types"] == {"Slack": 1, "Pv": 2, "Pq": 6}
    assert d["iterations"] == len(d["passes"][0]["max_mismatch_history"])
    assert all(o > 1.5 for o in d["passes"][0]["order_estimates"])        # Newton: about quadratic near the answer
    assert d["worst_bus_last_iteration"]["equation"] in ("P", "Q") and d["worst_bus_last_iteration"]["bus_id"]
    assert d["jacobian"]["rows"] == 2 * 8 - 2                               # 8 P equations, 6 Q equations
    assert d["physics_gate_pass"] is True and d["exitCode"] == 0


def test_explain_solve_is_deterministic_and_reads_an_edited_case(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    a = _ok(call("explain_solve", case_id=c))
    b = _ok(call("explain_solve", case_id=c))
    assert a["evidence"]["result_hash"] == b["evidence"]["result_hash"]
    _ok(call("case_edit", case_id=c, op="set", kind="load", key="5", values={"pd": 120.0}))
    d = _ok(call("explain_solve", case_id=c))["document"]
    assert d["converged"] is True and any(n.startswith("round trip:") for n in d["convert_notes"])


def test_explain_solve_non_convergence_maps_to_troubleshooting(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    for key in ("5", "7", "9"):
        _ok(call("case_edit", case_id=c, op="set", kind="load", key=key, values={"pd": 600.0, "qd": 200.0}))
    d = _ok(call("explain_solve", case_id=c))["document"]
    assert d["converged"] is False and d["exitCode"] == 3
    assert "did not converge" in d["gridsim_message"]
    assert d["troubleshooting"]["heading"] == "`DID NOT CONVERGE` and exit 3"


def test_break_it_actions_on_a_copy(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    before = _ok(call("case_inspect", case_id=c))["evidence"]["case_hash"]
    over = _ok(call("break_it", case_id=c, action="overload", factor=6))
    d = over["document"]
    assert d["outcome"] == "did not converge" and "did not converge" in d["gridsim_message"]
    assert d["troubleshooting"]["heading"] == "`DID NOT CONVERGE` and exit 3" and d["troubleshooting"]["text"]
    assert over["broken_case_id"] != c
    d = _ok(call("break_it", case_id=c, action="remove_slack"))["document"]
    assert d["outcome"].startswith("edit refused") and "No slack" in d["gridsim_message"]
    assert d["troubleshooting"]["match"].startswith("closest")
    d = _ok(call("break_it", case_id=c, action="trip_branch", target="1-4"))["document"]
    assert d["outcome"].startswith("edit refused") and "island" in d["gridsim_message"]
    d = _ok(call("break_it", case_id=c, action="trip_branch", target="4-5"))["document"]
    assert d["outcome"].startswith("converged") and d["troubleshooting"] is None
    d = _ok(call("break_it", case_id=c, action="fault", target="5"))["document"]
    assert d["outcome"] == "fault result" and d["fault"]["summary"]["fault_current_pu"] > 0
    # The student's case is untouched.
    assert _ok(call("case_inspect", case_id=c))["evidence"]["case_hash"] == before


def test_break_it_needs_a_target(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    structured, text, is_error, _ = call("break_it", case_id=c, action="trip_branch")
    assert is_error and "target" in text


def test_import_model_convert_round_trip_and_notes(call):
    d = _ok(call("import_model", source="case9", name="imp9"))
    doc = d["document"]
    assert doc["importer"] == "convert" and doc["exitCode"] == 0
    assert any("solves bit-identically" in r for r in doc["round_trip"])
    assert any(n.startswith("note:") for n in doc["notes"])
    assert doc["loaded"]["buses"] == 9 and d["case_id"]
    assert all(f["path"].startswith("imports/imp9/") and len(f["sha256"]) == 64 for f in doc["files"])
    # GridSim never overwrites: the same name again is refused.
    structured, text, is_error, _ = call("import_model", source="case9", name="imp9")
    assert is_error and "already holds files" in text


def test_import_model_fail_loud_and_matpower_target(call):
    doc = _ok(call("import_model", source="case14", name="imp14v2"))["document"]
    assert doc["exitCode"] == 1 and "baseKv" in doc["message"]          # JSON v2 needs a base voltage
    d = _ok(call("import_model", source="case14", name="imp14m", target="matpower"))
    assert d["document"]["loaded"]["buses"] == 14


def test_import_model_dgs_and_scl(call, workspace):
    (workspace / "bad.dgs").write_text("$$General;ID(a:40);Descr(a:40);Val(a:40)\n1;Version;5.0\n", encoding="utf-8")
    doc = _ok(call("import_model", source="bad.dgs", name="dgs1"))["document"]
    assert doc["importer"] == "dgs-import" and doc["exitCode"] == 1 and "dgs-import" in doc["message"]
    assert doc["round_trip"][0].startswith("no round trip")
    scl = REPO / "tests" / "GridSim.Tests" / "fixtures" / "openscd-scl" / "valid2007B4.scd"
    if not scl.is_file():
        pytest.skip("not inside a GridSim checkout")
    shutil.copy(scl, workspace / "s.scd")
    structured, text, is_error, _ = call("import_model", source="s.scd", name="scl0")
    assert is_error and "params" in text
    (workspace / "p.json").write_text('{"schema": "x"}', encoding="utf-8")
    doc = _ok(call("import_model", source="s.scd", name="scl1", params="p.json"))["document"]
    assert doc["importer"] == "scl-import" and doc["exitCode"] == 1 and "unknown member" in doc["message"]


def test_line_parameters_rpc_and_per_unit(call):
    d = _ok(call("line_parameters", geometry=GEOMETRY, length_km=10.0, base_kv=132.0, base_mva=100.0))["document"]
    sm = d["summary"]
    assert sm["z1_r_ohm_per_km"] == 0.157 and 0.3 < sm["z1_x_ohm_per_km"] < 0.5
    assert abs(sm["r1_pu"] - 0.157 * 10 / (132.0 ** 2 / 100.0)) < 1e-12
    structured, text, is_error, _ = call("line_parameters", geometry=GEOMETRY, length_km=10.0)
    assert is_error and "all three" in text


def test_study_case_run_planning_fixture(call, workspace):
    src = REPO / "tests" / "GridSim.Tests" / "fixtures" / "planning"
    if not src.is_dir():
        pytest.skip("not inside a GridSim checkout")
    shutil.copytree(src, workspace / "planning", dirs_exist_ok=True)
    doc = _ok(call("study_case_run", file="planning/seasonal.json", out="runs/seasonal",
                   kind="planning-study"))["document"]
    assert doc["manifest"]["file"] == "runs/seasonal/run-manifest.json" and len(doc["manifest"]["sha256"]) == 64
    assert doc["exitCode"] in (0, 3) and doc["files"]
    structured, text, is_error, _ = call("study_case_run", file="planning/seasonal.json", out="runs/seasonal",
                                         kind="planning-study")
    assert is_error and "already holds files" in text


def test_report_render_md_then_html(call):
    c = _ok(call("case_load", case="case9"))["case_id"]
    rid = _ok(call("solve_pf", case_id=c, full=True))["evidence"]["result"]
    doc = _ok(call("report_render", result=rid, out="reports/p3.md"))["document"]
    assert doc["template"] == "reports/p3.template.json" and len(doc["report_sha256"]) == 64
    html = _ok(call("report_render", result=rid, out="reports/p3.html", format="html",
                    template="reports/p3.template.json"))["document"]
    assert html["template_sha256"] == doc["template_sha256"] and html["result_sha256"] == doc["result_sha256"]
    structured, text, is_error, _ = call("report_render", result=rid, out="reports/p3.md")
    assert is_error and "never overwrites" in text


def test_docs_resources_from_the_checkout():
    root = docs_index.find_docs_root([CLI])
    if root is None:
        pytest.skip("not inside a GridSim checkout")
    idx = docs_index.troubleshooting_index(root)
    assert "## Power flow does not converge" in idx and "`DID NOT CONVERGE` and exit 3" in idx
    assert "GDA and the bridge" not in idx and "Interposer" not in idx
    assert "Per-unit conventions" in docs_index.per_unit(root)
    assert "Grounded values per class" in docs_index.grounded_values(root)
    assert "- Exciters" in docs_index.dynamic_models(root)
    m = docs_index.match_troubleshooting(root, "error: the dense-newton solve of x did not converge (status MaxIterations)")
    assert m["heading"] == "`DID NOT CONVERGE` and exit 3" and m["text"].startswith("### `DID NOT CONVERGE`")
    # Every heading in the map is a real entry of chapter 30.
    text = docs_index.read_chapter(root, docs_index.TROUBLESHOOTING)
    for _, heading, _ in docs_index.TROUBLESHOOTING_MAP:
        assert docs_index.section(text, heading, level=3), heading


def test_docs_resources_without_a_checkout():
    # A release install has no docs folder: the plugin's snapshot serves the same sections.
    idx = docs_index.troubleshooting_index(None)
    assert "## Power flow does not converge" in idx
    assert "Per-unit conventions" in docs_index.per_unit(None)
    assert "Grounded values per class" in docs_index.grounded_values(None)
    m = docs_index.match_troubleshooting(None, "DID NOT CONVERGE")
    assert m["installed"] is True and m["source"] == "snapshot" and m["text"].startswith("### `DID NOT CONVERGE`")
    assert "### pf" in docs_index.verb_help(None, "pf")


def test_docs_resources_without_checkout_or_snapshot(tmp_path):
    assert docs_index.served(None, "per-unit.md", folder=tmp_path) == (None, None)


def test_lesson_prompts_carry_the_evidence_rules():
    assert len(prompts.LESSONS) == 6
    for title, desc, text in prompts.LESSONS.values():
        assert "{case}" in text and title.startswith("Lesson:")
