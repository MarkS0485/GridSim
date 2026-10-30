# Model and simulation credibility assessment (NASA-STD-7009 framing)

**Assurance statement. Status as at GridSim 1.95.296.** This document assesses the credibility of GridSim as a
computational model, using the credibility factors of NASA-STD-7009 (Standard for Models and Simulations) as the
scoring frame, and noting the ASME V&V 20 verification-and-validation framing where it applies to the solver
cross-checks. It is a companion to the security-facing assurance set (`caf-mapping.md`, `ssdf-mapping.md`,
`slsa-provenance.md`): those map the system and its build to cyber frameworks, this one maps the numerics to a
model-credibility standard.

It is an assessment of existing evidence, not a plan to build new machinery. The evidence base is
`docs/technical/0011 Validation.md` and the test suite; this document indexes that evidence onto the credibility
factors and states, factor by factor, where GridSim is strong, where it is moderate, and the one place it is thin
and why that is an accepted, bounded limit rather than a gap to close. No numerics are changed by this document, and
the constitutional-tests rule applies: nothing here asserts a property that an existing passing test does not already
evidence.

The status vocabulary follows the rest of the assurance set: Achieved, Partially achieved, Not achieved, Out of
scope (deploying-party). For a credibility assessment these read as the strength of the evidence for each factor.

---

## 1. Why a model-credibility frame, alongside the security frames

GridSim's central claim is numerical: the same inputs produce the same bits, on every CPU solver, every SIMD tier, every
CPU and every operating system (GPU arithmetic is outside the contract), and the power-flow, harmonic, impedance and
small-signal results agree with independent reference tools to 4096 machine epsilons; the state-estimation and
dynamics cross-checks, which compare different algorithms, agree to stated engineering tolerances. A cyber
framework does not test that claim; a model-and-simulation credibility standard does. NASA-STD-7009 is the cleanest
such frame because it separates credibility into named factors (verification, validation, input pedigree, results
uncertainty and robustness, use history, management, and people) and asks for evidence against each. GridSim scores
unusually high on the factors that concern the computation itself, precisely because determinism and the
constitutional-tests doctrine were design goals from the start.

---

## 2. Verification (is the maths solved correctly)

**Evidence: strong.**

- `DeterministicMathGuardTests` is an IL-level analyser that scans the compiled `GridSim.Core`, `Gda`, `Gpu` and
  `Cli` assemblies and fails the test run (it is a test in the suite, not a compiler check) if any method calls
  platform-dependent mathematics: the `System.Math` and `System.Numerics.Complex` transcendentals, a fused multiply-add,
  or a horizontal vector reduction. The one allowed exception is the fused arm of `SimdKernels.AddScaledRow`, which a
  second check in the same test proves no numerical path selects. The property is checked over every method of those
  assemblies, not sampled by test cases.
- The bit-identity suite (around thirty files, including `DenseSparseParityTests`, `Doc16NumericsDeterminismTests`,
  and the `*BitIdentity*` and `*Pin*` families) asserts that the dense and sparse backends, and the SIMD tiers,
  produce identical IEEE-754 bits for voltages, angles, injections, flows, losses, mismatch and iteration counts.
- Cross-tier portability is pinned by the SIMD-seam tests and the fdlibm strict-math cross-language vectors.

This is verification to the bit, which is a stronger standard than the tolerance-based verification the standard
expects.

## 3. Validation (is the right maths being solved)

**Evidence: strong.**

- The Python cross-validation harnesses under `tools/crossval/` re-solve GridSim's problems in independent tools and
  compare every bus: power flow against pandapower, cases against the MATPOWER library, dynamics against SciPy
  `solve_ivp`, eigenvalues against `scipy.linalg.eig`. The default agreement tolerance is 4096 machine epsilons
  (about 9.1e-13), and the measured power-flow agreement reported in `docs/technical/0011 Validation.md` is 5.6e-12
  per unit.
- `N243CrossvalMachineEpsilonTests` pins the tolerance contract in-suite, so the validation bar cannot silently
  loosen.
- Published benchmark results are reproduced: IEEE 14/30/57/118/300 bus cases, the IEEE 123-node and 4-node
  distribution feeders against their published voltages and currents, and the GB reduced and 2224-bus models with
  independent PYPOWER cross-checks.
- Real measured grid data is validated: the 9 August 2019 GB frequency event is reconstructed from first principles
  (`Asset2019EventTests`), scoped honestly as a mechanism-and-envelope reproduction rather than a point prediction,
  because only one such GB cascade event exists to validate against (documented in
  `docs/technical/0014 Cascade validation and blind prediction.md`).

## 4. Input pedigree (do the inputs have known provenance)

**Evidence: strong.** Every emitted series carries a `ProvenanceTag` (Recorded, DerivedFromRecorded,
CuratedPlaceholder, Synthetic, Counterfactual, Interpolated, UnvalidatedAtScale), so the trust class of every datum
is known. `ProvenanceTrust` refuses an output tag offered as an input and a computed tag raised above its inputs, so a
tag only moves down through a computation; no code can check that a `Recorded` tag in a hand-written file is backed by
a record, and the forward-inference guard checks time stamps only and never reads a tag. Benchmark inputs carry SHA-256 provenance and licence
records (`data/matpower/README.md`). The forward-inference guard is the input-admissibility control: an undated input
is refused, not assumed old, and an input newer than the 169-hour horizon is rejected at ingestion.

## 5. Results uncertainty (is the uncertainty in the results quantified)

**Evidence: thin, and recorded as a bounded, accepted limit.**

This is the one factor where GridSim does not score strongly, and the assessment says so plainly. There is a
Monte-Carlo accuracy and chi-square calibration sweep (`tools/crossval/mc_estimate_sweep.py`, with a committed golden
sweep that reproduces byte-for-byte), but it is explicitly an analysis, not a gate, and there is no formal
input-uncertainty propagation or sensitivity-analysis framework.

This is an accepted limit, not an open remediation item, for two reasons. First, GridSim's results are deterministic
and bit-reproducible, so the numerical uncertainty of a given solve is, by construction, zero to machine epsilon;
the uncertainty that a full UQ framework would quantify is input uncertainty, which is a property of the deploying
party's data, not of the engine. Second, building a general UQ layer sits in tension with the bit-identity contract
and would be disproportionate to the engine's role as a historical state estimator. It is therefore handled the same
way the assurance set handles the feasible-landing FDIA residual in `caf-mapping.md`: named as a scoped, accepted
limit with the reasoning stated, not hidden and not pretended away. If a deploying party needs formal UQ over its own
inputs, that is a layer above the engine.

## 6. Results robustness (do the results hold across conditions)

**Evidence: moderate to strong.** Robustness across the dimensions GridSim controls is tested to the bit:
dense versus sparse solver, SIMD tier, CPU and operating system all produce identical results. Every release is gated
on the CI matrix that runs the suite against the same pins on Linux and Windows (normal, AVX2 off, hardware
intrinsics off, forced AVX-512 and FMA off) and on macOS on Apple silicon (normal and hardware intrinsics off), with
no advisory leg; between releases the matrix runs by hand and the local batch run is the day-to-day check. These
tests compare numerical results; they are not a reproducible-build check of the binaries. Robustness across
input regimes is exercised by the benchmark corpus (transmission, distribution, GB models) and the hardware runs in
`docs/developer/benchmarks/server-results/` (A100, H100, MI300, CPU).

## 7. Use history (has the model been used and checked over time)

**Evidence: moderate.** The benchmark corpus, the per-release CHANGELOG discipline, and the recorded validation
figures tied to specific tool versions (pandapower 3.5.5, NumPy 2.4.6, SciPy 1.18.1) give a traceable use and check
history. The engine is young, so use history is recorded as moderate rather than strong.

## 8. Management (is the model under configuration control)

**Evidence: strong.** A single `<Version>` (`1.95.NNN`) governs every assembly, enforced by `VersionContractTests`;
every commit bumps the version and adds a CHANGELOG heading; the tests are constitutional and cannot be loosened to
pass a change. This is configuration management at a standard most simulation codebases do not reach.

## 9. People qualifications

**Status: Out of scope (deploying-party).** The standard's people factor concerns the staffing of the modelling
effort, which is not a property of the engine and is not assessed here.

---

## 10. Factor-to-evidence matrix

| Credibility factor | Evidence | Strength |
|---|---|---|
| Verification | `DeterministicMathGuardTests`, bit-identity suite, SIMD-seam tests | Strong |
| Validation | `tools/crossval/*` vs pandapower/MATPOWER/SciPy; benchmark reproduction; 2019 GB event | Strong |
| Input pedigree | `ProvenanceTag`; benchmark SHA-256 provenance; forward-inference guard | Strong |
| Results uncertainty | `mc_estimate_sweep.py` only; no formal UQ | Thin (bounded, accepted) |
| Results robustness | cross-solver/tier/CPU/OS bit identity; benchmark and hardware runs | Moderate to strong |
| Use history | benchmark corpus; versioned validation figures | Moderate |
| Management | single-version contract; CHANGELOG; constitutional tests | Strong |
| People | not a property of the engine | Out of scope |

---

## 11. Credibility register

One substantive item, carried as accepted:

1. **Formal uncertainty quantification (results-uncertainty factor).** No input-uncertainty propagation or
   sensitivity framework beyond the Monte-Carlo sweep. Accepted as a bounded limit (section 5): the engine's own
   numerical uncertainty is zero to machine epsilon by construction, and input uncertainty is a deploying-party
   property. Recorded, with its reasoning, as an accepted limit rather than a remediation item. Revisited only if the
   engine's role changes from historical estimation to something that owns input uncertainty.

The assessment's guiding rule matches the rest of the set: a factor is scored strong only where an enforced control
or a passing test evidences it, and the one thin factor is named as thin, with the reason it is accepted stated in
full.
