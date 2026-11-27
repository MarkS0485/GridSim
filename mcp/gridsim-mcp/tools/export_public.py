"""Write the public plugin tree: this plugin without the maintainer tools.

    python tools/export_public.py <folder> [--force]

The maintainer tools (compare, the case lab and rebuild on demand), their tests, their skill, their plugin options and
the README paragraphs between the maintainer-only markers are left out. Everything else is copied byte for byte. The
server imports its maintainer modules optionally, so the exported server starts and offers only the public tools.
The folder must lie outside this checkout; with --force an existing folder is emptied first. Nothing is published.
"""

import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[1]

#: Paths (relative to the plugin folder, POSIX) left out of the public tree. A folder path ends in "/".
PRIVATE_PATHS = (
    "server/gridsim_mcp/tools_compare.py",
    "server/gridsim_mcp/tools_caselab.py",
    "server/gridsim_mcp/tools_rebuild.py",
    "server/gridsim_mcp/caselab/",
    "tests/test_compare.py",
    "tests/test_caselab.py",
    "tests/test_rebuild.py",
    "tests/test_scrub.py",
    "skills/gridsim-validate/",
    "tests/test_export_public.py",
    "tools/export_public.py",
)

#: Plugin options and the server environment variables they feed, maintainer only.
PRIVATE_CONFIG = ("enable_compare", "harness_path", "enable_caselab", "enable_rebuild", "rebuild_dir")
PRIVATE_ENV = ("GRIDSIM_MCP_ENABLE_COMPARE", "GRIDSIM_MCP_HARNESS", "GRIDSIM_MCP_ENABLE_CASELAB",
               "GRIDSIM_MCP_ENABLE_REBUILD", "GRIDSIM_MCP_REBUILD_DIR")

SKIP_PARTS = {"__pycache__", ".pytest_cache", ".DS_Store"}
MARKER_BLOCK = re.compile(r"[ \t]*<!-- maintainer-only -->.*?<!-- /maintainer-only -->[ \t]*(\r?\n)?", re.DOTALL)
MARKERS = ("<!-- maintainer-only -->", "<!-- /maintainer-only -->")


def is_private(rel):
    return any(rel == p or (p.endswith("/") and rel.startswith(p)) for p in PRIVATE_PATHS)


def source_files():
    """(relative POSIX path, path) of every file of the public tree, sorted."""
    out = []
    for p in PLUGIN.rglob("*"):
        if not p.is_file() or SKIP_PARTS & set(p.parts) or p.suffix == ".pyc":
            continue
        rel = p.relative_to(PLUGIN).as_posix()
        if not is_private(rel):
            out.append((rel, p))
    return sorted(out)


def strip_markdown(text):
    out = MARKER_BLOCK.sub("", text)
    nl = "\r\n" if "\r\n" in out else "\n"
    out = re.sub(rf"(?:{re.escape(nl)}){{3,}}", nl * 2, out)
    for m in MARKERS:
        if m in out:
            raise ValueError(f"unbalanced maintainer-only marker: {m}")
    return out


def public_manifest(text):
    m = json.loads(text)
    m["userConfig"] = {k: v for k, v in m.get("userConfig", {}).items() if k not in PRIVATE_CONFIG}
    return json.dumps(m, indent=2, ensure_ascii=False) + "\n"


def public_mcp_json(text):
    m = json.loads(text)
    for server in m.get("mcpServers", {}).values():
        env = server.get("env")
        if env:
            server["env"] = {k: v for k, v in env.items() if k not in PRIVATE_ENV}
    return json.dumps(m, indent=2, ensure_ascii=False) + "\n"


def transform(rel, data):
    """The public bytes of one file."""
    if rel == ".claude-plugin/plugin.json":
        return public_manifest(data.decode("utf-8")).encode("utf-8")
    if rel == ".mcp.json":
        return public_mcp_json(data.decode("utf-8")).encode("utf-8")
    if rel.endswith(".md"):
        return strip_markdown(data.decode("utf-8")).encode("utf-8")
    return data


def checkout_root():
    try:
        r = subprocess.run(["git", "-C", str(PLUGIN), "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                           check=False)
    except OSError:
        return None
    return Path(r.stdout.strip()).resolve() if r.returncode == 0 and r.stdout.strip() else None


def export(dest, force=False):
    dest = Path(dest).resolve()
    for root in (PLUGIN.resolve(), checkout_root()):
        if root is not None and (dest == root or root in dest.parents):
            raise SystemExit(f"refused: {dest} lies inside {root}; export to a folder outside the checkout")
    if dest.exists() and any(dest.iterdir()):
        if not force:
            raise SystemExit(f"refused: {dest} is not empty (use --force to empty it first)")
        shutil.rmtree(dest)
    files = source_files()
    for rel, p in files:
        target = dest / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(transform(rel, p.read_bytes()))
    return dest, files


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    if len(args) != 1:
        print(__doc__)
        return 2
    dest, files = export(args[0], force="--force" in argv)
    sizes = sorted(((dest / rel).stat().st_size, rel) for rel, _ in files)
    print(f"wrote {len(files)} files, {sum(s for s, _ in sizes)} bytes, to {dest}")
    print(f"largest: {sizes[-1][1]} ({sizes[-1][0]} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
