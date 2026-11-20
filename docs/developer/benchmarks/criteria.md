# AC power-flow benchmark criteria

These criteria apply to GridSim Dense and Sparse on the pinned 1,354-bus
PEGASE case. Use the quiet-system run for performance comparisons.

## Correctness and repeatability

- Each GridSim path must converge on all ten measured solves. Every
  converged solution must have a maximum independent power mismatch of
  at most 1e-8 pu on the original MATPOWER case. Every retained GridSim
  physics audit must pass.
- GridSim's bitwise claim requires zero differences in the designated
  electrical outputs within Dense, within Sparse, and between Dense
  and Sparse. Any bit difference fails this check for the tested case
  and build, regardless of its engineering magnitude.
- Use MATPOWER as the comparison point for voltage magnitude,
  slack-relative angle, and branch-end active and reactive power.
  Report each largest absolute difference and its location. Use the
  independent residual and model checks to investigate discrepancies.

## Performance

- Report all ten solve times and the arithmetic mean, minimum and
  maximum in milliseconds. Keep GridSim Dense and Sparse as separate rows.
- Compare GridSim Sparse with MATPOWER and other comparable sparse
  paths under the same case, hardware and timing protocol. Report the
  ratio of mean times. A ratio of 0.80 means GridSim used 20% less time,
  and 1.25 means it used 25% more.
- Call small gaps inconclusive when the observed timing spread is large
  enough to obscure them. A consistently higher time than a comparable
  sparse path is an unfavourable result. A reproducible slowdown against
  a previous GridSim build under the same protocol is a regression.
- Rehearsal timings serve only to verify the benchmark workflow.
