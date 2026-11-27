### harmonic-pf

`gridsim harmonic-pf <case> --spectra <file.json> [--orders 5,7,11] [--norton-g 0] [--norton-b 0] [--out <file.json>]`

Experimental: the console headline carries `[EXPERIMENTAL]` and the result file `"experimental": true`, because the
coupling model is an assumed placeholder and the solver has not been checked against an independent coupled harmonic
solver.

Coupled harmonic power flow (Core `CoupledHarmonicPowerFlow`). The network is the study model the `harmonics` verb uses:
machine source reactance, loads as damping shunts, balanced sequences. The sources come from a `gridsim-harmonics/1` file, the
same file `harmonics --spectra` reads. Each source bus carries a Norton coupling admittance `g + j b h` (pu on the case base)
in parallel with its current source. With both values 0 (the default) the result is the plain penetration solve
`V_h = Z_h I_h`. The fixed-point loop over the source injections runs to 1e-10 pu, at most 50 passes per order. The
voltage THD of each bus is taken over the solved fundamental (Newton, 100 iterations, reactive limits on). See
[ch. 17](<0017 Harmonic studies.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--spectra` | required | The `gridsim-harmonics/1` source file. A component at an order the study does not analyse is refused (exit 1), as `harmonics` refuses it. |
| `--orders` | 3 to 49, odd | The orders to solve, comma separated, each 2 or more. |
| `--norton-g` | 0 | Coupling conductance at each source bus, pu (not negative). |
| `--norton-b` | 0 | Coupling susceptance per harmonic order at each source bus, pu (the admittance at order h is g + j b h). |
| `--out` | none | Write the result as JSON (`gridsim-harmonic-pf/1`: per bus the fundamental voltage, the THD and, per order, the real and imaginary parts, magnitude and angle of V_h; the iteration count and the convergence flag); an existing file is never overwritten. |

The JSON from `--out` is the result file: the verb is not on the shared `--json`/`--csv` writer. The coupling model is an
assumed placeholder (provenance CuratedPlaceholder), not a measured converter characteristic.

Exit 0 when the fixed-point loop converged at every order. Exit 3 when it did not, or when the fundamental power flow did
not converge. Exit 1 for a case or source file that cannot be read, or a singular Y(h). Exit 2 for usage.
