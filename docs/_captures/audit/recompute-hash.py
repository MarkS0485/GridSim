# Recompute the hash of every audit entry found in a capture file (lines that start with "{").
# Payload (src/GridSim.Core/Diagnostics/AuditLog.cs, ComputeHash): fields joined with "|", UTF-8, SHA-256, upper-case hex.
import hashlib, json, sys

for line in open(sys.argv[1], encoding="utf-8"):
    if not line.startswith("{"):
        continue
    e = json.loads(line)
    state = "-" if e["stateUtc"] is None else e["stateUtc"]
    payload = "|".join([str(e["seq"]), e["runUtc"], state, e["provenance"], e["summary"], e["prevHash"]])
    print("payload:", payload)
    print("stored:    ", e["hash"])
    print("recomputed:", hashlib.sha256(payload.encode("utf-8")).hexdigest().upper())
    if "status=Converged" in payload:
        tampered = payload.replace("status=Converged", "status=Diverged")
        print("one word changed (status=Diverged):", hashlib.sha256(tampered.encode("utf-8")).hexdigest().upper())
