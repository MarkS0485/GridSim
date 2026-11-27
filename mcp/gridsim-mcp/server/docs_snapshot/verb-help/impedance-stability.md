### impedance-stability

`gridsim impedance-stability [<case>] [--dir <path>] [--bus <id>] [--converter gfl|gfm] [--criterion auto|siso|mimo] [--grid canonical|real] [flags]`

Impedance-based (generalised Nyquist) inverter-grid stability screen: forms the loop gain of the grid impedance and the
converter admittance over frequency (SISO: the positive- and negative-sequence loops in the converter's dq frame, with the
grid read at f + f0 and f - f0; MIMO: the 2x2 dq matrices), counts encirclements of (-1, 0), reports gain and phase
margins, and sweeps SCR for the weak-grid instability boundary.
The converter operating point is the chosen unit's solved output (the bus's solved generation, split between co-located
units by scheduled P and by reactive range), not its scheduled Pg / Qg, at the solved bus voltage.
Cross-checks against the right-half-plane eigenvalue count of the case's small-signal model where one can be built:
that swing model uses the WSCC-9 Anderson-Fouad X'd for `case9` (as `stability` and `modal` do), else the synthetic
0.30 pu, and zero damping (no bundled case carries damping data); the cross-check line names its basis in brackets.
The `SCR boundary` line reports what the sweep found: the critical SCR at an unstable-to-stable crossing; "stable
across the swept SCR range" only when every swept SCR is stable; "UNSTABLE at every swept SCR" when none is; the count of
unstable SCRs when the sweep has no crossing; and `n/a` with `--grid real`, where no sweep runs.
The converter model comes from the case's bound asset class when there is one, otherwise from the flags (tagged
`CuratedPlaceholder` `[VERIFY]`). See [ch. 15](<0015 RMS, modal and impedance stability.md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory (`-d` is a short alias of `--dir`). |
| `--bus` | largest generator's bus | Generator bus to attach the converter model to. |
| `--converter` | `gfl` | `gfl` (grid-following) or `gfm` (grid-forming); any other value exits 2. The grid-forming model's power loop is per unit on the unit's rating (MBase): a bound class's damping D (MW/Hz) and synthetic inertia J (MW.s/Hz) give the droop S / (D f0) and the lag J / D (R140, October 2026; they used to be scaled as 0.01 D pu and 1e-4 J s whatever the unit's size). The flag-built grid-forming model (no damping, no synthetic inertia) is unchanged; its power-loop bandwidth is `--pll-bw` when given, else the grid-forming placeholder 5 Hz. |
| `--criterion` | `auto` | `auto` (MIMO for `gfl`, SISO for `gfm` and for `--outer-loop-only`), `siso` or `mimo` (the generalised criterion); any other value exits 2, and `mimo` with `--outer-loop-only` exits 2. The grid-following admittance couples the two sequences, which SISO drops, so its SISO verdict can be wrong; the grid-forming admittance does not, so SISO is exact there and keeps the margins. With the ideal PLL of `--outer-loop-only` the dq loop is improper and its MIMO winding misses a right-half-plane root. |
| `--fmin` / `--fmax` | 0.1 / 250 | Frequency range (Hz). |
| `--ppd` | 50 | Points per decade. |
| `--grid` | `canonical` | Grid impedance: `canonical` (the exact single-inverter-vs-Thevenin model, swept over SCR) or `real` (the network's own harmonic driving-point impedance at the converter bus; fails with exit 3 if that solve is singular). Any other value exits 2. |
| `--scr` | 3.0 | Short-circuit ratio of the Nyquist scan (`--grid canonical` only; with `--grid real` the SCR flags exit 2). |
| `--scr-min` / `--scr-max` / `--scr-steps` | 1 / 10 / 37 | SCR sweep for the boundary search. |
| `--pll-bw` | 20 Hz | PLL bandwidth (flag-built model, or an override of a grid-following asset class). The value must be a finite number above 0: 0, a negative value, `NaN` or `Infinity` exits 2 (N266, October 2026; it used to be floored silently by the flag-built model and crash a bound asset class). With a grid-forming asset class it exits 2 (the class sets its own power-loop bandwidth); with the flag-built grid-forming model it sets the power-loop bandwidth (default 5 Hz). The default is the one grid-following PLL default the EMT and small-signal models also use (R138, October 2026; it was 5 Hz here, 20 Hz in EMT): a faster PLL is the destabilising direction on a weak grid, so an unauthored device is screened on the conservative side. A grid-following asset class without `pllBandwidthHz` takes the same 20 Hz. |
| `--pll-damping` | 0.707 | PLL damping ratio of the grid-following model: the flag-built model's value, or an override of a bound grid-following asset class's authored damping (without the flag the class's own damping is used, and any override tags the model `CuratedPlaceholder`, not `Recorded`). The value must be a finite number above 0 (0 or below, `NaN` or `Infinity` exits 2; N266). With `--converter gfm` (bound class or not) it exits 2: the grid-forming model takes no PLL damping ratio. |
| `--current-bw` | - (no default) | Current-loop bandwidth (Hz); setting it adds the current loop to the model detail (`PllCurrentOuter`). There is no default value: without the flag the model has no current-loop bandwidth (the `PllOuter` level and its fast stand-in roll-off), and the flag without a value exits 2. The value must be a finite number above 0 (0 or below exits 2). Grid-following only; with `--converter gfm` it exits 2. |
| `--outer-loop-only` | off | The outer-loop-only detail level (`ImpedanceModelDetail.OuterLoopOnly`): the grid-following PLL is ideal (gain frozen at 1/Vd0) and the current loop is the fast stand-in roll-off, a first screen when no inner-loop data exist. With `--pll-bw`, `--pll-damping` or `--current-bw` (which it would ignore), or with `--converter gfm` (whose admittance does not use the detail level), it exits 2. |
| `--enableExport[=path]` | `results/impedance-stability-export.json` | Write the scan to JSON. |

Exit 3 on a non-converged power flow; exit 2 on an unknown case name, a second case argument, a positional case and a
different `--dir`, or no generator at the chosen bus; exit 1 for a missing or unloadable file or folder. A completed
scan exits 0 whether the verdict is STABLE or UNSTABLE. The case may sit anywhere on the line and may be a built-in
name, a `.m` file or a case folder.
