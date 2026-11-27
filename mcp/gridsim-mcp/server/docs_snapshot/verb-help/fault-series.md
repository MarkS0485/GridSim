### fault-series

`gridsim fault-series <case|case.m|case-dir> --branch <index> --open 1|2 [--json <file.json>] [--csv <file.csv>]`

Open-conductor (series) fault in the series element of one branch: one conductor open (`--open 1`, phase a) or two open
(`--open 2`, phases b and c). It runs on the operating point as `fault --type` does: the prefault power flow, then the
same sequence networks (the positive sequence with every unit as X''d, the negative with X2 and the phase shifts
reversed, the zero sequence from the winding connections, line Z0 = 3 x Z1 where a line carries none). `--branch` is the
branch's index in the case (the `index` column of `fault --type`'s `branches` table; `0` is the first branch). The break
sits in series with the element, inside its pi section: the element's series impedance is taken out of the network, its
line-charging halves stay at its two buses, the Thevenin impedance between its two buses is
`Zth = Zii + Zjj - 2 Zij` of what remains in each sequence (the Zbus columns `fault --type` solves), and the element's own
series impedance is added back, so `Zs = Zth + Zb`. With `I` the prefault current through the element,
`(V_from - V_to) / Zb`, the open-circuit voltage across the break is `Zs1 I`, and the sequence connection is the textbook
one (Grainger and Stevenson; Anderson): one conductor open puts the three sequence networks in parallel across the break,
`I1 = Zs1 I / (Zs1 + Zs2 || Zs0)`; two open puts them in series, `I1 = I2 = I0 = Zs1 I / (Zs0 + Zs1 + Zs2)`. The result is
the sequence and phase currents through the break and the phase voltages across it (Core `SeriesFault.OpenConductors`;
the network side is `FaultAnalysis.SeriesBreak`).

A break is defined for a line, or a grounded-wye / grounded-wye transformer without mutual coupling, with a tap ratio of 0
or 1 and no phase shift. Anything else is refused by name with exit 1: a branch out of service, an off-nominal or
phase-shifting branch, a delta or ungrounded winding (its zero-sequence model is a shunt, so the series element has no
zero-sequence impedance), a break that leaves one side with no source, load or shunt, and a break whose zero-sequence
network is not earthed on both sides. The last is a common case (a radial feeder behind a delta transformer): the
zero-sequence impedance across the break is infinite, `SeriesFault.OpenConductors` takes a finite one, and no number is
invented. A MATPOWER or JSON v1 case carries no winding connection, so it has no earthed neutral and every break is refused
for that reason; use a JSON v2 case that gives windings (see [fault](#fault)). Converter-interfaced units are open in all three
sequence networks. The prefault solve must converge: otherwise `fault-series: the pre-fault power flow did not converge (...)`
and exit 3. The output gives the prefault current through the element, `Zs1`, `Zs2` and `Zs0`, the three phase currents
(pu, and kA when the bus has a base kV) and the three phase voltages across the break.

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | - | The index of the branch whose series element is broken (required). |
| `--open` | - | The number of conductors open: `1` (phase a) or `2` (phases b and c) (required). |

Exit 0 done; 1 the case cannot be loaded or the break is refused (the reason is named); 2 usage, including a `--branch` that
is not in the case; 3 the prefault power flow did not converge. Result files: `--json <file.json>`, `--csv <file.csv>` or
`--out <file.json|file.csv>` write the result to a file ([Result files](#result-files)): the summary (case, branch, buses,
open conductors, the prefault current, `zs1`, `zs2` and `zs0` real and imaginary parts, the sequence current magnitudes) and a `phases`
table (`phase,i_re_pu,i_im_pu,i_mag_pu,i_mag_ka,v_across_re_pu,v_across_im_pu,v_across_mag_pu`).
