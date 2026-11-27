"""Write the docs snapshot the plugin ships: server/docs_snapshot, from GridSim's docs folder.

    python tools/sync_docs.py            # write the snapshot
    python tools/sync_docs.py --check    # exit 1 when the snapshot differs from what the checkout gives

Only the sections the server serves are extracted (the Troubleshooting index and mapped entries of manual ch. 30, the
per-unit section of technical ch. 2, the provenance tags and grounded values of technical ch. 13, the model list of
technical ch. 24 and the ch. 19 section of every allowlisted verb), each through the server's filter. MANIFEST.json
names each source chapter with its SHA-256. GridSim's documents are read, never changed. Run inside a GridSim checkout.
"""

import sys
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PLUGIN / "server"))

from gridsim_mcp import docs_index  # noqa: E402

DOCS = PLUGIN.parents[1] / "docs"
TARGET = docs_index.SNAPSHOT_DIR


def differences():
    if not (DOCS / "manual").is_dir():
        return [f"source {DOCS.name} folder not found (run inside a GridSim checkout)"]
    want = docs_index.extract_all(DOCS)
    have = {p.relative_to(TARGET).as_posix() for p in TARGET.rglob("*") if p.is_file()} if TARGET.is_dir() else set()
    out = []
    for name in sorted(set(want) | have):
        if name not in have:
            out.append(f"missing in snapshot: {name}")
        elif name not in want:
            out.append(f"extra in snapshot: {name}")
        elif (TARGET / name).read_bytes() != want[name].encode("utf-8"):
            out.append(f"differs: {name}")
    return out


def sync():
    files = docs_index.extract_all(DOCS)
    TARGET.mkdir(parents=True, exist_ok=True)
    for p in TARGET.rglob("*"):
        if p.is_file() and p.relative_to(TARGET).as_posix() not in files:
            p.unlink()
    for name, text in files.items():
        (TARGET / name).parent.mkdir(parents=True, exist_ok=True)
        (TARGET / name).write_bytes(text.encode("utf-8"))
    return files


if __name__ == "__main__":
    if "--check" in sys.argv[1:]:
        diff = differences()
        print("\n".join(diff) if diff else "docs snapshot matches the checkout")
        sys.exit(1 if diff else 0)
    print(f"wrote {len(sync())} files")
