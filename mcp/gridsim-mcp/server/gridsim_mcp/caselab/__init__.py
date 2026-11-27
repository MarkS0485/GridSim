"""The case lab (maintainer only): an automated loop that generates small, plausible cases from a seed, runs them in
GridSim and the rival tools through the Lab Mode daemon, triages every non-agreement, shrinks each new finding to a
minimal reproducer and keeps the findings in a private store in the workspace.

- rng: a seeded generator of numbers (SplitMix64), so the same seed gives the same case bytes on any machine.
- model: the case model (buses, generators, branches), its MATPOWER writer and the edits refinement applies.
- generator: the case families (radial MV feeders with an optional LV tail, small meshed 33/132 kV networks, IEEE-like
  transmission networks), parameters drawn from ranged typical values and tagged Synthetic.
- triage: the signature and dedup key of a finding.
- shrink: the greedy search for the smallest case that still shows a finding.
- propose: the template an AI fills to design a case itself (the server validates it, it never calls a model).
- campaign: the loop, the store (campaign.json, cases/, bundles/, findings.jsonl, summary.md).

A disagreement is evidence, not proof. Every finding carries a proposal as text only; no code, test, core or
determinism change is made by the lab.
"""
