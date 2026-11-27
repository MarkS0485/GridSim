using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.Partition;

/// <summary>
/// One single-conductor lossless (or distortionless) Bergeron line, built from two <see cref="BergeronEnd"/> elements that
/// couple ONLY through history: the current into each end is <c>i_e(t) = v_e(t) / Zc - h w_m(t - tau)</c>, where
/// <c>w_m(u) = v_m(u) / Zc + i_m(u)</c> is the wave the other end launched at <c>u</c> and <c>h</c> the attenuation
/// (<c>h = exp(-R l / Zc)</c> is exact for a distortionless line). The travel time must be at least one step, so every history
/// value a step reads was written by an EARLIER step: this is what lets two ends sit in different partitions and be solved in
/// parallel (the contract of <see cref="PartitionedEngine"/>).
/// <para>A travel time that is not a whole number of steps is read with linear interpolation between the two nearest stored
/// steps (exact when it is a multiple of the step). The two ends may be added to the same network (the monolithic form: the
/// incumbent solver then runs the line unchanged) or to two networks (the partitioned form): the arithmetic is the same
/// code either way.</para>
/// </summary>
public sealed class NodalBergeronLine
{
    internal NodalBergeronLine(string name, double zc, double tau, double attenuation)
    {
        Name = name; SurgeImpedanceOhm = zc; TravelSeconds = tau; Attenuation = attenuation;
    }

    /// <summary>The line name; the ends are <c>name.a</c> and <c>name.b</c>.</summary>
    public string Name { get; }
    /// <summary>Surge impedance, ohms.</summary>
    public double SurgeImpedanceOhm { get; }
    /// <summary>Travel time, seconds.</summary>
    public double TravelSeconds { get; }
    /// <summary>Attenuation of the wave over one travel, in (0, 1].</summary>
    public double Attenuation { get; }
    /// <summary>End A.</summary>
    public BergeronEnd A { get; internal set; } = null!;
    /// <summary>End B.</summary>
    public BergeronEnd B { get; internal set; } = null!;
    /// <summary>The partition of end A (set by <see cref="PartitionedModel"/>; empty for a line made directly).</summary>
    public string PartitionA { get; internal set; } = "";
    /// <summary>The partition of end B.</summary>
    public string PartitionB { get; internal set; } = "";

    /// <summary>Create a line with its two ends and add them to the given networks (the same network twice is the monolithic form).
    /// <paramref name="negA"/> and <paramref name="negB"/> are the return nodes (ground for a single conductor with earth return).</summary>
    public static NodalBergeronLine Add(NodalNetwork netA, int posA, int negA, NodalNetwork netB, int posB, int negB, string name,
        double surgeImpedanceOhm, double travelSeconds, double attenuation = 1.0)
    {
        ArgumentNullException.ThrowIfNull(netA);
        ArgumentNullException.ThrowIfNull(netB);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A line needs a name.", nameof(name));
        if (!(surgeImpedanceOhm > 0) || !double.IsFinite(surgeImpedanceOhm)) throw new ArgumentException($"{name}: the surge impedance must be finite and positive.");
        if (!(travelSeconds > 0) || !double.IsFinite(travelSeconds)) throw new ArgumentException($"{name}: the travel time must be finite and positive.");
        if (!(attenuation > 0 && attenuation <= 1.0)) throw new ArgumentException($"{name}: the attenuation must be in (0, 1].");
        if (posA == negA || posB == negB) throw new ArgumentException($"{name}: an end has both terminals on the same node.");
        var line = new NodalBergeronLine(name, surgeImpedanceOhm, travelSeconds, attenuation);
        line.A = new BergeronEnd(name + ".a", line, posA, negA);
        line.B = new BergeronEnd(name + ".b", line, posB, negB);
        line.A.Peer = line.B; line.B.Peer = line.A;
        netA.Add(line.A);
        netB.Add(line.B);
        return line;
    }
}

/// <summary>One end of a <see cref="NodalBergeronLine"/>: a conductance <c>1/Zc</c> between its terminals plus a history current
/// source taken from the wave the other end launched one travel time ago. Positive current is INTO the line.</summary>
public sealed class BergeronEnd : NodalElement
{
    private readonly NodalBergeronLine _line;
    private readonly int _pos, _neg;
    private double[] _wave = [];         // wave launched by THIS end, ring indexed by step number
    private int _ringLen;
    private int _qFloor;
    private double _qFrac;
    private long _n;                     // accepted steps
    private double _hist, _i, _gm, _w0, _i0;

    internal BergeronEnd(string name, NodalBergeronLine line, int pos, int neg) : base(name)
    {
        _line = line; _pos = pos; _neg = neg;
    }

    internal BergeronEnd Peer { get; set; } = null!;

    /// <summary>The line this end belongs to.</summary>
    public NodalBergeronLine Line => _line;

    /// <summary>Start the line in a steady state instead of at rest: this end's terminal voltage <paramref name="volts"/> and current into the
    /// line <paramref name="ampsIntoLine"/> before t = 0 (for a steady DC state the far end has the same voltage and the opposite current when
    /// the attenuation is 1). The wave history before t = 0 is that of the steady state.</summary>
    public void SetInitialState(double volts, double ampsIntoLine)
    {
        _i0 = ampsIntoLine; _w0 = volts / _line.SurgeImpedanceOhm + ampsIntoLine;
        if (_wave.Length > 0) Array.Fill(_wave, _w0);
    }

    /// <summary>Whole steps in the travel time (at the step of the last run), at least 1.</summary>
    public int TravelSteps => _qFloor;

    internal override void Validate(double dt, int nodeCount)
    {
        if (_line.TravelSeconds < dt * (1.0 - 1e-12))
            throw new ArgumentException(FormattableString.Invariant($"travel time {_line.TravelSeconds:R} s is shorter than the step {dt:R} s; a Bergeron line decouples only when the step does not exceed its travel time"));
        double q = _line.TravelSeconds / dt;
        double rq = Math.Round(q);
        if (Math.Abs(q - rq) < 1e-9) q = rq;
        _qFloor = (int)Math.Floor(q);
        _qFrac = q - _qFloor;
        // The writer (this end, step s) and the reader (the peer, steps s .. s + window - 1 reading back at least qFloor) must never
        // alias in the ring, whatever the window of a partitioned run: 2 qFloor + 4 slots.
        _ringLen = 2 * _qFloor + 4;
        _wave = new double[_ringLen];
        Array.Fill(_wave, _w0);
    }

    internal override void Configure(double dt, IntegrationRule rule) => _gm = 1.0 / _line.SurgeImpedanceOhm;

    internal override void Reset()
    {
        if (_wave.Length > 0) Array.Fill(_wave, _w0);
        _n = 0; _hist = 0.0; _i = _i0;
    }

    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_pos, _neg, _gm);

    /// <summary>The peer's launched wave at step index <paramref name="idx"/> (the initial-state wave at and before the start: zero when the line starts at rest).</summary>
    private double PeerWave(long idx) => idx <= 0 ? Peer._w0 : Peer._wave[(int)(idx % Peer._ringLen)];

    internal override void StampSources(double t, NodalStamper s)
    {
        long step = _n + 1;
        double w = PeerWave(step - _qFloor);
        if (_qFrac != 0.0) w = (1.0 - _qFrac) * w + _qFrac * PeerWave(step - _qFloor - 1);
        _hist = _line.Attenuation * w;
        // i = G v - hist leaves the positive node into the line, so the history source INJECTS hist at the positive node.
        s.AddCurrentInto(_pos, _hist);
        s.AddCurrentInto(_neg, -_hist);
    }

    internal override void Accept(double t, double[] v)
    {
        double vb = V(v, _pos) - V(v, _neg);
        _i = _gm * vb - _hist;
        long step = _n + 1;
        _wave[(int)(step % _ringLen)] = 2.0 * _gm * vb - _hist;     // v/Zc + i
        _n = step;
    }

    /// <summary>Channel 0: the current into the line at the last accepted step, amperes.</summary>
    internal override double ProbeCurrent(int channel) => _i;
}
