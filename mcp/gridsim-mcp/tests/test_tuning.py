"""Tuning: the list, the strict checks and the TUNED / off-standard marking (units), and tuned runs against the real
GridSim CLI (solve_pf, study, run_verb, reproduce_check, record replay)."""

import json
import re
from pathlib import Path

import pytest

from conftest import needs_cli
from gridsim_mcp import allowlist, record, tuning
from gridsim_mcp.allowlist import Refused

SNAPSHOT = Path(tuning.__file__).resolve().parents[1] / "docs_snapshot" / "verb-help"


# -- the list -----------------------------------------------------------------------------------------------------------

def test_list_loads_and_is_well_formed():
    doc = tuning.load()
    assert doc["schema"] == "gridsim-mcp-tunables/1"
    assert set(doc["classes"]) == set(tuning.CLASSES)
    keys = [t["key"] for t in doc["tunables"]]
    assert len(keys) == len(set(keys))
    for t in doc["tunables"]:
        assert t["class"] in ("STANDARD", "NUMERICS"), t["key"]          # PHYSICS is never tunable
        assert t["key"].split(".", 1)[0] in t["verbs"]
        for v in t["verbs"]:
            assert v in allowlist.ALLOWED_VERBS, (t["key"], v)
        assert t["flag"].startswith("--") and t["meaning"]
        if t["class"] == "STANDARD":
            assert t["standard"]
        allowlist.check_flag(t["flag"])                                    # no tunable flag is a refused flag
    for r in doc["rebuild_allowlist"]:
        assert r["class"] == "NUMERICS" and r["min"] < r["default"] < r["max"]
        assert re.compile(r["pattern"]).groupindex.get("value")


def test_no_guard_setting_is_listed():
    text = tuning.TUNABLES_FILE.read_text(encoding="utf-8").lower()
    for word in ("horizon", "169", "utc-now", "utc_now", "lake", "gda", "licence", "license", "telemetry", "sbom",
                 "bus limit", "1,500", "1500"):
        assert word not in text, word


def test_each_flag_is_the_verbs_own_flag():
    if not SNAPSHOT.is_dir():
        pytest.skip("no docs snapshot")
    for t in tuning.load()["tunables"]:
        for v in t["verbs"]:
            text = (SNAPSHOT / f"{v}.md").read_text(encoding="utf-8")
            assert f"`{t['flag']}`" in text or f"`{t['flag']} " in text or t["flag"] in text, (v, t["flag"])


# -- the checks ---------------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("verb,tuning_map,words", [
    ("pf", {"pf.tolerance": 1e-9}, "is not a tunable value"),
    ("pf", {"cpf.step": 0.1}, "belongs to cpf"),
    ("pf", {"pf.max_iter": 0}, "outside its safe range"),
    ("pf", {"pf.max_iter": 2.5}, "whole number"),
    ("pf", {"pf.max_iter": True}, "whole number"),
    ("pf", {"pf.diverge_limit": 0}, "outside its safe range"),
    ("pf", {"pf.diverge_limit": "never"}, "takes a number"),
    ("pf", {"pf.diverge_limit": float("nan")}, "finite"),
    ("pf", {"pf.gs_accel": 1.5}, "needs pf.engine"),
    ("pf", {"pf.engine": "gs", "pf.gs_accel": 2.0}, "outside its safe range"),
    ("pf", {"pf.engine": "magic"}, "takes one of"),
    ("pf", {"pf.q_revert": "yes"}, "true or false"),
    ("cpf", {"cpf.min_step": 0.5}, "at most cpf.step"),
    ("restore", {"restore.vmax": 0.95}, "outside its safe range"),
    ("restore", {"restore.vmin": 1.0, "restore.vmax": 1.0}, "must be above restore.vmin"),
    ("protection-check", {"protection-check.uf": 47.0}, "together with protection-check.of"),
    ("pf", {"horizon_hours": 1}, "guards are not tunable"),
    ("pf", {"pf.engine": "utc-now"}, "guards are not tunable"),
    ("nminus1", {"licence_cap": 100000}, "guards are not tunable"),
])
def test_bad_tuning_is_refused(verb, tuning_map, words):
    with pytest.raises(Refused) as ex:
        tuning.apply(verb, tuning_map)
    assert words in str(ex.value) and "Nothing was run" in str(ex.value)


def test_a_tunable_flag_in_plain_args_is_refused():
    with pytest.raises(Refused, match="pass it as tuning"):
        tuning.apply("pf", None, ["--max-iter", "5"])
    with pytest.raises(Refused, match="pass it as tuning"):
        tuning.apply("restore", None, ["--order", "#1", "--vmin=0.8"])
    # The same flag name on a verb that does not tune it is untouched (qv-curve's --step is its own).
    assert tuning.apply("qv-curve", None, ["--step", "0.02"]).flags == []


def test_flags_and_markers():
    a = tuning.apply("pf", {"pf.engine": "gs", "pf.gs_accel": 1.6, "pf.max_iter": 10000, "pf.q_revert": True})
    assert a.flags == ["--engine", "gs", "--gs-accel", "1.6", "--max-iter", "10000", "--q-revert"]
    # pf.max_iter 10000 is the Gauss-Seidel default, so it is not a tuned value.
    assert a.marker() == "tuned: pf.engine=gs, pf.gs_accel=1.6, pf.q_revert=true"
    assert a.off_standard == []
    d = tuning.apply("pf", {"pf.max_iter": 100, "pf.q_revert": False, "pf.diverge_limit": 10})
    assert not d.is_tuned and d.marker() is None and d.evidence() == {}
    off = tuning.apply("pf", {"pf.diverge_limit": "off"})
    assert off.flags == ["--diverge-limit", "off"] and off.tuned == {"pf.diverge_limit": "off"}
    s = tuning.apply("protection-check", {"protection-check.uf": 47.0, "protection-check.of": 52.0})
    assert s.tuned == {"protection-check.uf": 47.0}
    assert s.off_standard == ["off-standard: ENA EREC G99 frequency protection settings for a 50 Hz system no longer "
                              "complied with"]
    h = tuning.apply("harmonics", {"harmonics.synth_spectrum": "ideal"})
    assert h.evidence()["off_standard"][0].startswith("off-standard: ENA EREC G5")
    assert tuning.apply("nminus1", {"nminus1.batched_lanes": 2}).tuned == {"nminus1.batched_lanes": 2}


def test_record_carries_tuned_fields_only_when_tuned():
    r = record.Record({"server": "x"})
    plain = r.append("solve_pf", {"case_id": "c1"}, None, "ok")
    assert "tuned" not in plain and "offStandard" not in plain
    t = r.append("run_verb", {"verb": "restore"}, None, "ok", tuned="tuned: restore.vmax=1.15",
                 off_standard=["off-standard: x no longer complied with"])
    assert t["tuned"] == "tuned: restore.vmax=1.15" and t["offStandard"] == ["off-standard: x no longer complied with"]
    assert record.verify(r.header, r.entries)[0]


def test_listing():
    assert len(tuning.listing()["tunables"]) == len(tuning.load()["tunables"])
    pf = tuning.listing("pf")
    assert pf["verb"] == "pf" and {t["key"] for t in pf["tunables"]} == {
        "pf.engine", "pf.gs_accel", "pf.max_iter", "pf.max_q_passes", "pf.q_revert", "pf.diverge_limit"}


# -- tuned runs against the real CLI -------------------------------------------------------------------------------------

@needs_cli
def test_tuned_solve_is_marked_and_untuned_is_not(call):
    st, *_ = call("case_load", session="tu", case="case14")
    cid = st["evidence"]["case"]
    plain, _, err, e0 = call("solve_pf", session="tu", case_id=cid, full=True)
    assert not err and not plain["evidence"]["build_tag"].endswith("TUNED") and "tuned" not in e0
    st, text, err, entry = call("solve_pf", session="tu", case_id=cid, tuning={"pf.engine": "fdxb"})
    assert not err, text
    ev = st["evidence"]
    assert ev["build_tag"] == "[UNSUPPORTED BUILD] TUNED" and ev["tuned"] == "tuned: pf.engine=fdxb"
    assert "off_standard" not in ev
    assert entry["tuned"] == "tuned: pf.engine=fdxb" and entry["arguments"]["tuning"] == {"pf.engine": "fdxb"}
    assert st["document"]["schema"] == "gridsim-cli-result/1" and "--engine" in json.dumps(st["document"].get("args"))
    assert "TUNED" in text.splitlines()[0]
    # A tuning map of defaults only is not a tuned run.
    st, _, err, entry = call("solve_pf", session="tu", case_id=cid, tuning={"pf.max_iter": 100})
    assert not err and not st["evidence"]["build_tag"].endswith("TUNED") and "tuned" not in entry


@needs_cli
def test_refused_tuning_runs_nothing(call):
    st, *_ = call("case_load", session="tr", case="case9")
    cid = st["evidence"]["case"]
    st, text, err, entry = call("study", session="tr", case_id=cid, verb="pf", tuning={"pf.max_iter": -1})
    assert err and text.startswith("REFUSED (tuning)") and entry["status"] == "refused"
    assert entry["resultHash"] is None and entry["result"] is None
    st, text, err, entry = call("study", session="tr", case_id=cid, verb="pf", args=["--engine", "gs"])
    assert err and "pass it as tuning" in text


@needs_cli
def test_standard_tuning_is_flagged_off_standard(call):
    st, *_ = call("case_load", session="ts", case="case9")
    cid = st["evidence"]["case"]
    st, text, err, entry = call("run_verb", session="ts", verb="protection-check", case_id=cid,
                                tuning={"protection-check.uv": 0.85})
    assert not err, text
    ev = st["evidence"]
    assert ev["build_tag"].endswith(" TUNED") and ev["tuned"] == "tuned: protection-check.uv=0.85"
    assert ev["off_standard"] == ["off-standard: ENA EREC G99 Issue 2 Table 10.1 (HV interface protection settings) "
                                  "no longer complied with"]
    assert entry["offStandard"] == ev["off_standard"]


@needs_cli
def test_reproduce_and_replay_honour_tuning(call, pool):
    st, *_ = call("case_load", session="rp", case="case14")
    cid = st["evidence"]["case"]
    st, text, err, _ = call("reproduce_check", session="rp", case_id=cid, verb="pf",
                            tuning={"pf.engine": "gs", "pf.gs_accel": 1.4})
    assert not err, text
    assert st["reproduce"]["verdict"] == "MATCH" and st["evidence"]["build_tag"].endswith("TUNED")
    tuned_hash = st["reproduce"]["hashes"][0]
    st, *_ = call("study", session="rp", case_id=cid, verb="pf")
    assert st["evidence"]["result_hash"] != tuned_hash        # Gauss-Seidel at 1.4 is not Newton's bits
    st, *_ = call("study", session="rp", case_id=cid, verb="pf", tuning={"pf.engine": "gs", "pf.gs_accel": 1.4})
    assert st["evidence"]["result_hash"] == tuned_hash
    st, *_ = call("record_export", session="rp", name="tuned-replay")
    st, text, err, _ = call("record_replay", session="rp2", path=st["document"]["path"])
    assert not err and st["document"]["verdict"] == "REPRODUCED", text
