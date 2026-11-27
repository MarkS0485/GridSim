# DC power flow fidelity pins (B33)

`dcpf-fidelity-pins.json` holds reference values for `Rc5WpDcFidelityTests`. They come from
`tools/crossval/pf/dcpf_fidelity.py`, an independent NumPy transcription of MATPOWER's `makeBdc` and the DC branch of
`runpf`/`rundcpf`:

```
b      = (1 / x) / tap            tap 0 read as 1
Bbus   = Cft' diag(b) Cft
Pfinj  = b (-shift pi / 180),     Pbusinj = Cft' Pfinj
Pbus   = (Pg - Pd) / base - Pbusinj - Gs / base
Va     = Bbus(pvpq, pvpq) \ Pbus(pvpq)            reference angle 0
Pf     = (Bf Va + Pfinj) base
```

The script reads the bundled MATPOWER files the way GridSim's reader keeps them (type-4 buses, status-0 branches and
status-0 generators left out). It writes angles in radians, flows in per unit and net injections in MW at 17 significant
digits, with the script's SHA-256, the NumPy and SciPy versions and each case file's LF-normalised SHA-256 in the header.

Regenerate from the repository root:

```
python tools/crossval/pf/dcpf_fidelity.py . tests/GridSim.Tests/fixtures/dcpf-fidelity/dcpf-fidelity-pins.json
```

Octave and MATLAB were not available, so `rundcpf` is represented by the transcription. pandapower was not installed, so
its `rundcpp` was not run.

## Tolerance

Each value must satisfy `|x - ref| <= 4096 eps max(||ref||_inf, 1e-3)`, with angles in radians and flows in per unit. The
bound is normwise, not entry by entry. A near-zero flow on a stiff branch is the difference of two large angle products,
so it has no relative accuracy of its own in either code. As a check, two NumPy solves of the same system that differed
only in pivot ordering (LAPACK dense, and SciPy SuperLU with NATURAL, MMD and COLAMD orderings) were compared:

- entrywise, with the spec's per-entry floor, they differed by up to 3667 eps on case57, 4047 eps on case118 and 50667 eps
  on case300;
- normwise, they differed by at most 190 eps on every pinned case.

## Hand case

The three-bus case has 1-2 with x 0.1 and a 5 degree shift, 2-3 and 1-3 with x 0.2, Pd 60 MW at bus 3, Gs 10 MW at bus 2,
and a 100 MVA base. Its closed forms are:

- theta2 = -(100 phi + 4)/125 = -0.10181317007977317
- theta3 = -(50 phi + 9.5)/125 = -0.11090658503988658
- P12 = 32 - 200 phi = 14.546707480056698 MW
- P23 = 4.5467074800567 MW
- P13 = 55.45329251994329 MW
- reference injection 70 MW

The values are NumPy's. They agree with the closed forms, evaluated in double precision, to within 5 ulp: theta2 and
theta3 to the bit, and P12 = 14.546707480056698 against 32 - 200 phi = 14.546707480056707.

## Re-pins of pre-existing tests

A pre-existing test whose DC value moves on a case with a tap other than 0 or 1, a phase shift or Gs may be re-pinned only with
this proof. Take the value from the built CLI, then run:

```
python tools/crossval/pf/dcpf_repin_check.py . dcpf  <case under data/matpower> <dcpf --json output>
python tools/crossval/pf/dcpf_repin_check.py . dcopf <case under data/matpower> <dcopf --json output>
```

The check must pass at the 4096 eps rule above. For `dcopf`, the dispatch must also meet sum Pd + sum Gs to 1e-6 MW, and
every binding-line flow must be the transcription's flow for that dispatch. The check does not prove that the LP is
optimal.

The `dcpf` banner is unchanged for a case with no tap other than 0 or 1, no phase shift and no Gs, so the case9 stdout
fixture (`cli-stdout/dcpf-case9-top3.txt`) is as before. case14 has taps, so its numbers moved: `cli-stdout/dcpf-case14.txt`
(which also carries the new banner), `cli-files/dcpf-case14.csv` and `cli-files/dcpf-case14-buses.csv` were regenerated
from the built CLI and proved with the `dcpf` check above. The transcription's case14 values are in the pin file.
