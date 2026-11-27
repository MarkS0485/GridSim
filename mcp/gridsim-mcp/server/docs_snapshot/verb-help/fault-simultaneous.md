### fault-simultaneous

`gridsim fault-simultaneous <case|case.m|case-dir> --at <bus>:<3ph|slg|ll|dlg>[,<bus>:<type>...] [--json <file.json>] [--csv <file.csv>]`

Solid shunt faults at several buses at the same instant (a cross-country fault, or one fault with a second developing at
another bus), on the operating point and the same sequence networks as `fault --type`. `--at` lists the buses and the fault at each:
`3ph`, `slg` (phase a to ground), `ll` (phases b-c) or `dlg` (phases b-c to ground); each bus takes one fault. The study
assembles the transfer-impedance matrices of the three sequence networks between the fault buses (one Zbus column per
bus, Core `FaultAnalysis.SequencePorts`), puts them in the phase domain and solves the per-bus boundary conditions
together (Core `SimultaneousFault.Solve`: a dense complex Gaussian elimination over `3N` phase unknowns, with partial
pivoting). A single `--at` entry is the ordinary unbalanced fault and agrees with `fault --type` to rounding. For each bus
it prints the phase currents into the fault (pu, and kA when the bus has a base kV), the earth current `|3I0|`, the sequence
current magnitudes and the phase voltages after the faults. The faults are solid: there is no `--zf`, and the relay overlay
of `fault` is not run. An earth fault (`slg`, `dlg`) at a bus that reaches no earthed neutral in the zero sequence has no earth
current to carry: it is refused with exit 3 and a message (and, on a case with no winding connection at all, that
the result would reflect missing data), as `fault` exits 3 for the same case; `3ph` and `ll` faults do not use the zero sequence.
Converter-interfaced units keep their prefault current, as in `fault --type`. A shunt fault and a series fault at the
same time is not offered: Core has no combined solution of the two (see [fault-series](#fault-series)). The prefault solve
must converge (exit 3 otherwise).

| Flag | Default | Meaning |
|---|---|---|
| `--at` | - | The fault buses and types, `<bus>:<type>` separated by commas (required). Types: `3ph`, `slg`, `ll`, `dlg`. |

Exit 0 done; 1 the case cannot be loaded or the study fails (a singular boundary-condition system is named); 2 usage, including a
bus that is not in the case or is named twice; 3 the prefault power flow did not converge, or an earth fault where no earth
current can flow. Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a
file ([Result files](#result-files)): the summary (case, buses, ports) and a `ports` table
(`bus,type,base_kv,phase_a_pu,phase_b_pu,phase_c_pu,phase_a_ka,phase_b_ka,phase_c_ka,earth_current_pu,i1_pu,i2_pu,i0_pu,va_pu,vb_pu,vc_pu,zero_sequence_grounded`).
