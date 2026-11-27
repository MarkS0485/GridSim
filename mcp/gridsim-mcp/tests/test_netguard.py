import socket

import pytest

import netguard


def test_guard_blocks_connect_and_lookup():
    with pytest.raises(RuntimeError, match="network access is forbidden"):
        socket.create_connection(("127.0.0.1", 9), timeout=1)
    with pytest.raises(RuntimeError):
        socket.getaddrinfo("example.org", 443)
    assert netguard.ATTEMPTS


def test_socketpair_still_works():
    a, b = socket.socketpair()
    a.close()
    b.close()


def test_server_drops_the_sdk_telemetry_middleware():
    from gridsim_mcp.app import build_server
    from gridsim_mcp.host import Pool
    server = build_server(Pool(config=object()))
    assert not [m for m in server.middleware if "Telemetry" in type(m).__name__]
    names = {t.name for t in server._tool_manager.list_tools()}
    assert {"session_open", "session_close", "case_new", "case_load", "case_inspect", "case_edit", "case_history",
            "case_save", "case_validate", "solve_pf", "study", "run_verb", "physics_check", "diff_cases",
            "diff_results", "reproduce_check", "record_export", "record_replay", "preflight",
            "import_model", "line_parameters", "study_case_run", "report_render", "explain_solve", "break_it",
            "tunables_list"} == names
