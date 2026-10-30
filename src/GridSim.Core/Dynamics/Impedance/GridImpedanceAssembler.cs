using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>
/// Assembles the grid-side driving-point impedance an inverter sees, reusing the harmonic Y(h) stack
/// (<see cref="FrequencyDependentYBus.Build"/> takes a real-valued h) and the driving-point solve seam
/// (<see cref="HarmonicImpedanceScan"/>: solve <c>Y(h) x = e_k</c>, take <c>x[k]</c>). It generalises that
/// solve to any real - and any SIGNED - frequency ratio, which is exactly what the dq +/-w0 sequence transform
/// needs.
///
/// <para><b>Conjugate symmetry (FIX 5).</b> Passing the SIGNED h (not <c>|h|</c>) to
/// <see cref="FrequencyDependentYBus.Build"/> makes <c>Y(-h) = conj(Y(h))</c> - the series term
/// <c>1/(R+jhX)</c>, the line charging <c>jhB</c>, and BOTH shunt rules (<c>Bs*h</c> for a capacitor,
/// <c>Bs/h</c> for a reactor) are odd in h - so the driving-point solve satisfies
/// <c>Z(-f) = conj(Z(f))</c> exactly. The naive <c>|h|</c> mirror does NOT (it loses the sign the reactor
/// rule needs), which is why this always solves at the true signed h.</para>
///
/// <para><b>DC arm (FIX 5).</b> The dq negative arm lands on <c>h = 0</c> when the perturbation frequency
/// equals f0. At h=0 the network is singular (a lossless branch is a short, a reactor shunt blows up), so a
/// null driving-point solve is raised as an EXPLICIT error, never silently returned as Z = 0, and the scan
/// skips <c>f_p ~ f0</c>.</para>
/// </summary>
public static class GridImpedanceAssembler
{
    /// <summary>Perturbation frequencies within this fraction of f0 hit the dq DC arm (h -&gt; 0) and are skipped.</summary>
    public const double DcArmGuardFraction = 1e-4;

    /// <summary>Base voltage (kV) of both buses of the canonical single-inverter-vs-Thevenin model. Everything in
    /// that model is per-unit, so the value only labels the buses; 400 kV is the GB transmission level (R164).</summary>
    public const double CanonicalBaseKv = 400.0;

    /// <summary>Capacitive shunt at the canonical PCC, in pu on the system base (stamped as
    /// <c>CanonicalPccShuntPu * baseMva</c> MVAr): a small ground path so the harmonic driving-point solve is
    /// not singular, standing in for the filter and charging shunt every real weak-grid PCC carries.</summary>
    public const double CanonicalPccShuntPu = 1e-3;

    /// <summary>
    /// The driving-point impedance at <paramref name="busId"/> for the SIGNED frequency ratio
    /// <paramref name="hSigned"/> (= f/f0, may be negative for the dq negative arm). Solves
    /// <c>Y(h) x = e_k</c> and returns <c>x[k]</c>. Throws when the solve is singular (a null column) - the
    /// explicit-error contract that keeps a DC/lossless singularity from masquerading as Z = 0.
    /// </summary>
    public static Complex DrivingPointAtRatio(
        GridModel model, int busId, double hSigned, HarmonicModelOptions opt, IComplexLinearSolver solver)
    {
        if (Math.Abs(hSigned) < DcArmGuardFraction)
            throw new InvalidOperationException(
                $"driving-point solve at h={hSigned:E3} is the DC arm (h -> 0): the network is singular there; the dq transform must skip f_p ~ f0.");

        int n = model.BusCount;
        int k = -1;
        for (int i = 0; i < n; i++) if (model.Buses[i].Id == busId) { k = i; break; }
        if (k < 0) throw new ArgumentException($"bus {busId} is not in the model.", nameof(busId));

        // The signed-frequency builder: a negative ratio (the dq negative arm) is a negative frequency, not a harmonic order.
        ComplexCsr y = FrequencyDependentYBus.BuildSignedFrequency(model, hSigned, opt);
        var e = new Complex[n];
        e[k] = Complex.One;
        Complex[]? x = solver.Solve(y, e);
        if (x is null)
            throw new InvalidOperationException(
                $"singular driving-point solve at bus {busId}, h={hSigned:E3} (no ground path / resonance); reported as an explicit error, not Z=0.");
        return x[k];
    }

    /// <summary>Positive-sequence grid impedance <c>Z+(f)</c> at a bus for a real network (via the signed-h
    /// driving-point solve). <paramref name="fHz"/> is the perturbation frequency in the stationary frame.</summary>
    public static Complex PositiveSequence(
        GridModel model, int busId, double fHz, double f0Hz, HarmonicModelOptions opt, IComplexLinearSolver solver)
        => DrivingPointAtRatio(model, busId, fHz / f0Hz, opt, solver);

    /// <summary>
    /// The dq grid impedance <c>Z_dq(f_p)</c> from the +/-w0-shifted positive-/negative-sequence driving-point
    /// impedances (modified-sequence transform): with <c>Zp = Z(f_p + f0)</c> and <c>Zn = Z(f_p - f0)</c>
    /// (signed h, so <c>Zn</c> is conjugate-symmetric for a sub-synchronous <c>f_p &lt; f0</c>),
    /// <c>Z_dq = 1/2 [[Zp+Zn, j(Zp-Zn)], [-j(Zp-Zn), Zp+Zn]]</c>. Throws (via the DC-arm guard) when
    /// <c>f_p ~ f0</c>.
    /// </summary>
    public static Complex2x2 Dq(
        GridModel model, int busId, double fpHz, double f0Hz, HarmonicModelOptions opt, IComplexLinearSolver solver)
    {
        Complex zp = DrivingPointAtRatio(model, busId, (fpHz + f0Hz) / f0Hz, opt, solver);
        Complex zn = DrivingPointAtRatio(model, busId, (fpHz - f0Hz) / f0Hz, opt, solver);
        Complex sum = 0.5 * (zp + zn);
        Complex dif = 0.5 * Complex.ImaginaryOne * (zp - zn);
        return new Complex2x2(sum, dif, -dif, sum);
    }

    /// <summary>
    /// The analytic Thevenin impedance <c>Z_th(f) = R + jX*(f/f0)</c> [pu on baseMva] of the canonical
    /// single-inverter-vs-Thevenin case at a given short-circuit ratio. SCR = S_sc / P_n = 1 / (|Z_th| * P_pu)
    /// at V = 1 pu, so <c>|Z_th| = 1 / (SCR * P_pu)</c>; X and R split by <paramref name="xOverR"/>. This is the
    /// EXACT grid impedance the SISO minor loop uses on the canonical case (machine-epsilon testable) - it is
    /// the Thevenin seen from the PCC with the ideal source shorted, so it is NOT the (singular) 2-bus Y-bus
    /// driving-point.
    /// </summary>
    public static Complex CanonicalTheveninImpedance(double scr, double xOverR, double pRatedPu, double fHz, double f0Hz)
    {
        (double r, double x) = TheveninRx(scr, xOverR, pRatedPu);
        return new Complex(r, x * (fHz / f0Hz));
    }

    /// <summary>The canonical Thevenin as a dq impedance matrix: an RL branch is
    /// <c>Z_dq(jw) = (R + jw L) I + w0 L J</c> with <c>L = X/w0</c>, <c>J = [[0,-1],[1,0]]</c>. Exact.</summary>
    public static Complex2x2 CanonicalTheveninDq(double scr, double xOverR, double pRatedPu, double omega, double f0Hz)
    {
        (double r, double x) = TheveninRx(scr, xOverR, pRatedPu);
        double w0 = 2.0 * Math.PI * f0Hz;
        double l = x / w0;
        Complex diag = new(r, omega * l);
        double off = w0 * l;   // = x
        return new Complex2x2(diag, new Complex(-off, 0), new Complex(off, 0), diag);
    }

    /// <summary>Split the Thevenin |Z| (from SCR) into R and X by the X/R ratio. Guards SCR &gt; 0 (FIX 2).</summary>
    public static (double R, double X) TheveninRx(double scr, double xOverR, double pRatedPu)
    {
        if (scr <= 0.0) throw new ArgumentOutOfRangeException(nameof(scr), scr, "SCR must be > 0 (an infinitely weak grid is not a valid operating point).");
        if (pRatedPu <= 0.0) throw new ArgumentOutOfRangeException(nameof(pRatedPu), pRatedPu, "rated power must be > 0.");
        double zMag = 1.0 / (scr * pRatedPu);
        double denom = Math.Sqrt(1.0 + (xOverR * xOverR));
        double r = zMag / denom;
        double x = zMag * xOverR / denom;
        return (r, x);
    }

    /// <summary>
    /// The canonical 2-bus model: an ideal slack behind the Thevenin branch (R + jX) feeding the PCC, where the
    /// inverter injects <c>P = pRatedPu * baseMva</c> MW. Reused by the SCR sweep so the sweep goes through the
    /// same assembler, not a special path. A shunt is stamped at the PCC to give the harmonic driving-point
    /// solve a ground path (a real weak-grid PCC always carries filter/charging shunt).
    /// </summary>
    public static GridModel CanonicalThevenin(double scr, double xOverR, double pRatedPu, double baseMva, double f0Hz = FrequencyLimits.NominalHz)
    {
        (double r, double x) = TheveninRx(scr, xOverR, pRatedPu);
        double pMw = pRatedPu * baseMva;
        var buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: CanonicalBaseKv, Vm: 1.0, Name: "grid"),
            new(2, BusType.Pv, BaseKv: CanonicalBaseKv, Vm: 1.0, Bs: CanonicalPccShuntPu * baseMva, Name: "pcc"),  // small capacitive shunt = ground path
        };
        var branches = new List<Branch> { new(1, 2, r, x, B: 0.0, Name: "thevenin") };
        var gens = new List<Generator> { new(Bus: 2, Pg: pMw, Vg: 1.0, MBase: pMw, Name: "inverter", Type: "gfl") };
        return new GridModel
        {
            BaseMva = baseMva,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            FrequencyHz = f0Hz,
            Name = $"canonical-scr{scr:F2}",
        };
    }
}
