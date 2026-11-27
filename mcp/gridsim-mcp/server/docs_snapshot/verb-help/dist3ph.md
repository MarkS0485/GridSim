### dist3ph

`gridsim dist3ph <feeder.json> [sweep] [--tol 1e-10] [--max-iter 200]`
`gridsim dist3ph <feeder.json> fault --bus ID [--type all|3ph|slg|ll|llg] [--zf PU]`
`gridsim dist3ph <feeder.json> estimate --measurements <m.json> [--method wls|wlav|branch-current] [--tol 1e-10] [--max-iter 50] [--robust-passes 8] [--robust-floor 0.001]`
`gridsim dist3ph <feeder.json> harmonics --bus ID --spectrum TYPE --current PU [--afe feeder]`
`gridsim dist3ph <feeder.json> to-v2 --to <out.json>`

EXPERIMENTAL. The unbalanced three-phase distribution stack (`GridSim.Core.Distribution`) on a radial feeder read from
a `gridsim-dist3ph/1` JSON file, or (N339, October 2026) its JSON v2 form `gridsim-dist3ph/2` (format in [technical ch. 18](<../technical/0018 Real-time, distribution and dynamic estimation.md>);
bundled examples under `data/dist3ph/`, including the IEEE 4-node and the IEEE 123-node test feeders; user guide in
[ch. 7](<0007 Three-phase distribution feeders.md>)). The mode word follows the file and defaults to `sweep`. Each
mode accepts only its own flags; a flag from another mode is a usage error (exit 2), not silently ignored.

- `sweep`: the three-phase backward/forward sweep (`ThreePhaseSweep`). Prints per bus and phase |V| (pu and
  line-to-neutral volts), angle and the voltage unbalance factor. A non-converged sweep prints its status and the final
  voltage change on stderr, reports no voltages, and exits 3. The sweep solves single- and two-phase laterals (a phase
  a bus does not have prints `-` and has no result row; the unbalance factor is printed for three-phase buses only),
  line charging, regulators on fixed taps, closed switches, capacitors, and constant-current, constant-impedance and
  delta loads; a feeder with any of these (closed switches alone included) prints one line counting them, and a named
  bus (such as RG1) is shown by its name.
- `to-v2` (N339): writes the feeder as a `gridsim-dist3ph/2` document (`schemas/v2/dist3ph.schema.json`) at `--to`,
  then proves it: the written file is read back with the v2 reader and compared with the feeder bit for bit (every
  per-unit number by its 64 bits, ids, kinds, phases, transformer elements, order). The v2 form is the
  solver-authoritative per-unit model (closed unit vocabulary: `pu@system`, MVA, kV; nothing converted at load) with
  provenance: a document default and optional per-element entries. A feeder read from a v1 file (or lifted from a case)
  carries no provenance and is written with `CuratedPlaceholder` and a note naming the source and carrying `[VERIFY]`
  (GridSim invents no tag; set it before relying on the file); a v2 feeder keeps its own. Prints
  `wrote <file> (gridsim-dist3ph/2); read back: the same feeder, bit for bit` and the provenance tag. Exit 2 without
  `--to` or when the output exists (never overwritten); 1 when the feeder holds something the v2 form cannot (named,
  nothing written) or the file cannot be written; 3 when the written file does not read back to the same feeder (the
  difference named, the file kept). `gridsim validate <file>` checks a `gridsim-dist3ph/2` file against the schema and
  then with the reader itself. Every mode reads a v2 feeder as it reads a v1 one; the v1 format is unchanged.
- `fault`, `estimate` and `harmonics` model the elements above that they can define exactly and refuse the rest (exit
  1, naming each refused kind), never leaving an element out silently. `fault`: shunt capacitors, line charging and
  loads of every kind are neglected (shunt elements, as the constant-power loads are; the fault current is that of the
  series path) and a closed switch is a zero impedance; a single- or two-phase branch or a regulator on the path to the
  faulted bus is refused, naming the branch (elsewhere in the feeder it does not enter the study). `estimate` (WLS and
  WLAV): loads and capacitors are accepted (its measurement model has no injection measurements); laterals, line
  charging, regulators and closed switches are refused. `estimate --method branch-current`: every element is modelled
  (a regulator and line charging are undone from the measured From-end current in the forward sweep, a lateral drops
  only on its phases, a closed switch has no drop); a regulator or a charged line must face away from the source, and a
  current on a phase the branch does not carry is refused. `harmonics`: shunt capacitors and line charging (G + j h B)
  and closed switches (their buses one node) are in Y(h), and the printed network line says so; laterals and regulators
  are refused.
- The feeder may also be a radial positive-sequence case, a MATPOWER `.m` file or a JSON case folder: it is lifted into
  a balanced three-phase feeder (`MultiPhaseGridModelAdapter`: each branch's r + jx and charging on every phase, each
  bus load per phase, ZIP loads as constant-impedance, constant-current and constant-power parts, shunt capacitors, the
  slack as the source). A case the sweep cannot model is refused (exit 1): a voltage-controlled (PV) bus, an off-nominal
  tap or phase shift, a shunt conductance or reactor, a bus without a kV base, or a network that is not one radial
  tree. On such a lift the sweep agrees with the `pf` Newton solution.
- `fault`: sequence-network fault currents at `--bus` (`SequenceFaultStudy`): three-phase, single-line-to-ground,
  line-to-line and double-line-to-ground (ground current), in pu and amps, from a 1.0 pu prefault voltage behind an
  infinite source. Each branch is reduced to its transposed-equivalent sequence impedances. A fault at the source bus
  is refused (exit 1): the infinite bus has zero impedance. Every fault type refuses a zero loop impedance (for example
  a bolted fault at a bus joined to the source only by closed switches) and a non-finite current, naming the type,
  rather than printing Infinity or NaN.
- `estimate`: the three-phase node-voltage WLS estimator (`ThreePhaseWlsEstimator`, robust WLAV with `--method wlav`)
  on a `gridsim-dist3ph-measurements/1` file (per-phase voltage magnitudes, per-phase branch current phasors, optional
  nominal-voltage pseudo-measurements for unmetered buses). Non-convergence (including an unobservable set) exits 3
  with no estimate reported. A converged estimate prints the voltage table and then one residual row per measurement
  in the fit (`| measurement | phase | measured pu | estimate pu | r/sigma |`, magnitudes first, then currents as
  `re+jim`; a magnitude at the source bus is not in the fit and is not listed); the result file has the same rows as
  a `residuals` table. The estimator is dense (a 6n x 6n gain inverted every iteration), so a feeder with more than
  416 non-source buses (2,500 states) is refused, exit 1. `--method wlav` re-weights the measurements by
  1 / max(|r / sigma|, floor) for at most `--robust-passes` passes (default 8, at least 1) with the floor
  `--robust-floor` (default 0.001, above 0); both flags are refused (exit 2) with another method. When the last pass
  still moves a weight by 0.001 or more, the estimate prints `WARNING: the robust re-weighting (IRLS) reached its cap of
  N pass(es) before the weights settled; ...` (exit stays 0, as for the single-phase `estimate`; the result file has
  `irls_cap_reached`). With `--method branch-current` it is the branch-current estimator instead
  (`BranchCurrentDsse`): the current phasors are WLS-combined per branch and phase and one forward sweep from the
  source gives the voltages. It uses current phasors only, so a measurement file that also has voltage magnitudes or
  pseudo-measurements is refused (exit 1), as are `--tol`, `--max-iter`, `--robust-passes` and `--robust-floor`
  (exit 2). A current named against the reversed endpoints (`from` the To bus) is accepted as minus the From-end
  current, as `wls` and `wlav` accept it, except across a regulator or a charged line (exit 1, naming the branch). Every
  phase a tree branch carries needs a current phasor: a branch with no phasor, or with phasors on some of its phases
  only, leaves the estimate not converged (exit 3), and the message lists the unobservable branch phases (`1-2 b`).
- `harmonics`: the three-phase harmonic penetration (`ThreePhaseHarmonicPenetration`) with one balanced harmonic
  current source at `--bus`: the `--spectrum` profile of the harmonic source library (the same names as the
  `harmonics` verb's sources, for example `6-pulse-g5`; there is no default) at a fundamental current of `--current` pu,
  rotated by h x 120 degrees on phases b and c. The network is the feeder's series impedances with the reactance scaled
  by the order (with any shunt capacitors and line charging, and closed switches as one node), the source as the
  harmonic reference, and no loads. Prints, per order, the harmonic voltage at the source bus on each phase and in
  sequence components, and the bus with the largest harmonic voltage; then the root-sum-square harmonic voltage at the
  source bus per phase. Exit 1 for a bus not in the feeder, the source bus, or a singular network; 2 without one of the
  three flags or for an unknown spectrum.

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | 1e-10 | sweep: max per-phase voltage change (pu); estimate: max state update. Above 0. |
| `--max-iter` | 200 (sweep), 50 (estimate) | Iteration cap; at least 1. |
| `--bus` | required (fault, harmonics) | Fault or harmonic-source bus id; must be in the feeder and not the source. |
| `--type` | `all` | `all`, `3ph`, `slg`, `ll` or `llg`. |
| `--zf` | 0 | Fault resistance (pu on the feeder base), at least 0: Zf = `--zf` + j0. The fault header prints it as `Zf = ... pu (resistive)`. Note that `fault` and `protection-check` read `--zf` as a fault REACTANCE (Zf = j`--zf`, printed `Zf=j...`); the same flag name means a resistance here and a reactance there (D-DOC-166). |
| `--measurements` | required (estimate) | The measurement file. |
| `--method` | `wls` | `wls`, `wlav` (robust IRLS outer loop) or `branch-current` (the branch-current estimator). |
| `--spectrum` | required (harmonics) | A harmonic source library profile: `6-pulse`, `12-pulse`, `pv-inverter`, `ev-charger`, `heat-pump-inverter`, `arc-furnace`, `saturated-transformer`, `6-pulse-g5`, `12-pulse-g5`, `afe-g5` or `ideal` (the idealised 1/h six-pulse spectrum, the same as `6-pulse`). |
| `--current` | required (harmonics) | The source's fundamental current (pu on the feeder base), above 0. |
| `--afe` | - | N354, harmonics only: `feeder` stamps the feeder file's active-front-end converter units (its v1 `converters` section: `id`, `bus`, `kva`, `xPercent`, optional `rPercent`, the internal impedance in percent on the unit's own rating; no default value exists [VERIFY]) into Y(h), each phase R + j h X between the phase and the converter's floating star point (a three-wire shunt: positive and negative sequence, no zero sequence). A line names the units. A feeder with no `converters` section exits 1; any other value exits 2. Without the flag the units are not stamped and the study is unchanged. N360: the v2 form holds the units too (its `converters` section: `id`, `bus`, `ratingMva`, `xOnRating`, optional `rOnRating`, per unit on the unit's own rating, the same doubles the v1 file gives), so `to-v2` writes them and reads them back bit for bit, and `--afe feeder` works on a v2 feeder. |

Exit 0 on success; 1 on an input error (unreadable or invalid feeder or measurement file, every message naming the file
and JSON path; a bus the feeder does not have; a study that refuses its input); 2 on a usage error; 3 when the sweep or
the estimator does not converge.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
