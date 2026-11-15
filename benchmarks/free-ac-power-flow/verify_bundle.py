"""Check whether a benchmark bundle contains evidence for the full roster."""
import argparse
import hashlib
import json
import math
from pathlib import Path

ROSTER = {"dense", "sparse", "matpower", "pandapower", "pypsa",
          "veragrid", "powsybl", "andes", "powermodels"}
ATTEMPTS = list(range(1, 11))


def validate(folder, manifest):
    issues = []
    if manifest.get("changedHarnessFiles"):
        issues.append(f"Source files changed during the run: {manifest['changedHarnessFiles']}")
    rows = {row["mode"]: row for row in manifest["paths"]}
    if set(rows) != ROSTER or len(rows) != len(manifest["paths"]):
        issues.append(f"roster mismatch: missing {sorted(ROSTER - rows.keys())}, "
                      f"extra {sorted(rows.keys() - ROSTER)}")
    for mode, row in rows.items():
        if row.get("exitCode") != 0:
            issues.append(f"{mode}: worker exit {row.get('exitCode')}, {row.get('error', '')}")
        attempts = row["attempts"]
        if [entry["attempt"] for entry in attempts] != ATTEMPTS:
            issues.append(f"{mode}: expected exactly ten numbered attempts")
        for entry in attempts:
            index = entry["attempt"]
            filename = folder / mode / f"attempt-{index:02}.json"
            if entry.get("status") in ("missing", "invalid") or not filename.exists():
                issues.append(f"{mode} attempt {index}: raw attempt missing or invalid")
                continue
            duration = entry.get("elapsedMs")
            if duration is None or not math.isfinite(duration) or duration <= 0:
                issues.append(f"{mode} attempt {index}: invalid elapsed time")
            if entry.get("converged") and (
                "referenceMismatchPu" not in entry or "referenceVoltageTargetErrorPu" not in entry
            ):
                issues.append(f"{mode} attempt {index}: missing electrical comparison")
        if mode in ("dense", "sparse") and any(entry.get("converged") for entry in attempts):
            if not row.get("audits"):
                issues.append(f"{mode}: missing physics audit")
            for audit in row.get("audits", []):
                export = folder / mode / audit["export"]
                output = folder / mode / audit["audit"]
                if not export.is_file() or not output.is_file():
                    issues.append(f"{mode} attempt {audit['attempt']}: missing export or audit")
                elif hashlib.sha256(export.read_bytes()).hexdigest() != audit["sha256"]:
                    issues.append(f"{mode} attempt {audit['attempt']}: export hash mismatch")
                elif audit["exitCode"] != 0:
                    issues.append(f"{mode} attempt {audit['attempt']}: physics audit exit {audit['exitCode']}")
    return issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    args = parser.parse_args()
    manifest = json.loads((args.bundle / "manifest.json").read_text(encoding="utf-8"))
    issues = validate(args.bundle, manifest)
    for issue in issues:
        print(issue)
    if issues:
        return 1
    print("All nine paths have ten recorded attempts and required evidence.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
