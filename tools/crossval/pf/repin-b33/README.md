# B33 re-pins: proof records

B33 moves the DC power flow, and everything built on it, to MATPOWER's `makeBdc` model (tap ratios, phase-shift
injections, bus Gs as load). Pre-existing tests whose DC values move on a case with a tap other than 0 or 1, a shift or Gs may
be re-pinned, but only with a maths proof against the independent NumPy transcription in `../dcpf_fidelity.py`. This
folder records the method and the values for each re-pin made at 2.1.144.

Tolerance, as in `tests/GridSim.Tests/fixtures/dcpf-fidelity/README.md`:
`|x - ref| <= 4096 eps max(||ref||_inf, 1e-3)` (flows in per unit, angles in radians; LODF dimensionless).

Run from the repository root with NumPy 2.5.3 and SciPy 1.18.1 after a Release build.

## 1. CLI fixtures on case14 (`CliResultFilesProcessTests`)

The fixtures were regenerated from the built CLI:

- `fixtures/cli-stdout/dcpf-case14.txt`: `dcpf case14`
- `fixtures/cli-files/dcpf-case14.csv` and `dcpf-case14-buses.csv`: `dcpf case14 --out dcpf-case14.csv`
- `fixtures/cli-stdout/lodf-case14.txt`: `lodf case14 --branch 3`

Proofs:

```
python tools/crossval/pf/dcpf_repin_check.py . dcpf lib/case14.m <dcpf case14 --json output>
python tools/crossval/pf/dcpf_repin_check.py . lodf lib/case14.m <lodf case14 --branch 3 --json output>
python tools/crossval/pf/repin-b33/cli_case14_check.py .
```

| check | largest difference (allowed 4096 eps) |
|---|---|
| dcpf `--json` flows / angles | 3.4 eps / 1.7 eps |
| CSV flows / angles / injections | 3.4 eps / 1.7 eps / 4.1 eps |
| LODF column 3, all 19 rows of `--json` | 2.2 eps |
| stdout: 12 printed flows (2 decimals), top-12 set and order, island line | all equal the transcription, none near a rounding boundary |
| stdout: 12 printed LODFs (4 decimals) and percentages | all equal the transcription |

The `lodf` mode of `dcpf_repin_check.py` was added for this re-pin. It builds the LODF from the makeBdc B matrix:
`PTDF = diag(b) (Binv(f,:) - Binv(t,:))`, `phi = PTDF(:, f) - PTDF(:, t)`, `LODF(:, k) = phi(:, k) / (1 - phi(k, k))`.

case9 is tap-free. `dcpf case9 --top 3` from the build is byte-identical to `dcpf-case9-top3.txt`, banner included; the
fixture is unchanged.

## 2. Security-constrained DC-OPF rows (`DcOpfSecuritySimplexParityTests`)

The new pins come from the built `GridSim.Core` through `dcopf_export.fsx`, which makes the test's own call
(`DcOptimalPowerFlow.Solve(model, null, true)`, the same loaders) and writes the model and the answer as JSON:

```
dotnet fsi tools/crossval/pf/repin-b33/dcopf_export.fsx <out> matpower/lib/case24_ieee_rts.m ... gb-synthetic
python tools/crossval/pf/repin-b33/dcopf_optimality_check.py . <out>/*.json
python tools/crossval/pf/dcpf_repin_check.py . dcopf <case .m> <dcopf <case> --json output>     (the six .m cases)
```

`dcopf_optimality_check.py` checks, for each case: the export equals the `.m` file as `dcpf_fidelity.load` reads it;
the balance `sum Pg = sum Pd + sum Gs` within 1e-6 MW; the binding flows against the makeBdc flows at GridSim's
dispatch (4096 eps of the norm); every held line within its rating and every unit inside its limits; and optimality: the
same LP, in angle form on the makeBdc matrices, solved by `scipy.optimize.linprog(method='highs')`, must reach GridSim's
cost within

`tol = ||y||_1 (delta_gridsim + delta_highs) + 1e-7 ||Pg_highs||_1 + 4096 eps |z_highs|`

(y the HiGHS duals, delta each solution's largest constraint violation in the makeBdc model, 1e-7 HiGHS's dual
feasibility tolerance). By LP duality a dispatch feasible within delta whose cost lies within tol of the HiGHS optimum is
optimal to that tolerance. Several of these LPs are degenerate (tied costs), so the dispatch may differ from HiGHS's at
the same cost; only the objective is compared.

| case | taps / shifts / Gs buses | binding flows | balance (MW) | GridSim cost | HiGHS cost | abs. diff | tol |
|---|---|---|---|---|---|---|---|
| case24_ieee_rts | 5 / 0 / 0 | 1.2 eps | 0 | 142500 | 142500 | 0 | 2.9e-4 |
| case145 | 52 / 0 / 85 | 53.5 eps | 5.8e-11 | 17666823.5 | 17666823.499999996 | 3.7e-9 | 3.5e-2 |
| pglib case73_ieee_rts | 15 / 0 / 0 | 1.7 eps | 0 | 427499.99999999994 | 427500 | 5.8e-11 | 8.6e-4 |
| pglib case162_ieee_dtc | 31 / 0 / 0 | 2.1 eps | 9.1e-13 | 361953 | 361953.0000000015 | 1.5e-9 | 7.2e-4 |
| pglib case588_sdet | 86 / 0 / 0 | 53.8 eps | 1.1e-11 | 533055.4999999995 | 533055.4999999442 | 5.5e-8 | 1.2e-3 |
| case1354pegase | 234 / 6 / 0 | 40.8 eps | 5.8e-11 | 3652983.500000002 | 3652983.500000016 | 1.4e-8 | 8.2e-3 |
| gb-synthetic | 2137 / 0 / 0 | 7.0 eps | 2.2e-11 | 602944.9458505847 | 602944.9458505817 | 2.9e-9 | 3.9e-3 |

`dcpf_repin_check.py dcopf` on the CLI's `dcopf --json` output gives the same binding-flow figures and passes the
balance on the six MATPOWER cases (gb-synthetic is a JSON case, so it is covered by the export check only). Controls:
case9 and cigre_mv reproduce their unchanged pins bit for bit through the same export and pass every check.

New pins (status Optimal, feasible and constrained unchanged on every row):

| case | costBits | genBits | binding | dispatchHash | bindingHash |
|---|---|---|---|---|---|
| case24_ieee_rts | 4101652000000000 (same) | 40A6440000000001 (same) | 1 | 4584CA31... (same) | C638F828... |
| case145 | 4170D93078000000 | 411590E1E147AE14 | 3 -> 1 | 6F7A7BDF... | 01ED5B5E... |
| pglib case73 | 411A17AFFFFFFFFF | same | 2 | 84701C59... | 1FF9F5A5... |
| pglib case162 | 4116178400000000 | same | 7 | 4AB55634... | 0E31B5E9... |
| pglib case588 | 4120447EFFFFFFFC | 40C4D28E147AE143 | 8 -> 6 | 62759137... | FC2D44CE... |
| case1354pegase | same | same | 10 | 638BE34E... | 637FAE8E... |
| gb-synthetic | 41226681E4468720 | same | 4 | 4F5B1899... | 045BFE16... |

## 3. Other tests at risk

Every other slice named in the B33 handover ran green on the 2.1.144 build with no change, so nothing else was re-pinned:
S4SolverDataFollowUps (including N111 on case118: `acopf` keeps its DC passes on the legacy view, so its AC values do
not move), R1EGbSynthetic, N240DcOpfTieBreak, N109, S4SolverConvergence, Doc16 N262, LF7GbSynthetic,
DcPowerFlowAndPvCurve, Scopf, Scuc, Contingenc, TransferCapability, TransmissionSecurity, PlanningStudy, Probabilistic
and Sensitivity. The N262 corrective row for pglib case240_pserc runs longer than one slice allows on the laptop; that
case has no tap, shift or Gs, so B33 cannot move it.
