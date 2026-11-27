### emt-line

`gridsim emt-line <study.json> [--out <result.json>]`

Lossy travelling-wave line impulse study (RC3 P8). One constant-parameter line between a Thevenin source and a
termination is run on the same EMT engine as `emt`, unchanged: the line is a cascade of Bergeron sections with the
EMTP lumped-resistance split (per section `R/4`, a lossless half-section, `R/2`, a lossless half-section, `R/4`, and
`G/4` to ground at each half-section terminal), built from the engine's own lossless line, resistor and conductance
elements. The `emt` verb and its lossless travelling-wave line model are not changed. Frequency-dependent and
multi-phase line models are not implemented, and nothing has been compared with PSCAD or EMTP (the result says
`comparator: unverified`).

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `line` (`lengthM`, `rOhmPerM`,
`lHenryPerM`, `cFaradPerM`, `gSiemensPerM`, SI units per metre); `segments` (sections for the base run); `source`
(`kind` `Step` or `DoubleExponential`, `amplitudeV`, `matched` true for a source resistance equal to Zc or false with
`resistanceOhm`, and `alphaPerS`, `betaPerS` for the double exponential `A (exp(-alpha t) - exp(-beta t))`);
`termination` (`kind` `Open`, `Short`, `Resistance` or `Capacitance`, with `value` in ohm or farad for the last two; a
short is a 1e-6 ohm resistance); `integration` (`dtSeconds`, `durationSeconds`, a whole number of steps);
`refinement` (`timestepDivisors`, starting at 1 and increasing, and `segmentCounts`, increasing); `output`
(`sampleEvery`, the trace decimation). Unknown, duplicated or missing fields are refused. Every run's half-section
travel time `tau/(2n)` must be a whole number of its timesteps (the engine would otherwise round the delay).

The result gives Zc `sqrt(L'/C')`, the velocity, the travel time `length/velocity`, the front attenuation
`exp(-(R'/(2Zc) + G'Zc/2) length)` and the source and load reflection coefficients `(Z - Zc)/(Z + Zc)`; for the base
run and every refinement pair the measured delay, front attenuation and reflection beside them; the base-run traces
(sending and receiving voltage, sending current, and the analytic values when they exist); and, when the oracle is
`EXACT` (a distortionless or lossless line with a matched source, where the travelling-wave solution is closed form),
the largest error over every step and the observed convergence order along the timestep and section axes. A line that
distorts (`R'C' != G'L'`) or an unmatched source has `FRONT AND DELAY ONLY`. `status` is `EVALUATED`.

Exit 0 when evaluated. Exit 1 for input it cannot accept (a malformed or refused study, a delay that is not a whole
number of steps, an existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
