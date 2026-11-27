import json
import re
import subprocess
import sys
from pathlib import Path

import pytest

PLUGIN = Path(__file__).resolve().parents[1]

# Machine paths, private folders and data roots that must never appear in the plugin.
FORBIDDEN = [
    (re.compile(r"\b[A-Za-z]:[\\/]{1,2}[A-Za-z]"), "a drive-letter path"),
    (re.compile(r"Users[\\/]", re.IGNORECASE), "a user profile path"),
    (re.compile(r"/home/[a-z]"), "a home folder path"),
    (re.compile(r"GridSim-G" + r"DA|GDA-S" + r"OLVES|GDA_ROOT\s*=|GDA_DATA_ROOT\s*=|[\\/]GDA[\\/]", re.IGNORECASE), "a GDA path"),
    (re.compile(r"Electrical" + r"Eng", re.IGNORECASE), "a private folder name"),
    (re.compile(r"_cl" + r"aude"), "a private notes folder"),
    (re.compile(r"[\\/]Notes[\\/]GridSim", re.IGNORECASE), "private notes"),
    (re.compile(r"[\\/]lake[\\/]|lake_root\s*=|LAKE_ROOT\s*=", re.IGNORECASE), "a lake root"),
    (re.compile(r"Co-Authored" + r"-By|Generated with \[Cl" + r"aude", re.IGNORECASE), "an AI attribution line"),
]

SKIP_PARTS = {"__pycache__", ".pytest_cache"}

#: The maintainer options, present only in the full (private) tree; the public export has none of them.
MAINTAINER_CONFIG = {"enable_compare", "harness_path", "enable_caselab", "enable_rebuild", "rebuild_dir"}
FULL_TREE = (PLUGIN / "server" / "gridsim_mcp" / "tools_compare.py").is_file()


def plugin_files():
    for p in PLUGIN.rglob("*"):
        if p.is_file() and not SKIP_PARTS & set(p.parts) and p.suffix != ".pyc":
            yield p


def test_vendor_copy_is_byte_identical():
    r = subprocess.run([sys.executable, str(PLUGIN / "tools" / "sync_vendor.py"), "--check"], capture_output=True,
                       text=True)
    if "not found" in r.stdout:
        pytest.skip("not inside a GridSim checkout")
    assert r.returncode == 0, r.stdout


def test_no_forbidden_strings_in_plugin():
    hits = []
    for p in plugin_files():
        if p.name == "LICENSE.md":
            continue
        text = p.read_text(encoding="utf-8", errors="replace")
        for rx, what in FORBIDDEN:
            for m in rx.finditer(text):
                hits.append(f"{p.relative_to(PLUGIN)}: {what}: {m.group(0)!r}")
    assert hits == []


def test_no_assistant_files_in_plugin():
    names = {p.name.lower() for p in plugin_files()}
    assert "claude.md" not in names and "agents.md" not in names


def test_manifest_and_mcp_json():
    m = json.loads((PLUGIN / ".claude-plugin" / "plugin.json").read_text(encoding="utf-8"))
    assert m["name"] == "gridsim"
    assert m["userConfig"]["licence_file"]["sensitive"] is True
    public = {"gridsim_cli", "licence_file", "workspace"}
    mcp = json.loads((PLUGIN / ".mcp.json").read_text(encoding="utf-8"))
    s = mcp["mcpServers"]["gridsim"]
    assert s["command"] == "uv" and "${CLAUDE_PLUGIN_ROOT}/server/main.py" in s["args"]
    assert m["network"]["hosts"] == []
    if FULL_TREE:
        assert set(m["userConfig"]) == public | MAINTAINER_CONFIG
        for k in ("enable_compare", "enable_caselab", "enable_rebuild"):
            assert m["userConfig"][k]["type"] == "boolean" and m["userConfig"][k]["default"] is False
        assert s["env"]["GRIDSIM_MCP_ENABLE_COMPARE"] == "${user_config.enable_compare}"
        assert s["env"]["GRIDSIM_MCP_ENABLE_CASELAB"] == "${user_config.enable_caselab}"
        assert s["env"]["GRIDSIM_MCP_HARNESS"] == "${user_config.harness_path}"
        assert s["env"]["GRIDSIM_MCP_ENABLE_REBUILD"] == "${user_config.enable_rebuild}"
    else:
        assert set(m["userConfig"]) == public
        assert set(s["env"]) == {"GRIDSIM_MCP_CLI", "GRIDSIM_MCP_LICENCE_FILE", "GRIDSIM_MCP_WORKSPACE"}
    main = (PLUGIN / "server" / "main.py").read_text(encoding="utf-8")
    assert re.search(r'"mcp==\d+\.\d+\.\d+"', main), "the SDK must be pinned to an exact version"


def test_licence_matches_repository():
    repo = PLUGIN.parents[1] / "LICENSE.md"
    if repo.is_file():
        assert (PLUGIN / "LICENSE.md").read_bytes() == repo.read_bytes()
