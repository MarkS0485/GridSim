using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>Compensator settings for one regulator of a feeder, by the regulator's authored id: one setting per phase the
/// regulator carries (in a, b, c order) and whether the first phase's compensator gangs the bank.</summary>
public sealed record RegulatorControlSpec(IReadOnlyList<LdcSettings> PerPhase, bool Ganged);

/// <summary>Options of <see cref="MultiPhaseFeederAdapter.FromFeeder"/>.</summary>
public sealed record FeederAdapterOptions
{
    /// <summary>Per-unit series impedance given to a closed switch (null: a closed switch is refused by name).</summary>
    public double? SwitchZPu { get; init; }
    /// <summary>Per-unit impedance of each ideal regulator winding (on the line-to-neutral base). Default 1e-6.</summary>
    public double RegulatorWindingPu { get; init; } = 1e-6;
    /// <summary>The regulator step per tap, per unit (the incumbent loader's "tapStepPercent" is not kept in the model, so the
    /// ratio 1 + step tap is read back on this grid; a ratio off it is refused by name). Default 0.00625.</summary>
    public double RegulatorStepPu { get; init; } = 0.00625;
    /// <summary>The tap limit. Default 16.</summary>
    public int MaxTap { get; init; } = 16;
    /// <summary>Compensators by regulator id; a regulator without an entry keeps its fixed taps.</summary>
    public IReadOnlyDictionary<string, RegulatorControlSpec>? Control { get; init; }
}

/// <summary>The outcome of lifting a whole incumbent feeder, transformers and regulators included.</summary>
/// <param name="Frame">The regulated frame, or null when something is not carried.</param>
/// <param name="Unsupported">Named elements not carried (empty when <paramref name="Frame"/> is not null).</param>
/// <param name="Notes">Recorded modelling decisions.</param>
/// <param name="NeedsPinnedReference">True when a delta secondary makes a floating component: solve with
/// <see cref="FloatingPolicy.PinFirstWire"/> and compare delta nodes line to line.</param>
public sealed record FeederAdapterResult(RegulatedFrame? Frame, IReadOnlyList<string> Unsupported, IReadOnlyList<string> Notes, bool NeedsPinnedReference);

/// <summary>
/// RC4 R4-15b: lifts an incumbent <see cref="MultiPhaseFeeder"/> (per unit, each bus on its own kV base) into the SI phase frame
/// WITH its transformers (delta / grounded wye, ungrounded wye / delta, delta / delta, open wye / open delta, open delta /
/// open delta, each step-down or step-up as the incumbent loader reads it) and its step-voltage regulators, so the shipped IEEE
/// 4-node transformer cases and the IEEE 123-node feeder run through the meshed solver. Per-unit to SI uses each bus's own
/// base: V = V_pu V_LN(bus), S = S_pu S_base/3, Z = Z_pu V_LN^2 / (S_base/3). The incumbent is not changed.
/// </summary>
public static class MultiPhaseFeederAdapter
{
    private static readonly string[] Wire = ["a", "b", "c"];

    /// <summary>Lift <paramref name="feeder"/>.</summary>
    public static FeederAdapterResult FromFeeder(MultiPhaseFeeder feeder, FeederAdapterOptions? options = null)
    {
        FeederAdapterOptions opt = options ?? new FeederAdapterOptions();
        MultiPhaseModel model = feeder.Model;
        double sBase = feeder.BaseMva * 1e6 / 3.0;
        string Name(int id) => MultiPhaseFrameAdapter.Name(id);
        var unsupported = new List<string>();
        var notes = new List<string>();
        bool pinned = false;

        var busPhases = new Dictionary<int, Phase>();
        foreach (PhaseBus b in model.Buses) busPhases[b.Id] = b.IsSource ? Phase.Abc : Phase.None;
        foreach (PhaseBranch br in model.Branches) { busPhases[br.From] |= br.Phases; busPhases[br.To] |= br.Phases; }

        var nodes = new List<FrameNode>();
        foreach (PhaseBus b in model.Buses)
        {
            var w = new List<string>();
            for (int p = 0; p < 3; p++) if (PhaseIndex.Has(busPhases[b.Id], p)) w.Add(Wire[p]);
            if (w.Count == 0) w.Add("a");
            nodes.Add(new FrameNode(Name(b.Id), w));
        }

        var sources = new List<FrameSource>();
        foreach (PhaseBus b in model.Buses.Where(x => x.IsSource))
        {
            Complex[] sv = b.SourceV ?? MultiPhaseModel.BalancedNominal();
            double vb = feeder.VoltBaseLN(b.Id);
            sources.Add(new FrameSource(Name(b.Id), Wire, [sv[0] * vb, sv[1] * vb, sv[2] * vb]));
        }

        var branches = new List<FrameBranch>();
        var banks = new List<BankSpec>();
        var regulators = new List<StepRegulator>();
        for (int k = 0; k < model.Branches.Count; k++)
        {
            PhaseBranch br = model.Branches[k];
            string id = k < feeder.BranchIds.Count ? feeder.BranchIds[k] : $"br{k}";
            string label = $"{br.From}-{br.To} ({id})";
            double vFromLn = feeder.VoltBaseLN(br.From), vToLn = feeder.VoltBaseLN(br.To);
            double zBaseFrom = vFromLn * vFromLn / sBase, zBaseTo = vToLn * vToLn / sBase;
            var carried = new List<int>();
            for (int p = 0; p < 3; p++) if (PhaseIndex.Has(br.Phases, p)) carried.Add(p);

            if (br.TransformerFlagCount > 1) { unsupported.Add($"branch {label}: more than one transformer flag"); continue; }

            if (br.IsTransformerElement)
            {
                if (TryBank(br, id, feeder, sBase, out BankSpec? spec, out string? why)) { banks.Add(spec!); pinned |= br.HasDeltaSecondary; }
                else unsupported.Add($"branch {label}: {why}");
                continue;
            }

            if (br.RegulatorRatio is { } ratios)
            {
                var taps = new List<int>();
                var phases = new List<string>();
                bool ok = true;
                foreach (int p in carried)
                {
                    double t = (ratios[p] - 1.0) / opt.RegulatorStepPu;
                    int tap = (int)Math.Round(t);
                    if (Math.Abs(tap * opt.RegulatorStepPu - (ratios[p] - 1.0)) > 1e-9 || Math.Abs(tap) > opt.MaxTap)
                    {
                        unsupported.Add(FormattableString.Invariant($"branch {label}: step-voltage regulator ratio {ratios[p]:R} on phase {Wire[p]} is not a tap on the {opt.RegulatorStepPu:R} per unit grid within +/-{opt.MaxTap}"));
                        ok = false;
                        break;
                    }
                    taps.Add(tap);
                    phases.Add(Wire[p]);
                }
                if (!ok) continue;
                IReadOnlyList<LdcSettings>? ldc = null;
                bool ganged = false;
                if (opt.Control is not null && opt.Control.TryGetValue(id, out RegulatorControlSpec? cs))
                {
                    if (cs.PerPhase.Count != phases.Count) { unsupported.Add($"branch {label}: {cs.PerPhase.Count} compensator settings for {phases.Count} phase(s)"); continue; }
                    ldc = cs.PerPhase;
                    ganged = cs.Ganged;
                }
                regulators.Add(new StepRegulator(id, Name(br.From), Name(br.To), RegulatorConnection.Wye, phases, taps, opt.RegulatorStepPu, opt.MaxTap,
                    opt.RegulatorWindingPu * zBaseFrom, ldc, ganged));
                continue;
            }

            int q = carried.Count;
            var z = new Complex[q, q];
            bool zero = true;
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++) { z[i, j] = br.Z[carried[i], carried[j]] * zBaseFrom; if (br.Z[carried[i], carried[j]] != Complex.Zero) zero = false; }
            if (zero)
            {
                if (opt.SwitchZPu is not { } sz) { unsupported.Add($"branch {label}: closed switch (zero impedance) without a switch impedance"); continue; }
                for (int i = 0; i < q; i++) z[i, i] = new Complex(sz * zBaseFrom, 0);
            }
            Complex[,]? sh = null;
            if (br.ShuntY is { } y)
            {
                sh = new Complex[q, q];
                for (int i = 0; i < q; i++) for (int j = 0; j < q; j++) sh[i, j] = y[carried[i], carried[j]] / zBaseFrom;
            }
            branches.Add(new FrameBranch($"br{k}:{br.From}-{br.To}", Name(br.From), Name(br.To), carried.Select(p => Wire[p]).ToArray(), z, sh));
        }

        var loads = new List<FrameLoad>();
        for (int k = 0; k < model.Loads.Count; k++)
        {
            PhaseLoad l = model.Loads[k];
            double vLn = feeder.VoltBaseLN(l.Bus);
            Zip zip = l.Model switch
            {
                PhaseLoadModel.ConstantCurrent => Zip.ConstantCurrent,
                PhaseLoadModel.ConstantImpedance => Zip.ConstantImpedance,
                _ => Zip.ConstantPower,
            };
            Complex[] s = [l.SPerPhase[0] * sBase, l.SPerPhase[1] * sBase, l.SPerPhase[2] * sBase];
            string name = $"load{k}@{l.Bus}";
            loads.Add(l.Connection == PhaseLoadConnection.Wye
                ? FrameLoad.WyeEarthed(name, Name(l.Bus), s, vLn, zip)
                : FrameLoad.Delta(name, Name(l.Bus), s, vLn * Math.Sqrt(3.0), zip));
        }
        for (int k = 0; k < model.Capacitors.Count; k++)
        {
            PhaseCapacitor c = model.Capacitors[k];
            double vLn = feeder.VoltBaseLN(c.Bus);
            Complex[] s = [new Complex(0, -c.QPerPhase[0] * sBase), new Complex(0, -c.QPerPhase[1] * sBase), new Complex(0, -c.QPerPhase[2] * sBase)];
            loads.Add(FrameLoad.WyeEarthed($"cap{k}@{c.Bus}", Name(c.Bus), s, vLn, Zip.ConstantImpedance));
        }

        if (unsupported.Count > 0) return new FeederAdapterResult(null, unsupported, notes, pinned);
        if (pinned) notes.Add("a delta secondary is galvanically separate: solve with FloatingPolicy.PinFirstWire and read its nodes line to line.");
        var frame = new RegulatedFrame(feeder.Name, nodes, sources, branches, loads, null, null, null, banks, regulators);
        return new FeederAdapterResult(frame, Array.Empty<string>(), notes, pinned);
    }

    private static bool TryBank(PhaseBranch br, string id, MultiPhaseFeeder f, double sBase, out BankSpec? spec, out string? why)
    {
        spec = null;
        why = null;
        double baseFrom = f.BaseKvLLByBus[br.From], baseTo = f.BaseKvLLByBus[br.To];
        double t = br.TransformerRatio;
        // The rated voltages are not kept by the incumbent model, only the per-unit ratio t = (base_from / rated_from)(rated_to / base_to).
        // The frame takes rated_from = base_from and rated_to = t base_to: the same open-circuit ratio and the same ohms.
        double kvFrom = baseFrom, kvTo = t * baseTo;
        double unitKva = sBase / 1000.0;
        string name = id;

        // The incumbent's Z is per unit on the To bus base; on the rated voltage t base_to it is Z / t^2.
        double rebase = 1.0 / (t * t);
        const double tol = 1e-12;

        Complex zTotal;
        BankConnection conn;
        if (br.IsDeltaWye)
        {
            if (!IsDiagonalEqual(br.Z, out zTotal)) { why = "delta / grounded-wye impedance is not a single equal diagonal"; return false; }
            conn = BankConnection.DeltaGroundedWye;
        }
        else if (br.WyeDeltaStepDown || br.WyeDeltaStepUp)
        {
            if (!IsDiagonalEqual(br.Z, out zTotal)) { why = "wye / delta with unequal winding impedances is not carried"; return false; }
            conn = BankConnection.WyeDelta;
        }
        else if (br.DeltaDelta)
        {
            if (!IsDiagonalEqual(br.Z, out zTotal)) { why = "delta / delta with unequal winding impedances is not carried"; return false; }
            conn = BankConnection.DeltaDelta;
        }
        else if (br.OpenWyeOpenDelta || br.OpenDeltaOpenDelta)
        {
            // Z = [[2a, 0, -b], [-a, 0, -b], [-a, 0, 2b]] (TransformerWindings.OpenDeltaSeriesZ)
            Complex a = br.Z[0, 0] / 2.0, b = -br.Z[0, 2];
            if (Mag(a - b) > tol * Math.Max(1.0, Mag(a))) { why = "open bank with unequal unit impedances is not carried"; return false; }
            zTotal = a;
            conn = br.OpenWyeOpenDelta ? BankConnection.OpenWyeOpenDelta : BankConnection.OpenDeltaOpenDelta;
        }
        else { why = "unknown transformer element"; return false; }

        // Step-up / step-down by the sweep's flags (the loader chose them by the rated kV), made consistent for the builder.
        bool stepUpFlag = br.DeltaWyeStepUp || br.WyeDeltaStepUp;
        bool stepDownFlag = br.DeltaWyeStepDown || br.WyeDeltaStepDown;
        if (stepUpFlag && !(kvFrom < kvTo)) { why = "step-up element whose rated voltages are not step-up"; return false; }
        if (stepDownFlag && !(kvFrom > kvTo)) { why = "step-down element whose rated voltages are not step-down"; return false; }
        // Direction-free connections (and the loader's nominal ratio of exactly the bus bases) keep the same builder rules.
        Complex half = zTotal * rebase / 2.0;
        spec = new BankSpec(name, MultiPhaseFrameAdapter.Name(br.From), MultiPhaseFrameAdapter.Name(br.To), conn, kvFrom, kvTo, unitKva, half, half);
        return true;
    }

    private static double Mag(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);

    private static bool IsDiagonalEqual(PhaseImpedance z, out Complex d)
    {
        d = z[0, 0];
        double tol = 1e-12 * Math.Max(1e-300, Mag(d));
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Complex want = i == j ? d : Complex.Zero;
                if (Mag(z[i, j] - want) > tol) return false;
            }
        return d != Complex.Zero;
    }
}
