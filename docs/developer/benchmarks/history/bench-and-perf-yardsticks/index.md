# Bench matrix and single-thread yardsticks, July and August 2026

*Status: historical. Measured on early builds (Core v0.1 and v1.3) before the RC4 ordering and numerical changes. Superseded by the [RC4 (2.1.073) results](../../rc4-2.1.073/index.md). Back to the [benchmarks landing page](../../index.md).*

These files hold the first single-machine benchmark data and the `bench --perf` single-thread yardstick. The user manual
chapter [Benchmarking and GPU](<../../../../manual/0028 Benchmarking and GPU.md>) quotes them.

| File | Measured | Run |
| --- | --- | --- |
| [`benchmark-results.csv`](benchmark-results.csv), [`run-metadata.json`](run-metadata.json) | CPU bench matrix of 25 cases, three engines, 10 runs each | 2026-07-08, 8-core Windows desktop, Release |
| [`benchmark-results.md`](benchmark-results.md) | pointer to the manual (the report itself is a stub) | |
| [`bench-estimate.csv`](bench-estimate.csv) | state-estimation timing per case (measurements, states, dense and sparse), mean of 5 runs | commit not recorded |
| [`perf-after.md`](perf-after.md), [`perf-after.csv`](perf-after.csv) | single-thread yardstick after the performance programme, median of 20 runs | 2026-07-19, 8-core Windows desktop, Debug build |
| [`perf-baseline.md`](perf-baseline.md), [`perf-baseline.csv`](perf-baseline.csv) | single-thread yardstick on the reference server, median of 40 runs | 2026-08-11, AMD EPYC 7763, 28 logical cores, Release |

The two yardsticks run on different machines and build configurations, so their numbers are not comparable with each other.
