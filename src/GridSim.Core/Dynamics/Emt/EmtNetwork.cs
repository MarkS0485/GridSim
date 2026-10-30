using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Emt;

/// <summary>How to energise a bus in an EMT build: a source (Thévenin if <paramref name="Rs"/>&gt;0, else ideal)
/// with EMF <c>Amplitude·cos(2π·FrequencyHz·t + phase)</c>, or a DC level when <paramref name="FrequencyHz"/>
/// is 0. A build needs at least one source to provide the ground reference.</summary>
public sealed record EmtSourceSpec(int BusId, double Rs = 0.0, double AmplitudePu = 1.0, double FrequencyHz = 0.0, double PhaseDeg = 0.0);

/// <summary>Options for building an EMT network from a <see cref="GridModel"/>.</summary>
/// <param name="Sources">Sources to energise the network (at least one, for the ground reference).</param>
/// <param name="IncludeLoads">Model each bus's <c>Pd/Qd</c> demand as a constant-impedance shunt (a
/// conductance plus an inductor/capacitor). On by default - it damps the otherwise near-lossless network.</param>
/// <param name="UseTravellingWaveLines">Model charged, non-transformer branches as lossless Bergeron
/// travelling-wave lines (surge impedance + travel delay) instead of lumped pi sections. Off by default;
/// series losses are dropped in this mode, so it is best for wave-propagation studies.</param>
/// <param name="NegativeDemand">N029: what <c>IncludeLoads</c> does with a bus whose real demand is negative
/// (<c>Pd &lt; 0</c>, embedded generation netted into the load, common in bundled cases). See
/// <see cref="EmtNegativeDemand"/>. Has no effect when <c>IncludeLoads</c> is off.</param>
public sealed record EmtBuildOptions(
    IReadOnlyList<EmtSourceSpec> Sources, bool IncludeLoads = true, bool UseTravellingWaveLines = false,
    EmtNegativeDemand NegativeDemand = EmtNegativeDemand.ConstantCurrent);

/// <summary>
/// N029: how an EMT build with loads on treats a bus with negative real demand (<c>Pd &lt; 0</c>).
/// <para>Why it matters: a load is modelled as a constant impedance, <c>G = Pd/BaseMva</c>. For <c>Pd &lt; 0</c> that
/// is a NEGATIVE conductance, an element that generates energy in proportion to the voltage across it. It is not
/// passive, so it feeds the network's natural modes and the time-domain run grows without bound (IEEE 145,
/// IEEE 300, several pglib and PEGASE cases; case533mt_hi reached 1.16e75). A negative-impedance load is therefore
/// never built, in either mode.</para>
/// </summary>
public enum EmtNegativeDemand
{
    /// <summary>The default. The negative-demand bus's real power is modelled as an independent current source
    /// injecting <c>|Pd|/BaseMva</c> per unit (the power at 1 pu voltage), shaped like the FIRST source's EMF
    /// normalised to unit amplitude: a constant for a DC source, <c>cos(2*pi*f*t + phase)</c> for an AC one. A
    /// current source is a bounded input, not an energy-feeding element, so the network stays passive and the run
    /// stays bounded. It is exact for a DC study; for an AC study the injected current is in phase with the
    /// reference source, not with the bus's own power-flow angle, so it approximates the generation. The bus's
    /// reactive demand is still a constant-impedance inductor or capacitor, as for any other load.
    /// <para>This is the default (rather than <see cref="Refuse"/>) only because an existing constitutional test
    /// (<c>Bundled_series_compensated_cases_build_their_capacitors</c>, which builds IEEE 145 / 300 and pglib 240 /
    /// 588, all with negative Pd, using default options) must keep building.</para></summary>
    ConstantCurrent = 0,

    /// <summary>Refuse the build with an <see cref="ArgumentException"/> listing every bus with <c>Pd &lt; 0</c>.</summary>
    Refuse = 1,
}

/// <summary>
/// A resolved EMT network: a node count and the passive elements addressed by node id (0..NodeCount-1, or
/// <see cref="Ground"/> for the reference). This first cut is single-phase (one node per bus); the node
/// indexing is deliberately kept indirect so a later three-phase build can widen each bus to a 3-node block
/// without changing the engine. Built directly for unit tests, or from a <see cref="Model.GridModel"/> via
/// the Slice-2 <c>FromGridModel</c> factory.
/// </summary>
public sealed class EmtNetwork
{
    /// <summary>The reference (ground) node id; its voltage is fixed at zero and it never enters the solve.</summary>
    public const int Ground = -1;

    /// <summary>Number of non-reference nodes (voltage unknowns before Dirichlet elimination).</summary>
    public int NodeCount { get; }

    /// <summary>The passive elements (series branches, shunts, sources).</summary>
    public IReadOnlyList<EmtElement> Elements { get; }

    /// <summary>Bus id -&gt; node index (populated when built from a <see cref="GridModel"/>; empty otherwise).</summary>
    public IReadOnlyDictionary<int, int> BusToNode { get; }

    /// <summary>(from-bus, to-bus, element id) for each series branch - lets a caller resolve a current probe.</summary>
    public IReadOnlyList<(int From, int To, int ElementId)> SeriesBranchIds { get; }

    public EmtNetwork(int nodeCount, IReadOnlyList<EmtElement> elements,
        IReadOnlyDictionary<int, int>? busToNode = null,
        IReadOnlyList<(int, int, int)>? seriesBranchIds = null)
    {
        if (nodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(nodeCount), "EMT network needs at least one node.");
        NodeCount = nodeCount;
        Elements = elements;
        BusToNode = busToNode ?? new Dictionary<int, int>();
        SeriesBranchIds = seriesBranchIds ?? Array.Empty<(int, int, int)>();
    }

    /// <summary>
    /// Build a single-phase EMT network from a phasor <see cref="GridModel"/>. Each in-service branch becomes a
    /// series R-L arm (<c>L = X/ω0</c>) plus a line-charging capacitor (<c>C = (B/2)/ω0</c>) at each end; each
    /// bus shunt becomes a conductance (<c>Gs/BaseMva</c>) and a capacitor/reactor (from <c>Bs/BaseMva</c>);
    /// sources are added per <paramref name="options"/>. All values per-unit; <c>w0 = 2*pi*FrequencyHz</c>.
    /// <para>R078: every in-service branch is modelled with the power flow's own branch model
    /// (<see cref="PowerFlow.AdmittanceStamp.Branch"/>), not dropped or simplified:</para>
    /// <list type="bullet">
    /// <item>An off-nominal tap <c>a</c> (a tap &lt;= 0 means 1, as in the power flow) becomes an ideal
    /// transformer <c>a : 1</c> on the From side of the series arm (<see cref="SeriesBranch.Tap"/>), and the From-end
    /// charging capacitance is divided by <c>a^2</c>, which reproduces <c>Yff = (ys + jb/2)/a^2</c>,
    /// <c>Yft = Ytf = -ys/a</c>, <c>Ytt = ys + jb/2</c> exactly at the system frequency.</item>
    /// <item>A negative series reactance <c>X &lt; 0</c> (series compensation, or a network-reduction equivalent)
    /// becomes a series R-C arm, <see cref="SeriesCapacitorBranch"/>, with <c>C = 1/(|X|*w0)</c>, whose reactance at
    /// the system frequency is exactly X. (It used to become a plain resistor with no warning.)</item>
    /// <item>X = 0 with R != 0 is a pure resistance arm, as before.</item>
    /// </list>
    /// <para>What cannot be modelled is refused with an <see cref="ArgumentException"/> naming the branch:
    /// a phase-shifting transformer (<c>PhaseShiftDeg != 0</c>: a phase shift multiplies the phasor by a complex
    /// number, which a real-valued single-phase time-domain network has no element for; it would need a
    /// three-phase build), and a zero-impedance branch (R = X = 0, an infinite conductance). A travelling-wave
    /// line is used only for an untapped, unshifted, non-transformer branch, so a tap is never silently lost to
    /// the Bergeron model either.</para>
    /// </summary>
    public static EmtNetwork FromGridModel(GridModel model, EmtBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Sources.Count == 0)
            throw new ArgumentException("An EMT build needs at least one source (for the ground reference).", nameof(options));

        double omega = EmtUnits.Omega(model);
        var busToNode = new Dictionary<int, int>();
        foreach (Bus b in model.Buses) busToNode[b.Id] = busToNode.Count;

        var elements = new List<EmtElement>();
        var seriesIds = new List<(int, int, int)>();
        int id = 0;

        for (int bi = 0; bi < model.Branches.Count; bi++)
        {
            Branch br = model.Branches[bi];
            if (!br.InService) continue;
            // N030: an in-service branch whose end bus is not in the model used to be skipped with a bare
            // `continue`, silently removing a live branch from the network. It is now refused by name. (A branch
            // that is out of service is still skipped above, as it should be.)
            if (!busToNode.TryGetValue(br.From, out int f) || !busToNode.TryGetValue(br.To, out int t))
                throw new ArgumentException(
                    $"EMT build: {BranchLabel(bi, br)} is in service but its " +
                    (busToNode.ContainsKey(br.From) ? $"To bus {br.To}" : $"From bus {br.From}") +
                    " is not in the model, so it would be silently dropped from the network. Fix the case data (add " +
                    "the bus, correct the branch's end, or take the branch out of service).", nameof(model));

            // R078: refuse, by name, what a real-valued single-phase EMT network cannot represent.
            if (br.PhaseShiftDeg != 0)
                throw new ArgumentException(
                    $"EMT build: {BranchLabel(bi, br)} is a phase-shifting transformer (shift {br.PhaseShiftDeg:R} deg). A phase " +
                    "shift multiplies the voltage phasor by a complex number, which a real-valued single-phase time-domain " +
                    "network cannot represent; it would be silently dropped. Remove the shift or use a phasor study.",
                    nameof(model));
            if (br.R == 0 && br.X == 0)
                throw new ArgumentException(
                    $"EMT build: {BranchLabel(bi, br)} has R = X = 0, a zero-impedance coupler (an infinite conductance). " +
                    "Merge the two buses, or give the branch an impedance.", nameof(model));

            // The tap exactly as the power flow reads it (AdmittanceStamp.FromAdmittance: <= 0 means a plain line).
            double a = br.TapRatio <= 0 ? 1.0 : br.TapRatio;

            if (options.UseTravellingWaveLines && !br.IsTransformer && br.B > 0 && br.X > 0 && a == 1.0)
            {
                (double zc, double tau) = EmtUnits.SurgeParams(br.X, br.B, omega);
                int lid = id++;
                elements.Add(new BergeronLine(lid, f, t, zc, tau)); // lossless: series R dropped in this mode
                seriesIds.Add((br.From, br.To, lid));
                continue;
            }

            int sid = id++;
            if (br.X < 0)
            {
                // R078: series compensation. A capacitor whose reactance at w0 is exactly X: C = 1/(|X|*w0).
                elements.Add(new SeriesCapacitorBranch(sid, f, t, br.R, EmtUnits.SeriesC(br.X, omega), a));
            }
            else
            {
                (double r, double l) = EmtUnits.SeriesRl(br, omega);
                // a == 1 keeps the original element exactly (Tap defaults to 1), so untapped runs are bit-identical.
                elements.Add(a == 1.0 ? new SeriesBranch(sid, f, t, r, l) : new SeriesBranch(sid, f, t, r, l, a));
            }
            seriesIds.Add((br.From, br.To, sid));
            if (br.B < 0)
            {
                // N029: negative line charging (a network-reduction equivalent; IEEE 300 has 6, pglib 162 has 5).
                // A capacitor C = (B/2)/w0 would be NEGATIVE: not passive, and the run grows without bound. A
                // negative susceptance is inductive, so each end is modelled the way this file already models a
                // negative bus shunt Bs: a shunt reactor whose susceptance at w0 is exactly the branch's -|B|/2 per
                // end, L = 1/((|B|/2)*w0). The From end sits behind the ideal transformer like the capacitor case
                // below, so it is seen as (b/2)/a^2, i.e. L_from = a^2 * L_end. Positive charging is untouched.
                double lEnd = 1.0 / ((-br.B / 2.0) * omega);
                double lFrom = a == 1.0 ? lEnd : lEnd * (a * a);
                elements.Add(new ShuntElement(id++, f, L: lFrom));
                elements.Add(new ShuntElement(id++, t, L: lEnd));
            }
            else if (br.B != 0)
            {
                double cEnd = EmtUnits.ShuntC(br.B / 2.0, omega);
                // R078: the From-end charging sits behind the ideal transformer, so it is seen as (b/2)/a^2 at the
                // From bus (Yff = (ys + jb/2)/a^2). For a == 1 the original value is used untouched.
                double cFrom = a == 1.0 ? cEnd : cEnd / (a * a);
                elements.Add(new ShuntElement(id++, f, C: cFrom));
                elements.Add(new ShuntElement(id++, t, C: cEnd));
            }
        }

        if (options.IncludeLoads)
        {
            // N029: a negative Pd would become a negative conductance (not passive; the run grows without bound).
            // In Refuse mode every such bus is listed in one error, before anything is built from the loads.
            if (options.NegativeDemand == EmtNegativeDemand.Refuse && model.BaseMva > 0)
            {
                var negative = new List<string>();
                foreach (Bus b in model.Buses)
                    if (b.Pd < 0) negative.Add($"bus {b.Id} (Pd = {b.Pd:R} MW)");
                if (negative.Count > 0)
                    throw new ArgumentException(
                        $"EMT build: {negative.Count} bus(es) carry negative real demand, which a constant-impedance load " +
                        "would turn into a negative conductance. A negative-impedance load is not passive: it feeds energy " +
                        "into the network's natural modes and the run grows without bound. Offending buses: " +
                        string.Join(", ", negative) + ". Use EmtNegativeDemand.ConstantCurrent to model the negative " +
                        "demand as a current source, model it as a generator, or turn loads off.", nameof(options));
            }

            // The reference waveform for ConstantCurrent: the first source's EMF shape at unit amplitude.
            EmtSourceSpec refSrc = options.Sources[0];
            double refPhase = refSrc.PhaseDeg * Math.PI / 180.0, refFreq = refSrc.FrequencyHz;

            foreach (Bus b in model.Buses)
            {
                if (model.BaseMva <= 0 || (b.Pd == 0 && b.Qd == 0)) continue;
                if (b.Pd < 0)
                {
                    // N029 ConstantCurrent: the real part becomes a bounded current injection |Pd|/BaseMva (the
                    // generation at 1 pu voltage) shaped like the reference source; the reactive part stays a
                    // passive constant-impedance L or C exactly as for any other load. This block runs only for
                    // Pd < 0, so every case without negative demand builds exactly the same elements as before.
                    int nodeNeg = busToNode[b.Id];
                    double iPu = -b.Pd / model.BaseMva;
                    Func<double, double> inj = refFreq > 0
                        ? tt => iPu * DetMath.Cos(2.0 * Math.PI * refFreq * tt + refPhase)
                        : _ => iPu;
                    elements.Add(new CurrentSource(id++, nodeNeg, inj));
                    double bMagNeg = Math.Abs(b.Qd) / model.BaseMva;
                    double lNeg = b.Qd > 0 && bMagNeg > 0 ? 1.0 / (bMagNeg * omega) : 0.0;
                    double cNeg = b.Qd < 0 && bMagNeg > 0 ? bMagNeg / omega : 0.0;
                    if (lNeg != 0 || cNeg != 0)
                        elements.Add(new ShuntElement(id++, nodeNeg, C: cNeg, L: lNeg));
                    continue;
                }
                // Constant-impedance load at 1 pu: Y = (Pd - jQd)/BaseMva. Qd>0 inductive -> L; Qd<0 -> C.
                double gLoad = b.Pd / model.BaseMva;
                double bMag = Math.Abs(b.Qd) / model.BaseMva;
                double lLoad = b.Qd > 0 && bMag > 0 ? 1.0 / (bMag * omega) : 0.0;
                double cLoad = b.Qd < 0 && bMag > 0 ? bMag / omega : 0.0;
                if (gLoad != 0 || lLoad != 0 || cLoad != 0)
                    elements.Add(new ShuntElement(id++, busToNode[b.Id], G: gLoad, C: cLoad, L: lLoad));
            }
        }

        foreach (Bus b in model.Buses)
        {
            double gPu = model.BaseMva > 0 ? b.Gs / model.BaseMva : 0.0;
            double bPu = model.BaseMva > 0 ? b.Bs / model.BaseMva : 0.0;
            if (gPu == 0 && bPu == 0) continue;
            int node = busToNode[b.Id];
            double c = bPu > 0 ? EmtUnits.ShuntC(bPu, omega) : 0.0;
            double lReactor = bPu < 0 ? 1.0 / (-bPu * omega) : 0.0;
            elements.Add(new ShuntElement(id++, node, G: gPu, C: c, L: lReactor));
        }

        foreach (EmtSourceSpec s in options.Sources)
        {
            if (!busToNode.TryGetValue(s.BusId, out int node))
                throw new ArgumentException($"source bus {s.BusId} is not in the model.", nameof(options));
            double phase = s.PhaseDeg * Math.PI / 180.0;
            double amp = s.AmplitudePu, freq = s.FrequencyHz;
            Func<double, double> emf = freq > 0 ? tt => amp * DetMath.Cos(2.0 * Math.PI * freq * tt + phase) : _ => amp;
            elements.Add(new VoltageSource(id++, node, s.Rs, emf));
        }

        return new EmtNetwork(busToNode.Count, elements, busToNode, seriesIds);
    }

    /// <summary>R078: how a refused branch is named in an error: its position in the model's branch list, its end
    /// buses and its name when it has one, so the user can find it in the case file.</summary>
    private static string BranchLabel(int index, Branch br) =>
        $"branch #{index} ({br.From}->{br.To}{(string.IsNullOrEmpty(br.Name) ? "" : ", '" + br.Name + "'")})";
}

/// <summary>A fully-specified EMT run: the network, the fixed timestep and duration, the probes to record, and
/// any scheduled switching events.
/// <para>R077: <see cref="Network"/> and <see cref="DtSeconds"/> duplicate what an <see cref="EmtEngine"/> is
/// constructed with. They are kept (existing callers pass them) but are now CHECKED, not trusted:
/// <see cref="EmtEngine.Simulate"/> throws <see cref="ArgumentException"/> unless the network is the engine's own
/// (or element-for-element equivalent) and the timestep equals the engine's exactly.</para></summary>
/// <param name="Network">The resolved network; must be the one the engine was built from.</param>
/// <param name="DtSeconds">Fixed integration step (seconds); must equal the engine's construction timestep.</param>
/// <param name="DurationSeconds">Total simulated time (seconds).</param>
/// <param name="Probes">Channels to record each step.</param>
/// <param name="Events">Scheduled switching events (empty for a plain transient).</param>
public sealed record EmtCase(
    EmtNetwork Network, double DtSeconds, double DurationSeconds,
    IReadOnlyList<EmtProbe> Probes, IReadOnlyList<EmtEvent>? Events = null);
