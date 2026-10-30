using System.Numerics;
using GridSim.Core.Estimation.Dse;

namespace GridSim.Core.Distribution;

/// <summary>A per-phase voltage-magnitude measurement (per-unit) at a bus - the realistic distribution
/// telemetry (AMI/SCADA gives magnitudes, rarely angles).</summary>
/// <param name="BusId">The measured bus.</param>
/// <param name="Phase">Phase index 0=A, 1=B, 2=C.</param>
/// <param name="Value">Voltage magnitude (per-unit).</param>
/// <param name="Sigma">Measurement sigma (per-unit).</param>
public sealed record ThreePhaseVMag(int BusId, int Phase, double Value, double Sigma);

/// <summary>A per-phase branch current-phasor measurement (per-unit, rectangular) - anchors the angles the
/// magnitude-only telemetry cannot.</summary>
/// <param name="FromBusId">Branch from-bus.</param>
/// <param name="ToBusId">Branch to-bus.</param>
/// <param name="Phase">Phase index 0=A, 1=B, 2=C.</param>
/// <param name="Value">Current phasor into the from end (per-unit).</param>
/// <param name="Sigma">Measurement sigma (per-unit) on each rectangular component.</param>
public sealed record ThreePhaseCurrent(int FromBusId, int ToBusId, int Phase, Complex Value, double Sigma);

/// <summary>The solved three-phase estimate: per-bus complex phase voltages and convergence metadata.</summary>
/// <param name="Converged">True when Gauss-Newton met tolerance.</param>
/// <param name="Iterations">Iterations performed.</param>
/// <param name="MaxDelta">Final max state update.</param>
/// <param name="BusIds">Bus ids aligned with <see cref="V"/>.</param>
/// <param name="V">Per-bus complex phase voltages (each length 3, per-unit).</param>
/// <param name="ObjectiveJ">Weighted residual sum at the estimate.</param>
public sealed record ThreePhaseWlsResult(
    bool Converged, int Iterations, double MaxDelta, IReadOnlyList<int> BusIds, Complex[][] V, double ObjectiveJ)
{
    /// <summary>
    /// D-DOC-115 (October 2026): true when the robust (WLAV) re-weighting ran to its pass cap
    /// (<c>maxRobustIterations</c>) without the weights settling: the last re-weighting still moved some measurement's
    /// weight by at least <see cref="ThreePhaseWlsEstimator.IrlsWeightTolerance"/>. The state is then the Gauss-Newton
    /// solve under the second-last weights, not a fixed point of the robust estimator. This is reported exactly as the
    /// single-phase estimator reports it (<c>EstimationResult.IrlsCapReached</c>, R175 / Q13): a flag, with
    /// <see cref="Converged"/> left as the Gauss-Newton verdict, so a caller can never mistake a truncated robust
    /// estimate for a settled one. Before, the cap was hit silently. Always false for plain WLS.
    /// </summary>
    public bool IrlsCapReached { get; init; }

    /// <summary>
    /// D-DOC-217 (October 2026): one row per measurement that entered the fit, in input order (voltage magnitudes first,
    /// then current phasors), with the measured value, the estimate's value and the residual in sigmas. A measurement on
    /// the source bus is not listed: the source voltage is the fixed reference, so the fit cannot reduce its residual
    /// and it does not count in <see cref="ObjectiveJ"/> either (R237). Before there were no per-measurement residuals,
    /// so a reader could not see which measurement the objective came from.
    /// </summary>
    public IReadOnlyList<ThreePhaseMeasurementResidual> Residuals { get; init; } = Array.Empty<ThreePhaseMeasurementResidual>();
}

/// <summary>
/// D-DOC-217 (October 2026): the residual of one measurement at a three-phase WLS estimate.
/// </summary>
/// <param name="Kind"><c>"vmag"</c> for a voltage magnitude, <c>"current"</c> for a branch current phasor.</param>
/// <param name="FromBusId">The measured bus (a magnitude) or the branch's From bus as the measurement names it (a current).</param>
/// <param name="ToBusId">The branch's To bus as the measurement names it (a current); -1 for a magnitude.</param>
/// <param name="Phase">Phase index 0 = a, 1 = b, 2 = c.</param>
/// <param name="Measured">The measured value: a magnitude on the real axis, or the current phasor (per-unit).</param>
/// <param name="Estimated">The estimate's value of the same quantity, in the same orientation as the measurement.</param>
/// <param name="Sigma">The measurement sigma (per-unit).</param>
public sealed record ThreePhaseMeasurementResidual(
    string Kind, int FromBusId, int ToBusId, int Phase, Complex Measured, Complex Estimated, double Sigma)
{
    /// <summary>|measured - estimated| / sigma: the standardised residual the robust re-weighting uses (an absolute
    /// value for a magnitude, the modulus of the complex difference for a current).</summary>
    public double Standardised => (Measured - Estimated).Magnitude / Sigma;
}

/// <summary>
/// A three-phase <b>node-voltage</b> WLS distribution state estimator. State is the per-phase rectangular
/// voltage [Vre, Vim] of every non-source bus (the source voltage is the fixed reference). Per-phase voltage
/// magnitudes fix the magnitudes (nonlinear rows); per-phase branch current phasors - linear in the
/// rectangular state via the 3x3 series admittance Y = Z^-1 - anchor the angles the magnitude-only telemetry
/// cannot. A Gauss-Newton loop resolves the state; on a network solved by <see cref="ThreePhaseSweep"/> a full
/// consistent measurement set recovers the truth to machine precision. A static, measurement-only filter -
/// no Dynamics/ process model, so no review-gate entry (see <see cref="GridSim.Core.Diagnostics.RestrictionReview"/>).
/// </summary>
public static class ThreePhaseWlsEstimator
{
    /// <summary>
    /// The robust re-weighting has settled when no measurement's weight moved by this much or more in the last pass
    /// (the value the loop has always used; named so <see cref="ThreePhaseWlsResult.IrlsCapReached"/> can cite it).
    /// </summary>
    public const double IrlsWeightTolerance = 1e-3;

    /// <summary>
    /// D-DOC-112 (October 2026): the largest state this estimator accepts, in reals (six per non-source bus: the real
    /// and imaginary voltage of each phase). Each Gauss-Newton iteration forms a dense s x s gain matrix and inverts it
    /// by Gauss-Jordan (an s x 2s working matrix), so memory grows as s^2 and time as s^3 per iteration. It shares the
    /// existing "dense O(s^3), called repeatedly" limit (<see cref="GridSim.Core.Estimation.DenseEstimationLimits.RecursiveFilterMaxState"/>,
    /// 2,500 states, so 416 non-source buses: about 150 MB of working matrices) rather than inventing a new value.
    /// Above it the estimator refuses with <see cref="NotSupportedException"/> before allocating anything. A
    /// factorisation without the explicit inverse was considered and not taken: it would change the last bits of every
    /// existing estimate, and the guard alone closes the defect (no size check at all).
    /// </summary>
    public const int MaxState = GridSim.Core.Estimation.DenseEstimationLimits.RecursiveFilterMaxState;

    /// <summary>Solve the unbalanced state from per-phase voltage magnitudes and branch current phasors.
    /// With <paramref name="robust"/> the Gauss-Newton solve is wrapped in a weighted-least-absolute-value
    /// IRLS outer loop (weight 1/|standardized residual|), so a gross measurement is down-weighted rather than
    /// followed - the distribution-robust estimator for AMI/telemetry outliers.</summary>
    public static ThreePhaseWlsResult Estimate(
        MultiPhaseModel model,
        IReadOnlyList<ThreePhaseVMag> vmags,
        IReadOnlyList<ThreePhaseCurrent> currents,
        double tolerance = 1e-10,
        int maxIterations = 50,
        bool robust = false,
        int maxRobustIterations = 8,
        double robustFloor = 1e-3)
    {
        // N167 (October 2026): the measurement model is per-phase voltage magnitudes and per-phase series currents
        // I = Z^-1 (V_from - V_to); no load or shunt capacitor appears in it (there are no injection measurements), so
        // those are accepted, exactly as the constant-power loads always were. A lateral (a Z with absent phases), a
        // closed switch (Z = 0) or a regulator cannot be written as Z^-1 (V_from - V_to), and line charging would add a
        // shunt current the rows leave out: those are still refused, by name.
        model.RequireSupportedElements("the three-phase WLS estimator",
            MultiPhaseElement.Capacitor | MultiPhaseElement.VoltageDependentLoad | MultiPhaseElement.DeltaLoad,
            "loads and capacitors do not enter its measurement model, which has no injection measurements");
        int n = model.Buses.Count;
        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idToIndex[model.Buses[i].Id] = i;

        // Non-source local state layout: 6 reals per non-source bus (Vre,Vim per phase).
        var localIdx = new int[n];
        Complex[] sourceV = MultiPhaseModel.BalancedNominal();
        int nNonSource = 0;
        for (int i = 0; i < n; i++)
        {
            if (model.Buses[i].IsSource) { localIdx[i] = -1; sourceV = model.Buses[i].SourceV ?? sourceV; }
            else localIdx[i] = nNonSource++;
        }
        int s = 6 * nNonSource;

        // D-DOC-217 (October 2026): the iteration controls are checked, so a value that cannot mean anything (a zero or
        // negative tolerance, no iterations, no robust pass, a floor that is not a positive number) is refused by name
        // instead of giving an estimate that silently never ran. Every value that worked before is accepted unchanged.
        if (!(tolerance > 0.0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The Gauss-Newton tolerance must be a finite number above 0.");
        if (maxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "At least one Gauss-Newton iteration is needed.");
        if (robust && maxRobustIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRobustIterations), maxRobustIterations, "The robust (WLAV) estimator needs at least one re-weighting pass.");
        if (robust && (!(robustFloor > 0.0) || !double.IsFinite(robustFloor)))
            throw new ArgumentOutOfRangeException(nameof(robustFloor), robustFloor,
                "The robust (WLAV) residual floor must be a finite number above 0 (it bounds the weight 1 / |standardised residual|).");
        // N264 (October 2026): every measurement sigma must be usable (finite, above 0, finite positive weight 1 / sigma^2),
        // refused by name. A zero sigma used to give an infinite weight and NaN state; see ThreePhaseSigmaChecks.
        ThreePhaseSigmaChecks.Require("the three-phase WLS estimator", vmags, currents);

        // D-DOC-112 (October 2026): the gain below is a dense s x s matrix inverted by Gauss-Jordan every iteration, and
        // there was no size check: a large lifted case would exhaust memory or run for hours. Refuse above the shared
        // dense limit before anything of size s x s is allocated.
        if (s > MaxState)
            throw new NotSupportedException(
                $"the three-phase WLS estimator is dense (a {s} x {s} gain matrix inverted every iteration: six states per " +
                $"non-source bus, {nNonSource} non-source buses) and is limited to {MaxState} states ({MaxState / 6} non-source " +
                "buses). Use the branch-current estimator (it needs no matrix) or the three-phase sweep for a feeder this size.");

        int Col(int busIdx, int phase, bool imag) => localIdx[busIdx] * 6 + phase * 2 + (imag ? 1 : 0);

        // Precompute per-branch series admittance and endpoint indices for the current rows.
        var branchY = new Dictionary<(int, int), (PhaseImpedance Y, int F, int T)>();
        foreach (PhaseBranch br in model.Branches)
            if (idToIndex.TryGetValue(br.From, out int f) && idToIndex.TryGetValue(br.To, out int t))
                branchY[(br.From, br.To)] = (br.Z.Invert(), f, t);

        // R236: a current measurement authored against the reversed endpoints (To, From) used to be skipped
        // silently (the lookup is by the exact (From, To) key), so this estimator and BranchCurrentDsse - which
        // flags the same input - diverged on identical data. A PhaseBranch is a pure series element, so the
        // current into its To end is exactly the negation of the current into its From end: re-key such a row
        // onto the model orientation and negate it. A measurement naming no branch in either orientation is
        // an input error and fails loud rather than being dropped.
        var oriented = new List<ThreePhaseCurrent>(currents.Count);
        // D-DOC-217: which rows were re-keyed, so the residual table can report each in the orientation it was authored.
        var reversedRow = new List<bool>(currents.Count);
        foreach (ThreePhaseCurrent cur in currents)
        {
            if (branchY.ContainsKey((cur.FromBusId, cur.ToBusId))) { oriented.Add(cur); reversedRow.Add(false); }
            else if (branchY.ContainsKey((cur.ToBusId, cur.FromBusId)))
            {
                oriented.Add(cur with { FromBusId = cur.ToBusId, ToBusId = cur.FromBusId, Value = -cur.Value });
                reversedRow.Add(true);
            }
            else
                throw new ArgumentException(
                    $"Current measurement on branch {cur.FromBusId}->{cur.ToBusId} (phase {cur.Phase}) matches no branch of the model in either orientation.",
                    nameof(currents));
        }
        currents = oriented;

        // Flat start: every non-source bus at the source voltage.
        var x = new double[s];
        for (int i = 0; i < n; i++)
        {
            if (localIdx[i] < 0) continue;
            for (int p = 0; p < 3; p++)
            {
                x[Col(i, p, false)] = sourceV[p].Real;
                x[Col(i, p, true)] = sourceV[p].Imaginary;
            }
        }

        Complex[][] BuildV()
        {
            var v = new Complex[n][];
            for (int i = 0; i < n; i++)
            {
                v[i] = new Complex[3];
                for (int p = 0; p < 3; p++)
                    v[i][p] = localIdx[i] < 0 ? sourceV[p] : new Complex(x[Col(i, p, false)], x[Col(i, p, true)]);
            }
            return v;
        }

        // Robust (WLAV) reweighting multipliers per measurement (all 1 = plain WLS).
        var vmagRw = new double[vmags.Count];
        var curRw = new double[currents.Count];
        Array.Fill(vmagRw, 1.0);
        Array.Fill(curRw, 1.0);

        bool converged = false;
        bool singular = false;
        bool irlsCapReached = false;   // D-DOC-115
        int iter = 0;
        double maxDelta = double.PositiveInfinity;
        int robustPasses = robust ? maxRobustIterations : 1;
        for (int outer = 0; outer < robustPasses; outer++)
        {
            converged = false;
            for (int gn = 0; gn < maxIterations; gn++, iter++)
            {
                Complex[][] v = BuildV();
                var g = new double[s, s];
                var rhs = new double[s];

                // Voltage-magnitude rows (nonlinear), robust-reweighted.
                for (int mi = 0; mi < vmags.Count; mi++)
                {
                    ThreePhaseVMag vm = vmags[mi];
                    // An unknown bus is an input error, not a row to drop silently; the source bus (localIdx < 0, its
                    // voltage is fixed, not estimated) is the one deliberate exclusion.
                    if (!idToIndex.TryGetValue(vm.BusId, out int bi))
                        throw new ArgumentException($"Three-phase WLS: voltage-magnitude measurement {mi} names bus {vm.BusId}, which is not in the feeder.");
                    if (vm.Phase is < 0 or > 2)
                        throw new ArgumentException($"Three-phase WLS: voltage-magnitude measurement {mi} names phase {vm.Phase}; phases are 0, 1 and 2.");
                    if (localIdx[bi] < 0) continue;
                    double vre = v[bi][vm.Phase].Real, vim = v[bi][vm.Phase].Imaginary;
                    double mag = Math.Sqrt(vre * vre + vim * vim);
                    double w = vmagRw[mi] / (vm.Sigma * vm.Sigma);
                    var row = new double[s];
                    row[Col(bi, vm.Phase, false)] = vre / mag;
                    row[Col(bi, vm.Phase, true)] = vim / mag;
                    Accumulate(g, rhs, row, vm.Value - mag, w, s);
                }

                // Branch current-phasor rows (linear): I = Y (V_from - V_to), split real/imag.
                for (int ci = 0; ci < currents.Count; ci++)
                {
                    ThreePhaseCurrent cur = currents[ci];
                    if (!branchY.TryGetValue((cur.FromBusId, cur.ToBusId), out (PhaseImpedance Y, int F, int T) bc)) continue;
                    double w = curRw[ci] / (cur.Sigma * cur.Sigma);
                    int p = cur.Phase;
                    var rowRe = new double[s];
                    var rowIm = new double[s];
                    double hRe = 0, hIm = 0;
                    for (int q = 0; q < 3; q++)
                    {
                        double gpq = bc.Y[p, q].Real, bpq = bc.Y[p, q].Imaginary;
                        Complex vf = v[bc.F][q], vt = v[bc.T][q];
                        hRe += gpq * (vf.Real - vt.Real) - bpq * (vf.Imaginary - vt.Imaginary);
                        hIm += bpq * (vf.Real - vt.Real) + gpq * (vf.Imaginary - vt.Imaginary);
                        AddCol(rowRe, localIdx, bc.F, q, false, gpq); AddCol(rowRe, localIdx, bc.F, q, true, -bpq);
                        AddCol(rowRe, localIdx, bc.T, q, false, -gpq); AddCol(rowRe, localIdx, bc.T, q, true, bpq);
                        AddCol(rowIm, localIdx, bc.F, q, false, bpq); AddCol(rowIm, localIdx, bc.F, q, true, gpq);
                        AddCol(rowIm, localIdx, bc.T, q, false, -bpq); AddCol(rowIm, localIdx, bc.T, q, true, -gpq);
                    }
                    Accumulate(g, rhs, rowRe, cur.Value.Real - hRe, w, s);
                    Accumulate(g, rhs, rowIm, cur.Value.Imaginary - hIm, w, s);
                }

                double[] dx;
                try { dx = DseLinAlg.MatVec(DseLinAlg.Invert(g, s), rhs, s); }
                catch (InvalidOperationException) { singular = true; break; } // unobservable

                maxDelta = 0;
                for (int i = 0; i < s; i++) { x[i] += dx[i]; maxDelta = Math.Max(maxDelta, Math.Abs(dx[i])); }
                if (maxDelta < tolerance) { converged = true; iter++; break; }
            }

            if (singular || !robust || !converged) break;

            // IRLS reweight: WLAV weight 1/max(|standardized residual|, floor) at the converged state.
            Complex[][] vr = BuildV();
            double maxChange = 0;
            for (int mi = 0; mi < vmags.Count; mi++)
            {
                ThreePhaseVMag vm = vmags[mi];
                if (!idToIndex.TryGetValue(vm.BusId, out int bi) || localIdx[bi] < 0) continue;
                double rstd = Math.Abs((vm.Value - vr[bi][vm.Phase].Magnitude) / vm.Sigma);
                double nw = 1.0 / Math.Max(rstd, robustFloor);
                maxChange = Math.Max(maxChange, Math.Abs(nw - vmagRw[mi]));
                vmagRw[mi] = nw;
            }
            for (int ci = 0; ci < currents.Count; ci++)
            {
                ThreePhaseCurrent cur = currents[ci];
                if (!branchY.TryGetValue((cur.FromBusId, cur.ToBusId), out (PhaseImpedance Y, int F, int T) bc)) continue;
                Complex[] diff = { vr[bc.F][0] - vr[bc.T][0], vr[bc.F][1] - vr[bc.T][1], vr[bc.F][2] - vr[bc.T][2] };
                Complex ihat = bc.Y.Multiply(diff)[cur.Phase];
                double rstd = Math.Sqrt(
                    (cur.Value.Real - ihat.Real) * (cur.Value.Real - ihat.Real) +
                    (cur.Value.Imaginary - ihat.Imaginary) * (cur.Value.Imaginary - ihat.Imaginary)) / cur.Sigma;
                double nw = 1.0 / Math.Max(rstd, robustFloor);
                maxChange = Math.Max(maxChange, Math.Abs(nw - curRw[ci]));
                curRw[ci] = nw;
            }
            if (maxChange < IrlsWeightTolerance) break;
            // D-DOC-115 (October 2026): the last allowed pass re-weighted and the weights still moved, so the loop ends
            // here with the state solved under the previous weights: not a settled robust estimate. Record it (the
            // single-phase estimator's rule, R175); the arithmetic is unchanged.
            if (outer == robustPasses - 1) irlsCapReached = true;
        }

        Complex[][] vFinal = BuildV();
        double objective = Objective(model, idToIndex, localIdx, branchY, vFinal, vmags, currents);
        var busIds = new int[n];
        for (int i = 0; i < n; i++) busIds[i] = model.Buses[i].Id;
        return new ThreePhaseWlsResult(converged, iter, maxDelta, busIds, vFinal, objective)
        {
            IrlsCapReached = irlsCapReached,
            Residuals = ResidualRows(idToIndex, localIdx, branchY, vFinal, vmags, currents, reversedRow),
        };

        static void AddCol(double[] row, int[] localIndex, int busIdx, int phase, bool imag, double val)
        {
            if (localIndex[busIdx] < 0) return; // source column does not exist
            row[localIndex[busIdx] * 6 + phase * 2 + (imag ? 1 : 0)] += val;
        }
    }

    private static void Accumulate(double[,] g, double[] rhs, double[] row, double residual, double w, int s)
    {
        for (int i = 0; i < s; i++)
        {
            double hwi = row[i] * w;
            if (hwi == 0.0) continue;
            rhs[i] += hwi * residual;
            for (int j = 0; j < s; j++) g[i, j] += hwi * row[j];
        }
    }

    /// <summary>
    /// D-DOC-217 (October 2026): the per-measurement residual rows of <see cref="ThreePhaseWlsResult.Residuals"/>. The
    /// same filters as the fit and <see cref="Objective"/> (a source-bus magnitude and a current naming no branch are
    /// left out); the estimated value is formed exactly as the IRLS re-weighting forms it. A current that was authored
    /// against the reversed endpoints is reported as authored: its measured value as given and the estimate negated
    /// (the current into the To end of a series element is minus the current into its From end). Reads only; nothing
    /// here feeds back into the estimate or the objective.
    /// </summary>
    private static IReadOnlyList<ThreePhaseMeasurementResidual> ResidualRows(
        Dictionary<int, int> idToIndex, int[] localIdx,
        Dictionary<(int, int), (PhaseImpedance Y, int F, int T)> branchY, Complex[][] v,
        IReadOnlyList<ThreePhaseVMag> vmags, IReadOnlyList<ThreePhaseCurrent> orientedCurrents, IReadOnlyList<bool> reversedRow)
    {
        var rows = new List<ThreePhaseMeasurementResidual>(vmags.Count + orientedCurrents.Count);
        foreach (ThreePhaseVMag vm in vmags)
        {
            if (!idToIndex.TryGetValue(vm.BusId, out int bi) || localIdx[bi] < 0) continue;
            rows.Add(new ThreePhaseMeasurementResidual("vmag", vm.BusId, -1, vm.Phase,
                new Complex(vm.Value, 0.0), new Complex(v[bi][vm.Phase].Magnitude, 0.0), vm.Sigma));
        }
        for (int ci = 0; ci < orientedCurrents.Count; ci++)
        {
            ThreePhaseCurrent cur = orientedCurrents[ci];
            if (!branchY.TryGetValue((cur.FromBusId, cur.ToBusId), out (PhaseImpedance Y, int F, int T) bc)) continue;
            Complex[] diff = { v[bc.F][0] - v[bc.T][0], v[bc.F][1] - v[bc.T][1], v[bc.F][2] - v[bc.T][2] };
            Complex ihat = bc.Y.Multiply(diff)[cur.Phase];
            rows.Add(reversedRow[ci]
                ? new ThreePhaseMeasurementResidual("current", cur.ToBusId, cur.FromBusId, cur.Phase, -cur.Value, -ihat, cur.Sigma)
                : new ThreePhaseMeasurementResidual("current", cur.FromBusId, cur.ToBusId, cur.Phase, cur.Value, ihat, cur.Sigma));
        }
        return rows;
    }

    private static double Objective(
        MultiPhaseModel model, Dictionary<int, int> idToIndex, int[] localIdx,
        Dictionary<(int, int), (PhaseImpedance Y, int F, int T)> branchY, Complex[][] v,
        IReadOnlyList<ThreePhaseVMag> vmags, IReadOnlyList<ThreePhaseCurrent> currents)
    {
        double j = 0;
        foreach (ThreePhaseVMag vm in vmags)
        {
            // R237: the same filter as the fit - a source-bus magnitude has no state column (the source voltage
            // is the fixed reference), so no estimate can reduce its residual and it must not count in J.
            if (!idToIndex.TryGetValue(vm.BusId, out int bi) || localIdx[bi] < 0) continue;
            double r = vm.Value - v[bi][vm.Phase].Magnitude;
            j += r * r / (vm.Sigma * vm.Sigma);
        }
        foreach (ThreePhaseCurrent cur in currents)
        {
            if (!branchY.TryGetValue((cur.FromBusId, cur.ToBusId), out (PhaseImpedance Y, int F, int T) bc)) continue;
            Complex[] diff = { v[bc.F][0] - v[bc.T][0], v[bc.F][1] - v[bc.T][1], v[bc.F][2] - v[bc.T][2] };
            Complex i = bc.Y.Multiply(diff)[cur.Phase];
            double rRe = cur.Value.Real - i.Real, rIm = cur.Value.Imaginary - i.Imaginary;
            j += (rRe * rRe + rIm * rIm) / (cur.Sigma * cur.Sigma);
        }
        return j;
    }
}
