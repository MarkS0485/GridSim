#!/usr/bin/env python3
"""Write data/bundled.sha256: the shipped-content manifest of the Bundled Networks (N229, October 2026).

GridSim counts a network as bundled (exempt from the licence tier's bus limit) only when it lies under one of the
shipped data folders AND its content is what shipped: BundledNetworks (src/GridSim.Core/Licensing/BundledNetworks.cs)
checks every file against this manifest. Run this script whenever a file under a bundled data folder is added, changed
or removed, and commit the result with the data change:

    python tools/gen_bundled_manifest.py

The test N229_the_committed_manifest_matches_the_shipped_files (tests/GridSim.Tests/Lf5RemainingTests.N229.cs) fails
until the manifest is regenerated, naming the files that differ.

Rules (the same as BundledNetworks.ContentHash / ParseManifest):
  * the folders are BundledNetworks.DataFolders (kept equal to FOLDERS below; the test checks the folder set);
  * every file under them, at any depth, is listed, path relative to data/ with '/' separators;
  * the hash is SHA-256 of the file's bytes with every CR LF pair read as LF (a Windows checkout writes text files
    with CR LF, a Linux one with LF), as 64 lower-case hex digits;
  * one line per file, "<hash>  <path>", sorted by path (ordinal), LF line ends, a final newline.
"""
import hashlib
import os
import sys

FOLDERS = ["matpower", "uk", "gb-synthetic", "gb-full", "intl", "dist3ph"]
MANIFEST = "bundled.sha256"


def content_hash(path):
    with open(path, "rb") as f:
        data = f.read()
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()


def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    data = os.path.join(repo, "data")
    entries = []
    for folder in FOLDERS:
        top = os.path.join(data, folder)
        # GR-912: every listed folder is part of the shipped data; a missing one is an incomplete checkout, refused
        # rather than silently left out of the manifest.
        if not os.path.isdir(top):
            raise SystemExit(f"gen_bundled_manifest: data/{folder} is missing; the manifest would leave it out.")
        for dirpath, _dirs, files in os.walk(top):
            for name in files:
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, data).replace(os.sep, "/")
                entries.append((rel, content_hash(full)))
    entries.sort(key=lambda e: e[0])
    out = os.path.join(data, MANIFEST)
    with open(out, "w", encoding="ascii", newline="\n") as f:
        for rel, h in entries:
            f.write(f"{h}  {rel}\n")
    print(f"wrote {out}: {len(entries)} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
