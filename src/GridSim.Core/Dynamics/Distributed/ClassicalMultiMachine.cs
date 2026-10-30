using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// Classical multi-machine transient-stability kernel - the per-generator backbone reserved for the
/// small canonical validation cases (WSCC-9, Kundur two-area, IEEE-39), distinct from the domain-reduced
/// COI kernel (<see cref="MultiDomainSwing"/>) that carries GB scale.
///
/// Each machine is the classical model: a constant voltage <c>E'angle delta</c> behind its transient reactance,
/// swinging over the network reduced (Kron) to the generator internal nodes. The electrical power out of
/// machine i is
///     Pe_i = sum_j |E_i||E_j| ( G_ij cos(delta_i-delta_j) + B_ij sin(delta_i-delta_j) )
/// with the reduced admittance <c>Y = G + jB</c>, and the swing per machine (per-unit, H in seconds):
///     ddelta_i/dt = omega_s * omegabar_i
///     domegabar_i/dt = ( P_m,i - P_e,i - D_i*omegabar_i ) / (2 H_i)
/// integrated with classical <b>RK4</b> for first-swing accuracy - the refinement the domain-reduced
/// kernel defers. A fault is a window [<see cref="Fault.OnSeconds"/>, <see cref="Fault.ClearSeconds"/>)
/// during which a different reduced admittance <see cref="Fault.YFaulted"/> applies (e.g. all-zero for a
/// bolted terminal fault => Pe = 0), after which the network returns to <paramref name="yReduced"/>. The window's
/// edges are read with the RMS integrator's R249 tolerance (N047): an RK4 sample within a millionth of a step of an
/// edge counts as having reached it, so the one-ulp rounding of an edge formed as onset + clearing never decides
/// whether a sample sees the fault.
///
/// This kernel takes ALREADY-REDUCED inputs (H, D, Pm, |E'|, Y_red) so it is validatable in isolation
/// against exact analytic references - the single-machine-infinite-bus mode frequency, the equal-area
/// critical clearing time, and undamped energy conservation - without depending on a power-flow solve.
/// <see cref="ClassicalInit"/> builds those inputs from a solved <see cref="Model.GridModel"/>.
/// </summary>
public static class ClassicalMultiMachine
{
    /// <summary>A three-phase fault as a temporary reduced admittance over a time window.</summary>
    public sealed record Fault(double OnSeconds, double ClearSeconds, Complex[,] YFaulted);

    /// <summary>Per-machine trajectory: rotor angle delta(t) (rad) and per-unit speed deviation omegabar(t).</summary>
    public sealed record MachineTrace(
        IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double> DeltaRad,
        IReadOnlyList<double> SpeedPu);

    public sealed record Result(IReadOnlyList<MachineTrace> Machines, bool StayedInSynchronism);

    /// <summary>
    /// Integrate the classical swing of <paramref name="h"/>.Length machines. <paramref name="eq"/> = |E'|
    /// magnitudes (pu), <paramref name="pm"/> = mechanical powers (pu on the system base), <paramref
    /// name="yReduced"/> = admittance among the internal nodes (system base). delta0 = the initial rotor
    /// angles (rad). Loss of synchronism is declared when any inter-machine angle exceeds
    /// <paramref name="slipLimitRad"/> (default 2pi ~ one pole slip) relative to the inertia-weighted COI.
    ///
    /// <paramref name="mechanicalPower"/> is an optional per-step governor hook: <c>(machineIndex,
    /// speedDeviationPu) =&gt; Pm</c>, evaluated once per machine at the start of each outer step and held
    /// across that step's RK4 sub-steps (operator splitting - the governor state is frozen within the
    /// step). When null the mechanical powers stay at <paramref name="pm"/> and the integration is
    /// byte-for-byte the constant-Pm classical swing; a governed run therefore collapses onto the classical
    /// result exactly when the hook returns the initial powers. This is how <see
    /// cref="Models.GovernedMultiMachine"/> drives per-machine turbine-governors without a second kernel.
    ///
    /// <paramref name="internalVoltage"/> is the analogous per-step flux-decay hook: <c>(machineIndex,
    /// currentInternalVoltageEqp, recoveredDAxisCurrentId) =&gt; |E'q| for the next step</c>. It lets the
    /// constant-<c>E'</c> classical model become the varying-<c>E'q</c> flux-decay model: the internal
    /// voltage magnitude <c>|E'|</c> the swing uses for <c>Pe</c> is no longer fixed at <paramref
    /// name="eq"/> but is refreshed each step. The kernel recovers the machine's d-axis current from the
    /// network reactive injection - <c>Id_i = Qe_i / |E'q|_i = sum_j |E'|_j (G_ij sin(delta_ij) -
    /// B_ij cos(delta_ij))</c>, the same reduced admittance the real power uses - at the start-of-step
    /// state, then after the swing advance calls the hook to set each machine's <c>|E'q|</c> for the next
    /// step (operator splitting: <c>|E'q|</c> is frozen across the RK4 sub-steps, exactly as the SMIB
    /// <see cref="Models.OneAxisMachine"/> freezes it). When null the internal voltages stay at <paramref
    /// name="eq"/> verbatim and the run is byte-for-byte the constant-<c>E'</c> classical swing; a flux-decay
    /// run collapses onto it exactly when the hook returns its <c>currentEqp</c> argument unchanged. This is
    /// how <see cref="Models.MultiMachineFluxDecay"/> drives per-machine field flux-linkage decay - the
    /// multi-machine generalisation of the SMIB one-axis machine - without a second kernel.
    /// </summary>
    public static Result Simulate(
        double[] h, double[] d, double[] pm, double[] eq, double[] delta0,
        Complex[,] yReduced,
        double f0, double stepSeconds, double durationSeconds,
        Fault? fault = null,
        double slipLimitRad = 2.0 * Math.PI,
        Func<int, double, double>? mechanicalPower = null,
        Func<int, double, double, double>? internalVoltage = null)
        => SimulateAtTier(Numerics.SimdCapabilities.Tier, h, d, pm, eq, delta0, yReduced, f0, stepSeconds, durationSeconds,
            fault, slipLimitRad, mechanicalPower, internalVoltage);

    /// <summary>
    /// <see cref="Simulate"/> with the vector <paramref name="tier"/> of its batch kernels (the angle-difference SinCos
    /// and the two dot products of the electrical power) named explicitly: the R102 test seam that runs the swing on
    /// every tier's arm. Production calls <see cref="Simulate"/>, which passes <see cref="Numerics.SimdCapabilities.Tier"/>.
    /// D1 (October 2026): both kernels give the same bits at every tier, so the trajectory does too
    /// (<c>D1CrossTierTests</c>).
    /// </summary>
    internal static Result SimulateAtTier(
        Numerics.SimdTier tier,
        double[] h, double[] d, double[] pm, double[] eq, double[] delta0,
        Complex[,] yReduced,
        double f0, double stepSeconds, double durationSeconds,
        Fault? fault,
        double slipLimitRad,
        Func<int, double, double>? mechanicalPower,
        Func<int, double, double, double>? internalVoltage)
    {
        int n = h.Length;
        if (d.Length != n || pm.Length != n || eq.Length != n || delta0.Length != n)
            throw new ArgumentException("h, d, pm, eq, delta0 must all have the same length.");
        if (yReduced.GetLength(0) != n || yReduced.GetLength(1) != n)
            throw new ArgumentException("yReduced must be an nxn matrix.", nameof(yReduced));
        if (!(stepSeconds > 0) || !double.IsFinite(stepSeconds)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "the step must be a finite number of seconds above 0 (NaN used to pass this check).");

        double ws = 2.0 * Math.PI * f0;
        int steps = (int)Math.Round(durationSeconds / stepSeconds);
        // N047: the fault-edge tolerance (see Faulted below), the RMS engine's R249 rule.
        double edgeTolerance = stepSeconds * Models.MultiMachineSubtransient.StageTimeToleranceSteps;

        // Split real/imag once; the kernel reads G,B millions of times.
        var g = new double[n, n];
        var b = new double[n, n];
        var gF = new double[n, n];
        var bF = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                g[i, j] = yReduced[i, j].Real; b[i, j] = yReduced[i, j].Imaginary;
                Complex yf = fault?.YFaulted[i, j] ?? Complex.Zero;
                gF[i, j] = yf.Real; bF[i, j] = yf.Imaginary;
            }

        var delta = (double[])delta0.Clone();
        var omega = new double[n];   // per-unit speed deviation omegabar = deltaomega/omega_s
        // Mechanical power the derivative reads. A private copy so the optional governor hook can update it
        // per step without touching the caller's array; with no hook it holds pm verbatim (byte-identical).
        var pmv = (double[])pm.Clone();
        // Internal voltage magnitude the derivative reads. Same discipline: a private copy the optional
        // flux-decay hook refreshes each step; with no hook it holds eq verbatim, so Deriv reads exactly the
        // caller's magnitudes and the run is byte-for-byte the constant-E' classical swing.
        var eqv = (double[])eq.Clone();
        // d-axis current recovered from the network reactive injection at the start of each step, fed to the
        // flux-decay hook. Only allocated when that hook is present.
        double[]? idRec = internalVoltage is null ? null : new double[n];

        var tSeries = new List<double>[n];
        var dSeries = new List<double>[n];
        var wSeries = new List<double>[n];
        for (int i = 0; i < n; i++)
        {
            tSeries[i] = new List<double>(steps + 1);
            dSeries[i] = new List<double>(steps + 1);
            wSeries[i] = new List<double>(steps + 1);
        }

        bool inSync = true;

        // RK4 slope buffers + Deriv scratch, allocated ONCE. The step loop runs thousands of times;
        // the previous code newed ~14 double[n] per step, pure GC churn in the hot integrator.
        double[] d1 = new double[n], o1 = new double[n];
        double[] d2 = new double[n], o2 = new double[n];
        double[] d3 = new double[n], o3 = new double[n];
        double[] d4 = new double[n], o4 = new double[n];
        double[] dk = new double[n], ok = new double[n];   // Step probe point, reused each sub-step
        double[] dij = new double[n], sinB = new double[n], cosB = new double[n];
        double[] ecos = new double[n], esin = new double[n];

        for (int k = 0; k <= steps; k++)
        {
            double t = k * stepSeconds;
            for (int i = 0; i < n; i++) { tSeries[i].Add(t); dSeries[i].Add(delta[i]); wSeries[i].Add(omega[i]); }

            // COI angle (inertia-weighted) - the reference the synchronism test measures against.
            double hSum = 0.0, hDelta = 0.0;
            for (int i = 0; i < n; i++) { hSum += h[i]; hDelta += h[i] * delta[i]; }
            double coi = hDelta / hSum;
            for (int i = 0; i < n; i++)
                // A non-finite state (delta or omegabar gone NaN/+/-inf - e.g. a 0/0 swing from a zero-inertia machine)
                // is a LOSS of synchronism, not "in sync": Math.Abs(NaN) > limit is false, so without this
                // guard an un-integrable run would falsely report StayedInSynchronism = true.
                if (!double.IsFinite(delta[i]) || !double.IsFinite(omega[i]) || Math.Abs(delta[i] - coi) > slipLimitRad)
                    inSync = false;

            if (k == steps) break;

            // Optional governor hook: refresh each machine's mechanical power from its speed deviation at the
            // start of this step, then hold it across the RK4 sub-steps below.
            if (mechanicalPower is not null)
                for (int i = 0; i < n; i++) pmv[i] = mechanicalPower(i, omega[i]);

            // Which admittance is live over the RK4 sub-steps of this step. N047 (October 2026): the fault edges carry
            // the RMS integrator's R249 tolerance. An edge such as onset + clearing (0.1 + 167 * 0.002) is formed in
            // floating point and can land one ulp away from the RK4 sample time it means (0.43400000000000005 against
            // 0.434), so whether the sample AT the clearing instant saw the faulted or the cleared network was decided
            // by rounding. An edge now counts as reached at a sample time within a millionth of a step of it
            // (MultiMachineSubtransient.StageTimeToleranceSteps, shared, so both engines read their fault windows by
            // one convention): the fault is on from the first sample at or within that tolerance of the onset, and off
            // from the first sample at or within it of the clearing time. An edge genuinely between samples still
            // waits for the next sample.
            bool Faulted(double tt) => fault is not null
                && tt + edgeTolerance >= fault.OnSeconds && tt + edgeTolerance < fault.ClearSeconds;

            // Flux-decay hook: recover each machine's d-axis current from the network reactive injection at
            // the start-of-step state (the live admittance), Id_i = sum_j |E'|_j (G_ij sin d_ij - B_ij cos d_ij).
            // Held across the RK4 sub-steps; E'q is advanced from it after the swing (operator splitting).
            if (idRec is not null)
            {
                double[,] gg = Faulted(t) ? gF : g;
                double[,] bb = Faulted(t) ? bF : b;
                for (int i = 0; i < n; i++)
                {
                    double di = delta[i];
                    double id = 0.0;
                    for (int j = 0; j < n; j++)
                    {
                        (double s, double c) = DetMath.SinCos(di - delta[j]);
                        id += eqv[j] * (gg[i, j] * s - bb[i, j] * c);
                    }
                    idRec[i] = id;
                }
            }

            Deriv(delta, omega, Faulted(t), d1, o1);
            Step(delta, omega, d1, o1, stepSeconds * 0.5, dk, ok);
            Deriv(dk, ok, Faulted(t + 0.5 * stepSeconds), d2, o2);
            Step(delta, omega, d2, o2, stepSeconds * 0.5, dk, ok);
            Deriv(dk, ok, Faulted(t + 0.5 * stepSeconds), d3, o3);
            Step(delta, omega, d3, o3, stepSeconds, dk, ok);
            Deriv(dk, ok, Faulted(t + stepSeconds), d4, o4);

            for (int i = 0; i < n; i++)
            {
                delta[i] += stepSeconds / 6.0 * (d1[i] + 2 * d2[i] + 2 * d3[i] + d4[i]);
                omega[i] += stepSeconds / 6.0 * (o1[i] + 2 * o2[i] + 2 * o3[i] + o4[i]);
            }

            // Advance each machine's internal voltage for the next step from its start-of-step value and the
            // recovered d-axis current (the flux decay lives in the hook). eqv still holds the start-of-step
            // magnitude here - the RK4 above read it, and Deriv of the next step will read the refreshed value.
            if (internalVoltage is not null && idRec is not null)
                for (int i = 0; i < n; i++) eqv[i] = internalVoltage(i, eqv[i], idRec[i]);
        }

        // Electrical-power derivative, vectorised: the O(n^2) sin/cos over rotor-angle differences is
        // the density of the whole integrator (evaluated 4x per RK4 step, thousands of steps). For each
        // machine i the reduction pe = eq_i * SUM_j!=i (G_ij*eq_j*cos(d_ij) + B_ij*eq_j*sin(d_ij)) becomes
        // a batch SinCos over the angle-difference row, then two vectorised dot products over the
        // contiguous G/B rows (self term excluded by zeroing ecos_i/esin_i, added back explicitly).
        // D1 (October 2026, Mark's determinism rule 2026-10-04; fixes R113): the batch SinCos is the deterministic
        // fdlibm sin/cos per element (it was a ~1e-13 Cephes polynomial on a width-dependent prefix and the OS libm on
        // the rest), and Dot sums in one fixed order with no fused multiply-add (it summed in a lane-count-dependent
        // order with FMA where present), so the trajectory is the same bits on every CPU tier and operating system.
        void Deriv(double[] del, double[] om, bool faulted, double[] dDot, double[] oDot)
        {
            double[,] gg = faulted ? gF : g;
            double[,] bb = faulted ? bF : b;
            ref double gOrigin = ref gg[0, 0];
            ref double bOrigin = ref bb[0, 0];
            ref double ecosRef = ref ecos[0];
            ref double esinRef = ref esin[0];
            for (int i = 0; i < n; i++)
            {
                double deli = del[i];
                for (int j = 0; j < n; j++) dij[j] = deli - del[j];
                Numerics.SimdKernels.SinCos(dij, sinB, cosB, n, tier);
                for (int j = 0; j < n; j++) { double e = eqv[j]; ecos[j] = e * cosB[j]; esin[j] = e * sinB[j]; }
                ecos[i] = 0.0; esin[i] = 0.0; // exclude self (cos 0 = 1); diagonal added explicitly below

                double pe = eqv[i] * eqv[i] * gg[i, i]
                          + eqv[i] * (Numerics.SimdKernels.Dot(ref gOrigin, i * n, ref ecosRef, 0, n, tier)
                                   + Numerics.SimdKernels.Dot(ref bOrigin, i * n, ref esinRef, 0, n, tier));
                dDot[i] = ws * om[i];
                oDot[i] = (pmv[i] - pe - d[i] * om[i]) / (2.0 * h[i]);
            }
        }

        var traces = new List<MachineTrace>(n);
        for (int i = 0; i < n; i++) traces.Add(new MachineTrace(tSeries[i], dSeries[i], wSeries[i]));
        return new Result(traces, inSync);
    }

    /// <summary>y = base + slope*h - one RK4 sub-step probe point, written into caller buffers
    /// (reused across sub-steps and steps - the integrator runs thousands of steps).</summary>
    private static void Step(double[] delta, double[] omega, double[] dSlope, double[] oSlope, double h,
        double[] dOut, double[] oOut)
    {
        int n = delta.Length;
        for (int i = 0; i < n; i++) { dOut[i] = delta[i] + dSlope[i] * h; oOut[i] = omega[i] + oSlope[i] * h; }
    }
}
