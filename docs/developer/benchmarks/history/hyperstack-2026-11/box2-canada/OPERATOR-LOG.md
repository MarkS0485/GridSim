# Operator log hyperstack-2 (2026-11-17T23:46:39Z)
- 23:46Z hyperstack-2 reachable (28 vCPU). Wrote prep-benchmark-server-v2.sh (checkout harness ae2f53a / GridSim d629adda, Release build via build-driver.sh instead of Debug). Started on server, log ~/prep.log
- 23:52Z prep done (log in server ~/prep.log): harness/GridSim revisions + box spec in box.txt
- 23:56Z 2-case check (case4gs.m case5.m) OK, all 9 paths 10/10. FULL SUITE started --jobs 1 --pin, log ~/suite-full.log (name in ~/runname)
- made local make-charts.py copy with box-2 footer
- wrote VIRTUAL_MACHINE.md (run suite-full-20261117T2356Z)
- 01:22Z suite finished; downloading
- verifying manifest sha256 (local run.tar had text prefix from tail; stripped before extraction)
- 01:49Z download verified: 8833/8833 files SHA-256 OK. Charts + SUMMARY.md written.
