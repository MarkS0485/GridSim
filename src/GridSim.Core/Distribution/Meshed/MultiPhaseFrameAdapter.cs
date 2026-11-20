using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>The outcome of lifting a per-unit <see cref="MultiPhaseModel"/> into the SI phase frame.</summary>
/// <param name="Model">The phase-frame model, or null when the incumbent model has elements the frame adapter does not carry.</param>
/// <param name="Unsupported">Named elements left out of scope (empty when <paramref name="Model"/> is not null).</param>
public sealed record FrameAdapterResult(PhaseFrameModel? Model, IReadOnlyList<string> Unsupported);

/// <summary>
/// RC4 R4-15: lifts the incumbent per-unit radial model (<see cref="MultiPhaseModel"/>, solved by
/// <see cref="ThreePhaseSweep"/>) into the explicit phase frame so the meshed solver can be run on the same feeder and compared.
/// Per-unit to SI: V = V_pu vBase, S = S_pu sBase (per phase), Z = Z_pu vBase^2 / sBase, Y = Y_pu sBase / vBase^2. Carried: three-
/// and partial-phase lines with series impedance and pi shunt, wye (earthed neutral, as the sweep assumes) and delta loads of any
/// ZIP model, shunt capacitors. NOT carried (named in <see cref="FrameAdapterResult.Unsupported"/>, never dropped silently):
/// step-voltage regulators, every transformer element, and zero-impedance switches unless a switch impedance is given.
/// </summary>
public static class MultiPhaseFrameAdapter
{
    /// <summary>Lift <paramref name="model"/> on the line-to-neutral voltage base <paramref name="vBaseLn"/> (volts) and the per-phase
    /// power base <paramref name="sBasePerPhaseVa"/> (volt-amperes). <paramref name="switchZPu"/>: per-unit series impedance to give
    /// a closed switch (null refuses it).</summary>
    public static FrameAdapterResult FromMultiPhase(MultiPhaseModel model, double vBaseLn, double sBasePerPhaseVa, double? switchZPu = null)
    {
        if (!(vBaseLn > 0 && sBasePerPhaseVa > 0 && double.IsFinite(vBaseLn) && double.IsFinite(sBasePerPhaseVa)))
            throw new ArgumentException("the voltage and power bases must be finite and above 0.");
        double zBase = vBaseLn * vBaseLn / sBasePerPhaseVa;
        double yBase = 1.0 / zBase;
        string[] wire = ["a", "b", "c"];
        var unsupported = new List<string>();

        var busPhases = new Dictionary<int, Phase>();
        foreach (PhaseBus b in model.Buses) busPhases[b.Id] = b.IsSource ? Phase.Abc : Phase.None;
        foreach (PhaseBranch br in model.Branches)
        {
            busPhases[br.From] |= br.Phases;
            busPhases[br.To] |= br.Phases;
        }

        var nodes = new List<FrameNode>();
        foreach (PhaseBus b in model.Buses)
        {
            var w = new List<string>();
            for (int p = 0; p < 3; p++) if (PhaseIndex.Has(busPhases[b.Id], p)) w.Add(wire[p]);
            if (w.Count == 0) w.Add("a");
            nodes.Add(new FrameNode(Name(b.Id), w));
        }

        var sources = new List<FrameSource>();
        foreach (PhaseBus b in model.Buses.Where(x => x.IsSource))
        {
            Complex[] sv = b.SourceV ?? MultiPhaseModel.BalancedNominal();
            sources.Add(new FrameSource(Name(b.Id), wire, [sv[0] * vBaseLn, sv[1] * vBaseLn, sv[2] * vBaseLn]));
        }

        var branches = new List<FrameBranch>();
        for (int k = 0; k < model.Branches.Count; k++)
        {
            PhaseBranch br = model.Branches[k];
            string label = $"{br.From}-{br.To}";
            if (br.IsTransformerElement) { unsupported.Add($"branch {label}: {br.TransformerName} transformer"); continue; }
            if (br.RegulatorRatio is not null) { unsupported.Add($"branch {label}: step-voltage regulator"); continue; }
            var carried = new List<int>();
            for (int p = 0; p < 3; p++) if (PhaseIndex.Has(br.Phases, p)) carried.Add(p);
            int q = carried.Count;
            var z = new Complex[q, q];
            bool zero = true;
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++) { z[i, j] = br.Z[carried[i], carried[j]] * zBase; if (br.Z[carried[i], carried[j]] != Complex.Zero) zero = false; }
            if (zero)
            {
                if (switchZPu is not { } sz) { unsupported.Add($"branch {label}: closed switch (zero impedance) without a switch impedance"); continue; }
                for (int i = 0; i < q; i++) z[i, i] = new Complex(sz * zBase, 0);
            }
            Complex[,]? sh = null;
            if (br.ShuntY is { } y)
            {
                sh = new Complex[q, q];
                for (int i = 0; i < q; i++) for (int j = 0; j < q; j++) sh[i, j] = y[carried[i], carried[j]] * yBase;
            }
            branches.Add(new FrameBranch($"br{k}:{label}", Name(br.From), Name(br.To), carried.Select(p => wire[p]).ToArray(), z, sh));
        }

        var loads = new List<FrameLoad>();
        for (int k = 0; k < model.Loads.Count; k++)
        {
            PhaseLoad l = model.Loads[k];
            Zip zip = l.Model switch
            {
                PhaseLoadModel.ConstantCurrent => Zip.ConstantCurrent,
                PhaseLoadModel.ConstantImpedance => Zip.ConstantImpedance,
                _ => Zip.ConstantPower,
            };
            Complex[] s = [l.SPerPhase[0] * sBasePerPhaseVa, l.SPerPhase[1] * sBasePerPhaseVa, l.SPerPhase[2] * sBasePerPhaseVa];
            string name = $"load{k}@{l.Bus}";
            loads.Add(l.Connection == PhaseLoadConnection.Wye
                ? FrameLoad.WyeEarthed(name, Name(l.Bus), s, vBaseLn, zip)
                : FrameLoad.Delta(name, Name(l.Bus), s, vBaseLn * Math.Sqrt(3.0), zip));
        }
        for (int k = 0; k < model.Capacitors.Count; k++)
        {
            PhaseCapacitor c = model.Capacitors[k];
            // Capacitor susceptance Q (pu): current j Q V, which is a constant-impedance leg of power -j Q sBase at vBase.
            Complex[] s = [new Complex(0, -c.QPerPhase[0] * sBasePerPhaseVa), new Complex(0, -c.QPerPhase[1] * sBasePerPhaseVa), new Complex(0, -c.QPerPhase[2] * sBasePerPhaseVa)];
            loads.Add(FrameLoad.WyeEarthed($"cap{k}@{c.Bus}", Name(c.Bus), s, vBaseLn, Zip.ConstantImpedance));
        }

        if (unsupported.Count > 0) return new FrameAdapterResult(null, unsupported);
        return new FrameAdapterResult(new PhaseFrameModel("from-multiphase", nodes, sources, branches, loads), Array.Empty<string>());
    }

    /// <summary>The node name the adapter gives incumbent bus <paramref name="busId"/>.</summary>
    public static string Name(int busId) => "bus" + busId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
