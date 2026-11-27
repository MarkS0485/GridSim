using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>Options of <see cref="CgmesShortCircuitProfile.Apply"/>.</summary>
/// <param name="AllowUnmapped">True: a value GridSim's model has no place for (ACLineSegment.b0ch / g0ch, a transformer
/// end's rground, a machine's r2) is left out with a note instead of refused.</param>
public sealed record CgmesShortCircuitOptions(bool AllowUnmapped = false);

/// <summary>What <see cref="CgmesShortCircuitProfile.Apply"/> made: a new model holding the sequence data, and the notes.</summary>
public sealed record CgmesShortCircuitResult(GridModel Model, IReadOnlyList<string> Notes);

/// <summary>
/// RC5 WP10: the CGMES short-circuit data (CGMES 2.4.15: the EquipmentShortCircuit/3/1 part of the EQ profile; attribute
/// names and units from EquipmentProfileCoreShortCircuitRDFSAugmented-v2_4_15-4Jul2016). <b>Export</b>: a document of
/// rdf:about descriptions of the bus-branch document's objects:
/// <list type="bullet">
/// <item>ACLineSegment r0, x0 (ohm) = R0 x Zbase, X0 x Zbase with Zbase = kV^2 / MVA at the line's base voltage (the
/// CgmesWriter base), written only when the line carries zero-sequence data;</item>
/// <item>PowerTransformerEnd r0, x0 (ohm at the end's ratedU, on end 1 as CgmesWriter places the positive sequence),
/// connectionKind (D = Delta, Y = WyeUngrounded, Yn = WyeGrounded), TransformerEnd.grounded (true for Yn) and xground
/// (ohm) = the neutral reactance x Zbase of that end;</item>
/// <item>SynchronousMachine satDirectSubtransX = X''d, x2 = X2, r0 / x0 = the zero-sequence impedance (all PU on ratedS =
/// MBase in CGMES 2.4.15) and earthing (true when the machine has a zero-sequence path; GridSim holds any neutral
/// impedance inside it, so earthingStarPointR / X are 0).</item>
/// </list>
/// Each ohm value is the double the reader's arithmetic converts back to the model's bits (the CgmesWriter search); the
/// ones with none are counted in the notes. <b>Import</b> (<see cref="Apply"/>): the same attributes on a bridged model,
/// returning a NEW model (the default import and bridge are unchanged).</summary>
/// <remarks>Not carried: mutual couplings, earth-fault compensators, HVDC and FACTS sequence data, the generator step-up
/// reactance, IEC 60909 voltage factors and ExternalNetworkInjection short-circuit data. CGMES 3.0 holds machine x0 / x2 /
/// r0 / r2 in ohms (Reactance) where 2.4.15 holds PU. The CGMES 3.0 export (WP24, through
/// <see cref="CgmesBusBranch30Writer"/>) writes them in ohms on Zbase = ratedU^2 / ratedS; <see cref="Apply"/> reads 2.4.15 PU
/// values only.</remarks>
public static class CgmesShortCircuitProfile
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static void WriteInto(ExtraDocument doc, GridModel model, CgmesWriteResult written, List<string> refused, List<CgmesOmission> omissions, List<string> notes)
    {
        double baseMva = model.BaseMva;
        var index = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) index[model.Buses[i].Id] = i;
        double Kv(int bus) => model.Buses[index[bus]].BaseKv;
        int inexact = 0, lines = 0, transformers = 0, machines = 0, windingKinds30 = 0;
        var lineMrids = written.Network.Branches.Select(b => b.MRID).ToHashSet(StringComparer.Ordinal);

        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService) continue;
            string mrid = "L" + (k + 1).ToString(Inv);
            bool asLine = lineMrids.Contains(mrid);
            bool hasZ0 = br.R0 != 0 || br.X0 != 0;
            if (asLine)
            {
                if (br.FromWinding is not null || br.ToWinding is not null || br.FromNeutralXPu != 0 || br.ToNeutralXPu != 0)
                {
                    refused.Add($"--profiles sc: branch {k + 1} is written as an ACLineSegment but carries winding connections or a neutral reactance; an ACLineSegment has no winding.");
                    continue;
                }
                if (!hasZ0) continue;
                double kv = Kv(br.From), zb = kv * kv / baseMva;
                double r0 = CgmesExtraProfiles.Exact(br.R0, br.R0 * zb, x => x / zb, ref inexact);
                double x0 = CgmesExtraProfiles.Exact(br.X0, br.X0 * zb, x => x / zb, ref inexact);
                doc.About("ACLineSegment", mrid, ("ACLineSegment.r0", CgmesExtraProfiles.Num(r0)), ("ACLineSegment.x0", CgmesExtraProfiles.Num(x0)));
                lines++;
                continue;
            }
            string tx = "T" + (k + 1).ToString(Inv);
            if (!hasZ0 && br.FromWinding is null && br.ToWinding is null && br.FromNeutralXPu == 0 && br.ToNeutralXPu == 0) continue;
            double kvF = Kv(br.From), kvT = Kv(br.To), zb1 = kvF * kvF / baseMva, zb2 = kvT * kvT / baseMva;
            for (int end = 1; end <= 2; end++)
            {
                WindingConnection? w = end == 1 ? br.FromWinding : br.ToWinding;
                double xn = end == 1 ? br.FromNeutralXPu : br.ToNeutralXPu;
                double zb = end == 1 ? zb1 : zb2;
                if (xn != 0 && w != WindingConnection.WyeGrounded)
                {
                    refused.Add($"--profiles sc: branch {k + 1} winding {end} has a neutral reactance but is not a grounded wye.");
                    continue;
                }
                var props = new List<(string, string)>();
                if (end == 1 && hasZ0)
                {
                    props.Add(("PowerTransformerEnd.r0", CgmesExtraProfiles.Num(CgmesExtraProfiles.Exact(br.R0, br.R0 * zb1, x => x / zb1 + 0.0 / zb2, ref inexact))));
                    props.Add(("PowerTransformerEnd.x0", CgmesExtraProfiles.Num(CgmesExtraProfiles.Exact(br.X0, br.X0 * zb1, x => x / zb1 + 0.0 / zb2, ref inexact))));
                }
                else if (hasZ0) { props.Add(("PowerTransformerEnd.r0", "0")); props.Add(("PowerTransformerEnd.x0", "0")); }
                if (w is WindingConnection c)
                {
                    // CGMES 3.0 moved PowerTransformerEnd.connectionKind to the Equipment profile (the 3.0 Equipment RDFS
                    // holds it, the ShortCircuit RDFS does not), so the 3.0 SC document leaves it out and lists the omission;
                    // grounded, rground and xground stay in SC. The 2.4.15 document writes it as before.
                    if (doc.Version == CgmesVersion.V3_0) windingKinds30++;
                    else props.Add(("PowerTransformerEnd.connectionKind", doc.EnumOf("WindingConnection." + (c switch { WindingConnection.Delta => "D", WindingConnection.WyeUngrounded => "Y", _ => "Yn" }))));
                    props.Add(("TransformerEnd.grounded", c == WindingConnection.WyeGrounded ? "true" : "false"));
                    if (c == WindingConnection.WyeGrounded)
                    {
                        props.Add(("TransformerEnd.rground", "0"));
                        props.Add(("TransformerEnd.xground", CgmesExtraProfiles.Num(CgmesExtraProfiles.Exact(xn, xn * zb, x => x / zb, ref inexact))));
                    }
                }
                if (props.Count > 0) doc.About("PowerTransformerEnd", tx + "_e" + end.ToString(Inv), props.ToArray());
            }
            transformers++;
        }

        int stepUp = 0;
        // WP24: CGMES 3.0 holds SynchronousMachine r0 / x0 / r2 / x2 in ohms (ShortCircuit-EU RDFS of entsoe/CGMES-AP: Resistance
        // and Reactance; satDirectSubtransX stays PU). The base is the machine's rating: Zbase = ratedU^2 / ratedS, with ratedU the
        // bus base voltage (written as RotatingMachine.ratedU in the 3.0 EQ) and ratedS = MBase (RotatingMachine.ratedS). The
        // 2.4.15 path writes PU exactly as before.
        bool ohms = doc.Version == CgmesVersion.V3_0;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator g = model.Generators[k];
            if (g.UnitTransformerXPu != 0) stepUp++;
            var props = new List<(string, string)>();
            double zm = 0.0;
            if (ohms)
            {
                double kvm = Kv(g.Bus);
                zm = kvm * kvm / g.MBase;
                if (!(g.MBase > 0) && (g.NegativeSeqXPu != 0 || g.ZeroSequenceZPu is not null))
                {
                    refused.Add($"--profiles sc (CGMES 3.0): generator {k + 1} has no positive MBase, so its PU sequence impedances have no ohm form.");
                    continue;
                }
            }
            string M(double pu)
            {
                if (!ohms) return CgmesExtraProfiles.Num(pu);
                double z = zm;
                return CgmesExtraProfiles.Num(CgmesExtraProfiles.Exact(pu, pu * z, x => x / z, ref inexact));
            }
            if (g.SubtransientXdPu != 0) props.Add(("SynchronousMachine.satDirectSubtransX", CgmesExtraProfiles.Num(g.SubtransientXdPu)));
            if (g.NegativeSeqXPu != 0) { props.Add(("SynchronousMachine.r2", "0")); props.Add(("SynchronousMachine.x2", M(g.NegativeSeqXPu))); }
            if (g.ZeroSequenceZPu is MachineZeroSequence z0)
            {
                props.Add(("SynchronousMachine.r0", M(z0.RPu)));
                props.Add(("SynchronousMachine.x0", M(z0.XPu)));
                props.Add(("SynchronousMachine.earthing", "true"));
                props.Add(("SynchronousMachine.earthingStarPointR", "0"));
                props.Add(("SynchronousMachine.earthingStarPointX", "0"));
            }
            else if (props.Count > 0) props.Add(("SynchronousMachine.earthing", "false"));
            if (props.Count == 0) continue;
            doc.About("SynchronousMachine", "G" + (k + 1).ToString(Inv), props.ToArray());
            machines++;
        }
        if (inexact > 0)
            notes.Add($"SC: {inexact} value(s) have no ohm form that the reader converts back to the same bits; each is written as the nearest double.");
        notes.Add($"SC: zero-sequence data of {lines} line(s) and {transformers} transformer(s), and fault data of {machines} machine(s) written.");
        if (windingKinds30 > 0) omissions.Add(new CgmesOmission(CgmesProfileKind.SC, "PowerTransformerEnd.connectionKind",
            $"{windingKinds30} transformer end(s) carry a winding connection; CGMES 3.0 holds connectionKind in the Equipment profile, not in SC, and the 3.0 bus-branch EQ does not write it. TransformerEnd.grounded, rground and xground are written."));
        if (stepUp > 0) omissions.Add(new CgmesOmission(CgmesProfileKind.SC, "generator step-up reactance", $"{stepUp} generator(s) carry one; CGMES models a step-up transformer as a PowerTransformer, which the bus-branch export does not add."));
        if (model.ZeroSequenceMutuals.Count > 0) omissions.Add(new CgmesOmission(CgmesProfileKind.SC, "MutualCoupling", $"{model.ZeroSequenceMutuals.Count} zero-sequence mutual coupling(s) are not written."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.SC, "ACLineSegment b0ch / g0ch, earthing impedances as separate values, IEC 60909 voltage factors",
            "GridSim holds no zero-sequence charging; a machine's neutral impedance is held inside its zero-sequence impedance."));
    }

    /// <summary>
    /// The model of <paramref name="bridge"/> with the short-circuit data of <paramref name="documents"/> (the SC document
    /// and the EQ document that defines the transformer ends) on its branches and machines: a new model; the bridge's is
    /// not changed. Refused by name (an <see cref="InvalidDataException"/> listing every reason): connection kinds other
    /// than D, Y and Yn; a Yn end that is not grounded or a Y / D end that is; a three-winding transformer's data; an end
    /// whose ratedU differs from its bus base (the impedance would be referred); a machine earthed through
    /// earthingStarPointR / X; and, unless <see cref="CgmesShortCircuitOptions.AllowUnmapped"/>, non-zero b0ch, g0ch, rground
    /// and r2.
    /// </summary>
    public static CgmesShortCircuitResult Apply(CimBridgeResult bridge, IEnumerable<string> documents, CgmesShortCircuitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(documents);
        options ??= new CgmesShortCircuitOptions();
        CgmesObjectSet set = CgmesObjectSet.Parse(documents);
        GridModel m = bridge.Model;
        double baseMva = m.BaseMva;
        var refused = new List<string>();
        var notes = new List<string>();
        var branches = m.Branches.ToList();
        var gens = m.Generators.ToList();
        double BusKv(int id) => m.Buses.First(b => b.Id == id).BaseKv;
        void Unmapped(string what)
        {
            if (options.AllowUnmapped) notes.Add(what + ": not held by GridSim's model, left out (AllowUnmapped).");
            else refused.Add(what + ": GridSim's model has no place for it.");
        }

        int lines = 0;
        foreach (CimBranch line in bridge.Network.Branches)
        {
            CgmesObject? o = set.Get(line.MRID);
            if (o is null || !bridge.BranchIndexOfMrid.TryGetValue(line.MRID, out int k)) continue;
            double? r0 = CgmesObjectSet.Double(o, "ACLineSegment.r0"), x0 = CgmesObjectSet.Double(o, "ACLineSegment.x0");
            if (CgmesObjectSet.Double(o, "ACLineSegment.b0ch") is double b0 && b0 != 0) Unmapped($"ACLineSegment '{line.MRID}' b0ch {b0.ToString("R", Inv)} S");
            if (CgmesObjectSet.Double(o, "ACLineSegment.g0ch") is double g0 && g0 != 0) Unmapped($"ACLineSegment '{line.MRID}' g0ch {g0.ToString("R", Inv)} S");
            if (r0 is null && x0 is null) continue;
            Branch br = branches[k];
            double kvF = BusKv(br.From), kvT = BusKv(br.To);
            double kv = br.TapRatio != 1.0 ? kvT : kvF > 0 ? kvF : kvT;
            if (!(kv > 0)) { refused.Add($"ACLineSegment '{line.MRID}': no base voltage, so its ohm values cannot be put in per-unit."); continue; }
            double zb = kv * kv / baseMva;
            branches[k] = br with { R0 = (r0 ?? 0.0) / zb, X0 = (x0 ?? 0.0) / zb };
            lines++;
        }

        int transformers = 0;
        var endsOf = new Dictionary<string, List<CgmesObject>>(StringComparer.Ordinal);
        foreach (CgmesObject e in set.OfClass("PowerTransformerEnd"))
            if (e.Ref("PowerTransformerEnd.PowerTransformer") is string t)
            {
                if (!endsOf.TryGetValue(t, out var l)) endsOf[t] = l = new();
                l.Add(e);
            }
        foreach (CimTransformer tx in bridge.Network.Transformers)
        {
            if (!endsOf.TryGetValue(tx.MRID, out var ends)) continue;
            bool any = ends.Any(e => e.Has("PowerTransformerEnd.r0") || e.Has("PowerTransformerEnd.x0") || e.Has("PowerTransformerEnd.connectionKind")
                || e.Has("TransformerEnd.xground") || e.Has("TransformerEnd.grounded") || e.Has("TransformerEnd.rground"));
            if (!any) continue;
            if (tx.Ends.Count != 2) { refused.Add($"PowerTransformer '{tx.MRID}': short-circuit data of a {tx.Ends.Count}-winding transformer is not read."); continue; }
            int k = bridge.BranchIndexOfMrid[tx.MRID];
            Branch br = branches[k];
            double r0 = 0, x0 = 0;
            bool hasZ0 = false;
            WindingConnection? w1 = null, w2 = null;
            double xn1 = 0, xn2 = 0;
            foreach (CgmesObject e in ends)
            {
                int end = int.TryParse(e.Text("TransformerEnd.endNumber"), NumberStyles.Integer, Inv, out int n) ? n : 0;
                if (end is not (1 or 2)) { refused.Add($"PowerTransformerEnd '{e.Mrid}': endNumber is not 1 or 2."); continue; }
                int bus = end == 1 ? br.From : br.To;
                double ratedU = CgmesObjectSet.Double(e, "PowerTransformerEnd.ratedU") ?? BusKv(bus);
                if (!CgmesExtraProfiles.Same(ratedU, BusKv(bus)))
                {
                    refused.Add($"PowerTransformerEnd '{e.Mrid}': ratedU {ratedU.ToString("R", Inv)} kV differs from its bus's base {BusKv(bus).ToString("R", Inv)} kV, so its zero-sequence impedance would need the referral the positive sequence gets; not read.");
                    continue;
                }
                double zb = ratedU * ratedU / baseMva;
                if (CgmesObjectSet.Double(e, "PowerTransformerEnd.r0") is double r) { r0 += r / zb; hasZ0 = true; }
                if (CgmesObjectSet.Double(e, "PowerTransformerEnd.x0") is double x) { x0 += x / zb; hasZ0 = true; }
                if (CgmesObjectSet.Double(e, "TransformerEnd.rground") is double rg && rg != 0) Unmapped($"PowerTransformerEnd '{e.Mrid}' rground {rg.ToString("R", Inv)} ohm");
                bool grounded = e.Text("TransformerEnd.grounded") == "true";
                double xg = CgmesObjectSet.Double(e, "TransformerEnd.xground") ?? 0.0;
                WindingConnection? wc = null;
                if (e.Ref("PowerTransformerEnd.connectionKind") is string kind)
                {
                    string leaf = CgmesObjectSet.EnumLeaf(kind);
                    wc = leaf switch { "D" => WindingConnection.Delta, "Y" => WindingConnection.WyeUngrounded, "Yn" => WindingConnection.WyeGrounded, _ => null };
                    if (wc is null) { refused.Add($"PowerTransformerEnd '{e.Mrid}': connectionKind {leaf} is not D, Y or Yn, the windings GridSim's zero-sequence network holds."); continue; }
                    if ((wc == WindingConnection.WyeGrounded) != grounded)
                    {
                        refused.Add($"PowerTransformerEnd '{e.Mrid}': connectionKind {leaf} with grounded {(grounded ? "true" : "false")}; GridSim holds a Yn winding as grounded and D / Y as not.");
                        continue;
                    }
                }
                else if (grounded || xg != 0) { refused.Add($"PowerTransformerEnd '{e.Mrid}': a neutral grounding without a connectionKind."); continue; }
                if (xg != 0 && wc != WindingConnection.WyeGrounded) { refused.Add($"PowerTransformerEnd '{e.Mrid}': xground on a winding that is not Yn."); continue; }
                double xn = xg / zb;
                if (end == 1) { w1 = wc; xn1 = xn; } else { w2 = wc; xn2 = xn; }
            }
            branches[k] = br with
            {
                R0 = hasZ0 ? r0 : br.R0, X0 = hasZ0 ? x0 : br.X0,
                FromWinding = w1 ?? br.FromWinding, ToWinding = w2 ?? br.ToWinding,
                FromNeutralXPu = w1 is null ? br.FromNeutralXPu : xn1, ToNeutralXPu = w2 is null ? br.ToNeutralXPu : xn2,
            };
            transformers++;
        }

        int machines = 0;
        for (int k = 0; k < bridge.Network.Generators.Count && k < gens.Count; k++)
        {
            CgmesObject? o = set.Get(bridge.Network.Generators[k].MRID);
            if (o is null) continue;
            double? xpp = CgmesObjectSet.Double(o, "SynchronousMachine.satDirectSubtransX");
            double? x2 = CgmesObjectSet.Double(o, "SynchronousMachine.x2");
            double? r2 = CgmesObjectSet.Double(o, "SynchronousMachine.r2");
            double? r0 = CgmesObjectSet.Double(o, "SynchronousMachine.r0"), x0 = CgmesObjectSet.Double(o, "SynchronousMachine.x0");
            string? earthing = o.Text("SynchronousMachine.earthing");
            if (xpp is null && x2 is null && r2 is null && r0 is null && x0 is null && earthing is null) continue;
            if (r2 is double rr && rr != 0) Unmapped($"SynchronousMachine '{o.Mrid}' r2 {rr.ToString("R", Inv)} pu");
            Generator g = gens[k];
            MachineZeroSequence? z0 = g.ZeroSequenceZPu;
            if (earthing == "true")
            {
                double er = CgmesObjectSet.Double(o, "SynchronousMachine.earthingStarPointR") ?? 0.0, ex = CgmesObjectSet.Double(o, "SynchronousMachine.earthingStarPointX") ?? 0.0;
                if (er != 0 || ex != 0)
                {
                    refused.Add($"SynchronousMachine '{o.Mrid}': earthed through a star-point impedance ({er.ToString("R", Inv)} + j{ex.ToString("R", Inv)} ohm); GridSim holds the neutral inside the zero-sequence impedance, and folding it in needs the machine's rated voltage, so it is not read.");
                    continue;
                }
                z0 = new MachineZeroSequence(r0 ?? 0.0, x0 ?? 0.0);
            }
            else if (earthing == "false")
            {
                z0 = null;
                if (r0 is not null || x0 is not null) notes.Add($"SynchronousMachine '{o.Mrid}': r0 / x0 given for an unearthed machine; it has no zero-sequence path, so they are not used.");
            }
            gens[k] = g with { SubtransientXdPu = xpp ?? g.SubtransientXdPu, NegativeSeqXPu = x2 ?? g.NegativeSeqXPu, ZeroSequenceZPu = z0 };
            machines++;
        }
        if (set.OfClass("MutualCoupling").Any()) notes.Add($"{set.OfClass("MutualCoupling").Count()} MutualCoupling(s): not read.");
        if (refused.Count > 0)
            throw new InvalidDataException("The CGMES short-circuit data cannot be applied, so nothing was changed:" + Environment.NewLine + "  - "
                + string.Join(Environment.NewLine + "  - ", refused));
        notes.Add($"SC: zero-sequence data of {lines} line(s) and {transformers} transformer(s), and fault data of {machines} machine(s) read.");
        return new CgmesShortCircuitResult(m.CopyWith(branches: branches, generators: gens), notes);
    }
}
