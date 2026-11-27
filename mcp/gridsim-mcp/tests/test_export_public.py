"""The public export (tools/export_public.py): the tree without the maintainer tools imports cleanly, lists only the
public tools even with every maintainer option set, carries no maintainer option, file or paragraph, passes the
forbidden-string scan and keeps to the plugin directory's size limits. Its own repository tests pass on it."""

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

PLUGIN = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PLUGIN / "tools"))

import export_public  # noqa: E402
from test_vendor_and_repo import FORBIDDEN  # noqa: E402

MAINTAINER_TOOLS = {"compare", "crossval_single", "divergence_triage", "build_regression", "rebuild_on_demand",
                    "caselab_run", "caselab_status", "caselab_findings", "caselab_reproduce", "caselab_propose"}
MAINTAINER_WORDS = ("caselab", "rebuild_on_demand", "divergence_triage", "crossval_single", "build_regression",
                    "gridsim-validate", "enable_compare", "harness_path", "maintainer-only", "Lab Mode")

LIST_TOOLS = r"""
import asyncio, json, os, sys
sys.path.insert(0, os.path.join(sys.argv[1], "server"))
os.environ.update({"GRIDSIM_MCP_ENABLE_COMPARE": "true", "GRIDSIM_MCP_HARNESS": sys.argv[2],
                   "GRIDSIM_MCP_ENABLE_CASELAB": "true", "GRIDSIM_MCP_ENABLE_REBUILD": "true"})
from gridsim_mcp import registry
from gridsim_mcp.app import build_server
server = build_server()
tools = asyncio.run(server.list_tools())
print(json.dumps({"listed": sorted(t.name for t in tools),
                  "registered": sorted(registry.TOOLS),
                  "groups": sorted({s.group for s in registry.TOOLS.values()}),
                  "maintainer": registry.load_maintainer_modules(),
                  "modules": sorted(m for m in sys.modules if m.startswith("gridsim_mcp."))}))
"""


@pytest.fixture(scope="module")
def export(tmp_path_factory):
    dest = tmp_path_factory.mktemp("public") / "gridsim"
    out, files = export_public.export(dest)
    return out, [rel for rel, _ in files]


def _files(root):
    return [p for p in root.rglob("*") if p.is_file()]


def test_no_maintainer_file_option_or_paragraph(export):
    root, rels = export
    assert rels == sorted(p.relative_to(root).as_posix() for p in _files(root))
    for rel in rels:
        assert not export_public.is_private(rel), rel
    assert not (root / "skills" / "gridsim-validate").exists()
    m = json.loads((root / ".claude-plugin" / "plugin.json").read_text(encoding="utf-8"))
    assert set(m["userConfig"]) == {"gridsim_cli", "licence_file", "workspace"}
    s = json.loads((root / ".mcp.json").read_text(encoding="utf-8"))["mcpServers"]["gridsim"]
    assert set(s["env"]) == {"GRIDSIM_MCP_CLI", "GRIDSIM_MCP_LICENCE_FILE", "GRIDSIM_MCP_WORKSPACE"}
    hits = []
    for p in _files(root):
        if p.suffix in (".md", ".json") and p.name != "LICENSE.md" and "docs_snapshot" not in p.parts:
            text = p.read_text(encoding="utf-8")
            hits += [f"{p.relative_to(root)}: {w}" for w in MAINTAINER_WORDS if w in text]
    assert hits == []
    readme = (root / "README.md").read_text(encoding="utf-8")
    assert "## What is sent: nothing" in readme and "## What runs" in readme


def test_export_imports_and_lists_only_public_tools(export, tmp_path):
    root, _ = export
    r = subprocess.run([sys.executable, "-c", LIST_TOOLS, str(root), str(tmp_path)], capture_output=True, text=True,
                       cwd=tmp_path, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
    assert r.returncode == 0, r.stderr
    out = json.loads(r.stdout)
    assert out["maintainer"] == [] and out["groups"] == ["core"]
    assert out["listed"] == out["registered"] and not MAINTAINER_TOOLS & set(out["listed"])
    assert {"solve_pf", "study", "physics_check", "break_it", "report_render", "record_replay"} <= set(out["listed"])
    assert not [m for m in out["modules"] if "compare" in m or "caselab" in m or "rebuild" in m]
    assert not list(root.rglob("__pycache__"))


def test_export_passes_the_forbidden_string_scan(export):
    root, _ = export
    hits = []
    for p in _files(root):
        if p.name == "LICENSE.md":
            continue
        text = p.read_text(encoding="utf-8", errors="replace")
        hits += [f"{p.relative_to(root)}: {what}: {m.group(0)!r}" for rx, what in FORBIDDEN for m in rx.finditer(text)]
    assert hits == []


def test_export_keeps_to_the_directory_limits(export):
    root, _ = export
    files = _files(root)
    assert len(files) < 10_000 and sum(p.stat().st_size for p in files) < 50 * 1024 * 1024
    big = [(p.relative_to(root).as_posix(), p.stat().st_size) for p in files if p.stat().st_size > 256 * 1024]
    assert big == []
    assert not [p for p in root.rglob("*") if p.is_symlink() or p.name in (".DS_Store", ".gitmodules", ".gitattributes")]


def test_export_own_repository_tests_pass(export, tmp_path):
    root, _ = export
    r = subprocess.run([sys.executable, "-m", "pytest", "-q", "-p", "no:cacheprovider",
                        str(root / "tests" / "test_vendor_and_repo.py"), str(root / "tests" / "test_docs_snapshot.py"),
                        str(root / "tests" / "test_allowlist.py")],
                       capture_output=True, text=True, cwd=tmp_path, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
    assert r.returncode == 0, r.stdout[-3000:]


def test_export_refuses_a_folder_inside_the_checkout():
    with pytest.raises(SystemExit):
        export_public.export(PLUGIN / "build-public")
