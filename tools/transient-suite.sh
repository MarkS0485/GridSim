#!/usr/bin/env bash
# Demanding time-domain test suite: run the IEEE ladder (case9->case1354pegase) as a headless transient
# for a simulated duration at a fine slice, integrating frequency every slice. WARNING: 60 min @ 1 ms is
# 3.6M warm-started solves PER case - hours each on the big ones. AutoFlush makes it Ctrl-C-safe.
#
# Usage:
#   ./transient-suite.sh                       # 60 min @ 1 ms, steady (very long)
#   ./transient-suite.sh 5 1                    # 5 min @ 1 ms (300k slices/case) - sane smoke of the suite
#   ./transient-suite.sh 60 1 "--trip 1000 --trip-at 30"   # inject a 1 GW trip at t=30s
set -uo pipefail

MIN="${1:-60}"; DT="${2:-1}"; EXTRA="${3:-}"

if   [ -x ./gridsim ]; then BIN="./gridsim"
elif command -v dotnet >/dev/null 2>&1 && [ -f src/GridSim.Cli/GridSim.Cli.csproj ]; then
     BIN="dotnet run -c Release --project src/GridSim.Cli --"
else echo "ERROR: no gridsim binary and no dotnet SDK found." >&2; exit 1
fi

mkdir -p results
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="results/transient-suite-$STAMP"

echo "======================================================================"
echo " GridSim transient suite  ::  ${MIN} sim-min @ ${DT} ms  ::  cases 9..1354"
echo "   out : $OUT"
echo "======================================================================"

# `--cases suite` expands to case9,case14,case30,case57,case118,case300,case1354pegase (ascending).
# Capture the transient exit code (PIPESTATUS[0], since the pipe to tee would otherwise mask it) and
# propagate it as the suite's exit status, so a failed run doesn't report success.
$BIN transient --cases suite --minutes "$MIN" --dt-ms "$DT" --out "$OUT" $EXTRA 2>&1 | tee "$OUT.log"
rc=${PIPESTATUS[0]}

tar czf "$OUT.tar.gz" -C results "$(basename "$OUT")" 2>/dev/null && echo "bundled -> $OUT.tar.gz"
echo "DONE. Per-case: timeseries.csv, exports/*.json, summary.md under $OUT/<case>/."
exit "$rc"
