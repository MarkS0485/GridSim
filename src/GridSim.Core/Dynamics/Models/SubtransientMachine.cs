using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The <b>subtransient</b> synchronous machine on a single-machine-infinite-bus (SMIB) network - the
/// GENROU/GENSAL-class model, the step up from the two-axis machine (<see cref="TwoAxisMachine"/>). It adds
/// the fast <b>subtransient EMFs</b> <c>E''q</c> and <c>E''d</c> behind the subtransient reactances
/// <c>X''d</c>, <c>X''q</c>: the damper-winding flux linkages that dominate the first cycle or two after a
/// disturbance, cascaded off the transient EMFs by the short subtransient time constants <c>T''do</c>,
/// <c>T''qo</c>.
///
/// <para>Six states per machine (per-unit, angle in rad, times in seconds):</para>
/// <code>
///   ddelta/dt     = omega_s * omega                            (rotor angle)
///   domega/dt     = (Pm - Pe - D*omega) / (2 H)                (swing)
///   T'do  dE'q/dt = Efd - E'q - (Xd - X'd)*Id                  (d-axis field flux decay; with saturation
///                                                               also - E''q*Se(|E''|), N045, see SaturationCurve)
///   T''do dE''q/dt = E'q - E''q - (X'd - X''d)*Id              (d-axis damper flux, cascaded off E'q)
///   T'qo  dE'd/dt = -E'd + (Xq - X'q)*Iq                       (q-axis flux decay; with saturation also
///                                                               - E''d*Se(|E''|)*(Xq - Xl)/(Xd - Xl), N045)
///   T''qo dE''d/dt = E'd - E''d + (X'q - X''q)*Iq              (q-axis damper flux, cascaded off E'd)
/// </code>
/// The stator (Rs = 0) sees the subtransient EMFs behind the subtransient reactances,
/// <c>Vd = E''d + X''q*Iq</c>, <c>Vq = E''q - X''d*Id</c>, so against the infinite bus
/// <code>
///   Id = (E''q - Vinf*cos(delta)) / (X''d + Xe)
///   Iq = (Vinf*sin(delta) - E''d) / (X''q + Xe)
/// </code>
/// - a closed-form network solve - and <c>Pe = E''d*Id + E''q*Iq + (X''q - X''d)*Id*Iq</c>.
///
/// <para>Every flux state rides the same <b>exact zero-order-hold</b> <see cref="FirstOrderLag"/>: over a step
/// with the currents and the transient EMFs held, each subtransient equation is a first-order lag on its
/// (constant) input, the same as the transient equations. The rotor swing advances by RK4 with the
/// subtransient EMFs frozen - the operator splitting used throughout the library.</para>
///
/// <para>It collapses onto <see cref="TwoAxisMachine"/> as a reduction identity: with <c>X''d = X'd</c> and
/// <c>X''q = X'q</c> the damper drivers vanish and the fixed points give <c>E''q = E'q</c>, <c>E''d = E'd</c>,
/// so the subtransient stator, currents and power are exactly the two-axis machine's.</para>
/// </summary>
public static class SubtransientMachine
{
    /// <summary>
    /// The open-circuit magnetic-saturation curve, the standard scaled-quadratic form
    /// <c>Se(x) = B*(x - A)^2 / x</c> for <c>x &gt; A</c> (else 0), fitted to the two tabulated points
    /// <c>Se(1.0) = SatAt1Pu</c> and <c>Se(1.2) = SatAt12Pu</c> - the PSS/E S(1.0), S(1.2) saturation FACTORS
    /// (dimensionless fractional extra field current over the air-gap line at 1.0 and 1.2 pu air-gap flux). The
    /// field-current increment that enters the field balance is <see cref="FieldIncrement"/> = <c>x * Se(x)</c>
    /// (R251, ruling Q9). The fit is closed-form:
    /// <c>k = sqrt(1.2*S12/S1)</c>, <c>A = (k - 1.2)/(k - 1)</c>, <c>B = S1/(1 - A)^2</c>. A curve with
    /// <c>SatAt1Pu = 0</c> is the unsaturated machine (<c>Se == 0</c>).
    /// </summary>
    public sealed record SaturationCurve
    {
        /// <summary>Air-gap flux (pu) of the LOWER saturation fit point, Se(1.0) = SatAt1Pu. The closed-form fit
        /// below is written for this point being exactly 1.0 (R170).</summary>
        public const double LowerFitPointPu = 1.0;

        /// <summary>Air-gap flux (pu) of the UPPER saturation fit point, Se(1.2) = SatAt12Pu (R170).</summary>
        public const double UpperFitPointPu = 1.2;

        /// <summary>Saturation Se at 1.0 pu air-gap flux (&gt;= 0; 0 = unsaturated).</summary>
        public double SatAt1Pu { get; }
        /// <summary>Saturation Se at 1.2 pu air-gap flux (&gt;= 0).</summary>
        public double SatAt12Pu { get; }
        private readonly double _a;
        private readonly double _b;

        public SaturationCurve(double satAt1Pu, double satAt12Pu)
        {
            if (satAt1Pu < 0.0) throw new ArgumentOutOfRangeException(nameof(satAt1Pu), "Se(1.0) must be >= 0.");
            if (satAt12Pu < 0.0) throw new ArgumentOutOfRangeException(nameof(satAt12Pu), "Se(1.2) must be >= 0.");
            SatAt1Pu = satAt1Pu;
            SatAt12Pu = satAt12Pu;
            if (satAt1Pu <= 0.0) return;   // unsaturated: A = B = 0, Se == 0
            if (satAt12Pu <= satAt1Pu)
                throw new ArgumentOutOfRangeException(nameof(satAt12Pu), "Se(1.2) must exceed Se(1.0) for a rising saturation curve.");
            double k = Math.Sqrt(UpperFitPointPu * satAt12Pu / satAt1Pu);
            _a = (k - UpperFitPointPu) / (k - 1.0);   // general form (k*x1 - x2)/(k - 1) with x1 = LowerFitPointPu = 1
            _b = satAt1Pu / ((1.0 - _a) * (1.0 - _a));
        }

        /// <summary>The saturation FACTOR <c>Se(x)</c> at air-gap flux <paramref name="x"/>: dimensionless, the
        /// fractional extra field current the saturated iron needs over the air-gap line at that flux
        /// (Se = (Ifd - Ifd_airgap) / Ifd_airgap). It is NOT itself a field current; see
        /// <see cref="FieldIncrement"/>.</summary>
        public double Se(double x) => _b > 0.0 && x > _a ? _b * (x - _a) * (x - _a) / x : 0.0;

        /// <summary>
        /// The extra field current (pu) the saturated iron needs at air-gap flux <paramref name="x"/>:
        /// <c>x * Se(x)</c> (R251, ruling Q9, 2026-10-02).
        ///
        /// <para>Convention cited: the PSS/E GENROU / GENSAL model (Siemens PTI PSS/E Model Library) and IEEE Std
        /// 1110 (Guide for Synchronous Generator Modeling Practices) define the tabulated S(1.0), S(1.2) as
        /// saturation FACTORS on the open-circuit characteristic: at terminal (air-gap) voltage x the field current
        /// is <c>Ifd = x * (1 + Se(x))</c> in pu of the air-gap-line field current, so the saturation increment
        /// over the air-gap line is <c>x * Se(x)</c>. With the scaled quadratic above that is exactly
        /// <c>B * (x - A)^2</c>. GridSim used to add <c>Se(x)</c> itself, which is 1/x of the standard increment:
        /// for a 0.10 / 0.30 curve it added 0.30 pu at x = 1.2 where the standard adds 1.2 * 0.30 = 0.36 pu
        /// (both agree at x = 1.0, where the increment equals S(1.0)).</para>
        ///
        /// <para>Computed literally as <c>x * Se(x)</c> (one extra multiply), so it is the product the standard
        /// writes, bit-for-bit, on every CPU (no fused multiply-add is formed by the JIT).</para>
        /// </summary>
        public double FieldIncrement(double x) => x * Se(x);

        /// <summary>
        /// N045 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): the GENROU saturation
        /// increments on BOTH axes, driven by the magnitude of the subtransient flux.
        ///
        /// <para>PSS/E GENROU (Siemens PTI, PSS/E Model Library, GENROU block diagram; the same in PowerWorld's and
        /// PowerFactory's GENROU-compatible models) evaluates saturation on <c>|psi''| = sqrt(psi''d^2 + psi''q^2)</c>, the
        /// air-gap flux behind the subtransient reactance, and applies it to both axes: the d-axis field balance gains
        /// <c>psi''d * Se(|psi''|)</c> and the q-axis flux balance <c>psi''q * Se(|psi''|) * (Xq - Xl) / (Xd - Xl)</c>
        /// (the q-axis magnetising path scaled by its reactance ratio). In GridSim's cascaded subtransient model the
        /// subtransient EMFs ARE that flux (the stator sees <c>Vq = E''q - X''d Id</c>, <c>Vd = E''d + X''q Iq</c>), so
        /// <c>psi''d = E''q</c> and the q-axis component is carried by <c>E''d</c>. GridSim used to apply
        /// <c>E'q * Se(E'q)</c> to the d-axis only: the d-axis TRANSIENT EMF in place of the subtransient flux magnitude,
        /// and nothing on the q-axis, so a loaded machine (whose air-gap flux has a large q-axis part) was under-saturated
        /// and its q-axis flux over-stated. With only a d-axis flux (open circuit: E''d = 0, E''q = E'q) the new d-axis
        /// increment is <c>x * Se(x)</c>, exactly the R251 / Q9 convention, so that ruling stands.</para>
        ///
        /// <para>Returns <c>(D, Q)</c>: <c>D = E''q * Se(|E''|)</c>, subtracted from the E'q driver;
        /// <c>Q = E''d * Se(|E''|) * qAxisRatio</c>, subtracted from the E'd driver (it opposes the q-axis flux as the
        /// d-axis term opposes the field flux). <paramref name="qAxisRatio"/> is <c>(Xq - Xl) / (Xd - Xl)</c> on the
        /// machine's own reactances. Plain multiplies in a fixed order (no fused multiply-add), so the same bits on every
        /// CPU.</para>
        /// </summary>
        public (double D, double Q) GenrouIncrements(double eqpp, double edpp, double qAxisRatio)
        {
            double psi = Math.Sqrt(eqpp * eqpp + edpp * edpp);
            double s = Se(psi);
            return (eqpp * s, edpp * s * qAxisRatio);
        }
    }

    /// <summary>
    /// N045: the GENROU q-axis saturation scale <c>(Xq - Xl) / (Xd - Xl)</c> from the machine's own reactances (any one
    /// consistent base; the ratio is base-free). <paramref name="xl"/> is the stator leakage reactance; 0 when the data
    /// gives none, the leakage-free limit <c>Xq / Xd</c> (documented, not hidden: the leakage only scales this ratio).
    /// Fails loud on a leakage that is negative, not finite, or not below <paramref name="xdpp"/> (PSS/E requires
    /// <c>Xl &lt; X''d</c>).
    /// </summary>
    public static double QAxisSaturationRatio(double xd, double xq, double xdpp, double xl)
    {
        if (!(xl >= 0.0) || !double.IsFinite(xl) || !(xl < xdpp))
            throw new ArgumentOutOfRangeException(nameof(xl), xl,
                $"The stator leakage reactance Xl must be finite, at least 0 and below X''d = {ControlBlocks.Num(xdpp)} (PSS/E GENROU rule; got {ControlBlocks.Num(xl)}).");
        return (xq - xl) / (xd - xl);
    }

    /// <summary>Machine and SMIB-network parameters, all per-unit on the machine base (times in seconds).
    /// Constraints are enforced on construction (fail-loud). <see cref="Saturation"/> is optional: null (the
    /// default) is the unsaturated machine, solving bit-for-bit as before. <see cref="LeakageReactance"/> (N045) is the
    /// stator leakage Xl, used only by the q-axis saturation scale; 0 (the default) is the leakage-free limit.</summary>
    public sealed record Parameters(
        double InertiaSeconds, double DampingPu,
        double Xd, double Xdp, double Xdpp, double Xq, double Xqp, double Xqpp,
        double Tdop, double Tdopp, double Tqop, double Tqopp,
        double ExternalReactance, double InfiniteBusVoltagePu,
        SaturationCurve? Saturation = null,
        double LeakageReactance = 0.0)
    {
        /// <summary>The one-axis field-current increment <c>x * Se(x)</c> at flux <paramref name="eqp"/>
        /// (<see cref="SaturationCurve.FieldIncrement"/>, R251 / Q9); 0 when unsaturated. N045: the machine no longer
        /// uses this (its saturation is the two-axis <see cref="SaturationCurve.GenrouIncrements"/> on |E''|); it is
        /// kept as the curve's open-circuit increment, which the two-axis form equals when E''d = 0 and E''q = x.</summary>
        public double SaturationTerm(double eqp) => Saturation?.FieldIncrement(eqp) ?? 0.0;

        /// <summary>N045: the q-axis saturation scale <c>(Xq - Xl) / (Xd - Xl)</c> (<see cref="QAxisSaturationRatio"/>).</summary>
        public double QAxisSaturationScale => QAxisSaturationRatio(Xd, Xq, Xdpp, LeakageReactance);

        /// <summary>N045: the two-axis GENROU saturation increments (D on the E'q driver, Q on the E'd driver) at the
        /// subtransient EMFs; (0, 0) when unsaturated.</summary>
        public (double D, double Q) SaturationIncrements(double eqpp, double edpp)
            => Saturation is { } sat ? sat.GenrouIncrements(eqpp, edpp, QAxisSaturationScale) : (0.0, 0.0);

        /// <summary>Inertia constant H (s); must be &gt; 0 (the swing divides by 2H).</summary>
        public double InertiaSeconds { get; } = InertiaSeconds > 0.0
            ? InertiaSeconds : throw new ArgumentOutOfRangeException(nameof(InertiaSeconds), "H must be > 0.");
        /// <summary>
        /// Synchronous d-axis reactance Xd (pu); must be finite, &gt; 0 and at least <see cref="Xdp"/>.
        ///
        /// <para>D-DOC-056 (docs-session finding, Ruling R1: Mark delegated 2026-10-04): Xd and Xq were the only
        /// reactances not validated, although both enter the field and q-axis flux equations as <c>Xd - X'd</c> and
        /// <c>Xq - X'q</c> (and Xq the equilibrium's <c>Iq = Vinf sin(delta) / (Xq + Xe)</c>). A zero, negative or NaN
        /// Xd / Xq, or one below its transient reactance, flips the sign of the flux-decay driver and gives a machine
        /// that is not a synchronous machine, with no error. The order Xd &gt;= X'd and Xq &gt;= X'q is the standard
        /// one (IEEE Std 1110; the GENROU / GENSAL data rules); equality is allowed, which is how a reduction identity
        /// (for example X'q = Xq, the one-axis limit) is written.</para>
        /// </summary>
        public double Xd { get; } = double.IsFinite(Xd) && Xd > 0.0 && Xd >= Xdp
            ? Xd : throw new ArgumentOutOfRangeException(nameof(Xd), Xd,
                $"Xd must be a finite number > 0 and at least X'd (got Xd = {Xd}, X'd = {Xdp}).");
        /// <summary>Synchronous q-axis reactance Xq (pu); must be finite, &gt; 0 and at least <see cref="Xqp"/>
        /// (D-DOC-056; see <see cref="Xd"/>).</summary>
        public double Xq { get; } = double.IsFinite(Xq) && Xq > 0.0 && Xq >= Xqp
            ? Xq : throw new ArgumentOutOfRangeException(nameof(Xq), Xq,
                $"Xq must be a finite number > 0 and at least X'q (got Xq = {Xq}, X'q = {Xqp}).");
        /// <summary>Transient d-axis reactance X'd (pu); must be &gt; 0.</summary>
        public double Xdp { get; } = Xdp > 0.0
            ? Xdp : throw new ArgumentOutOfRangeException(nameof(Xdp), "X'd must be > 0.");
        /// <summary>Subtransient d-axis reactance X''d (pu); must be &gt; 0.</summary>
        public double Xdpp { get; } = Xdpp > 0.0
            ? Xdpp : throw new ArgumentOutOfRangeException(nameof(Xdpp), "X''d must be > 0.");
        /// <summary>Transient q-axis reactance X'q (pu); must be &gt; 0.</summary>
        public double Xqp { get; } = Xqp > 0.0
            ? Xqp : throw new ArgumentOutOfRangeException(nameof(Xqp), "X'q must be > 0.");
        /// <summary>Subtransient q-axis reactance X''q (pu); must be &gt; 0.</summary>
        public double Xqpp { get; } = Xqpp > 0.0
            ? Xqpp : throw new ArgumentOutOfRangeException(nameof(Xqpp), "X''q must be > 0.");
        /// <summary>Open-circuit d-axis transient time constant T'do (s); must be &gt; 0.</summary>
        public double Tdop { get; } = Tdop > 0.0
            ? Tdop : throw new ArgumentOutOfRangeException(nameof(Tdop), "T'do must be > 0.");
        /// <summary>Open-circuit d-axis subtransient time constant T''do (s); must be &gt; 0.</summary>
        public double Tdopp { get; } = Tdopp > 0.0
            ? Tdopp : throw new ArgumentOutOfRangeException(nameof(Tdopp), "T''do must be > 0.");
        /// <summary>Open-circuit q-axis transient time constant T'qo (s); must be &gt; 0.</summary>
        public double Tqop { get; } = Tqop > 0.0
            ? Tqop : throw new ArgumentOutOfRangeException(nameof(Tqop), "T'qo must be > 0.");
        /// <summary>Open-circuit q-axis subtransient time constant T''qo (s); must be &gt; 0.</summary>
        public double Tqopp { get; } = Tqopp > 0.0
            ? Tqopp : throw new ArgumentOutOfRangeException(nameof(Tqopp), "T''qo must be > 0.");
        /// <summary>N045: stator leakage reactance Xl (pu); finite, &gt;= 0 and below X''d (PSS/E GENROU rule). Only the
        /// q-axis saturation scale reads it.</summary>
        public double LeakageReactance { get; } = double.IsFinite(LeakageReactance) && LeakageReactance >= 0.0 && LeakageReactance < Xdpp
            ? LeakageReactance : throw new ArgumentOutOfRangeException(nameof(LeakageReactance), LeakageReactance,
                $"Xl must be a finite number >= 0 and below X''d (got Xl = {LeakageReactance}, X''d = {Xdpp}).");
        /// <summary>External reactance Xe to the infinite bus (pu); must be &gt;= 0.</summary>
        public double ExternalReactance { get; } = ExternalReactance >= 0.0
            ? ExternalReactance : throw new ArgumentOutOfRangeException(nameof(ExternalReactance), "Xe must be >= 0.");
    }

    /// <summary>Trajectory: one entry per sample of each state and the derived terminal quantities.</summary>
    public sealed record Trace(
        IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double> DeltaRad,
        IReadOnlyList<double> SpeedPu,
        IReadOnlyList<double> EqpPu,
        IReadOnlyList<double> EdpPu,
        IReadOnlyList<double> EqppPu,
        IReadOnlyList<double> EddpPu,
        IReadOnlyList<double> TerminalVoltagePu,
        IReadOnlyList<double> ElectricalPowerPu);

    /// <summary>Closed-form dq stator currents (Rs = 0) from the subtransient EMFs and the SMIB network.</summary>
    public static (double Id, double Iq) Currents(Parameters p, double deltaRad, double eqpp, double edpp)
    {
        double id = (eqpp - p.InfiniteBusVoltagePu * DetMath.Cos(deltaRad)) / (p.Xdpp + p.ExternalReactance);
        double iq = (p.InfiniteBusVoltagePu * DetMath.Sin(deltaRad) - edpp) / (p.Xqpp + p.ExternalReactance);
        return (id, iq);
    }

    /// <summary>Air-gap power <c>Pe = E''d*Id + E''q*Iq + (X''q - X''d)*Id*Iq</c> (pu) from the subtransient EMFs.</summary>
    public static double ElectricalPower(Parameters p, double deltaRad, double eqpp, double edpp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqpp, edpp);
        return edpp * id + eqpp * iq + (p.Xqpp - p.Xdpp) * id * iq;
    }

    /// <summary>Terminal-voltage magnitude <c>sqrt(Vd^2 + Vq^2)</c> (pu), <c>Vd = E''d + X''q*Iq</c>,
    /// <c>Vq = E''q - X''d*Id</c>.</summary>
    public static double TerminalVoltage(Parameters p, double deltaRad, double eqpp, double edpp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqpp, edpp);
        double vd = edpp + p.Xqpp * iq;
        double vq = eqpp - p.Xdpp * id;
        return Math.Sqrt(vd * vd + vq * vq);
    }

    /// <summary>The initial flux states (E'q, E'd, E''q, E''d), field voltage and mechanical power that make
    /// the machine an exact equilibrium at <paramref name="delta0"/> with transient EMF <paramref name="eqp0"/>
    /// (E'q). The fixed points are closed-form: on the q-axis <c>Iq = Vinf sin(delta)/(Xq + Xe)</c> gives
    /// <c>E'd = (Xq - X'q)Iq</c> and <c>E''d = (Xq - X''q)Iq</c>; on the d-axis <c>E''q</c> and <c>Id</c> solve
    /// the subtransient stator against <c>E'q</c>, then <c>Efd = E'q + (Xd - X'd)Id</c> and <c>Pm = Pe</c>.</summary>
    public static (double MechanicalPowerPu, double FieldVoltagePu,
        double EqpPu, double EdpPu, double EqppPu, double EddpPu) Equilibrium(
        Parameters p, double delta0, double eqp0)
    {
        double iqSync = p.InfiniteBusVoltagePu * DetMath.Sin(delta0) / (p.Xq + p.ExternalReactance);
        double edp = (p.Xq - p.Xqp) * iqSync;      // E'd
        double eddp = (p.Xq - p.Xqpp) * iqSync;    // E''d = (Xq - X''q) Iq

        double vCos = p.InfiniteBusVoltagePu * DetMath.Cos(delta0);
        // E''q consistent with E'q0 through the transient/subtransient stator: solve
        // E''q = E'q0 - (X'd - X''d) Id, Id = (E''q - Vinf cos)/ (X''d + Xe).
        double eqpp = (eqp0 * (p.Xdpp + p.ExternalReactance) + (p.Xdp - p.Xdpp) * vCos) / (p.Xdp + p.ExternalReactance);
        double id = (eqpp - vCos) / (p.Xdpp + p.ExternalReactance);

        // N045 (October 2026, stream B3): GENROU saturation on |E''| on both axes. The d-axis is untouched by it (E''q and
        // Id above follow from E'q0 alone); the q-axis fixed point moves, because the q-axis flux balance now carries
        // - E''d * Se(|E''|) * (Xq - Xl)/(Xd - Xl). With s = Se(|E''|) * ratio, the q-axis equations
        //   E''d (1 + s) = (Xq - X''q) Iq,  Iq = (Vinf sin(delta) - E''d) / (X''q + Xe)
        // give E''d = (Xq - X''q) Vinf sin(delta) / ((1 + s)(X''q + Xe) + Xq - X''q), and s depends on |E''| through E''d,
        // so E''d is found by fixed-point iteration from the unsaturated value (a contraction: s is small and smooth).
        // When the unsaturated flux is below the curve's knee (s = 0) the old closed form above is kept, bit for bit.
        if (p.Saturation is { } sat && sat.Se(Math.Sqrt(eqpp * eqpp + eddp * eddp)) > 0.0)
        {
            double ratio = p.QAxisSaturationScale;
            double vSin = p.InfiniteBusVoltagePu * DetMath.Sin(delta0);
            double num = (p.Xq - p.Xqpp) * vSin;
            bool settled = false;
            for (int it = 0; it < SaturatedEquilibriumMaxIterations; it++)
            {
                double s = sat.Se(Math.Sqrt(eqpp * eqpp + eddp * eddp)) * ratio;
                double next = num / ((1.0 + s) * (p.Xqpp + p.ExternalReactance) + (p.Xq - p.Xqpp));
                // Settled at a few ulps: a rounding 2-cycle between neighbouring doubles must end the loop too.
                bool same = Math.Abs(next - eddp) <= SaturatedEquilibriumRelativeTolerance * Math.Abs(next);
                eddp = next;
                if (same) { settled = true; break; }
            }
            if (!settled)
                throw new InvalidOperationException($"The saturated subtransient equilibrium's q-axis flux did not settle in "
                    + $"{SaturatedEquilibriumMaxIterations} fixed-point iterations (delta0 = {delta0}, E'q0 = {eqp0}).");
            double iq = (vSin - eddp) / (p.Xqpp + p.ExternalReactance);
            edp = eddp - (p.Xqp - p.Xqpp) * iq;   // T''qo steady state: E''d = E'd + (X'q - X''q) Iq
        }

        // Field balance including saturation: Efd = E'q + (Xd - X'd)Id + E''q*Se(|E''|) (the GENROU d-axis increment,
        // N045; the integrator subtracts the same expression, so this is the equilibrium field).
        double efd = eqp0 + (p.Xd - p.Xdp) * id + p.SaturationIncrements(eqpp, eddp).D;
        double pm = ElectricalPower(p, delta0, eqpp, eddp);
        return (pm, efd, eqp0, edp, eqpp, eddp);
    }

    /// <summary>N045: cap on the fixed-point iterations of the saturated q-axis equilibrium. The map contracts by about
    /// the saturation slope (well below 1 for any physical curve), so it reaches a bit-exact fixed point in a few tens of
    /// iterations; the cap only turns a pathological curve into an error instead of a loop.</summary>
    public const int SaturatedEquilibriumMaxIterations = 200;

    /// <summary>N045: the saturated q-axis fixed point counts as settled when an iteration moves E''d by at most this
    /// fraction of itself (a few ulps: the iteration has then reached the bits rounding allows).</summary>
    public const double SaturatedEquilibriumRelativeTolerance = 1e-15;

    /// <summary>
    /// Integrate the machine from its six-state start. The field voltage is held at <paramref
    /// name="fieldVoltagePu"/> unless an <paramref name="exciter"/> is supplied (driven by
    /// <c>Efd = exciter(voltageReference - Vt)</c>). Per step, with the field held, all four flux states
    /// advance by the exact zero-order hold (currents and the transient EMFs frozen at start-of-step) and the
    /// rotor swing by RK4 (subtransient EMFs frozen) - operator splitting.
    /// </summary>
    public static Trace Simulate(
        Parameters p,
        double delta0, double omega0, double eqp0, double edp0, double eqpp0, double eddp0,
        double mechanicalPowerPu, double fieldVoltagePu,
        double f0, double stepSeconds, double durationSeconds,
        Sexs? exciter = null, double voltageReferencePu = double.NaN)
    {
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "the step must be a finite number of seconds above 0 (NaN used to pass this check).");
        if (!(durationSeconds >= 0.0) || !double.IsFinite(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "the duration must be a finite number of seconds, 0 or more.");
        if (exciter is not null && double.IsNaN(voltageReferencePu))
            throw new ArgumentException("An exciter needs a finite voltageReference.", nameof(voltageReferencePu));

        double ws = 2.0 * Math.PI * f0;
        int steps = (int)Math.Round(durationSeconds / stepSeconds);

        var fluxEqp = new FirstOrderLag(1.0, p.Tdop);    // E'q
        var fluxEqpp = new FirstOrderLag(1.0, p.Tdopp);  // E''q
        var fluxEdp = new FirstOrderLag(1.0, p.Tqop);    // E'd
        var fluxEdpp = new FirstOrderLag(1.0, p.Tqopp);  // E''d
        fluxEqp.Initialize(eqp0);
        fluxEqpp.Initialize(eqpp0);
        fluxEdp.Initialize(edp0);
        fluxEdpp.Initialize(eddp0);
        exciter?.Initialize(fieldVoltagePu);

        double delta = delta0, omega = omega0;

        var tS = new List<double>(steps + 1);
        var dS = new List<double>(steps + 1);
        var wS = new List<double>(steps + 1);
        var eqS = new List<double>(steps + 1);
        var edS = new List<double>(steps + 1);
        var eqppS = new List<double>(steps + 1);
        var eddpS = new List<double>(steps + 1);
        var vS = new List<double>(steps + 1);
        var pS = new List<double>(steps + 1);

        for (int k = 0; k <= steps; k++)
        {
            double eqp = fluxEqp.Output, edp = fluxEdp.Output;
            double eqpp = fluxEqpp.Output, eddp = fluxEdpp.Output;
            tS.Add(k * stepSeconds);
            dS.Add(delta);
            wS.Add(omega);
            eqS.Add(eqp);
            edS.Add(edp);
            eqppS.Add(eqpp);
            eddpS.Add(eddp);
            vS.Add(TerminalVoltage(p, delta, eqpp, eddp));
            pS.Add(ElectricalPower(p, delta, eqpp, eddp));

            if (k == steps) break;

            double efd = exciter is { } ex
                ? ex.Step(voltageReferencePu - vS[k], stepSeconds)
                : fieldVoltagePu;

            // All inputs frozen at the start-of-step state (operator splitting); each flux advances by ZOH.
            (double id0, double iq0) = Currents(p, delta, eqpp, eddp);
            // Flux decay with saturation (N045: GENROU, on |E''|, both axes), the increments frozen at the start-of-step
            // subtransient EMFs (operator splitting). Unsaturated: no increment on either axis, the historical arithmetic.
            if (p.Saturation is null)
            {
                fluxEqp.Step(efd - (p.Xd - p.Xdp) * id0 - p.SaturationTerm(eqp), stepSeconds);
                fluxEdp.Step((p.Xq - p.Xqp) * iq0, stepSeconds);
            }
            else
            {
                (double satD, double satQ) = p.SaturationIncrements(eqpp, eddp);
                fluxEqp.Step(efd - (p.Xd - p.Xdp) * id0 - satD, stepSeconds);
                fluxEdp.Step((p.Xq - p.Xqp) * iq0 - satQ, stepSeconds);
            }
            fluxEqpp.Step(eqp - (p.Xdp - p.Xdpp) * id0, stepSeconds);
            fluxEdpp.Step(edp + (p.Xqp - p.Xqpp) * iq0, stepSeconds);

            // Rotor swing: RK4 with the subtransient EMFs frozen at their start-of-step values.
            (delta, omega) = SwingStep(p, delta, omega, eqpp, eddp, mechanicalPowerPu, ws, stepSeconds);
        }

        return new Trace(tS, dS, wS, eqS, edS, eqppS, eddpS, vS, pS);
    }

    /// <summary>One RK4 step of the rotor swing (delta, omega) with the subtransient EMFs held constant.</summary>
    private static (double Delta, double Omega) SwingStep(
        Parameters p, double delta, double omega, double eqpp, double eddp, double pm, double ws, double dt)
    {
        SwingDeriv(p, delta, omega, eqpp, eddp, pm, ws, out double k1d, out double k1w);
        SwingDeriv(p, delta + 0.5 * dt * k1d, omega + 0.5 * dt * k1w, eqpp, eddp, pm, ws, out double k2d, out double k2w);
        SwingDeriv(p, delta + 0.5 * dt * k2d, omega + 0.5 * dt * k2w, eqpp, eddp, pm, ws, out double k3d, out double k3w);
        SwingDeriv(p, delta + dt * k3d, omega + dt * k3w, eqpp, eddp, pm, ws, out double k4d, out double k4w);
        return (delta + dt / 6.0 * (k1d + 2 * k2d + 2 * k3d + k4d),
                omega + dt / 6.0 * (k1w + 2 * k2w + 2 * k3w + k4w));
    }

    private static void SwingDeriv(
        Parameters p, double delta, double omega, double eqpp, double eddp, double pm, double ws,
        out double dDelta, out double dOmega)
    {
        double pe = ElectricalPower(p, delta, eqpp, eddp);
        dDelta = ws * omega;
        dOmega = (pm - pe - p.DampingPu * omega) / (2.0 * p.InertiaSeconds);
    }
}
