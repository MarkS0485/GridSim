"""The tools against the real GridSim CLI (one resident host per module; small cases only)."""

import json

import jsonschema
import pytest

from conftest import needs_cli

pytestmark = needs_cli


def _schema():
    from gridsim_mcp.app import ToolOutput
    return ToolOutput.model_json_schema()


def test_case9_load_solve_and_schema(call):
    st, text, err, entry = call("case_load", case="case9", reasoning="start from a known case")
    assert not err and st["evidence"]["case"] == "c1" and st["document"]["handle"] == "c1"
    st, text, err, entry = call("solve_pf", case_id="c1")
    assert not err and st["document"]["converged"] is True
    jsonschema.validate(st, _schema())
    assert st["evidence"]["build_tag"] == "[UNSUPPORTED BUILD]"
    assert "build_note" not in st["evidence"]
    assert entry["reasoning"] is None and entry["caseHash"] == st["evidence"]["case_hash"]
    st2, *_ = call("solve_pf", case_id="c1")
    assert st2["evidence"]["result_hash"] == st["evidence"]["result_hash"]


def test_no_host_token_anywhere(call, pool):
    call("case_load", case="case9")
    s = pool.sessions["t"]
    assert s.token and s.token not in json.dumps(s.record.lines())
    st, *_ = call("case_inspect", case_id="c1", kind="bus")
    assert s.token not in json.dumps(st)


def test_case14_nminus1_full_document(call):
    st, text, err, _ = call("case_load", case="case14", session="t14")
    cid = st["evidence"]["case"]
    st, text, err, _ = call("study", session="t14", case_id=cid, verb="nminus1")
    assert not err
    doc = st["document"]
    assert doc["schema"] == "gridsim-cli-result/1" and doc["verb"] == "nminus1"
    # GridSim's own build status in the document agrees with the server's identity: no note, same tag.
    assert doc["buildStatus"] == "UNSUPPORTED BUILD"
    assert st["evidence"]["build_tag"] == "[UNSUPPORTED BUILD]" and "build_note" not in st["evidence"]
    assert st["evidence"]["result"].startswith("r")
    jsonschema.validate(st, _schema())


def test_edit_refused_changes_nothing(call):
    call("case_load", case="case9", session="e")
    before, *_ = call("case_history", session="e", case_id="c1")
    st, text, err, entry = call("case_edit", session="e", case_id="c1", op="set", kind="bus", key="1", values={"type": 7})
    assert err and entry["status"] == "error"
    after, *_ = call("case_history", session="e", case_id="c1")
    assert before["document"] == after["document"]


def test_guards_refused_through_dispatch_and_recorded(call, pool):
    for verb, args in [("gda", []), ("estimate", ["case9"]), ("serve", []), ("interpose", []),
                       ("pmu-ingest", ["x"]), ("cycle", ["case9"]), ("replay", []), ("gda-replay", []),
                       ("estimate-noise-floor", []), ("pmu-estimate", ["case9"]), ("pf", ["case9", "--utc-now", "x"])]:
        st, text, err, entry = call("run_verb", session="g", verb=verb, args=args)
        assert err and text.startswith("REFUSED") and entry["status"] == "refused", (verb, text)
    st, text, err, _ = call("case_load", session="g", case="gb")
    assert not err
    st, text, err, _ = call("study", session="g", case_id=st["evidence"]["case"], verb="stability")
    assert err and "static verbs only" in text


def test_paths_confined(call):
    st, text, err, _ = call("case_save", session="p", case_id="c1", path="../outside.json")
    assert err  # no case yet, or outside the workspace
    call("case_load", session="p", case="case9")
    st, text, err, _ = call("case_save", session="p", case_id="c1", path="../outside.json")
    assert err and "outside" in text


def test_licence_never_recorded(tmp_path):
    import os
    from conftest import CLI
    from gridsim_mcp.host import Config, Pool
    from gridsim_mcp.registry import dispatch
    lic = tmp_path / "licence.txt"
    lic.write_text("SECRET-LICENCE-CONTENT-12345", encoding="utf-8")
    p = Pool(Config(cli=CLI, workspace=str(tmp_path / "ws"), licence_file=str(lic), environ=dict(os.environ)))
    try:
        st, text, err, _ = dispatch(p, "session_open", {}, "L")
        st, text, err, _ = dispatch(p, "record_export", {"name": "lic"}, "L")
        out = (tmp_path / "ws" / "records" / "lic.jsonl").read_text(encoding="utf-8")
        assert "SECRET-LICENCE" not in out and str(lic) not in out and '"licensed":"yes"' in out
        assert "SECRET-LICENCE" not in text
        assert p.sessions["L"].config.env["GRIDSIM_LICENCE_FILE"] == str(lic)
    finally:
        p.close_all()


def test_clean_environment_drops_private_roots():
    from gridsim_mcp.host import clean_environment
    env = clean_environment({"GDA_ROOT": "x", "GDA_DATA_ROOT": "y", "MY_LAKE": "z", "OTEL_EXPORTER_OTLP_ENDPOINT": "w",
                             "PATH": "p"})
    assert env == {"PATH": "p", "OTEL_SDK_DISABLED": "true"}


def test_same_calls_give_same_record_hashes(call, pool):
    for name in ("d1", "d2"):
        call("case_new", session=name, name="det", reasoning="seed")
        call("case_edit", session=name, case_id="c1", op="add", kind="bus", key="2", values={"base_kv": 132.0})
        call("case_edit", session=name, case_id="c1", op="add", kind="branch", key="1-2:1", values={"x": 0.1})
        call("case_edit", session=name, case_id="c1", op="add", kind="load", key="2", values={"pd": 50.0, "qd": 20.0})
        call("solve_pf", session=name, case_id="c1")
        call("study", session=name, case_id="c1", verb="pf")
    a, b = pool.sessions["d1"].record, pool.sessions["d2"].record
    assert a.lines() == b.lines() and a.head == b.head
