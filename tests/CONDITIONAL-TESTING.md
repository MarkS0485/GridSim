# Conditional testing: the RAM gate

GridSim's test suite includes power-flow cases of up to 9,241 buses. Not every machine can hold them: on an 8 GB
laptop, the dense solves of the largest cases push the test host into swap or out-of-memory. From 1.95.289, the
**installed RAM decides at test discovery which large-case tests this machine runs**. A test the machine physically
cannot execute is reported as **Skipped, with the reason**. It is never reported as passed.

Code: `tests/GridSim.Tests/MachineCapacity.cs`. Tests of the gate itself: `MachineCapacityTests.cs`.

## Validation is not weakened

- **Only when a test runs changes. What it checks does not.** Every gated test keeps its assertions exactly as
  they were: the same exact `Assert.Equal`, the same bit-for-bit dense/sparse comparisons, and the same tolerances.
  The gate is an attribute on the test (`[LargeCaseFact]`, `[LargeCaseInlineData]`); the test body is untouched.
- **The tests assert up to a given limit.** Each gated test belongs to one case of a known size (gb-full 3,539 buses,
  continental-europe 5,824, case9241pegase 9,241). A machine validates every case up to its cap. Everything above
  the cap is listed as skipped, so the report states exactly what was and was not validated on that machine.
- **The results stay bit-identical on any machine that can run more.** Only the test assembly reads the gate. No
  solver, loader or study in Core or Cli does, so a number GridSim computes never depends on the RAM it runs on. A
  16 GB machine runs the gated tests with the same code and gets the same bits. The bit-identity rules (across runs,
  parallel paths, CPUs, and the dense and sparse engines) apply to every test that runs, at every tier.
- **Nothing is dropped from the count.** passed + skipped is the same total on every machine. A gated case is a
  skipped row, not a missing one.
- **Releases run everything.** The release workflow sets `GRIDSIM_TEST_MAX_BUSES=all`, so no release is validated
  with a test left out by the gate.

This is Mark's ruling of 2026-10-12 on the constitutional test rule (CONTRIBUTING.md). It does not loosen the rule, because
it changes only when a test applies. A machine that cannot run a test skips it, and the validation stands wherever
the hardware can run it.

## The tiers

The machine is classed by **installed** memory, never free memory, so one machine always runs the same set of tests.
The figure is the .NET runtime's total available memory, which is the installed RAM on an ordinary machine; inside a
container or job object with a memory limit, or with `GCHeapHardLimit` set, it is that limit instead.
The figure is rounded up to whole GB, because an "8 GB" laptop reports about 7.7 GB once firmware and graphics have
taken their share.

| Installed RAM | Sparse cap (buses) | Dense cap (buses) | What runs |
|---|---|---|---|
| below 8 GB | 3,600 × GB / 8 | 3,600 × √(GB / 8) | small cases only; below the minimum system |
| 8 GB (minimum) | 3,600 | 3,600 | everything up to gb-full (about 1.5 GB free needed to study it) |
| 12 GB | 6,420 | 7,012 | adds continental-europe |
| 16 GB and up | unlimited | unlimited | the whole suite (slowly, with some swapping on 16 GB) |

Between 8 and 16 GB, the sparse cap rises linearly with RAM. The dense cap rises along n² instead, because dense
storage is about 32·n² bytes per matrix.

## Forcing a tier

`GRIDSIM_TEST_MAX_BUSES` overrides the cap for both engines:

- `all` runs every test, whatever the RAM (used by the release workflow, or to try a large case by hand).
- a positive whole number sets that cap.
- any other value fails loud at test discovery. It is never quietly ignored.

```
GRIDSIM_TEST_MAX_BUSES=all dotnet test GridSim.slnx -c Release --no-build
```

`MachineCapacityTests.This_machine_reports_its_tier` prints the machine's tier in its test output.

## What is gated

Only tests that **solve** a case above the 8 GB anchor are gated. Loading, parsing and validating a large case uses
memory in proportion to the file (a few MB), so those tests run everywhere.

| Test | Case | Engine |
|---|---|---|
| `DenseSparseParityTests.Case9241pegase_is_bit_identical_dense_vs_sparse` | case9241pegase | dense |
| `DenseSparseParityTests.Json_case_is_bit_identical_dense_vs_sparse` (continental-europe row) | continental-europe | dense |
| `SolverPropertyTests.Parsed_pegase_case_obeys_conservation` (9241 row) | case9241pegase | sparse |
| `MatpowerParserTests.Large_case_parses_and_converges` (9241 row) | case9241pegase | sparse |
| `S4SolverDataFollowUpsTests.N113_the_three_maps_do_not_solve_...` (continental-europe row) | continental-europe | sparse |
| `ConvertAndValidateTests.Every_bundled_case_round_trips_...` (both writers, 9241 row) | case9241pegase | sparse |

gb-full tests are not gated: the 8 GB minimum system runs them.

## Adding a large case

1. Put its bus count in `CaseSizes` (and, for a MATPOWER file, in `CaseSizes.LargeMatpowerFiles`).
   `MachineCapacityTests` fails if a bundled MATPOWER file above 3,600 buses is missing from that list, or if a
   constant does not match the loaded case.
2. Mark a test that solves it with `[LargeCaseFact(buses, engine)]`, `[LargeCaseTheory(buses, engine)]`, or a
   `[LargeCaseInlineData(buses, engine, ...)]` row. Leave the body as it is.
