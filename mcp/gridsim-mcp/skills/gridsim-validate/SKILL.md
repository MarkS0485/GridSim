---
name: gridsim-validate
description: Use when comparing GridSim with the rival tools (pandapower, MATPOWER, PowerModels) or with another GridSim build, when triaging a disagreement, and when running the case lab - the maintainer's divergence protocol with compare, crossval_single, divergence_triage, build_regression and the caselab tools.
---

# Comparing GridSim and triaging a disagreement (maintainer protocol)

The validation tools (`compare`, `crossval_single`, `divergence_triage`, `build_regression`) exist only when the plugin
option `enable_compare` is on and the Lab Mode harness folder is set. If they are not listed, they are not available:
say so and stop. Do not reach for another route to the rival tools.

## The rules

1. **A disagreement is evidence, not proof.** It says where to look, never which side is wrong. Every tool is first
   judged against physics by its own gate (residual, feasibility, independent reference); only answers that pass are
   compared.
2. **No change to GridSim's solver core or determinism is proposed without Mark.** A finding describes what was seen
   and the evidence. A proposed fix to core numerics or determinism is written as a question for Mark, not as a patch.
3. **A test change needs a mathematical proof** that the old expectation was wrong (derivation, closed form or an
   independent reference to the stated tolerance). "The tools disagree" is not a proof.
4. **Findings go to the private store** `.gridsim-mcp/findings/findings.jsonl` in the workspace (divergence_triage
   writes there), never into the GridSim repository, its docs or its tests.
5. Never tune a tolerance or a setting to make a disagreement go away. Record it.

## The protocol

1. **Preflight.** `preflight`: the build tag, version and commit of the GridSim under test. A comparison is about that
   build; say which.
2. **Reproduce first.** `reproduce_check` on the GridSim side: a result that does not reproduce bit for bit on the same
   build is itself the finding (and a determinism question for Mark).
3. **Compare.** `compare(mode, case, tools)`: one correctness-only Lab cell. Report, per tool, the status (`pass`,
   `gate-fail`, `n/a`, `unavailable`) with its reason, the cross-check, and the element diff maxima (max |dVm|, |dVa|
   per bus; |dP|, |dQ| per branch; dPg per bus for dcopf) with where they occur. Cite the bundle path and the case
   SHA-256 the harness solved. The first call starts the rival workers (minutes); later calls reuse them.
4. **Triage.** `divergence_triage(bundle)` sorts each rival tool into exactly one class:
   - **GridSim gate fail**: GridSim's own answer fails the physics gate. Highest priority: cite the gate reason and
     the physics-check and residual evidence. Report to Mark; no core change proposed.
   - **rival gate fail**: the other tool's answer fails the gate. Not a GridSim finding, but record it.
   - **documented model difference**: a known, written-down difference explains it: PowerModels' `compute_dc_pf`
     ignores branch tap ratios and phase shifts; MATPOWER's CPF limits the slack generator's Q (GridSim does not);
     GridSim refuses the EXDC2 exciter that ANDES runs. Name it; it is not a disagreement.
   - **genuine disagreement**: both answers pass their gates, yet the cross-check fails or the element diff exceeds
     the triage thresholds. This is the interesting class: attach both sides' evidence and write it up for Mark.
   - **agreement** (both pass, everything agrees) and **not compared** (a side did not run; the reason says why).
5. **Evidence of both sides.** divergence_triage attaches each harness gate, GridSim's `physics-check`, and the
   `residual_check` and `physics_audit` sidecars on GridSim's answer and on the rival's answer placed into the same
   recorded model. Quote the verdicts, the worst bus and the residual. Exit 77 from a sidecar means not run (a
   dependency is missing): nothing was validated, never a pass.
6. **Single-tool references.** `crossval_single(case, sidecar)` runs GridSim's own cross-validation sidecars:
   `crossval` (pandapower, tightest tolerance), `residual_check`, `physics_audit`, `crossval_smallsignal`,
   `crossval_classical`.
7. **Another build.** `build_regression(program=...)` re-runs this session's journal on a new or tuned build with
   `breaking-change validate`: report IDENTICAL / CHANGED, and for each changed field EXPLAINED (Bn), EXCEEDS-CLAIM,
   AMBIGUOUS, UNEXPLAINED or UNBOUNDED. A tuned build is `[UNSUPPORTED BUILD]`; say so.

## The case lab

The case lab tools (`caselab_run`, `caselab_status`, `caselab_findings`, `caselab_reproduce`, `caselab_propose`) exist
only when the plugin option `enable_caselab` is on as well as the validation tools. They run the protocol above on
cases the lab generates itself, so the same rules hold for every finding.

1. **Generate small and plausible.** `caselab_run(seed, n, families, modes)`: the bulk is small (radial MV feeders,
   small meshed 33/132 kV networks, IEEE-like 5-14 bus systems), with a few larger transmission-like ones up to 30
   buses. The same seed gives byte-identical cases. Every value is a ranged typical value and the cases are
   Synthetic; never present one as a real network.
2. **Gates before use.** Each case is converted to JSON v2, validated, physics-checked and solved; a case outside
   0.90-1.10 pu or one that does not pass is a generator reject with its reason. Report rejects; never adjust a case
   until it passes (no fitting to the solver).
3. **Triage every non-agreement.** Each Lab cell is triaged into the classes above and deduplicated by (class, mode,
   tool, signature). Report the counts by class and every finding with its first case and its occurrences.
4. **Refine, then shrink.** For each new finding the lab tries seeded perturbations around it (scale the load, move a
   tap one step, tighten a Q limit, remove a loop branch) and shrinks it greedily to a minimal reproducer (merge
   leaf buses, drop generators, loop branches and shunts, set taps to nominal) that still shows the same finding.
   Quote the reproducer's size, its steps and its file hashes; `caselab_reproduce` rebuilds it from its recipe and
   expects the same hashes and the same finding.
5. **Proposals are text only.** A finding's `proposal` is a sentence for Mark, never a patch. A documented model
   difference proposes nothing; a GridSim gate fail or a genuine disagreement is a question for Mark, with both
   sides' evidence; a test change needs a mathematical proof.
6. **Designing cases yourself.** `caselab_propose()` returns a template, the class ranges and the instructions; fill
   it, aiming at a stated reason for tools to differ, and send it back with `caselab_propose(case=...)`. The server
   checks and gates it; an accepted id goes into `caselab_run(proposed=[...])`. The server calls no model.

Write the campaign up from `caselab_findings` and the campaign's `summary.md`: the build tag, the seed and parameters,
counts by class, each finding (class, mode, tool, signature, reproducer, evidence of both sides) and what is not
known. "Verified solver, unvalidated cases."

## Licence

Cases above 1,500 buses run through GridSim's CLI paths, which need the operator licence named in the plugin
configuration. Never print, quote or record a licence or its contents; say only "licensed: yes" or "no".

## Writing a finding

One finding per divergence: what was compared (mode, case and its SHA-256, tools and versions, GridSim build and
commit), the triage class, the numbers (gate metrics, element diff maxima and where), both sides' independent
evidence, and what is not known. Wording: "GridSim and MATPOWER disagree at bus 9 by 2e-4 pu; both pass their
residual gates" - never "GridSim is wrong" or "MATPOWER is wrong". Agreement with another tool is cross-verification,
not validation against measurements.

## Citing evidence (every answer)

Cite the build tag and GridSim version, the case hash, the result hash and the weakest provenance tag. Say "verified
solver, unvalidated case".

## Refusals

A refused verb, flag or location is not available here. Do not work around it.
