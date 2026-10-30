#!/usr/bin/env bash
# GridSim server test regime. Run from the deployed publish dir (contains ./gridsim and data/).
# Produces results/<timestamp>/ with headered markdown + CSV + run-metadata.json per phase, then
# tars it for scp retrieval. Every report is stamped with machine + git-commit (RunInfo).
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE"
CLI="./gridsim"
CORES="$(nproc)"

# Cap the managed heap below physical RAM (57 GB box -> 50 GiB: 0xC80000000 = 53,687,091,200 bytes = 50 x 2^30;
# D-DOC-193, DOC-17: this comment used to say 48 GiB, the value has always been 50 GiB) so a giant case (up to ~78k buses)
# raises a catchable OutOfMemoryException - recorded per-case as OOM - rather than being OS-killed
# and taking the whole run down. Per-phase `timeout` stops any single phase wedging the regime;
# results are flushed per row, so a killed phase still leaves its completed rows on disk.
export DOTNET_GCHeapHardLimit=0xC80000000
tmo() { timeout --signal=TERM "$@"; }
TS="$(date -u +%Y%m%d-%H%M%S)"
OUT="results/$TS"
mkdir -p "$OUT"
LOG="$OUT/regime.log"

log() { echo "[$(date -u +%H:%M:%S)] $*" | tee -a "$LOG"; }

# Every phase's exit status is recorded, and the script exits non-zero when any phase failed or timed out, so a broken
# run is not reported as a clean one (it used to exit 0 whatever failed). The regime still runs every phase.
FAILED=0
run() { "$@"; local rc=$?; if [ "$rc" -ne 0 ]; then FAILED=$((FAILED + 1)); log "FAILED (exit $rc): $*"; fi; return 0; }

log "GridSim regime start  -  $CORES cores  -  $(uname -srm)"
$CLI case9 >/dev/null 2>>"$LOG" && log "smoke: case9 OK" || { log "smoke: case9 FAILED"; FAILED=$((FAILED + 1)); }

# Phase 1 - convergence + dense/sparse agreement over the whole archive (10 runs each).
log "PHASE 1  bench (all cases, 10 runs, parallel=$CORES)"
run tmo 180m $CLI bench --runs 10 --parallel "$CORES" --out "$OUT/bench.md" >>"$LOG" 2>&1
log "PHASE 1 done"

# Phase 2 - N-1 branch sweeps across the archive, sampled to bound the 10k-30k-bus monsters.
log "PHASE 2  nminus1-sweep --all (sample 3000, parallel=$CORES)"
run tmo 90m $CLI nminus1-sweep --all --sample 3000 --parallel "$CORES" --out "$OUT/nminus1-all.md" >>"$LOG" 2>&1
log "PHASE 2 done"

# Phase 2b - FULL (unsampled) N-1 on the PEGASE cases - the headline scale test.
for c in case1354pegase case2869pegase case9241pegase; do
  P="data/matpower/pegase/$c.m"
  [ -s "$P" ] || P="$(find data/matpower -name "$c.m" | head -1)"
  if [ -n "${P:-}" ] && [ -s "$P" ]; then
    log "PHASE 2b  full N-1 on $c"
    run tmo 30m $CLI nminus1-sweep "$P" --parallel "$CORES" --out "$OUT/nminus1-$c.md" >>"$LOG" 2>&1
  fi
done
log "PHASE 2b done"

# Phase 3 - load-to-collapse across the archive.
log "PHASE 3  stress --all (parallel=$CORES)"
run tmo 60m $CLI stress --all --from 0.5 --to 3.0 --step 0.05 --parallel "$CORES" --out "$OUT/stress.md" >>"$LOG" 2>&1
log "PHASE 3 done"

# Phase 4 - consecutive warm-start solve chains.
for spec in "case118 fixed" "case300 ramp" "case1354pegase.m daily"; do
  set -- $spec
  CASE="$1"; MODE="$2"
  P="$CASE"; [[ "$CASE" == *.m ]] && P="$(find data/matpower -name "$CASE" | head -1)"
  log "PHASE 4  chain $CASE mode=$MODE"
  run tmo 15m $CLI chain "$P" --steps 100 --mode "$MODE" --ramp-pct 1 --out "$OUT/chain-$MODE.md" >>"$LOG" 2>&1
done
log "PHASE 4 done"

# Phase 5 - collect.
TAR="gridsim-results-$(hostname)-$TS.tar.gz"
tar czf "$TAR" -C results "$TS"
log "REGIME COMPLETE  ->  $TAR  ($FAILED failed step(s))"
echo "$TAR"
[ "$FAILED" -eq 0 ]
