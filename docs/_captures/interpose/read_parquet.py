#!/usr/bin/env python3
"""Read-only dump of a quarantine Parquet store written by `gridsim interpose --out-store`.
    python read_parquet.py results/interpose-docs/iec104-store
Needs pyarrow. Prints every partition path (store-relative) and its rows, column by column.
"""
import glob, os, sys
import pyarrow.parquet as pq

root = sys.argv[1]
for f in sorted(glob.glob(os.path.join(root, "Parquet", "*", "year=*", "week=*", "*.parquet"))):
    rel = os.path.relpath(f, root).replace(os.sep, "/")
    print(rel)
    for row in pq.read_table(f).to_pylist():
        print("  " + ", ".join(f"{k}={v}" for k, v in row.items() if v is not None))
