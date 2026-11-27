namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WSIEG1, the WECC modified IEEE type 1 steam governor (PSS/E WSIEG1; PSS/E 33 model library data sheet 7.35, public copy at
/// pdfcoffee.com/models-pdf-free.html; PowerWorld "Governor Model: WSIEG1"). It is <see cref="Ieeeg1"/>'s structure with the
/// valve's gate position GV mapped through a nonlinear gain table to the steam flow PGV before the four turbine stages:
/// <code>
///   CV   = Pref - K leadlag_{T2/T1}(dw)                        (K = 1/R)
///   GV   = lag_{T3}(CV), rate in [Uc, Uo], position in [PMIN, PMAX] (non-windup)
///   PGV  = NGV(GV)                                             (piecewise linear through (0, 0), (GV1, PGV1) .. (GV5, PGV5))
///   y4..y7 = lags T4, T5, T6, T7 in cascade from PGV
///   PmechHP = K1 y4 + K3 y5 + K5 y6 + K7 y7;  PmechLP = K2 y4 + K4 y5 + K6 y6 + K8 y7
/// </code>
/// A table whose points are all 0 is the identity (PGV = GV). The points must rise in GV and must not fall in PGV, so the
/// table can be inverted at the start: GV0 = NGV^-1(Pmech0) and Pref = GV0. Beyond GV5 the last segment is extended.
/// IBLOCK 1 to 3 set PMIN, PMAX or both to the initial gate (PSS/E's rule, applied when that limit is 0). The RMS study
/// takes the unit as tandem-compound (one shaft, the eight fractions summing to 1), as for IEEEG1.
/// <para>Not modelled, refused by name: the intentional deadband db1 with its hysteresis err, and the unintentional
/// deadband (backlash) db2, each other than 0.</para>
/// </summary>
public sealed class Wsieg1 : IRmsGovernor
{
    private readonly double _k, _t3, _uo, _uc;
    private readonly LeadLag? _governor;
    private readonly FirstOrderLag _chest, _reheat, _crossover, _lp;
    private readonly double _k1, _k2, _k3, _k4, _k5, _k6, _k7, _k8;
    private readonly double[] _gv, _pgv;
    private readonly int _iblock;
    private readonly double _pmaxData, _pminData;
    private double _pmax, _pmin, _gate, _pref;

    /// <summary>WSIEG1 in its PSS/E CON order after JBUS and M (K T1 T2 T3 Uo Uc PMAX PMIN T4 K1 K2 T5 K3 K4 T6 K5 K6 T7 K7 K8
    /// db1 err db2 GV1 PGV1 .. GV5 PGV5 IBLOCK).</summary>
    public Wsieg1(double k, double t1, double t2, double t3, double uo, double uc, double pmax, double pmin, double t4,
        double k1, double k2, double t5, double k3, double k4, double t6, double k5, double k6, double t7, double k7, double k8,
        double db1, double err, double db2, double[] gv, double[] pgv, int iblock)
    {
        const string who = "WSIEG1";
        LibraryChecks.Positive(who, "K", k);
        LibraryChecks.NonNegative(who, "T3", t3);
        if (!(uo > 0.0)) throw new ArgumentOutOfRangeException(nameof(uo), uo, "WSIEG1: the valve opening rate Uo must be above 0 pu/s.");
        if (!(uc < 0.0)) throw new ArgumentOutOfRangeException(nameof(uc), uc, "WSIEG1: the valve closing rate Uc must be below 0 pu/s.");
        if (db1 != 0.0 || err != 0.0)
            throw new ArgumentOutOfRangeException(nameof(db1), db1, "WSIEG1: the intentional deadband db1 (with its hysteresis err) is not modelled; GridSim's WSIEG1 needs db1 = err = 0.");
        if (db2 != 0.0)
            throw new ArgumentOutOfRangeException(nameof(db2), db2, "WSIEG1: the unintentional deadband db2 is not modelled; GridSim's WSIEG1 needs db2 = 0.");
        if (iblock is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(iblock), iblock, "WSIEG1: IBLOCK must be 0, 1, 2 or 3.");
        if (iblock == 0) LibraryChecks.Limits(who, "PMIN", pmin, "PMAX", pmax);
        _governor = LibraryChecks.Stage(who, t2, "T2", t1, "T1");
        _chest = new FirstOrderLag(1.0, t4);
        _reheat = new FirstOrderLag(1.0, t5);
        _crossover = new FirstOrderLag(1.0, t6);
        _lp = new FirstOrderLag(1.0, t7);
        double[] ks = { k1, k2, k3, k4, k5, k6, k7, k8 };
        double sum = 0.0;
        for (int i = 0; i < ks.Length; i++)
        {
            if (!(ks[i] >= 0.0 && ks[i] <= 1.0))
                throw new ArgumentOutOfRangeException($"k{i + 1}", ks[i], $"WSIEG1: the stage fraction K{i + 1} must lie in [0, 1].");
            sum += ks[i];
        }
        if (!(Math.Abs(sum - 1.0) <= Ieeeg1.FractionSumTolerance))
            throw new ArgumentException($"WSIEG1: the stage fractions K1..K8 are fractions of the total turbine power and must sum to 1 (got {ControlBlocks.Num(sum)}).", nameof(k1));
        ArgumentNullException.ThrowIfNull(gv);
        ArgumentNullException.ThrowIfNull(pgv);
        if (gv.Length != 5 || pgv.Length != 5) throw new ArgumentException("WSIEG1: the gain table has five points (GV1..GV5, PGV1..PGV5).", nameof(gv));
        bool identity = gv.All(x => x == 0.0) && pgv.All(x => x == 0.0);
        if (identity) { _gv = Array.Empty<double>(); _pgv = Array.Empty<double>(); }
        else
        {
            // Points with GV = 0 after the first non-zero one end the table (PSS/E data leaves the unused points at 0).
            var g = new List<double> { 0.0 };
            var p = new List<double> { 0.0 };
            for (int i = 0; i < 5; i++)
            {
                if (gv[i] == 0.0 && pgv[i] == 0.0) break;
                if (!(gv[i] > g[^1]) || !double.IsFinite(gv[i]))
                    throw new ArgumentException($"WSIEG1: the gain table's GV{i + 1} = {ControlBlocks.Num(gv[i])} must be above the point before it (the table must rise in GV).", nameof(gv));
                if (!(pgv[i] >= p[^1]) || !double.IsFinite(pgv[i]))
                    throw new ArgumentException($"WSIEG1: the gain table's PGV{i + 1} = {ControlBlocks.Num(pgv[i])} must not fall below the point before it (the table must be invertible).", nameof(pgv));
                g.Add(gv[i]); p.Add(pgv[i]);
            }
            if (g.Count < 2) throw new ArgumentException("WSIEG1: the gain table has no point after (0, 0).", nameof(gv));
            if (!(p[^1] > p[^2])) throw new ArgumentException("WSIEG1: the gain table's last segment is flat, so it cannot be extended.", nameof(pgv));
            _gv = g.ToArray(); _pgv = p.ToArray();
        }
        _k = k; _t3 = t3; _uo = uo; _uc = uc; _iblock = iblock; _pmaxData = pmax; _pminData = pmin;
        _k1 = k1; _k2 = k2; _k3 = k3; _k4 = k4; _k5 = k5; _k6 = k6; _k7 = k7; _k8 = k8;
    }

    /// <summary>The low-pressure shaft output of the last step (pu, machine base).</summary>
    public double LowPressureShaftPowerPu { get; private set; }

    /// <summary>The gate position GV after the last step.</summary>
    public double Gate => _gate;

    /// <summary>The steam flow PGV = NGV(GV) of the gain table (pu).</summary>
    public double Flow(double gate)
    {
        if (_gv.Length == 0) return gate;
        int i = 1;
        while (i < _gv.Length - 1 && gate > _gv[i]) i++;
        if (gate <= 0.0) i = 1;
        return _pgv[i - 1] + (_pgv[i] - _pgv[i - 1]) * (gate - _gv[i - 1]) / (_gv[i] - _gv[i - 1]);
    }

    /// <summary>The gate GV that gives steam flow <paramref name="flow"/>: the inverse of <see cref="Flow"/> (on a flat
    /// segment, its lower end).</summary>
    public double GateFor(double flow)
    {
        if (_gv.Length == 0) return flow;
        int i = 1;
        while (i < _pgv.Length - 1 && flow > _pgv[i]) i++;
        if (flow <= 0.0) i = 1;
        if (_pgv[i] == _pgv[i - 1]) return _gv[i - 1];
        return _gv[i - 1] + (_gv[i] - _gv[i - 1]) * (flow - _pgv[i - 1]) / (_pgv[i] - _pgv[i - 1]);
    }

    /// <inheritdoc/>
    public void Initialize(double mechanicalPowerPu)
    {
        if (!double.IsFinite(mechanicalPowerPu)) throw new ArgumentOutOfRangeException(nameof(mechanicalPowerPu), mechanicalPowerPu, "WSIEG1: the initial power is not finite.");
        double gate = GateFor(mechanicalPowerPu);
        _pmax = (_iblock is 2 or 3) && _pmaxData == 0.0 ? gate : _pmaxData;
        _pmin = (_iblock is 1 or 3) && _pminData == 0.0 ? gate : _pminData;
        LibraryChecks.Limits("WSIEG1", "PMIN", _pmin, "PMAX", _pmax);
        ControlBlocks.RequireEquilibriumWithinLimits("WSIEG1", "gate position GV", gate, _pmin, _pmax, nameof(mechanicalPowerPu));
        double flow = Flow(gate);
        _pref = gate;
        _gate = gate;
        _governor?.Initialize(0.0);
        _chest.Initialize(flow);
        _reheat.Initialize(flow);
        _crossover.Initialize(flow);
        _lp.Initialize(flow);
        LowPressureShaftPowerPu = _k2 * flow + _k4 * flow + _k6 * flow + _k8 * flow;
    }

    /// <inheritdoc/>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double e = -_k * speedDeviationPu;
        double s1 = _governor is null ? e : _governor.Step(e, dtSeconds);
        _gate = RateLimitedLag.Step(_gate, _pref + s1, _t3, _uo, _uc, _pmin, _pmax, dtSeconds);
        double pgv = Flow(_gate);
        double y4 = _chest.Step(pgv, dtSeconds);
        double y5 = _reheat.Step(y4, dtSeconds);
        double y6 = _crossover.Step(y5, dtSeconds);
        double y7 = _lp.Step(y6, dtSeconds);
        LowPressureShaftPowerPu = _k2 * y4 + _k4 * y5 + _k6 * y6 + _k8 * y7;
        return _k1 * y4 + _k3 * y5 + _k5 * y6 + _k7 * y7;
    }
}

/// <summary>WP3b (RC5): WSIEG1 parameters (see <see cref="Wsieg1"/>'s constructor; per unit on the machine base). The RMS
/// study takes the unit as tandem-compound: the shaft carries the HP and the LP output.</summary>
public sealed record Wsieg1Data(
    double K, double T1, double T2, double T3, double Uo, double Uc, double Pmax, double Pmin, double T4, double K1, double K2,
    double T5, double K3, double K4, double T6, double K5, double K6, double T7, double K7, double K8, double Db1, double Err,
    double Db2, IReadOnlyList<double> Gv, IReadOnlyList<double> Pgv, int Iblock) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "WSIEG1";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Tandem(new Wsieg1(K, T1, T2, T3, Uo, Uc, Pmax, Pmin, T4, K1, K2, T5, K3, K4,
        T6, K5, K6, T7, K7, K8, Db1, Err, Db2, Gv.ToArray(), Pgv.ToArray(), Iblock));

    private sealed class Tandem(Wsieg1 g) : IRmsGovernor
    {
        public void Initialize(double mechanicalPowerPu) => g.Initialize(mechanicalPowerPu);

        public double Step(double speedDeviationPu, double dtSeconds)
        {
            double hp = g.Step(speedDeviationPu, dtSeconds);
            return hp + g.LowPressureShaftPowerPu;
        }
    }
}
