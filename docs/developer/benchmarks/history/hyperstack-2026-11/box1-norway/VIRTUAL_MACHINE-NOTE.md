# VIRTUAL_MACHINE - read before using these results

Run: suite-full-20261117T2131Z on hyperstack-1 (Hyperstack, Norway), started 2026-11-17 21:31Z.

- Host: KVM guest (`systemd-detect-virt` = kvm, `hypervisor` CPU flag set), 4 vCPU, 20 GB RAM, Ubuntu 24.04.3.
- CPU: AMD EPYC 7452 (Zen 2), reported 2350 MHz; no cpufreq/governor visible to the guest, so clock and boost are
  controlled by the host and not recorded.
- SIMD exposed to the guest: SSE4.2, AVX, AVX2, FMA = yes; AVX-512 = no (Zen 2 has no AVX-512 on bare metal either,
  so this is not the VM masking it). GridSim's AVX2 paths should therefore be selectable; not yet confirmed from
  GridSim's own `simdcheck` on this box (deferred so as not to disturb the running benchmark).
- Not known: whether the 4 vCPUs are SMT siblings on the host, host contention (noisy neighbours), and steal time.
- Run mode: `--jobs 1 --pin`, one solver path at a time on CPU 1, Release driver.

Use: compare solvers within this run only. Do not compare these timings with bare-metal or laptop runs, and do not
publish them as GridSim's performance without this note.

To confirm SIMD targeting (after the run): `gridsim simdcheck` on the box, and one case timed with
`DOTNET_EnableAVX2=0` vs default; record `/proc/stat` steal time before and after a run.
