# VIRTUAL_MACHINE - read before using these results

Run: suite-full-20261117T2356Z on hyperstack-2 (Hyperstack, Canada), started 2026-11-17 23:56Z.

- Host: KVM guest (systemd-detect-virt = kvm, hypervisor flag set), 28 vCPU, 56 GB RAM, Ubuntu 24.04.3, kernel 6.14.0-37-generic.
- CPU: AMD EPYC 7763 (Zen 3), 64-core part; guest topology 2 sockets x 14 cores x 1 thread, 2 NUMA nodes, L2 14 MiB (28 inst), L3 448 MiB (28 inst, shared on host).
  No cpufreq/governor visible to the guest, so clock and boost are controlled by the host and not recorded.
- SIMD exposed to the guest: SSE4.2, FMA, AVX2 = yes (28/28 vCPUs); no avx512* flags exposed (Zen 3 has no AVX-512 on bare metal either).
- GPU present (NVIDIA L40) but not used by the harness.
- Not known: host SMT siblings, host contention (noisy neighbours), steal time (cpu line of /proc/stat before run: steal=62).
- Run mode: --jobs 1 --pin, one solver path at a time pinned to one core, Release driver, TieredCompilation=false
  (harness ae2f53a, GridSim d629adda v2.1.071-RC-3, same versions as hyperstack-1). 67-case suite.
- Software: .NET SDK 10.0.115, Octave 8.4.0, MATPOWER 8.1, Python 3.12.3 (venv --copies, pinned lock), Julia 1.10.12.

Use: compare solvers within this run only. Do not compare these timings with bare-metal or laptop runs, and do not publish them as GridSim's performance without this note.
