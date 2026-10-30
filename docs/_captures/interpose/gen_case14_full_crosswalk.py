#!/usr/bin/env python3
"""Write iec104-case14-full.json: a gridsim-rtu-pointmap/1 crosswalk binding IOA 1001..1014 to the
voltage magnitude and IOA 1101..1114 to the voltage angle of case14 buses 1..14 (28 bindings).
    python gen_case14_full_crosswalk.py > iec104-case14-full.json
"""
import json

rows = []
for bus in range(1, 15):
    rows.append({"address": f"IOA:{1000 + bus}", "kind": "VmPu", "bus": bus, "sigma": 0.01})
for bus in range(1, 15):
    rows.append({"address": f"IOA:{1100 + bus}", "kind": "VaDeg", "bus": bus, "sigma": 0.2})

print("{")
print('  "schema": "gridsim-rtu-pointmap/1",')
print('  "case": "case14",')
print('  "baseMva": 100.0,')
print('  "bindings": [')
for i, r in enumerate(rows):
    print("    " + json.dumps(r) + ("," if i < len(rows) - 1 else ""))
print("  ]")
print("}")
