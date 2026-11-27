# 36. Determinism and reproducibility

GridSim gives the same answer to the same question. Run the same case with the same options on the same build and you
get the same numbers to the last bit, on any thread count, any CPU instruction-set tier and any operating system. This
chapter says what you can rely on, how to check it yourself, the three places where it does not hold, and how a release
tells you when results move. The specification and the tests are in
[technical ch. 46](<../technical/0046 Determinism - specification and evidence.md>), and a short standalone summary for
reviewers is [Understanding GridSim determinism](<../Understanding GridSim determinism.md>).

## Before you start

- Two result files from the same verb. `--json <file.json>` (or `--out`) writes every number at full round-trip
  precision ([ch. 19](<0019 CLI reference.md#result-files>)).
- A hashing tool: `sha256sum` on Linux and macOS, `Get-FileHash` on Windows PowerShell.

## What you can rely on

For one build of GridSim, the same case and options give bit-identical results:

- from run to run on one machine;
- between a sequential and a parallel run, and between 1, 2, 4 or any number of workers;
- on every CPU tier (AVX-512, AVX2, SSE, no SIMD), with or without hardware fused multiply-add;
- on Windows, Linux and macOS, on x64 and 64-bit Arm (first-generation AMD Zen, Zen 1, is the observed exception:
  deterministic on that CPU but not bit-identical to other machines);
- under any language and number-format setting of the machine: GridSim writes and reads numbers in the invariant culture.

Dense and sparse Newton-Raphson agree to the bit, so the size of the network does not change which numbers you get.

## Check that two runs match

Run the verb twice, hash each result file, and compare.

```
gridsim pf case118 --json run1.json
gridsim pf case118 --json run2.json
sha256sum run1.json run2.json
```

On Windows PowerShell, `Get-FileHash run1.json, run2.json`. Equal hashes mean equal bits. The result file records the
tool version and its own arguments, so write both runs to the same file name (one after the other, hashing between) or
compare the `summary` and `tables` of the two documents instead. To compare two machines, run the same build with the same
arguments and compare the hashes. `gridsim --version` prints the release and commit, and `gridsim simdcheck` prints the
CPU tier in use.

`--journal <file.jsonl>` records the SHA-256 of every input and output of a run for you
([ch. 19](<0019 CLI reference.md>)).

## The stated exceptions

- **The `cycle` verb.** It runs its analysis jobs against a wall-clock budget, so which jobs finish in a cycle depends on
  the machine's speed, load and core count. Each job that runs gives the same result it gives on its own. To pin a study,
  run it directly: `nminus1`, `screen` or `physics-check`.
- **The GPU path.** A GPU solve is deterministic, and repeats on the same build and device, but it is not bit-identical to
  the CPU. It agrees with the CPU to about 1e-12 per unit in voltage ([ch. 28](<0028 Benchmarking and GPU.md>)). If the
  GPU gives up on a case, the exact CPU solve takes over and matches the CPU to the bit.
- **Dense and sparse weighted least squares.** The two state-estimation steps factorise differently and do not agree to
  the bit. A CPU estimate always takes the sparse step, so the bus count does not change the result. The dense step runs
  only with a GPU linear solver, and the constrained, feasible and QR estimators are dense only.

Loss allocation across workers agrees with the sequential run within 1e-6 MW rather than to the bit.

Files that record wall time differ between two identical runs: the `summary.md` of `emt` and `transient`, and
`planning-study`'s `evidence.json`. Hash the result JSON, not those files.

## When results move between releases

A new release can change numbers. Every change that moves a result is labelled **BREAKING CHANGE (Bnn)** at the start of
its entry in `CHANGELOG.md`, followed by the size of the move. A release with no such label gives the same bits as the
one before it on the same inputs. When a label applies to your verb, the entry says how large the move is, and
`gridsim breaking-change validate` re-runs the entries in a run journal on a later build and reports whether each
difference is explained by a recorded change
([technical ch. 42](<../technical/0042 Run journal and breaking-change validation.md>)).

To get an old release's bits again, use the same release: the build pins one exact SDK version, restores packages from
lock files, and records its commit in `gridsim --version`.

## Pitfalls

- Comparing result files from different builds or different output paths: the tool version and arguments are inside the file.
- Comparing a `cycle` run against another `cycle` run and expecting the same job set.
- Expecting a GPU run to hash like a CPU run.

## Limits

- Bit-identity is for one build on one .NET major version. A different .NET major version is re-proved before it is trusted.
- Determinism is a statement about repeating the arithmetic exactly. It is not a validation against measurements of a real grid.
- Not every environment has been tested. The author's own observations, including first-generation AMD Zen, which is
  deterministic but not bit-identical to the reference results, are in
  [Understanding GridSim determinism](<../Understanding GridSim determinism.md>).

## See also

- [Understanding GridSim determinism](<../Understanding GridSim determinism.md>)
- [Technical ch. 46, Determinism: specification and evidence](<../technical/0046 Determinism - specification and evidence.md>)
- [Technical ch. 8, Provenance and invariants](<../technical/0008 Provenance and invariants.md#determinism-and-bit-identity-as-implemented>)
- [Benchmarking and GPU](<0028 Benchmarking and GPU.md>)
- [CLI reference](<0019 CLI reference.md>)
