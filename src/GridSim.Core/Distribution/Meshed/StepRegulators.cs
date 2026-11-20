using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

// RC4 R4-15b: step-voltage regulators for the meshed phase frame, and the line-drop-compensator tap search as an OUTER loop
// around the unchanged Newton solve. A regulator is a set of ideal single-phase units (the N-winding unit model with a very
// small winding impedance, so the nodal stamp stays regular); its taps are numbers, so a tap search just rebuilds the model.

/// <summary>How a regulator bank is connected.</summary>
public enum RegulatorConnection
{
    /// <summary>One ideal regulator per listed phase, line to earth (or to the return wire): V_to = r V_from. Type B,
    /// 3-phase, 2-phase or single-phase.</summary>
    Wye,
    /// <summary>Open delta, type B (two regulators): the ab regulator has its series winding in line a and is excited by the
    /// source line-to-line voltage A-B, the cb regulator has its series winding in line c and is excited by C-B; line b is
    /// straight through. So V_ab = r_ab V_AB and V_cb = r_cb V_CB (no phase shift). The taps are [ab, cb].</summary>
    OpenDelta,
    /// <summary>Closed delta, in the form every common program uses: three delta-connected ratio windings (a delta / delta
    /// transformer whose ratio is the regulator ratio, V_ab = r V_AB and so on, no phase shift). One ratio serves the
    /// loop: the three taps must be equal (a closed loop of ideal ratios cannot carry unequal ratios), else it is refused. The
    /// secondary delta is galvanically separate and needs a reference downstream (an earthed load, or the pinned-reference
    /// policy). GridSim's definition: the generalised matrices of a closed-delta regulator with unequal taps are not modelled.</summary>
    ClosedDelta,
}

/// <summary>
/// The line-drop-compensator settings of one regulated phase, in volts on the 120 V (relay) base as on the nameplate: the
/// relay voltage is V_relay = V_load_node / PT - (R + jX) I_line / CT, the tap is raised one step when
/// |V_relay| &lt; VSet - Bandwidth / 2 and lowered one step when |V_relay| &gt; VSet + Bandwidth / 2.
/// </summary>
/// <param name="VSetVolts">The voltage level (relay base, for example 120).</param>
/// <param name="BandwidthVolts">The bandwidth (total width, for example 2).</param>
/// <param name="PtRatio">Potential transformer ratio (for example 20).</param>
/// <param name="CtPrimaryAmps">CT primary rating (amperes, secondary 5 A is not needed: I / CT is taken with the rating as
/// the divisor, the compensator-setting convention R'(volts) = R (ohm) CT / PT).</param>
/// <param name="RVolts">R setting, volts.</param>
/// <param name="XVolts">X setting, volts.</param>
public sealed record LdcSettings(double VSetVolts, double BandwidthVolts, double PtRatio, double CtPrimaryAmps, double RVolts, double XVolts);

/// <summary>
/// A regulator bank between two nodes with its tap state. <paramref name="Taps"/> is one integer per listed phase for
/// <see cref="RegulatorConnection.Wye"/> (the ratio of phase p is 1 + StepPu * tap), two for open delta ([ab, cb]) and three for
/// closed delta. <paramref name="Ldc"/> gives one setting per listed phase (a wye regulator) when the tap is to be searched;
/// null: the taps are fixed. <paramref name="Ganged"/> (wye, with Ldc): the first listed phase's compensator controls the
/// whole bank and every phase takes its tap.
/// </summary>
public sealed record StepRegulator(string Name, string FromNode, string ToNode, RegulatorConnection Connection,
    IReadOnlyList<string> Phases, IReadOnlyList<int> Taps, double StepPu = 0.00625, int MaxTap = 16,
    double WindingOhm = 1e-5, IReadOnlyList<LdcSettings>? Ldc = null, bool Ganged = false, string? ReturnWire = null)
{
    /// <summary>The ratio 1 + StepPu * tap.</summary>
    public double Ratio(int tap) => 1.0 + StepPu * tap;
}

/// <summary>Builds the units of a regulator at given taps.</summary>
public static class StepRegulators
{
    private static readonly string[] Abc = ["a", "b", "c"];
    private const double TurnsFloor = 1e-9;

    /// <summary>Check one regulator's shape. Throws <see cref="ArgumentException"/> naming the problem.</summary>
    public static void Validate(StepRegulator r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("a regulator needs a name.");
        if (!(double.IsFinite(r.StepPu) && r.StepPu > 0 && r.StepPu < 0.1)) throw new ArgumentException($"regulator '{r.Name}': the step must be in (0, 0.1) per unit (a nameplate step is 0.00625).");
        if (r.MaxTap < 0 || r.MaxTap > 64) throw new ArgumentException($"regulator '{r.Name}': MaxTap must be in 0..64.");
        if (!(double.IsFinite(r.WindingOhm) && r.WindingOhm > 0)) throw new ArgumentException($"regulator '{r.Name}': the winding impedance must be finite and above 0.");
        foreach (int t in r.Taps)
            if (Math.Abs(t) > r.MaxTap) throw new ArgumentException($"regulator '{r.Name}': tap {t} is outside +/-{r.MaxTap}.");
        switch (r.Connection)
        {
            case RegulatorConnection.Wye:
                if (r.Phases.Count == 0 || r.Taps.Count != r.Phases.Count)
                    throw new ArgumentException($"regulator '{r.Name}': a wye regulator needs one tap per listed phase.");
                break;
            case RegulatorConnection.OpenDelta:
                if (r.Taps.Count != 2) throw new ArgumentException($"regulator '{r.Name}': an open delta needs two taps [ab, cb].");
                break;
            case RegulatorConnection.ClosedDelta:
                if (r.Taps.Count != 3) throw new ArgumentException($"regulator '{r.Name}': a closed delta needs three taps.");
                if (r.Taps[0] != r.Taps[1] || r.Taps[1] != r.Taps[2])
                    throw new ArgumentException($"regulator '{r.Name}': a closed delta carries one ratio, so its three taps must be equal (got {r.Taps[0]}, {r.Taps[1]}, {r.Taps[2]}).");
                break;
        }
        if (r.Ldc is not null)
        {
            if (r.Connection != RegulatorConnection.Wye) throw new ArgumentException($"regulator '{r.Name}': line-drop compensation is modelled for wye regulators only; a delta regulator keeps its fixed taps.");
            if (r.Ldc.Count != r.Phases.Count) throw new ArgumentException($"regulator '{r.Name}': one compensator setting per listed phase is needed.");
            foreach (LdcSettings l in r.Ldc)
                if (!(l.VSetVolts > 0 && l.BandwidthVolts > 0 && l.PtRatio > 0 && l.CtPrimaryAmps > 0 && double.IsFinite(l.RVolts) && double.IsFinite(l.XVolts)))
                    throw new ArgumentException($"regulator '{r.Name}': compensator settings need a voltage level, bandwidth, PT ratio and CT rating above 0 and finite R and X.");
        }
    }

    /// <summary>The unit name of phase <paramref name="phase"/> of wye regulator <paramref name="name"/>.</summary>
    public static string UnitName(string name, string phase) => $"{name}.{phase}";

    /// <summary>The ideal units of <paramref name="r"/> at its own <see cref="StepRegulator.Taps"/>.</summary>
    public static FrameParts Build(StepRegulator r) => Build(r, r.Taps);

    /// <summary>The ideal units of <paramref name="r"/> at the taps <paramref name="taps"/>.</summary>
    public static FrameParts Build(StepRegulator r, IReadOnlyList<int> taps)
    {
        Validate(r with { Taps = taps });
        var parts = new FrameParts();
        var z = new Complex(r.WindingOhm, 0);
        switch (r.Connection)
        {
            case RegulatorConnection.Wye:
                for (int i = 0; i < r.Phases.Count; i++)
                {
                    string w = r.Phases[i];
                    // v_in = E, v_out = r E, both from the wire to its return (earth): V_to = r V_from, I_from = r I_to.
                    parts.Units.Add(new FrameTransformerUnit(UnitName(r.Name, w),
                        [new FrameWinding(r.FromNode, w, r.ReturnWire, 1.0, z), new FrameWinding(r.ToNode, w, r.ReturnWire, r.Ratio(taps[i]), z)]));
                }
                break;
            case RegulatorConnection.OpenDelta:
            {
                // ab regulator: series winding in line a (P = a at the To node, Q = A at the From node), excited by A-B.
                // cb regulator: series winding in line c, excited by C-B. Line b is a straight (tiny impedance) connection.
                (string line, string e1, string e2, int tap, string tag)[] regs =
                    [("a", "a", "b", taps[0], "ab"), ("c", "c", "b", taps[1], "cb")];
                foreach ((string line, string e1, string e2, int tap, string tag) in regs)
                {
                    double turns = r.StepPu * tap;
                    if (Math.Abs(turns) < TurnsFloor) turns = TurnsFloor;
                    parts.Units.Add(new FrameTransformerUnit($"{r.Name}.{tag}",
                    [
                        new FrameWinding(r.FromNode, e1, e2, 1.0, z),
                        new FrameWinding(r.ToNode, line, line, turns, z, r.FromNode),
                    ]));
                }
                parts.Branches.Add(new FrameBranch($"{r.Name}.b", r.FromNode, r.ToNode, ["b"], new Complex[,] { { z } }));
                parts.Notes.Add($"regulator '{r.Name}': open delta, V_ab = r_ab V_AB and V_cb = r_cb V_CB (series windings in lines a and c, line b straight).");
                break;
            }
            case RegulatorConnection.ClosedDelta:
            {
                double ratio = r.Ratio(taps[0]);
                // Delta / delta ratio bank on the line-to-line voltages, one ratio, no shift. Turns: 1 and r on delta windings (a,b), (b,c), (c,a).
                for (int k = 0; k < 3; k++)
                    parts.Units.Add(new FrameTransformerUnit($"{r.Name}.{Abc[k]}",
                    [
                        new FrameWinding(r.FromNode, Abc[k], Abc[(k + 1) % 3], 1.0, z),
                        new FrameWinding(r.ToNode, Abc[k], Abc[(k + 1) % 3], ratio, z),
                    ]));
                parts.Notes.Add($"regulator '{r.Name}': closed delta as a delta / delta ratio bank (galvanically separate secondary).");
                break;
            }
        }
        return parts;
    }
}

/// <summary>A model assembled from fixed elements plus banks and regulators, rebuilt at any tap set.</summary>
public sealed class RegulatedFrame
{
    /// <summary>Case name.</summary>
    public string Name { get; }
    private readonly List<FrameNode> _nodes;
    private readonly IReadOnlyList<FrameSource> _sources;
    private readonly List<FrameBranch> _branches;
    private readonly IReadOnlyList<FrameLoad> _loads;
    private readonly IReadOnlyList<EarthingElement> _earthing;
    private readonly IReadOnlyList<EarthGrid> _grids;
    private readonly List<FrameTransformerUnit> _units;
    private readonly List<string> _notes = new();

    /// <summary>The regulators in search order.</summary>
    public IReadOnlyList<StepRegulator> Regulators { get; }
    /// <summary>Recorded modelling decisions of the banks.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Assemble. <paramref name="banks"/> are added as units (and star nodes); regulators are rebuilt at each tap set.</summary>
    public RegulatedFrame(string name, IReadOnlyList<FrameNode> nodes, IReadOnlyList<FrameSource> sources, IReadOnlyList<FrameBranch> branches,
        IReadOnlyList<FrameLoad>? loads = null, IReadOnlyList<EarthingElement>? earthing = null, IReadOnlyList<EarthGrid>? grids = null,
        IReadOnlyList<FrameTransformerUnit>? units = null, IReadOnlyList<BankSpec>? banks = null, IReadOnlyList<StepRegulator>? regulators = null)
    {
        Name = name;
        _nodes = nodes.ToList();
        _sources = sources;
        _branches = branches.ToList();
        _loads = loads ?? Array.Empty<FrameLoad>();
        _earthing = earthing ?? Array.Empty<EarthingElement>();
        _grids = grids ?? Array.Empty<EarthGrid>();
        _units = (units ?? Array.Empty<FrameTransformerUnit>()).ToList();
        foreach (BankSpec b in banks ?? Array.Empty<BankSpec>())
        {
            FrameParts p = TransformerBanks.Build(b);
            _nodes.AddRange(p.Nodes);
            _units.AddRange(p.Units);
            _branches.AddRange(p.Branches);
            _notes.AddRange(p.Notes);
        }
        Regulators = regulators ?? Array.Empty<StepRegulator>();
        foreach (StepRegulator r in Regulators) StepRegulators.Validate(r);
    }

    /// <summary>The same network with every regulator's starting taps replaced (one array per regulator, in order): the start of a tap search.</summary>
    public RegulatedFrame WithInitialTaps(IReadOnlyList<int[]> taps)
    {
        if (taps.Count != Regulators.Count) throw new ArgumentException("one tap array per regulator is needed.");
        var regs = Regulators.Select((r, i) => r with { Taps = taps[i] }).ToList();
        var copy = new RegulatedFrame(Name, _nodes, _sources, _branches, _loads, _earthing, _grids, _units, null, regs);
        copy._notes.AddRange(_notes);
        return copy;
    }

    /// <summary>The initial taps of every regulator.</summary>
    public IReadOnlyList<int[]> InitialTaps() => Regulators.Select(r => r.Taps.ToArray()).ToArray();

    /// <summary>The model at the given taps (one array per regulator, in order).</summary>
    public PhaseFrameModel Build(IReadOnlyList<int[]> taps)
    {
        if (taps.Count != Regulators.Count) throw new ArgumentException("one tap array per regulator is needed.");
        var nodes = _nodes.ToList();
        var branches = _branches.ToList();
        var units = _units.ToList();
        for (int i = 0; i < Regulators.Count; i++)
        {
            FrameParts p = StepRegulators.Build(Regulators[i], taps[i]);
            nodes.AddRange(p.Nodes);
            branches.AddRange(p.Branches);
            units.AddRange(p.Units);
        }
        return new PhaseFrameModel(Name, nodes, _sources, branches, _loads, _earthing, _grids, units);
    }
}

/// <summary>Result of the tap search.</summary>
public enum TapSearchStatus
{
    /// <summary>Every compensated phase is inside its band (or at a tap limit), and the final load flow converged.</summary>
    Settled,
    /// <summary>A tap set repeated: the controllers would hunt. The cycle is named; the last state is reported.</summary>
    Hunting,
    /// <summary>The outer iteration limit was reached before the taps settled.</summary>
    NotSettled,
    /// <summary>A load flow inside the search did not converge or was incomplete; its status is in <see cref="TapSearchResult.Final"/>.</summary>
    LoadFlowFailed,
}

/// <summary>One compensated phase at the end of the search.</summary>
/// <param name="Regulator">Regulator name.</param>
/// <param name="Phase">Phase wire.</param>
/// <param name="Tap">Final tap.</param>
/// <param name="RelayVolts">|V_relay| at the final state (relay base).</param>
/// <param name="LowVolts">Lower band edge.</param>
/// <param name="HighVolts">Upper band edge.</param>
/// <param name="InBand">True when the relay voltage is inside the band.</param>
/// <param name="AtLimit">True when the tap is at its limit and would have moved further.</param>
public sealed record TapRow(string Regulator, string Phase, int Tap, double RelayVolts, double LowVolts, double HighVolts, bool InBand, bool AtLimit);

/// <summary>The tap search outcome.</summary>
public sealed class TapSearchResult
{
    /// <summary>Terminal status.</summary>
    public required TapSearchStatus Status { get; init; }
    /// <summary>Outer iterations used (each is one full load flow).</summary>
    public required int OuterIterations { get; init; }
    /// <summary>Final taps per regulator.</summary>
    public required IReadOnlyList<int[]> Taps { get; init; }
    /// <summary>One row per compensated phase.</summary>
    public required IReadOnlyList<TapRow> Rows { get; init; }
    /// <summary>The load flow at the final taps.</summary>
    public required MeshedResult Final { get; init; }
    /// <summary>Tap sets visited, in order (the trace of the search).</summary>
    public required IReadOnlyList<string> Trace { get; init; }
    /// <summary>The model at the final taps.</summary>
    public required PhaseFrameModel Model { get; init; }
}

/// <summary>
/// The LDC tap search: an outer loop around <see cref="Newton3Ph.Solve"/>. Deterministic and bounded: every pass solves the load
/// flow from the same (no-load) start at the current taps, then in regulator order and listed-phase order decides one step per
/// compensated phase (+1 below the band, -1 above, 0 inside, clamped at the tap limits; a ganged bank follows its first
/// phase), and applies all the steps together. It stops when no tap moves, when a tap set repeats (hunting) or after
/// <c>maxOuter</c> passes. No thread order, hash order or time enters; the tap sets are compared as ordered text.
/// </summary>
public static class RegulatorTapSearch
{
    /// <summary>Run the search from the frame's initial taps.</summary>
    public static TapSearchResult Run(RegulatedFrame frame, MeshedOptions? options = null, int maxOuter = 40)
    {
        if (maxOuter < 1) throw new ArgumentException("maxOuter must be at least 1.");
        MeshedOptions opt = options ?? new MeshedOptions();
        int[][] taps = frame.InitialTaps().Select(t => t.ToArray()).ToArray();
        var seen = new List<string>();
        var trace = new List<string>();
        PhaseFrameModel model = frame.Build(taps);
        MeshedResult res = Newton3Ph.Solve(model, opt);
        for (int outer = 1; ; outer++)
        {
            string key = Key(frame, taps);
            trace.Add(key);
            if (res.Status != MeshedStatus.Converged)
                return Done(TapSearchStatus.LoadFlowFailed, outer, taps, frame, model, res, trace);
            // Decide the steps for this state.
            (int[][] next, List<TapRow> rows, bool moved) = Decide(frame, taps, model, res);
            if (!moved) return new TapSearchResult { Status = TapSearchStatus.Settled, OuterIterations = outer, Taps = taps, Rows = rows, Final = res, Trace = trace, Model = model };
            string nextKey = Key(frame, next);
            if (seen.Contains(nextKey) || nextKey == key)
                return new TapSearchResult { Status = TapSearchStatus.Hunting, OuterIterations = outer, Taps = taps, Rows = rows, Final = res, Trace = trace.Append("repeat " + nextKey).ToArray(), Model = model };
            seen.Add(key);
            if (outer >= maxOuter)
                return new TapSearchResult { Status = TapSearchStatus.NotSettled, OuterIterations = outer, Taps = taps, Rows = rows, Final = res, Trace = trace, Model = model };
            taps = next;
            model = frame.Build(taps);
            res = Newton3Ph.Solve(model, opt);
        }
    }

    /// <summary>Solve the frame at its own initial taps and report where every compensated phase stands (relay voltage against its
    /// band) without moving any tap. Status is <see cref="TapSearchStatus.Settled"/> when every phase is in band or at a limit.</summary>
    public static TapSearchResult Evaluate(RegulatedFrame frame, MeshedOptions? options = null)
    {
        MeshedOptions opt = options ?? new MeshedOptions();
        int[][] taps = frame.InitialTaps().Select(t => t.ToArray()).ToArray();
        PhaseFrameModel model = frame.Build(taps);
        MeshedResult res = Newton3Ph.Solve(model, opt);
        string key = Key(frame, taps);
        if (res.Status != MeshedStatus.Converged) return Done(TapSearchStatus.LoadFlowFailed, 1, taps, frame, model, res, [key]);
        (_, List<TapRow> rows, bool moved) = Decide(frame, taps, model, res);
        return new TapSearchResult { Status = moved ? TapSearchStatus.NotSettled : TapSearchStatus.Settled, OuterIterations = 1, Taps = taps, Rows = rows, Final = res, Trace = [key], Model = model };
    }

    private static TapSearchResult Done(TapSearchStatus status, int outer, int[][] taps, RegulatedFrame frame, PhaseFrameModel model, MeshedResult res, List<string> trace)
        => new() { Status = status, OuterIterations = outer, Taps = taps, Rows = Array.Empty<TapRow>(), Final = res, Trace = trace, Model = model };

    private static string Key(RegulatedFrame frame, IReadOnlyList<int[]> taps)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < frame.Regulators.Count; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(frame.Regulators[i].Name).Append('=').Append(string.Join(",", taps[i].Select(t => t.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return sb.ToString();
    }

    /// <summary>The relay voltage of compensated phase <paramref name="p"/> of <paramref name="r"/> in the state <paramref name="res"/>.</summary>
    public static Complex RelayVoltage(StepRegulator r, int p, MeshedResult res)
    {
        LdcSettings l = r.Ldc![p];
        string w = r.Phases[p];
        Complex vLoad = res.V(r.ToNode, w);
        UnitCurrents u = res.Units.First(x => x.Name == StepRegulators.UnitName(r.Name, w));
        Complex iLine = -u.WindingCurrent[1];   // winding 2 current enters at the To terminal, so the current into the feeder is its negative
        return vLoad / l.PtRatio - new Complex(l.RVolts, l.XVolts) * (iLine / l.CtPrimaryAmps);
    }

    private static (int[][] Next, List<TapRow> Rows, bool Moved) Decide(RegulatedFrame frame, int[][] taps, PhaseFrameModel model, MeshedResult res)
    {
        int[][] next = taps.Select(t => t.ToArray()).ToArray();
        var rows = new List<TapRow>();
        bool moved = false;
        for (int i = 0; i < frame.Regulators.Count; i++)
        {
            StepRegulator r = frame.Regulators[i];
            if (r.Ldc is null) continue;
            for (int p = 0; p < r.Phases.Count; p++)
            {
                if (r.Ganged && p > 0)
                {
                    // follows the first phase's decision; its row still reports its own relay voltage
                    LdcSettings lg = r.Ldc[p];
                    double vg = Mag(RelayVoltage(r, p, res));
                    rows.Add(new TapRow(r.Name, r.Phases[p], taps[i][p], vg, lg.VSetVolts - lg.BandwidthVolts / 2, lg.VSetVolts + lg.BandwidthVolts / 2,
                        vg >= lg.VSetVolts - lg.BandwidthVolts / 2 && vg <= lg.VSetVolts + lg.BandwidthVolts / 2, false));
                    continue;
                }
                LdcSettings l = r.Ldc[p];
                double lo = l.VSetVolts - l.BandwidthVolts / 2, hi = l.VSetVolts + l.BandwidthVolts / 2;
                double v = Mag(RelayVoltage(r, p, res));
                int step = v < lo ? 1 : v > hi ? -1 : 0;
                int t = taps[i][p];
                int nt = Math.Clamp(t + step, -r.MaxTap, r.MaxTap);
                bool limit = step != 0 && nt == t;
                if (nt != t) moved = true;
                if (r.Ganged) for (int q = 0; q < r.Phases.Count; q++) next[i][q] = nt; else next[i][p] = nt;
                rows.Add(new TapRow(r.Name, r.Phases[p], t, v, lo, hi, step == 0, limit));
            }
        }
        return (next, rows, moved);
    }

    private static double Mag(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);
}
