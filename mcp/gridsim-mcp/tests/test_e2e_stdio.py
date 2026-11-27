"""End to end over MCP stdio: the SDK's own client starts the server (with the network guard installed) and runs the
Phase 1 scenario: build a 5-bus network from scratch, solve, N-1, physics check, diff after an edit, export the
record, replay it to identical hashes, and see every guarded verb refused.

Also runnable on its own (prints a transcript):  python tests/test_e2e_stdio.py <workspace>
"""

import json
import os
import sys
from pathlib import Path

import anyio
import pytest

from conftest import CLI, needs_cli

HERE = Path(__file__).resolve().parent

GUARDED = [("gda", []), ("gda-replay", []), ("estimate", ["case9"]), ("estimate-noise-floor", ["case9"]),
           ("serve", []), ("interpose", []), ("pmu-ingest", ["x.csv"]), ("pmu-estimate", ["case9"]),
           ("cycle", ["case9"]), ("replay", []), ("replay-event", ["e1"]), ("lake_conformance", []),
           ("pf", ["case9", "--utc-now", "2020-01-01T00:00:00Z"]), ("pf", ["case9", "--horizon-hours", "1"]),
           ("pf", ["case9", "--as-of", "2020-01-01"])]


async def scenario(workspace, log=print):
    from mcp.client.session import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client

    errlog_path = Path(workspace) / "server-stderr.txt"
    Path(workspace).mkdir(parents=True, exist_ok=True)
    params = StdioServerParameters(
        command=sys.executable, args=[str(HERE / "run_server_offline.py")],
        env={"GRIDSIM_MCP_CLI": CLI, "GRIDSIM_MCP_WORKSPACE": str(workspace), "GRIDSIM_MCP_LICENCE_FILE": ""},
        cwd=str(workspace))
    out = {"calls": 0}
    with open(errlog_path, "w", encoding="utf-8") as errlog:
        async with stdio_client(params, errlog=errlog) as streams:
            read, write = streams[0], streams[1]
            async with ClientSession(read, write) as session:
                await session.initialize()
                tools = (await session.list_tools()).tools
                out["tools"] = {t.name: t for t in tools}
                out["prompts"] = {pr.name for pr in (await session.list_prompts()).prompts}
                out["resources"] = {str(rs.uri) for rs in (await session.list_resources()).resources}
                lesson = await session.get_prompt("lesson_n_minus_1", {"case": "case14"})
                out["lesson_text"] = lesson.messages[0].content.text

                async def call(tool_, **args):
                    r = await session.call_tool(tool_, args)
                    out["calls"] += 1
                    text = r.content[0].text if r.content else ""
                    log(f"{tool_}: {'ERROR ' if r.is_error else ''}{text.splitlines()[0][:160] if text else ''}")
                    return r

                def sc(r):
                    return r.structured_content

                why = "build a 5-bus teaching network from scratch"
                await call("preflight", reasoning="check the build first")
                await call("case_new", name="five_bus", base_kv=132.0, reasoning=why)
                for b in ("2", "3", "4", "5"):
                    await call("case_edit", case_id="c1", op="add", kind="bus", key=b, values={"base_kv": 132.0},
                               reasoning="add a 132 kV bus")
                for br in ("1-2:1", "1-3:1", "2-3:1", "2-4:1", "3-5:1", "4-5:1"):
                    await call("case_edit", case_id="c1", op="add", kind="branch", key=br,
                               values={"r": 0.01, "x": 0.08, "b": 0.02, "rate_a": 150.0},
                               reasoning="typical 132 kV overhead line in per unit on 100 MVA")
                for bus, p in (("2", 40.0), ("3", 50.0), ("4", 60.0), ("5", 30.0)):
                    await call("case_edit", case_id="c1", op="add", kind="load", key=bus, values={"pd": p, "qd": p / 4})
                await call("case_edit", case_id="c1", op="add", kind="generator", key="4:1",
                           values={"pg": 80.0, "vg": 1.01, "qmax": 100.0, "qmin": -50.0, "pmax": 200.0})
                out["inspect"] = sc(await call("case_inspect", case_id="c1"))
                out["solve"] = sc(await call("solve_pf", case_id="c1", reasoning="base case"))
                out["n1"] = sc(await call("study", case_id="c1", verb="nminus1", reasoning="is it N-1 secure?"))
                out["physics"] = sc(await call("physics_check", case_id="c1"))
                out["save"] = sc(await call("case_save", case_id="c1", path="cases/five_bus_base.json"))
                out["pf_before"] = sc(await call("solve_pf", case_id="c1", full=True))
                await call("case_edit", case_id="c1", op="set", kind="load", key="5", values={"pd": 45.0},
                           reasoning="demand growth at bus 5")
                out["pf_after"] = sc(await call("solve_pf", case_id="c1", full=True))
                out["diff_cases"] = sc(await call("diff_cases", a="cases/five_bus_base.json", b="c1"))
                out["diff_results"] = sc(await call("diff_results", a=out["pf_before"]["evidence"]["result"],
                                                    b=out["pf_after"]["evidence"]["result"]))
                out["reproduce"] = sc(await call("reproduce_check", case_id="c1", verb="pf"))
                out["refusals"] = []
                for verb, args in GUARDED:
                    r = await call("run_verb", verb=verb, args=args, reasoning="guard test")
                    out["refusals"].append((verb, args, r.is_error, r.content[0].text))
                out["export"] = sc(await call("record_export", name="phase1-e2e"))
                out["replay"] = sc(await call("record_replay", path="records/phase1-e2e.jsonl"))
                out["resource_allowlist"] = (await session.read_resource("gridsim://plugin/allowlist")).contents[0].text
    out["stderr"] = errlog_path.read_text(encoding="utf-8", errors="replace")
    return out


@needs_cli
def test_phase1_scenario_over_stdio(tmp_path):
    out = anyio.run(scenario, tmp_path / "ws", lambda *_: None)
    tools = out["tools"]
    for name, t in tools.items():
        assert len(name) <= 64 and t.title and t.annotations is not None and t.output_schema is not None, name
        assert t.annotations.read_only_hint is not None and t.annotations.open_world_hint is False
    for name in ("import_model", "line_parameters", "study_case_run", "report_render", "explain_solve", "break_it",
                 "tunables_list"):
        assert name in tools, name
    assert tools["tunables_list"].annotations.read_only_hint is True
    for name in ("solve_pf", "study", "run_verb", "reproduce_check"):
        assert "tuning" in tools[name].input_schema["properties"], name
    # The validation and rebuild tools are Mark-only: not listed unless their options are on.
    assert not {"compare", "crossval_single", "divergence_triage", "build_regression", "rebuild_on_demand",
                "caselab_run", "caselab_status", "caselab_findings", "caselab_reproduce", "caselab_propose"} & set(tools)
    assert {"lesson_per_unit", "lesson_pv_pq", "lesson_reactive_limits", "lesson_n_minus_1", "lesson_fault_level",
            "lesson_inertia_rocof", "study_writeup"} <= out["prompts"]
    assert {"gridsim://docs/troubleshooting", "gridsim://docs/per-unit", "gridsim://docs/grounded-values",
            "gridsim://docs/dynamic-models", "gridsim://plugin/starter-cases",
            "gridsim://plugin/construct-method", "gridsim://plugin/tunables"} <= out["resources"]
    assert "case14" in out["lesson_text"] and "verified solver, unvalidated case" in out["lesson_text"]
    assert out["inspect"]["document"]["counts"] == {"bus": 5, "generator": 2, "load": 4, "branch": 6, "shunt": 0}
    assert out["solve"]["document"]["converged"] is True
    assert out["n1"]["document"]["verb"] == "nminus1"
    assert out["physics"]["document"]["summary"]["gate_pass"] is True
    entries = out["diff_cases"]["document"]["tables"]["entries"]
    assert len(entries) == 1 and entries[0]["path"].endswith("pMw") and entries[0]["new"] == "45"
    assert out["diff_results"]["document"]["exitCode"] == 3
    assert out["reproduce"]["reproduce"]["verdict"] == "MATCH"
    for verb, args, is_error, text in out["refusals"]:
        assert is_error and text.startswith("REFUSED"), (verb, args, text)
    rep = out["replay"]["document"]
    assert rep["chain"] == "INTACT" and rep["verdict"] == "REPRODUCED" and rep["head"] == rep["recorded_head"]
    assert all(c["verdict"] in ("MATCH", "SKIPPED") for c in rep["calls"])
    assert "NETWORK-ATTEMPT" not in out["stderr"]
    assert "not available through the GridSim MCP server" in out["resource_allowlist"]
    assert "gda" not in out["resource_allowlist"].lower()


if __name__ == "__main__":
    ws = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd() / "e2e-ws"
    res = anyio.run(scenario, ws)
    rep = res["replay"]["document"]
    print(json.dumps({"calls": res["calls"], "tools": len(res["tools"]), "replay": rep["verdict"],
                      "head": rep["head"], "recorded_head": rep["recorded_head"],
                      "refused": sum(1 for r in res["refusals"] if r[2]), "guarded": len(res["refusals"]),
                      "network_attempts": res["stderr"].count("NETWORK-ATTEMPT")}, indent=1), flush=True)
    os._exit(0)
