"""The docs snapshot shipped in the plugin: identical to what the checkout gives, hashed in its manifest, filtered, and
preferred over a checkout that differs."""

import hashlib
import json
import subprocess
import sys
from pathlib import Path

import pytest

from gridsim_mcp import docs_index

PLUGIN = Path(__file__).resolve().parents[1]
DOCS = PLUGIN.parents[1] / "docs"


def test_snapshot_matches_the_checkout():
    r = subprocess.run([sys.executable, str(PLUGIN / "tools" / "sync_docs.py"), "--check"], capture_output=True,
                       text=True)
    if "not found" in r.stdout:
        pytest.skip("not inside a GridSim checkout")
    assert r.returncode == 0, r.stdout


def test_manifest_hashes_and_sources():
    manifest = json.loads(docs_index.snapshot(docs_index.MANIFEST))
    assert manifest["schema"] == docs_index.MANIFEST_SCHEMA and manifest["filter"] == docs_index.FILTER.pattern
    names = {f["file"] for f in manifest["files"]}
    from gridsim_mcp.allowlist import ALLOWED_VERBS
    assert names == {"troubleshooting-index.md", "troubleshooting-entries.md", "per-unit.md", "grounded-values.md",
                     "dynamic-models.md"} | {docs_index.verb_help_file(v) for v in ALLOWED_VERBS}
    for f in manifest["files"]:
        data = (docs_index.SNAPSHOT_DIR / f["file"]).read_bytes()
        assert hashlib.sha256(data).hexdigest() == f["sha256"], f["file"]
        assert f["source"].startswith("docs/") and len(f["source_sha256"]) == 64
        src = PLUGIN.parents[1] / f["source"]
        if src.is_file():
            lf = src.read_bytes().replace(b"\r\n", b"\n")
            assert hashlib.sha256(lf).hexdigest() == f["source_sha256"], f["source"]


def test_snapshot_carries_no_excluded_subject():
    for p in docs_index.SNAPSHOT_DIR.rglob("*"):
        if not p.is_file() or p.name == docs_index.MANIFEST:
            continue
        text = p.read_text(encoding="utf-8")
        hit = docs_index.FILTER.search(text)
        assert hit is None, f"{p.name}: {hit.group(0)!r}"


def test_every_allowlisted_verb_has_help_in_the_snapshot():
    from gridsim_mcp.allowlist import ALLOWED_VERBS
    missing = [v for v in sorted(ALLOWED_VERBS) if not docs_index.snapshot(docs_index.verb_help_file(v))]
    assert missing == []


def test_snapshot_files_are_small():
    """Every snapshot file stays well under the plugin directory's 256 KiB per-file review threshold."""
    big = [(p.name, p.stat().st_size) for p in docs_index.SNAPSHOT_DIR.rglob("*") if p.is_file()
           and p.stat().st_size > 128 * 1024]
    assert big == []


def test_filter_drops_only_the_matching_block():
    text = ("Intro paragraph.\n\n| a | b |\n|---|---|\n| keep | 1 |\n| GDA row | 2 |\n\n- item one\n- the lake item\n"
            "  continued\n- item three\n\nA paragraph about a nowcast\nover two lines.\n\nLast.\n")
    out = docs_index.filter_text(text)
    assert "| keep | 1 |" in out and "GDA row" not in out
    assert "- item one" in out and "- item three" in out and "lake" not in out and "continued" not in out
    assert "nowcast" not in out and "over two lines" not in out and "Last." in out and "Intro paragraph." in out


def test_a_checkout_that_differs_gives_the_snapshot(tmp_path):
    snap = tmp_path / "snap"
    snap.mkdir()
    (snap / "per-unit.md").write_bytes(b"snapshot text\n")
    root = DOCS if (DOCS / "technical").is_dir() else None
    text, source = docs_index.served(root, "per-unit.md", folder=snap)
    assert text == "snapshot text\n"
    assert source == ("snapshot (checkout differs)" if root else "snapshot")
    if root:
        same = docs_index.extract(root, "per-unit.md")
        (snap / "per-unit.md").write_bytes(same.encode("utf-8"))
        assert docs_index.served(root, "per-unit.md", folder=snap) == (same, "checkout")
