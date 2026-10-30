#!/usr/bin/env bash
# Fetch the full standard-case universe into <target>/data/matpower so the bench / N-1 / stress
# verbs pick them all up (they enumerate *.m recursively). Idempotent: skips files already present.
# GR-445: every download fails on an HTTP error (curl -f) and goes to a temporary file that is moved into place only on
# success, so a 404 / 429 body is never stored as a case (and then kept for ever by the skip rule). PGLib is still read
# from its master branch: pin a commit and add a checksum list before relying on these files for published results.
#   usage: fetch-cases.sh [target-dir]   (default: current dir)
set -euo pipefail

TARGET="${1:-.}"
MP="$TARGET/data/matpower"
mkdir -p "$MP/pglib" "$MP/matpower-all"

echo "== Fetching full PGLib-OPF archive =="
PGLIB_API="https://api.github.com/repos/power-grid-lib/pglib-opf/git/trees/master?recursive=0"
mapfile -t PGLIB < <(curl -fsS "$PGLIB_API" | grep -oE '"path": "pglib_opf_case[^"]*\.m"' | sed 's/"path": "//;s/"//')
echo "  ${#PGLIB[@]} PGLib cases found"
for f in "${PGLIB[@]}"; do
  out="$MP/pglib/$f"
  [ -s "$out" ] && { echo "  skip $f"; continue; }
  if curl -fsS -m 120 -o "$out.part" "https://raw.githubusercontent.com/power-grid-lib/pglib-opf/master/$f"; then
    mv "$out.part" "$out"; echo "  got  $f ($(wc -c < "$out") B)"
  else rm -f "$out.part"; echo "  FAIL $f"; fi
done

echo "== Fetching MATPOWER data/ case files =="
MP_API="https://api.github.com/repos/MATPOWER/matpower/git/trees/8.0?recursive=1"
mapfile -t MCASES < <(curl -fsS "$MP_API" | grep -oE '"path": "data/case[^"]*\.m"' | sed 's/"path": "data\///;s/"//')
echo "  ${#MCASES[@]} MATPOWER cases found"
for f in "${MCASES[@]}"; do
  out="$MP/matpower-all/$f"
  [ -s "$out" ] && { echo "  skip $f"; continue; }
  if curl -fsS -m 120 -o "$out.part" "https://raw.githubusercontent.com/MATPOWER/matpower/8.0/data/$f"; then
    mv "$out.part" "$out"; echo "  got  $f ($(wc -c < "$out") B)"
  else rm -f "$out.part"; echo "  FAIL $f"; fi
done

TOTAL=$(find "$MP" -name '*.m' | wc -l)
echo "== Done. $TOTAL .m case files under $MP =="
