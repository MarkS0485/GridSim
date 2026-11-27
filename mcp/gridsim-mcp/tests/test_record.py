import json
import math

import pytest

from gridsim_mcp import record


def _rec():
    r = record.Record({"server": "x", "build": {"version": "v", "sha256": "s", "build_status": "UNSUPPORTED BUILD"},
                       "licensed": "no"})
    r.append("case_load", {"case": "case9"}, "why", "ok", exit_code=0, case_id="c1", case_hash="a" * 64)
    r.append("solve_pf", {"case_id": "c1"}, None, "ok", exit_code=0, case_id="c1", result_hash="b" * 64)
    return r


def test_chain_and_determinism(tmp_path):
    a, b = _rec(), _rec()
    assert a.lines() == b.lines()
    assert a.entries[0]["prevHash"] == a.header["hash"]
    assert a.entries[1]["prevHash"] == a.entries[0]["hash"]
    p = tmp_path / "r.jsonl"
    a.write(p)
    header, entries = record.read(p)
    assert record.verify(header, entries)[0]


def test_tamper_detected(tmp_path):
    p = tmp_path / "r.jsonl"
    _rec().write(p)
    lines = p.read_text().splitlines()
    e = json.loads(lines[1])
    e["arguments"]["case"] = "case14"
    lines[1] = record.canonical(e)
    p.write_text("\n".join(lines) + "\n")
    ok, bad, msg = record.verify(*record.read(p))
    assert not ok and bad == 1 and "TAMPERED" in msg


def test_reorder_detected(tmp_path):
    p = tmp_path / "r.jsonl"
    _rec().write(p)
    lines = p.read_text().splitlines()
    lines[1], lines[2] = lines[2], lines[1]
    p.write_text("\n".join(lines) + "\n")
    assert not record.verify(*record.read(p))[0]


def test_canonical_refuses_nan():
    with pytest.raises(ValueError):
        record.canonical({"x": math.nan})
