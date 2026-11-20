using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt;

/// <summary>
/// RC3 P8 (EMT): the constant-parameter data of a distributed lossy transmission line, per metre, in SI units
/// (ohm, henry, farad, siemens). The lossless surge impedance <c>Zc = sqrt(L'/C')</c>, the propagation velocity
/// <c>v = 1/sqrt(L'C')</c> and the travel time <c>tau = length/v</c> are the Bergeron-model quantities; the series
/// resistance R' and shunt conductance G' are the losses.
/// </summary>
/// <param name="LengthM">Line length (m), positive.</param>
/// <param name="ROhmPerM">Series resistance R' (ohm/m), zero or positive.</param>
/// <param name="LHenryPerM">Series inductance L' (H/m), positive.</param>
/// <param name="CFaradPerM">Shunt capacitance C' (F/m), positive.</param>
/// <param name="GSiemensPerM">Shunt conductance G' (S/m), zero or positive.</param>
public sealed record LossyLineParameters(double LengthM, double ROhmPerM, double LHenryPerM, double CFaradPerM, double GSiemensPerM = 0.0)
{
    /// <summary>Lossless surge impedance <c>Zc = sqrt(L'/C')</c> (ohm).</summary>
    public double SurgeImpedanceOhm => Math.Sqrt(LHenryPerM / CFaradPerM);

    /// <summary>Propagation velocity <c>v = 1/sqrt(L'C')</c> (m/s).</summary>
    public double VelocityMPerS => 1.0 / Math.Sqrt(LHenryPerM * CFaradPerM);

    /// <summary>Travel time <c>tau = length * sqrt(L'C')</c> (s).</summary>
    public double TravelTimeSeconds => LengthM * Math.Sqrt(LHenryPerM * CFaradPerM);

    /// <summary>Total series resistance <c>R'*length</c> (ohm).</summary>
    public double TotalResistanceOhm => ROhmPerM * LengthM;

    /// <summary>Total shunt conductance <c>G'*length</c> (S).</summary>
    public double TotalConductanceS => GSiemensPerM * LengthM;

    /// <summary>
    /// The exponent of the attenuation of a wavefront over the whole line, <c>(R'/(2 Zc) + G' Zc / 2) * length</c>
    /// (nepers). For any constant-parameter RLCG line a discontinuity travels with amplitude
    /// <c>exp(-(R'/(2L') + G'/(2C')) t)</c>, which at <c>t = tau</c> is this exponent. For a distortionless line
    /// (<c>R'/L' = G'/C'</c>) it is <c>R'*length/Zc</c> and the whole waveform, not only its front, is attenuated by it.
    /// </summary>
    public double FrontAttenuationNepers => (ROhmPerM / (2.0 * SurgeImpedanceOhm) + GSiemensPerM * SurgeImpedanceOhm / 2.0) * LengthM;

    /// <summary>The front transmission factor <c>exp(-FrontAttenuationNepers)</c>.</summary>
    public double FrontAttenuation => DetMath.Exp(-FrontAttenuationNepers);

    /// <summary>True when the line is lossless (R' = G' = 0).</summary>
    public bool IsLossless => ROhmPerM == 0.0 && GSiemensPerM == 0.0;

    /// <summary>True when <c>R'C' = G'L'</c> to within <paramref name="relativeTolerance"/> (the Heaviside condition),
    /// including the lossless line.</summary>
    public bool IsDistortionless(double relativeTolerance = 1e-12)
    {
        double a = ROhmPerM * CFaradPerM, b = GSiemensPerM * LHenryPerM;
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale == 0.0 || Math.Abs(a - b) <= relativeTolerance * scale;
    }

    /// <summary>Every reason these parameters cannot be modelled (empty when they can).</summary>
    public IReadOnlyList<string> Problems()
    {
        var bad = new List<string>();
        void Positive(string name, double v) { if (!(v > 0) || !double.IsFinite(v)) bad.Add($"{name} must be a positive finite number; got {v:R}"); }
        void NonNegative(string name, double v) { if (!(v >= 0) || !double.IsFinite(v)) bad.Add($"{name} must be zero or a positive finite number; got {v:R}"); }
        Positive("lengthM", LengthM);
        NonNegative("rOhmPerM", ROhmPerM);
        Positive("lHenryPerM", LHenryPerM);
        Positive("cFaradPerM", CFaradPerM);
        NonNegative("gSiemensPerM", GSiemensPerM);
        return bad;
    }
}

/// <summary>
/// RC3 P8 (EMT): a constant-parameter lossy distributed line between two nodes, modelled as a cascade of
/// <paramref name="Segments"/> Bergeron sections with lumped-resistance split (the EMTP constant-parameter model).
/// <para>Each section of travel time <c>tau/n</c>, resistance <c>R/n</c> and conductance <c>G/n</c> is two lossless
/// half-sections of travel time <c>tau/(2n)</c> with the losses lumped at the quarter points:
/// <c>R/4n</c> in series at each end and <c>R/2n</c> in the middle, and <c>G/4n</c> to ground at each of the four
/// half-section terminals. Eliminating the middle node gives Dommel's two-port with <c>Z = Zc + R/4</c> and the
/// <c>(1 +- h)/2</c> history mixing, <c>h = (Zc - R/4)/(Zc + R/4)</c>; here the middle node is kept, so the line is
/// built from the incumbent lossless <see cref="BergeronLine"/>, <see cref="SeriesBranch"/> (pure resistance) and
/// <see cref="ShuntElement"/> (conductance) elements and the engine itself is unchanged.</para>
/// <para>This is deliberately NOT an <see cref="EmtElement"/>: <see cref="EmtEngine"/> does not know it, and it reaches
/// the engine only through <see cref="LossyLineExpansion.Expand"/>, so the lossless path and its fixtures are
/// untouched. Frequency-dependent (J. Marti / universal line) models are not implemented.</para>
/// </summary>
/// <param name="Id">The line's own id (reported in the layout; not an engine element id).</param>
/// <param name="From">Sending node.</param>
/// <param name="To">Receiving node.</param>
/// <param name="Line">Per-metre parameters.</param>
/// <param name="Segments">Number of Bergeron sections (1 to 4096).</param>
public sealed record LossyBergeronLine(int Id, int From, int To, LossyLineParameters Line, int Segments = 1);

/// <summary>Where an expanded lossy line sits in the engine network.</summary>
/// <param name="LineId">The <see cref="LossyBergeronLine.Id"/>.</param>
/// <param name="Segments">Number of sections.</param>
/// <param name="HalfSectionTravelSeconds">Travel time of each lossless half-section, <c>tau/(2n)</c>.</param>
/// <param name="HalfSectionSteps">The same in timesteps (an exact whole number, checked).</param>
/// <param name="InternalNodes">Internal node ids added for the line, in the order they were created (DP-27). Section by section
/// from the sending end: each section first takes its end node (the shared junction with the next section; the last section
/// ends on the line's own To terminal and takes none), then its inner nodes (with R > 0: the node after the sending-end R/4n
/// arm, the Bergeron junction, the node after the R/2n arm and the node before the receiving-end R/4n arm; with R = 0 only the
/// Bergeron junction). So the list is in spatial order from the sending end only for a single section; with more than one
/// section each junction precedes the inner nodes of the section it ends. Nothing reads positions in this list today; use
/// <paramref name="ElementIds"/> for spatial order.</param>
/// <param name="ElementIds">Engine element ids added for the line, in order from the sending end.</param>
/// <param name="SendingEndElementId">The first series element of the line (the sending-end R/4n arm, or the first
/// half-section when R = 0): its branch current is the current entering the line's series path, positive into the
/// line. With R = 0 and G &gt; 0 the G/4n shunt at the sending terminal is outside it.</param>
public sealed record LossyLineLayout(int LineId, int Segments, double HalfSectionTravelSeconds, int HalfSectionSteps,
    IReadOnlyList<int> InternalNodes, IReadOnlyList<int> ElementIds, int SendingEndElementId);

/// <summary>An engine network with its lossy lines expanded, and where each line went.</summary>
public sealed record LossyLineNetwork(EmtNetwork Network, IReadOnlyList<LossyLineLayout> Lines);

/// <summary>
/// RC3 P8 (EMT): expands <see cref="LossyBergeronLine"/>s into incumbent engine elements (see that type). The
/// incumbent <see cref="EmtEngine"/> rounds a line's travel time to a whole number of steps; this expansion refuses a
/// half-section travel time that is not a whole number of steps (to a relative 1e-6), so the delay the engine
/// realises is the declared one, not a silently rounded one.
/// </summary>
public static class LossyLineExpansion
{
    /// <summary>Largest accepted section count.</summary>
    public const int MaxSegments = 4096;

    /// <summary>Relative tolerance on "a whole number of steps".</summary>
    public const double StepQuantisationTolerance = 1e-6;

    /// <summary>
    /// Build the engine network: the given <paramref name="elements"/> on nodes <c>0..nodeCount-1</c>, plus each lossy
    /// line expanded with new internal nodes (numbered from <paramref name="nodeCount"/>) and new element ids (numbered
    /// above the largest existing id). Throws <see cref="ArgumentException"/> listing every problem.
    /// </summary>
    public static LossyLineNetwork Expand(int nodeCount, IReadOnlyList<EmtElement> elements, IReadOnlyList<LossyBergeronLine> lines, double dtSeconds)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(lines);
        if (nodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(nodeCount), "The network needs at least one node.");
        if (!(dtSeconds > 0) || !double.IsFinite(dtSeconds)) throw new ArgumentOutOfRangeException(nameof(dtSeconds), "The timestep must be positive and finite.");

        var bad = new List<string>();
        foreach (LossyBergeronLine ln in lines)
        {
            string label = $"lossy line {ln.Id} ({ln.From}->{ln.To})";
            foreach (string p in ln.Line.Problems()) bad.Add(label + ": " + p);
            if (ln.Segments < 1 || ln.Segments > MaxSegments) bad.Add($"{label}: segments must be 1 to {MaxSegments}; got {ln.Segments}");
            if (ln.From < 0 || ln.From >= nodeCount || ln.To < 0 || ln.To >= nodeCount || ln.From == ln.To)
                bad.Add($"{label}: both ends must be distinct nodes 0..{nodeCount - 1} (ground is not a line end)");
            if (bad.Count == 0)
            {
                double steps = ln.Line.TravelTimeSeconds / (2.0 * ln.Segments) / dtSeconds;
                double whole = Math.Round(steps);
                if (whole < 1 || Math.Abs(steps - whole) > StepQuantisationTolerance * Math.Max(1.0, whole))
                    bad.Add($"{label}: the half-section travel time tau/(2n) = {ln.Line.TravelTimeSeconds / (2.0 * ln.Segments):R} s is " +
                            $"{steps:R} timesteps of {dtSeconds:R} s; it must be a whole number of at least 1 (the engine would round it)");
            }
        }
        var ids = new HashSet<int>(lines.Select(l => l.Id));
        if (ids.Count != lines.Count) bad.Add("lossy line ids must be unique");
        if (bad.Count > 0)
            throw new ArgumentException("Lossy line expansion refused: " + string.Join("; ", bad), nameof(lines));

        var all = new List<EmtElement>(elements);
        int nextId = elements.Count == 0 ? 0 : elements.Max(e => e.Id) + 1;
        int nextNode = nodeCount;
        var layouts = new List<LossyLineLayout>();

        foreach (LossyBergeronLine ln in lines)
        {
            LossyLineParameters p = ln.Line;
            int n = ln.Segments;
            double zc = p.SurgeImpedanceOhm;
            double tauHalf = p.TravelTimeSeconds / (2.0 * n);
            int halfSteps = (int)Math.Round(tauHalf / dtSeconds);
            double rs = p.TotalResistanceOhm / n, gs = p.TotalConductanceS / n;
            var internalNodes = new List<int>();
            var elementIds = new List<int>();
            int firstElement = -1;

            int NewNode() { int k = nextNode++; internalNodes.Add(k); return k; }
            int Add(Func<int, EmtElement> make)
            {
                int id = nextId++;
                all.Add(make(id));
                elementIds.Add(id);
                if (firstElement < 0 && all[^1] is SeriesBranch or BergeronLine) firstElement = id;
                return id;
            }

            int a = ln.From;
            for (int s = 0; s < n; s++)
            {
                int b = s == n - 1 ? ln.To : NewNode();
                int start = a, end = b;
                if (rs > 0)
                {
                    int pNode = NewNode();
                    int aa = a;
                    Add(id => new SeriesBranch(id, aa, pNode, rs / 4.0, 0.0));
                    start = pNode;
                }
                if (gs > 0) { int at = start; Add(id => new ShuntElement(id, at, G: gs / 4.0)); }
                int q = NewNode();
                { int from = start; Add(id => new BergeronLine(id, from, q, zc, tauHalf)); }
                int r = q;
                if (rs > 0)
                {
                    r = NewNode();
                    int qq = q, rr = r;
                    Add(id => new SeriesBranch(id, qq, rr, rs / 2.0, 0.0));
                }
                if (gs > 0)
                {
                    if (r == q) { Add(id => new ShuntElement(id, q, G: gs / 2.0)); }
                    else
                    {
                        int qq = q, rr = r;
                        Add(id => new ShuntElement(id, qq, G: gs / 4.0));
                        Add(id => new ShuntElement(id, rr, G: gs / 4.0));
                    }
                }
                int sNode = rs > 0 ? NewNode() : end;
                { int from = r, to = sNode; Add(id => new BergeronLine(id, from, to, zc, tauHalf)); }
                if (gs > 0) { int at = sNode; Add(id => new ShuntElement(id, at, G: gs / 4.0)); }
                if (rs > 0) { int from = sNode, to = end; Add(id => new SeriesBranch(id, from, to, rs / 4.0, 0.0)); }
                a = b;
            }
            layouts.Add(new LossyLineLayout(ln.Id, n, tauHalf, halfSteps, internalNodes, elementIds, firstElement));
        }

        return new LossyLineNetwork(new EmtNetwork(nextNode, all), layouts);
    }
}
