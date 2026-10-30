using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim;

/// <summary>What <see cref="CgmesWriter.Write"/> wrote.</summary>
/// <param name="Text">The CGMES RDF/XML document (one file holding the EQ, TP, SSH and SV content).</param>
/// <param name="Network">The CIM network the document was written from.</param>
/// <param name="Notes">What the document does not carry that the power flow does not read (one line each).</param>
/// <param name="BranchOrder">The model branch each read-back branch is, in read-back order: in-service lines first, then
/// in-service transformers (the CIM reader builds lines before transformers).</param>
public sealed record CgmesWriteResult(string Text, CimNetwork Network, IReadOnlyList<string> Notes, IReadOnlyList<int> BranchOrder);

/// <summary>
/// Export pack (2026-10): writes a <see cref="GridModel"/> as CGMES RDF/XML (through <see cref="CimExporter"/>) such that
/// GridSim's own CIM reader, with <see cref="ReadBackOptions"/>, and <see cref="CimGridModelBridge"/> rebuild the same power
/// flow to the bit. Used by <c>gridsim convert</c> (an output ending in .xml), which proves each conversion.
/// </summary>
/// <remarks>
/// <para><b>Exactness.</b> CGMES holds impedances in ohms and siemens, voltages in kV and set-points in kV, so every per-unit
/// value is converted and the reader converts it back. Each written value is the double whose conversion by the reader's
/// own arithmetic (the bridge's expressions, term for term) gives the model's value back to the bit: the plain product or
/// quotient when that round-trips, otherwise the nearest double that does (searched outward, at most 64 steps each way).
/// Such a double does not always exist: a product or quotient by a base that is not a power of two does not reach every
/// double (about half the values of a typical MATPOWER case have none). Such a value is written as the double whose
/// read-back is nearest (one ulp off in practice), counted in a note, and the proof then shows the difference: CGMES can
/// carry a model to the bit only when its per-unit values are images of physical values (as a model read from CIM is).
/// Every bus needs a base voltage.</para>
/// <para><b>What is written.</b> Buses as TopologicalNodes with their base voltage and their stored voltage as an SV
/// SvVoltage (the solve's starting point); one EnergyConsumer per bus with demand; one LinearShuntCompensator per bus with a
/// fixed shunt; the in-service generators as SynchronousMachines (voltage control and target at slack and PV buses, the
/// slack's machines with referencePriority 1, reactive and active limits, ratedS = MBase, inertia); in-service branches as
/// ACLineSegments when they are plain lines on one base voltage, else as two-winding PowerTransformers (impedance on
/// winding 1 at the from bus's base, the ratio as a ratio tap changer, a shift as a linear phase tap changer); rate A as a
/// permanent ApparentPowerLimit. The read-back numbers buses 1..n in model order and lists lines before transformers;
/// <see cref="InFileOrder"/> gives the input in that form, which the proof compares with.</para>
/// <para><b>Refused</b> (an <see cref="InvalidDataException"/> listing every reason; nothing is written): devices the CIM reader does not
/// rebuild into the model the power flow solves (on-load tap changers and flow controls: the reader turns a regulating tap
/// changer into a CimControlledPowerFlow control, not the tap-changer loop; HVDC links, FACTS devices, static var
/// compensators, induction machines), voltage-dependent (ZIP) loads, a bus with no base voltage, a slack bus with no
/// in-service generator, machines at one voltage-controlled bus holding different set-points, and reactive limits CGMES
/// cannot hold.</para>
/// </remarks>
public static class CgmesWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The import options a written document is read back with: the SV starting voltages and the ratings.</summary>
    public static CimImportOptions ReadBackOptions { get; } = new(StartingVoltages: true, ApparentPowerRatings: true);

    /// <summary>Read back a document this writer wrote: CIM import with <see cref="ReadBackOptions"/>, bridged on
    /// <paramref name="baseMva"/> (CGMES carries no system base, so the caller gives the model's).</summary>
    public static GridModel ReadBack(string rdfXml, double baseMva, string name) =>
        CimGridModelBridge.ToGridModel(CimImporter.Parse(rdfXml, ReadBackOptions), baseMva, name);

    /// <summary>The generator types the CIM exporter and importer carry through a GeneratingUnit subclass.</summary>
    private static readonly HashSet<string> CarriedTypes = new(StringComparer.Ordinal)
    {
        "", "wind-onshore", "wind-offshore", "wind", "solar", "hydro", "pumped-storage", "nuclear", "thermal", "gas", "coal", "lignite", "oil",
    };

    /// <summary>Write <paramref name="model"/> as CGMES. Throws <see cref="InvalidDataException"/> listing every
    /// reason when it cannot be written exactly.</summary>
    public static CgmesWriteResult Write(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var refused = new List<string>();
        var notes = new List<string>();
        double baseMva = model.BaseMva;

        void Devices(int count, string what, IEnumerable<string?> names, string why)
        {
            if (count == 0) return;
            string[] shown = names.Where(n => !string.IsNullOrEmpty(n)).Take(5).Select(n => n!).ToArray();
            refused.Add($"{count} {what}{(shown.Length > 0 ? $" (first: {string.Join(", ", shown)})" : "")}: {why}");
        }
        const string notRebuilt = "GridSim's CIM reader does not rebuild them into the model the power flow solves, so the written case would solve differently.";
        Devices(model.Oltcs.Count, "on-load tap changer(s)", model.Oltcs.Select(o => o.Transformer),
            "CGMES holds a regulating RatioTapChanger, but GridSim's CIM reader makes it a CimControlledPowerFlow control, not the tap-changer loop the model solves with, so the written case would solve differently.");
        Devices(model.TransformerFlowControls.Count, "transformer flow control(s)", model.TransformerFlowControls.Select(f => f.Transformer), notRebuilt);
        Devices(model.LccLinks.Count, "two-terminal LCC HVDC link(s)", model.LccLinks.Select(l => l.Name), notRebuilt);
        Devices(model.VscLinks.Count, "VSC HVDC link(s)", model.VscLinks.Select(l => l.Name), notRebuilt);
        Devices(model.MtdcLinks.Count, "multi-terminal DC link(s)", model.MtdcLinks.Select(l => l.Name), notRebuilt);
        Devices(model.FactsDevices.Count, "FACTS device(s)", model.FactsDevices.Select(d => d.Name), notRebuilt);
        Devices(model.StaticVarCompensators.Count, "static var compensator(s)", model.StaticVarCompensators.Select(c => c.Name),
            "GridSim's CIM reader holds a CGMES StaticVarCompensator at its q in a plain solve, not as the model's voltage-regulating device, so the written case would solve differently.");
        Devices(model.InductionMachines.Count, "induction machine(s)", model.InductionMachines.Select(m => m.Name),
            "GridSim's CIM reader reads an AsynchronousMachine as a fixed P / Q load, not as the model's slip-dependent machine, so the written case would solve differently.");
        List<Bus> zip = model.Buses.Where(b => b.Zip is { VariesWithVoltage: true }).ToList();
        if (zip.Count > 0)
            refused.Add($"{zip.Count} bus(es) with a voltage-dependent (ZIP) load (first: {string.Join(", ", zip.Take(5).Select(b => b.Id))}): an EnergyConsumer's load response is not read by GridSim's CIM reader.");
        List<Bus> noBase = model.Buses.Where(b => !(b.BaseKv > 0)).ToList();
        if (noBase.Count > 0)
            refused.Add($"{noBase.Count} bus(es) with no base voltage (first: {string.Join(", ", noBase.Take(5).Select(b => b.Id))}): CGMES holds impedances in ohms and set-points in kV, which need one.");

        var index = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) index[model.Buses[i].Id] = i;
        string Node(int busId) => "N" + busId.ToString(Inv);
        double Kv(int busId) => model.Buses[index[busId]].BaseKv;

        // Every value is found here: the double the reader's arithmetic converts back to the target's bits, searched
        // outward from the plain conversion. The conversion (a product or quotient by a base that is not a power of two) is
        // not onto the doubles, so some targets have no such double at all: then the candidate whose read-back is nearest
        // is written (the plain conversion on a tie), the miss is counted by kind and the proof shows the difference.
        var inexact = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long worstUlps = 0;
        string? firstInexact = null;
        double Exact(double target, double guess, Func<double, double> back, string kind, string what)
        {
            if (Same(back(guess), target)) return guess;
            double up = guess, down = guess, best = guess;
            double bestErr = Math.Abs(back(guess) - target);
            for (int k = 0; k < 64; k++)
            {
                up = Math.BitIncrement(up);
                if (Same(back(up), target)) return up;
                down = Math.BitDecrement(down);
                if (Same(back(down), target)) return down;
                double eu = Math.Abs(back(up) - target), ed = Math.Abs(back(down) - target);
                if (eu < bestErr) { best = up; bestErr = eu; }
                if (ed < bestErr) { best = down; bestErr = ed; }
            }
            inexact[kind] = inexact.GetValueOrDefault(kind) + 1;
            worstUlps = Math.Max(worstUlps, Math.Abs(BitConverter.DoubleToInt64Bits(back(best)) - BitConverter.DoubleToInt64Bits(target)));
            firstInexact ??= $"{what} {target.ToString("R", Inv)} reads back as {back(best).ToString("R", Inv)}";
            return best;
        }

        // ---- generators: which buses hold their voltage, and with which set-point -----------------------------------
        var inService = model.Generators;
        var genAt = inService.GroupBy(g => g.Bus).ToDictionary(g => g.Key, g => g.ToList());
        // Bug #56: each kind is one line, counted, with the first five buses named (gb-full has 199 mixed-set-point buses,
        // which used to be 199 lines).
        var slackNoGen = new List<int>();
        var mixedVg = new List<string>();
        foreach (Bus b in model.Buses)
        {
            if (b.Type == BusType.Slack && !genAt.ContainsKey(b.Id)) slackNoGen.Add(b.Id);
            if (b.Type is BusType.Slack or BusType.Pv && genAt.TryGetValue(b.Id, out List<Generator>? gs)
                && gs.Any(g => !Same(g.Vg, gs[0].Vg)))
                mixedVg.Add($"{b.Id} ({string.Join(", ", gs.Select(g => g.Vg.ToString("R", Inv)).Distinct())} pu)");
        }
        if (slackNoGen.Count > 0)
            refused.Add($"{slackNoGen.Count} slack bus(es) with no in-service generator (first: {string.Join(", ", slackNoGen.Take(5))}): GridSim's CIM reader chooses the angle reference among the machines.");
        if (mixedVg.Count > 0)
            refused.Add($"{mixedVg.Count} voltage-controlled bus(es) whose machines hold different voltage set-points (first: {string.Join(", ", mixedVg.Take(5))}): one bus has one voltage.");
        if (refused.Count > 0) throw Refused(refused);

        // ---- buses, loads, shunts -------------------------------------------------------------------------------
        var buses = new List<CimBus>(model.Buses.Count);
        var loads = new List<CimLoad>();
        var shunts = new List<CimShunt>();
        foreach (Bus b in model.Buses)
        {
            double kv = b.BaseKv;
            double? v = null, a = null;
            if (!(Same(b.Vm, 1.0) && Same(b.VaDeg, 0.0)))
            {
                if (!(b.Vm > 0)) refused.Add($"bus {b.Id}'s stored voltage {b.Vm.ToString("R", Inv)} pu is not a starting voltage CGMES holds (SvVoltage.v must be above 0).");
                else v = Exact(b.Vm, b.Vm * kv, x => x / kv, "starting voltage", $"bus {b.Id}'s stored voltage");
                a = b.VaDeg;
            }
            buses.Add(new CimBus(Node(b.Id), b.Name ?? b.Id.ToString(Inv), kv, v, a));
            if (b.Pd != 0 || b.Qd != 0)
                loads.Add(new CimLoad("D" + b.Id.ToString(Inv), $"load {b.Id}", Node(b.Id), b.Pd, b.Qd));
            if (b.Gs != 0 || b.Bs != 0)
            {
                double scale = kv * kv;
                double g = Exact(b.Gs, b.Gs / scale, x => 0.0 + x * scale, "shunt Gs", $"bus {b.Id}'s shunt Gs");
                double bb = Exact(b.Bs, b.Bs / scale, x => 0.0 + x * scale, "shunt Bs", $"bus {b.Id}'s shunt Bs");
                shunts.Add(new CimShunt("S" + b.Id.ToString(Inv), $"shunt {b.Id}", Node(b.Id), g, bb, "LinearShuntCompensator"));
            }
        }

        // ---- generators -----------------------------------------------------------------------------------------
        var gens = new List<CimGenerator>(inService.Count);
        int typesLost = 0, converterFlags = 0, faultData = 0, unboundedPmin = 0;
        for (int k = 0; k < inService.Count; k++)
        {
            Generator g = inService[k];
            Bus b = model.Buses[index[g.Bus]];
            double kv = b.BaseKv;
            bool holds = b.Type is BusType.Slack or BusType.Pv;
            double? target = holds ? Exact(g.Vg, g.Vg * kv, x => x / kv, "voltage set-point", $"generator {g.Name ?? k.ToString(Inv)}'s voltage set-point") : null;
            if (!CarriedTypes.Contains(g.Type)) typesLost++;
            if (g.ConverterInterfaced is not null || g.ConverterFaultCurrentPu is not null || g.GridForming || g.PowerFlowOnly) converterFlags++;
            if (g.SubtransientXdPu != 0 || g.NegativeSeqXPu != 0 || g.UnitTransformerXPu != 0 || g.ZeroSequenceZPu is not null
                || g.DynamicsClass is not null) faultData++;
            if (double.IsNegativeInfinity(g.Pmin)) unboundedPmin++;
            if (double.IsNaN(g.Qmin) || double.IsNaN(g.Qmax) || double.IsPositiveInfinity(g.Qmin) || double.IsNegativeInfinity(g.Qmax))
                refused.Add($"generator {g.Name ?? k.ToString(Inv)} has reactive limits {g.Qmin.ToString("R", Inv)}..{g.Qmax.ToString("R", Inv)} MVAr, which CGMES cannot hold.");
            gens.Add(new CimGenerator("G" + (k + 1).ToString(Inv), g.Name ?? $"generator {k + 1}", Node(g.Bus), g.Pg, g.Qg,
                TargetKv: target, VoltageControl: holds,
                MinQ: double.IsNegativeInfinity(g.Qmin) ? null : g.Qmin,
                MaxQ: double.IsPositiveInfinity(g.Qmax) ? null : g.Qmax,
                MinP: double.IsFinite(g.Pmin) ? g.Pmin : null,
                MaxP: double.IsFinite(g.Pmax) ? g.Pmax : null,
                RatedSMva: g.MBase, H: g.H != 0 ? g.H : null,
                ReferencePriority: b.Type == BusType.Slack ? 1 : 0,
                Type: CarriedTypes.Contains(g.Type) ? g.Type : ""));
        }

        // ---- branches: lines, then transformers -------------------------------------------------------------------
        var lines = new List<CimBranch>();
        var transformers = new List<CimTransformer>();
        var lineOrder = new List<int>();
        var txOrder = new List<int>();
        int ratingsBc = 0, zeroSeq = 0, asTransformer = 0, outBranches = 0;
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService) { outBranches++; continue; }
            if (br.RateBMva != 0 || br.RateCMva != 0) ratingsBc++;
            if (br.R0 != 0 || br.X0 != 0 || br.FromWinding is not null || br.ToWinding is not null || br.FromNeutralXPu != 0 || br.ToNeutralXPu != 0) zeroSeq++;
            double kvF = Kv(br.From), kvT = Kv(br.To);
            double tap = br.TapRatio <= 0 ? 1.0 : br.TapRatio;
            string name = br.Name ?? $"{br.From}-{br.To}";
            string who = $"branch {k + 1} ({name})";
            double? rate = br.RateAMva > 0 ? br.RateAMva : null;
            if (!br.IsTransformer && tap == 1.0 && br.PhaseShiftDeg == 0 && kvF == kvT)
            {
                double zb = kvF * kvF / baseMva;
                double r = Exact(br.R, br.R * zb, x => x / zb, "R", who + " R");
                double xx = Exact(br.X, br.X * zb, x => x / zb, "X", who + " X");
                double bb = Exact(br.B, br.B / zb, x => x * zb, "B", who + " B");
                lines.Add(new CimBranch("L" + (k + 1).ToString(Inv), name, Node(br.From), Node(br.To), r, xx, bb, RateAMva: rate));
                lineOrder.Add(k);
                continue;
            }
            if (!br.IsTransformer) asTransformer++;
            double zb1 = kvF * kvF / baseMva, zb2 = kvT * kvT / baseMva;
            double r1 = Exact(br.R, br.R * zb1, x => x / zb1 + 0.0 / zb2, "R", who + " R");
            double x1 = Exact(br.X, br.X * zb1, x => x / zb1 + 0.0 / zb2, "X", who + " X");
            double b1 = br.B == 0.0 ? 0.0
                : Exact(br.B, br.B * baseMva / (kvF * kvF), x => (x * kvF * kvF + 0.0 * kvT * kvT) / baseMva, "B", who + " B");
            var ends = new[]
            {
                new CimTransformerEnd(1, Node(br.From), r1, x1, kvF, B: b1),
                new CimTransformerEnd(2, Node(br.To), 0.0, 0.0, kvT),
            };
            transformers.Add(new CimTransformer("T" + (k + 1).ToString(Inv), name, ends, tap, br.PhaseShiftDeg, rate));
            txOrder.Add(k);
        }
        if (refused.Count > 0) throw Refused(refused);

        // ---- what the document does not carry (none of it read by the power flow) -----------------------------------
        if (inexact.Count > 0)
            notes.Add($"{inexact.Values.Sum()} value(s) have no CGMES form that GridSim's reader converts back to the same bits "
                + $"({string.Join(", ", inexact.Select(kv => $"{kv.Key} {kv.Value}"))}; the first: {firstInexact}): ohms, siemens and kV "
                + "convert to per-unit by a product or quotient that does not reach every double. Each is written as the nearest "
                + $"one (at most {worstUlps} ulp off after read-back), so the proof below shows the difference.");
        notes.Add($"CGMES carries no system base: read back on {baseMva.ToString("R", Inv)} MVA; buses are numbered 1..{model.Buses.Count} in model order on read-back (TopologicalNode N<id> keeps the original number in its mRID).");
        if (model.OutOfServiceGenerators.Count > 0) notes.Add($"{model.OutOfServiceGenerators.Count} out-of-service generator(s) not written (GridSim's CIM reader leaves out-of-service equipment out).");
        if (outBranches > 0) notes.Add($"{outBranches} out-of-service branch(es) not written.");
        if (asTransformer > 0) notes.Add($"{asTransformer} line(s) with a ratio, a phase shift or ends on two base voltages written as two-winding PowerTransformers (an ACLineSegment has no ratio).");
        if (typesLost > 0) notes.Add($"{typesLost} generator type(s) outside the CGMES GeneratingUnit classes written as a plain GeneratingUnit (type not carried).");
        if (converterFlags > 0) notes.Add($"{converterFlags} generator(s) with converter, grid-forming or power-flow-only flags: not carried.");
        if (faultData > 0) notes.Add($"{faultData} generator(s) with fault data (X''d, X2, unit transformer, zero sequence) or a dynamics class: not carried.");
        if (unboundedPmin > 0) notes.Add($"{unboundedPmin} generator(s) with an unbounded Pmin: read back as 0 MW (not read by the power flow).");
        if (inService.Any(g => g.Cost is not null || g.ReactiveCost is not null)) notes.Add("generator cost curves: not carried.");
        if (ratingsBc > 0) notes.Add($"{ratingsBc} branch(es) with a rate B or C: only rate A is written (as the permanent limit).");
        if (zeroSeq > 0 || model.ZeroSequenceMutuals.Count > 0) notes.Add($"fault sequence data ({zeroSeq} branch(es), {model.ZeroSequenceMutuals.Count} mutual coupling(s)): not carried (no SC profile written).");
        if (model.Buses.Any(b => b.VminPu is not null || b.VmaxPu is not null)) notes.Add("per-bus voltage limits: not carried.");
        if (model.Buses.Any(b => b.X != 0 || b.Y != 0)) notes.Add("layout coordinates: not carried (no DL profile written).");
        if (model.Boundaries.Count > 0) notes.Add($"{model.Boundaries.Count} boundary(ies): not carried.");
        if (model.AssetDynamics is not null || model.Protections is not null) notes.Add("the asset-dynamics and protection bundles: not carried.");

        var net = new CimNetwork(buses, lines, loads, gens, transformers, ShuntCompensators: shunts);
        string text = CimExporter.Export(net);
        return new CgmesWriteResult(text, net, notes, lineOrder.Concat(txOrder).ToList());
    }

    /// <summary>
    /// <paramref name="model"/> as the read-back of <paramref name="written"/> holds it: buses numbered 1..n in model
    /// order, the in-service generators on those numbers, the in-service branches in <see cref="CgmesWriteResult.BranchOrder"/>
    /// (lines, then transformers). The same network; only the numbering and the order differ, and the branch order can
    /// change the last bits of a solve, so a read-back is compared with this.
    /// </summary>
    public static GridModel InFileOrder(GridModel model, CgmesWriteResult written)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);
        var id = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) id[model.Buses[i].Id] = i + 1;
        return new GridModel
        {
            BaseMva = model.BaseMva,
            Buses = model.Buses.Select((b, i) => b with { Id = i + 1 }).ToList(),
            Generators = model.Generators.Select(g => g with { Bus = id[g.Bus] }).ToList(),
            Branches = written.BranchOrder.Select(k => model.Branches[k] with { From = id[model.Branches[k].From], To = id[model.Branches[k].To] }).ToList(),
            FrequencyHz = model.FrequencyHz,
            Name = model.Name,
        };
    }

    /// <summary>The refusal: every reason, one line each.</summary>
    private static InvalidDataException Refused(IReadOnlyList<string> reasons) =>
        new("CGMES cannot hold this model exactly, so nothing was written:" + Environment.NewLine + "  - "
            + string.Join(Environment.NewLine + "  - ", reasons));

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
}
