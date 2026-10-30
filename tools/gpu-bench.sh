#!/usr/bin/env bash
# One-shot GridSim benchmark for a rented box (CPU / CUDA / MI-series). Runs the fail-fast preflight,
# the full engine matrix, and a couple of solve exports, into a timestamped, self-describing bundle.
#
# Usage:
#   ./gpu-bench.sh "H100 SXM 80GB - \$2.99/hr RunPod"           # NVIDIA/AMD box (auto --gpu)
#   ./gpu-bench.sh "EPYC 9654 96c - baseline" --cpu-only        # CPU baseline box
#
# The NOTE (arg 1) is stamped into the report header + run-metadata JSON so each report states the
# exact hardware and money. scp the resulting results/<stamp>.tar.gz back and compare.
set -uo pipefail

NOTE="${1:-unlabelled run}"
GPU_FLAG="--gpu"
[ "${2:-}" = "--cpu-only" ] && GPU_FLAG=""

# Find the CLI: a published self-contained binary (preferred on a bare box), else the SDK.
if   [ -x ./gridsim ]; then BIN="./gridsim"
elif [ -x ./GridSim.Cli ]; then BIN="./GridSim.Cli"   # a publish made before the October 2026 rename of the binary to gridsim (F03)
elif command -v dotnet >/dev/null 2>&1 && [ -f src/GridSim.Cli/GridSim.Cli.csproj ]; then
     BIN="dotnet run -c Release --project src/GridSim.Cli --"
else echo "ERROR: no gridsim binary and no dotnet SDK found. Publish first (see the manual, chapter 28 Benchmarking and GPU, section Renting a GPU server: Publish once)." >&2; exit 1
fi

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="results/$STAMP"
mkdir -p "$OUT"

echo "======================================================================"
echo " GridSim benchmark run"
echo "   note : $NOTE"
echo "   mode : ${GPU_FLAG:-CPU only}"
echo "   cli  : $BIN"
echo "   out  : $OUT"
echo "======================================================================"

echo; echo "[1/3] preflight (gpucheck) - go/no-go for each backend ..."
$BIN gpucheck 2>&1 | tee "$OUT/preflight.txt"

echo; echo "[2/3] engine matrix (this is the main result) ..."
$BIN bench $GPU_FLAG --note "$NOTE" --out "$OUT/bench.md"

echo; echo "[3/3] solve exports (full math trace of one solve per engine class) ..."
# case300 is the largest built-in case (case2869 was never a recognised name - it exited 2, masked by
# `|| true`, so the large-case export was silently missing from every bundle).
$BIN case118 $GPU_FLAG --enableExport="$OUT/case118-solve.json" 2>&1 | tail -1 || true
$BIN case300 $GPU_FLAG --enableExport="$OUT/case300-solve.json" 2>&1 | tail -1 || true

echo; echo "bundling ..."
tar czf "$OUT.tar.gz" -C results "$STAMP" && echo "  -> $OUT.tar.gz"
echo
echo "DONE. Bundle: $OUT.tar.gz   (scp it back, then compare bench.md across boxes)."
