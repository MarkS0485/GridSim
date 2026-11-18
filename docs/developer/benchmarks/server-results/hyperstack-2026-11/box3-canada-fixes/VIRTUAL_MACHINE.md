# VIRTUAL_MACHINE - read before using these results

Run: hyperstack-3 (Hyperstack, Canada), session 2026-11-18 08:30-11:00Z. Runs: focused-base/cand r1-r3, full-base, full-cand (sparse+dense), focused-others.

- Host: KVM guest (systemd-detect-virt = kvm), 28 vCPU, 56 GB RAM, Ubuntu 24.04.3, kernel 6.14.0-37-generic.
- CPU: AMD EPYC 7763 (Zen 3), guest topology 2 sockets x 14 cores x 1 thread, 2 NUMA nodes, L2 14 MiB, L3 448 MiB; no cpufreq visible. Same CPU class as hyperstack-2.
- SIMD exposed: SSE4.2, FMA, AVX2; no AVX-512. GPU (NVIDIA RTX A6000) present, unused.
- Steal time (/proc/stat 8th field): 207 ticks before the first run, 912 after the last timing run (about 7 s over about 2 h, negligible; per-run values are in each *.log).
- Run mode: --jobs 1 --pin, one solver path at a time pinned to one core, Release driver, TieredCompilation=false, harness ae2f53a, GridSim d629adda (RC3) and candidate 7dbbcfb (RC3 + src.patch, local commit, not pushed).
- Software: .NET SDK 10.0.115 (driver), 10.0.401 (tests only), Octave 8.4.0, MATPOWER 8.1, Python 3.12.3, Julia 1.10.12.

Use: timings are comparable only within this run. Do not compare with bare-metal or laptop runs, and do not publish as GridSim performance without this note. Candidate figures are an unreleased candidate, not RC3.
