### probabilistic

`gridsim probabilistic <study.json> [--out <result.json>] [--threads N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Probabilistic load flow and generation adequacy (RC4 R4-09), with the sampling method a request field: plain Monte Carlo
(`monte-carlo`), quasi-Monte Carlo (`qmc`, unscrambled Sobol; `qmc-shifted`, with an independent random digital shift per
replicate) or analytical `convolution`. One study can list several methods to compare them. Every method rests on the one
seeded generator of RC4 (`gridsim-rng/1`: counter based, so a draw depends only on seed, domain and index): a result is
bit-repeatable from its study file, and one thread gives the same bits as sixteen. The seed is mandatory; there is no
default seed.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`load-flow` or `adequacy`), `seed`
(unsigned integer), `methods` (a list of rows, each with `method` and `samples`; `samples` is required in EVERY row, including
`convolution`, which ignores its value, so give any positive integer there; `qmc-shifted` also takes `replicates`, default 8,
at least 2; `convolution` optionally takes `cellsPerSd`, default 100), and optionally `domain` (the stream label, default the study kind).
Unknown or duplicated fields and non-finite numbers are refused before any solve. `samples` is per replicate; a
`qmc-shifted` run draws `samples x replicates` points. Use a power of two for `qmc`.

`load-flow` fields: `case` (`path`, relative to the study file, or `builtIn`, plus `sha256`, which is required for both and
bound exactly as for `transmission-security`: for a built-in name it is the identity hash the loader reports as `case_sha256`
(`gridsim rpc` `load`), which depends on the GridSim build, so re-read it after upgrading), `evaluator` (`dc` or `ac`), `inputs` (each `id`, `target` `bus-load` or `generator-pg`, `bus` ID,
`unit` for a generator: its zero-based position among the in-service generators at that bus, optional `scale`: the law is
a multiplier of the case value, and `distribution`), optional `correlation` (rows of input positions `a`, `b` and `rho`,
a Gaussian copula applied through a Cholesky factor in fixed row order), `quantiles` (default 0.05, 0.5, 0.95),
`loadingLimitPercent` (default 100), `voltage` (`minPu`, `maxPu`, default 0.95 and 1.05), `monitorBranches` (zero-based
branch indices), `monitorBuses` (bus IDs, AC only), `qFollowsP` (AC: a load keeps its power factor, default true) and `stepMw`
(an explicit convolution lattice step). Distributions: `normal` (`mean`, `sd`), `lognormal` (`mu`, `sigma` of the
logarithm), `weibull` (`scale`, `shape`), `beta` (`alpha`, `beta`, optional `lo`, `hi`), `discrete` (`atoms` of `value`,
`probability`), `two-state` (`capacity`, `availability`) and `empirical` (`values`).

`adequacy` fields: `units` (each `id` with `capacityMw` and `availability`, or `states` of `capacityMw`, `probability`),
`load` (`hourlyMw`, or `constantMw` with `hours`) and optionally `stepMw`, the capacity lattice step for convolution
(default: the greatest common divisor of whole-MW capacities; a capacity off the lattice makes convolution OUT OF SCOPE).
Results: `lole` in HOURS per period (the sum over hours of the loss-of-load probability, loss when available capacity is
below the load), `lolp` (the average hourly probability), `eens` in MWh per period. Static adequacy only: no
transmission and no chronology.

Result per method: mean, standard deviation, requested quantiles (type 7 estimator on the sorted sample) and the exceedance
probability per monitored branch (loading above the limit, with RateA) and bus voltage (below the band, above the band).
Sampling error is stated per method: Monte Carlo the standard error (sd over the square root of N, and the binomial error
of a probability); `qmc-shifted` the spread of the replicate estimates over the square root of R; `qmc` "deterministic
point set, no error bar"; `convolution` no sampling error, with the lattice step per branch (`latticeStepMw`). The
convolution of continuous inputs inflates the variance by about step squared over 12 per term (a stated discretisation
tolerance); discrete inputs on the lattice are exact. A sample whose AC power flow does not converge is kept as a row in
`failedSamples` with its status, and every statistic (mean, deviation, quantiles and the probabilities) is taken over
the converged samples, whose count is given, so it is conditional on convergence. A run with any failed sample is
`INCOMPLETE`, and its reason gives the failed count; with every sample converged it is `COMPLETE`. Convolution applies only where the quantity is a sum of independent terms (DC
flows, capacity): an AC evaluator or correlated inputs return `OUT OF SCOPE` with the reason, and nothing is approximated.
Quasi-Monte Carlo accepts at most 64 dimensions (inputs or units); above that use Monte Carlo or convolution.

The result JSON (`schemaVersion`, `study`, `provenance` with study and case SHA-256, seed, domain and engine, `status`,
`methods`) records, per method, the seed, domain, method, sample count, replicates, generator version, direction-number
table id, a digest of the drawn samples and a digest of the result values. It is printed to stdout and, with `--out`, written
to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 every method `COMPLETE`. Exit 3 a method `OUT OF SCOPE` or `INCOMPLETE` (the result is still printed). Exit 1 for
input it cannot accept (a missing seed, a malformed or unknown-field study, a case identity mismatch, a non-finite or
out-of-range parameter, a correlation matrix that is not positive definite, an unreadable file, an existing output path).
Exit 2 for an invalid invocation. PowerFactory's probabilistic modules were not compared (COMPARATOR-UNVERIFIED).

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--threads` | runtime default | Most worker threads; the result is bit-identical for any value. |
