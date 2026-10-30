#!/usr/bin/env bash
# ISOLATED GPU validation + CPU-vs-GPU comparison. Runs entirely under this dir
# (~/gridsim-gpu/publish), writing to results-gpu/<timestamp>/ with its own log - it does NOT
# touch the CPU regime in ~/gridsim. NOT auto-run; invoke manually when the box is free.
set -uo pipefail
cd "$(dirname "$0")"
CLI="./gridsim"
CORES="$(nproc)"
TS="$(date -u +%Y%m%d-%H%M%S)"
OUT="results-gpu/$TS"
mkdir -p "$OUT"
LOG="$OUT/gpu.log"
log() { echo "[$(date -u +%H:%M:%S)] $*" | tee -a "$LOG"; }

log "GPU validation start  -  $CORES cores  -  $(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null || echo 'no nvidia-smi')"

# 1. Probe: does --gpu detect the device and solve the ground-truth case9 correctly?
log "PROBE  case9 --gpu"
$CLI case9 --gpu > "$OUT/probe-case9-gpu.txt" 2>&1
grep -E "backend:|Converged|DID NOT" "$OUT/probe-case9-gpu.txt" | head -4 | tee -a "$LOG"

# 2. Correctness: solve the same cases on CPU and GPU and DIFF the solutions (R057).
# Each solve uses --enableExport=<file>, which runs ONE traced solve, writes the JSON and exits. (A bare case118 /
# case300 / .m with no --once / --enableExport starts the interactive live loop, which never returns with stdout
# redirected: the old script hung here and the benches never ran.) GPU arithmetic is outside the bit-identity
# contract, so the comparison uses an explicit tolerance and prints the exact worst differences.
VM_TOL=1e-6      # pu
VA_TOL=1e-4      # degrees
FAILED=0
compare() {  # compare <label> <cpu.json> <gpu.json>
  python3 - "$1" "$2" "$3" "$VM_TOL" "$VA_TOL" <<'EOF' 2>&1 | tee -a "$LOG"
import json, sys
label, cpu_path, gpu_path, vm_tol, va_tol = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]), float(sys.argv[5])
try:
    cpu = {b["id"]: b for b in json.load(open(cpu_path))["result"]["buses"]}
    gpu = {b["id"]: b for b in json.load(open(gpu_path))["result"]["buses"]}
except Exception as e:
    print(f"COMPARE {label}: FAIL - could not read an export ({e})"); sys.exit(1)
if cpu.keys() != gpu.keys():
    print(f"COMPARE {label}: FAIL - bus sets differ ({len(cpu)} CPU vs {len(gpu)} GPU)"); sys.exit(1)
dvm = max(abs(cpu[i]["vm"] - gpu[i]["vm"]) for i in cpu)
dva = max(abs(cpu[i]["vaDeg"] - gpu[i]["vaDeg"]) for i in cpu)
ok = dvm <= vm_tol and dva <= va_tol
print(f"COMPARE {label}: {'PASS' if ok else 'FAIL'}  max|dVm| = {dvm:.17g} pu (tol {vm_tol:g})  max|dVa| = {dva:.17g} deg (tol {va_tol:g})")
sys.exit(0 if ok else 1)
EOF
  [ "${PIPESTATUS[0]}" -eq 0 ] || FAILED=1
}
solve_pair() {  # solve_pair <label> <case-or-file>
  if ! $CLI "$2" --cpu --enableExport="$OUT/$1-cpu.json" > "$OUT/$1-cpu.txt" 2>&1; then
    log "COMPARE $1: FAIL - CPU solve exited non-zero (see $1-cpu.txt)"; FAILED=1; return; fi
  if ! $CLI "$2" --gpu --enableExport="$OUT/$1-gpu.json" > "$OUT/$1-gpu.txt" 2>&1; then
    log "COMPARE $1: FAIL - GPU solve exited non-zero (see $1-gpu.txt)"; FAILED=1; return; fi
  compare "$1" "$OUT/$1-cpu.json" "$OUT/$1-gpu.json"
}
# The export routes by SolverSelector.DenseSparseCrossoverBuses (200 buses): case118 takes the GPU dense LU; case300 and
# case1354pegase are above the crossover and take the GPU sparse engine. case118 is the only dense-path comparison.
for c in case118 case300; do solve_pair "$c" "$c"; done   # case118 dense, case300 sparse
# sparse path (PEGASE)
PEG="$(find data/matpower -name 'case1354pegase.m' | head -1)"
if [ -n "$PEG" ]; then solve_pair pegase1354 "$PEG"; else log "COMPARE case1354pegase: FAIL - case file not found"; FAILED=1; fi

# 3. CPU-vs-GPU bench (both backends, same machine). Uses a modest run count; giants still slow.
log "BENCH --gpu (all cases)"
# DOTNET_GCHeapHardLimit=0xC80000000 caps the managed heap at 50 GiB (53,687,091,200 bytes = 50 x 2^30), the same
# cap as run-regime.sh, so a giant case raises a catchable OutOfMemoryException rather than being OS-killed.
DOTNET_GCHeapHardLimit=0xC80000000 $CLI bench --runs 3 --parallel "$CORES" --gpu --out "$OUT/bench-gpu.md" >>"$LOG" 2>&1
log "BENCH --cpu (all cases, same run for apples-to-apples)"
DOTNET_GCHeapHardLimit=0xC80000000 $CLI bench --runs 3 --parallel "$CORES" --cpu --out "$OUT/bench-cpu.md" >>"$LOG" 2>&1

TAR="gridsim-gpu-results-$(hostname)-$TS.tar.gz"
tar czf "$TAR" -C results-gpu "$TS"
log "GPU RUN COMPLETE  ->  $TAR"
echo "$TAR"
# Fail loud: a CPU/GPU mismatch (or a failed solve) is not a successful validation run, even though the benches ran.
if [ "$FAILED" -ne 0 ]; then log "GPU VALIDATION FAILED: at least one CPU/GPU comparison failed (see COMPARE lines)"; exit 1; fi
