"""Copy GridSim's python/gridsim package into server/vendor/gridsim, byte for byte.

    python tools/sync_vendor.py            # copy
    python tools/sync_vendor.py --check    # exit 1 when the copy differs from the source

Run from anywhere inside the GridSim checkout. Only the package's .py files are copied (no caches).
"""

import shutil
import sys
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[1]
SOURCE = PLUGIN.parents[1] / "python" / "gridsim"
TARGET = PLUGIN / "server" / "vendor" / "gridsim"


def files(folder):
    return sorted(p.name for p in folder.glob("*.py"))


def differences():
    if not SOURCE.is_dir():
        return [f"source {SOURCE.name} folder not found (run inside a GridSim checkout)"]
    out = []
    src, dst = files(SOURCE), files(TARGET) if TARGET.is_dir() else []
    for name in sorted(set(src) | set(dst)):
        if name not in dst:
            out.append(f"missing in vendor: {name}")
        elif name not in src:
            out.append(f"extra in vendor: {name}")
        elif (SOURCE / name).read_bytes() != (TARGET / name).read_bytes():
            out.append(f"differs: {name}")
    return out


def sync():
    if TARGET.is_dir():
        shutil.rmtree(TARGET)
    TARGET.mkdir(parents=True)
    for name in files(SOURCE):
        shutil.copyfile(SOURCE / name, TARGET / name)


if __name__ == "__main__":
    if "--check" in sys.argv[1:]:
        diff = differences()
        print("\n".join(diff) if diff else "vendor copy is byte-identical")
        sys.exit(1 if diff else 0)
    sync()
    print(f"copied {len(files(TARGET))} files")
