using System.Globalization;
using System.Text;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>A machine of a DY document: its PSS/E bus and ID, its rated power (the MVA base of its PU values) and its
/// armature resistance Ra (pu on MBase; for a PSS/E case the RAW file's ZSORCE resistance ZR, 0 when not given).</summary>
public sealed record CgmesMachineKey(int Bus, string Id, double MBase, double Ra = 0.0);

/// <summary>What <see cref="CgmesDynamicsProfile.Read"/> made of a DY document: PSS/E DYR text (one record per line), its
/// records, and whether a record needs <see cref="PsseDyrOptions.ExtendedLibrary"/>.</summary>
public sealed record CgmesDynamicsImport(string DyrText, IReadOnlyList<PsseDyrRecord> Records, bool NeedsExtendedLibrary, IReadOnlyList<string> Notes)
{
    /// <summary>The <c>gridsim-dynamics/2</c> document of these records through the existing DYR reader (so every refusal of
    /// <see cref="PsseDyrReader.Read"/> applies as it is), with the extended library switched on when a record needs it.</summary>
    public PsseDyrResult ToDynamics(PsseParseResult raw, string name, PsseDyrOptions? options = null)
    {
        options ??= new PsseDyrOptions();
        return PsseDyrReader.Read(DyrText, raw, name, NeedsExtendedLibrary && !options.ExtendedLibrary ? options with { ExtendedLibrary = true } : options);
    }
}

/// <summary>
/// RC5 WP10: the CGMES dynamics (DY) profile through PSS/E DYR records. <b>Export</b> (<see cref="CgmesExtraProfiles.Write(GridModel, CgmesWriteResult, CgmesExtraOptions)"/>
/// with DY) writes each mapped DYR record as its IEC 61970-302 standard-model class, linked to its machine
/// (SynchronousMachineDynamics.SynchronousMachine to the bus-branch document's SynchronousMachine; the exciter and the
/// governor to the machine's dynamics object). <b>Import</b> (<see cref="Read"/>) turns each mapped class back into a DYR
/// record in PSS/E CON order; <see cref="CgmesDynamicsImport.ToDynamics"/> then runs the existing DYR reader, so no new
/// dynamics mapping exists and its refusals apply as they are.
/// </summary>
/// <remarks>
/// <para><b>Mapped</b> (attribute meaning from the CGMES 2.4.15 dynamics RDFS, DynamicsProfileRDFSAugmented-v2_4_15-16Feb2016;
/// CON order from the PSS/E model data sheets, as the DYR reader already reads it):</para>
/// <list type="bullet">
/// <item>GENROU = SynchronousMachineTimeConstantReactance, rotorType roundRotor, modelType subtransient: tpdo tppdo tpqo tppqo
/// inertia damping xDirectSync xQuadSync xDirectTrans xQuadTrans xDirectSubtrans (= xQuadSubtrans) statorLeakageReactance
/// saturationFactor saturationFactor120. Refused on import when xQuadSubtrans differs from xDirectSubtrans, when tc, ks
/// or the q-axis saturation factors are not 0, or when statorResistance is not the machine's Ra.</item>
/// <item>GENSAL = the same class with rotorType salientPole: tpdo tppdo tppqo inertia damping xDirectSync xQuadSync
/// xDirectTrans xDirectSubtrans statorLeakageReactance saturationFactor saturationFactor120.</item>
/// <item>GENCLS = SynchronousMachineSimplified: inertia damping (statorLeakageReactance must be 0; PSS/E takes the source
/// reactance from the RAW file). Extended library.</item>
/// <item>statorResistance (every machine) = the RAW file's ZSORCE resistance ZR. PSS/E's machine models take the armature
/// resistance from ZR, which is pu on MBASE. CgmesWriter writes ratedS = MBase, the base of the DY PU values, so ZR is
/// written unchanged. On import it must equal the machine's Ra (<see cref="CgmesMachineKey.Ra"/>), because the DYR record
/// has no field for it.</item>
/// <item>SEXS = ExcSEXS: tatb tb k te emin emax, with kc = tc = 0 and efdmin = emin, efdmax = emax (PSS/E's SEXS has no PI
/// block and no separate clipping).</item>
/// <item>SCRX = ExcSCRX: tatb tb k te emin emax cswitch (true = solid fed, CSWITCH 1) rcrfd. Extended library. The CIM
/// name ExcSCRX for SCRX is also given by the Colib model library.</item>
/// <item>TGOV1 = GovSteam0: r t1 vmax vmin t2 t3 dt, with mwbase the machine's rated power. The CIM name GovSteam0 for TGOV1
/// is also given by the Colib model library.</item>
/// </list>
/// <para><b>Refused by name</b>: every other class, among them GovHydro1 (its correspondence to HYGOV is not confirmed by a
/// second public source, and its hdam has no HYGOV value), the IEEE exciters, stabilisers and governors whose two-source
/// attribute check is not done in this package, user-defined and proprietary models, wind, PV, load and HVDC dynamics, and
/// a function block with enabled = false.</para>
/// </remarks>
public static class CgmesDynamicsProfile
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The DYR models the DY export writes, with the CGMES class each becomes.</summary>
    public static readonly IReadOnlyDictionary<string, string> MappedModels = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["GENROU"] = "SynchronousMachineTimeConstantReactance", ["GENSAL"] = "SynchronousMachineTimeConstantReactance",
        ["GENCLS"] = "SynchronousMachineSimplified", ["SEXS"] = "ExcSEXS", ["SCRX"] = "ExcSCRX", ["TGOV1"] = "GovSteam0",
    };

    private static readonly string[] GenrouAttrs = { "tpdo", "tppdo", "tpqo", "tppqo", "inertia", "damping", "xDirectSync", "xQuadSync", "xDirectTrans", "xQuadTrans", "xDirectSubtrans", "statorLeakageReactance", "saturationFactor", "saturationFactor120" };
    private static readonly string[] GensalAttrs = { "tpdo", "tppdo", "tppqo", "inertia", "damping", "xDirectSync", "xQuadSync", "xDirectTrans", "xDirectSubtrans", "statorLeakageReactance", "saturationFactor", "saturationFactor120" };
    private static readonly string[] SexsAttrs = { "tatb", "tb", "k", "te", "emin", "emax" };
    private static readonly string[] ScrxAttrs = { "tatb", "tb", "k", "te", "emin", "emax", "cswitch", "rcrfd" };
    private static readonly string[] Tgov1Attrs = { "r", "t1", "vmax", "vmin", "t2", "t3", "dt" };

    /// <summary>The class of a CGMES attribute (RotatingMachineDynamics holds the shared machine data).</summary>
    private static string Owner(string cls, string attr) => attr switch
    {
        "inertia" or "damping" or "statorLeakageReactance" or "statorResistance" or "saturationFactor" or "saturationFactor120" => "RotatingMachineDynamics",
        _ => cls,
    };

    private static readonly IReadOnlyDictionary<string, string> RefusedClasses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GovHydro1"] = "its correspondence to PSS/E HYGOV is not confirmed by a second public source, and its hdam (head available at dam) has no HYGOV value",
    };

    /// <summary>For a case read from a RAW file: the PSS/E bus and ID of each in-service generator, in
    /// <see cref="GridModel.Generators"/> order (the order CgmesWriter writes machines G1, G2, ...).</summary>
    public static IReadOnlyList<PsseMachineKey> KeysOf(PsseParseResult raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var keys = new List<PsseMachineKey>();
        IReadOnlyList<Generator> all = raw.Model.AllGenerators;
        for (int j = 0; j < all.Count && j < raw.MachineKeys.Count; j++)
            if (all[j].InService) keys.Add(raw.MachineKeys[j]);
        return keys;
    }

    /// <summary>For a case read from a RAW file: the ZSORCE resistance ZR (pu on MBASE) of each in-service generator,
    /// aligned to <see cref="KeysOf"/>. Empty when the reader did not record it.</summary>
    public static IReadOnlyList<double> ArmatureResistancesOf(PsseParseResult raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var ras = new List<double>();
        IReadOnlyList<Generator> all = raw.Model.AllGenerators;
        if (raw.MachineSourceResistances.Count != raw.MachineKeys.Count) return ras;
        for (int j = 0; j < all.Count && j < raw.MachineKeys.Count; j++)
            if (all[j].InService) ras.Add(raw.MachineSourceResistances[j]);
        return ras;
    }

    /// <summary>For a DY document of a CgmesWriter export: the machine mRID G<i>k</i> -> its PSS/E key, MBase and Ra
    /// (<paramref name="armatureResistances"/>, aligned to <paramref name="keys"/>; Ra = 0 for every machine when it is
    /// not given).</summary>
    public static IReadOnlyDictionary<string, CgmesMachineKey> MachinesOf(GridModel model, IReadOnlyList<PsseMachineKey> keys,
        IReadOnlyList<double>? armatureResistances = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count != model.Generators.Count)
            throw new ArgumentException($"{keys.Count} machine key(s) for {model.Generators.Count} in-service generator(s).", nameof(keys));
        if (armatureResistances is not null && armatureResistances.Count != keys.Count)
            throw new ArgumentException($"{armatureResistances.Count} armature resistance(s) for {keys.Count} machine(s).", nameof(armatureResistances));
        var d = new Dictionary<string, CgmesMachineKey>(StringComparer.Ordinal);
        for (int k = 0; k < keys.Count; k++)
            d["G" + (k + 1).ToString(Inv)] = new CgmesMachineKey(keys[k].Bus, keys[k].Id, model.Generators[k].MBase, armatureResistances?[k] ?? 0.0);
        return d;
    }

    internal static void WriteInto(ExtraDocument doc, GridModel model, IReadOnlyList<PsseDyrRecord>? records, IReadOnlyList<PsseMachineKey>? keys,
        IReadOnlyList<double>? armatureResistances, List<string> refused, List<CgmesOmission> omissions, List<string> notes)
    {
        if (records is null) { refused.Add("--profiles dy needs the dynamic data (--dyr <file.dyr>)."); return; }
        if (keys is null || keys.Count != model.Generators.Count)
        {
            refused.Add("--profiles dy needs the PSS/E bus and ID of every in-service machine (a case read from a RAW file).");
            return;
        }
        if (armatureResistances is null || armatureResistances.Count != keys.Count)
        {
            refused.Add("--profiles dy needs the armature resistance (the RAW file's ZSORCE resistance ZR) of every in-service machine.");
            return;
        }
        var mrid = new Dictionary<(int, string), int>();
        for (int k = 0; k < keys.Count; k++) mrid[(keys[k].Bus, keys[k].Id)] = k;
        var byMachine = new SortedDictionary<int, Dictionary<string, PsseDyrRecord>>();
        foreach (PsseDyrRecord r in records)
        {
            string at = $"{r.Model} for machine '{r.Id}' at bus {r.Bus} (line {r.Line})";
            if (!MappedModels.ContainsKey(r.Model))
            {
                refused.Add($"--profiles dy: {at}: not in the WP10 DY mapping (mapped: {string.Join(", ", MappedModels.Keys)}).");
                continue;
            }
            if (!mrid.TryGetValue((r.Bus, r.Id), out int k)) { refused.Add($"--profiles dy: {at}: the case has no in-service machine there."); continue; }
            string role = r.Model switch { "SEXS" or "SCRX" => "exciter", "TGOV1" => "governor", _ => "machine" };
            if (!byMachine.TryGetValue(k, out var roles)) byMachine[k] = roles = new(StringComparer.Ordinal);
            if (!roles.TryAdd(role, r)) refused.Add($"--profiles dy: {at}: the machine already has a {role} record (line {roles[role].Line}).");
            int need = r.Model switch { "GENROU" => 14, "GENSAL" => 12, "GENCLS" => 2, "SEXS" => 6, "SCRX" => 8, _ => 7 };
            if (r.Values.Count != need) refused.Add($"--profiles dy: {at}: needs {need} values, has {r.Values.Count}.");
            foreach (string v in r.Values)
                if (!double.TryParse(v, NumberStyles.Float, Inv, out double x) || !double.IsFinite(x))
                { refused.Add($"--profiles dy: {at}: the value '{v}' is not a finite number."); break; }
        }
        foreach (var kv in byMachine)
            if (!kv.Value.ContainsKey("machine"))
                refused.Add($"--profiles dy: machine '{keys[kv.Key].Id}' at bus {keys[kv.Key].Bus} has controller records but no machine record.");
        if (refused.Count > 0) return;

        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        int written = 0;
        foreach (var (k, roles) in byMachine)
        {
            string machine = "G" + (k + 1).ToString(Inv);
            PsseDyrRecord m = roles["machine"];
            double ra = armatureResistances[k];
            if (!double.IsFinite(ra) || ra < 0)
            {
                refused.Add($"--profiles dy: machine '{keys[k].Id}' at bus {keys[k].Bus}: its ZSORCE resistance ZR is {Num(ra)}; statorResistance must be a number of 0 or more.");
                continue;
            }
            string smd = doc.NewId(machine + "/machine");
            var props = new List<(string, string)> { ("IdentifiedObject.name", $"{m.Model} {keys[k].Bus} {keys[k].Id}"), ("DynamicsFunctionBlock.enabled", "true") };
            string cls;
            if (m.Model == "GENCLS")
            {
                cls = "SynchronousMachineSimplified";
                props.Add(("RotatingMachineDynamics.damping", Num(D(m.Values[1]))));
                props.Add(("RotatingMachineDynamics.inertia", Num(D(m.Values[0]))));
                props.Add(("RotatingMachineDynamics.statorLeakageReactance", "0"));
                props.Add(("RotatingMachineDynamics.statorResistance", Num(ra)));
            }
            else
            {
                cls = "SynchronousMachineTimeConstantReactance";
                string[] attrs = m.Model == "GENROU" ? GenrouAttrs : GensalAttrs;
                for (int i = 0; i < attrs.Length; i++) props.Add((Owner(cls, attrs[i]) + "." + attrs[i], Num(D(m.Values[i]))));
                double xpp = D(m.Values[Array.IndexOf(attrs, "xDirectSubtrans")]);
                props.Add((cls + ".xQuadSubtrans", Num(xpp)));
                props.Add(("RotatingMachineDynamics.statorResistance", Num(ra)));
                props.Add((cls + ".tc", "0"));
                props.Add((cls + ".ks", "0"));
                props.Add((cls + ".rotorType", doc.EnumOf("RotorKind." + (m.Model == "GENROU" ? "roundRotor" : "salientPole"))));
                props.Add((cls + ".modelType", doc.EnumOf("SynchronousMachineModelKind.subtransient")));
            }
            props.Add(("SynchronousMachineDynamics.SynchronousMachine", doc.RefObject(machine)));
            doc.New(cls, smd, props.ToArray());
            written++;
            if (roles.TryGetValue("exciter", out PsseDyrRecord? ex))
            {
                string ecls = ex.Model == "SEXS" ? "ExcSEXS" : "ExcSCRX";
                var ep = new List<(string, string)> { ("IdentifiedObject.name", $"{ex.Model} {keys[k].Bus} {keys[k].Id}"), ("DynamicsFunctionBlock.enabled", "true") };
                string[] attrs = ex.Model == "SEXS" ? SexsAttrs : ScrxAttrs;
                for (int i = 0; i < attrs.Length; i++)
                    ep.Add((ecls + "." + attrs[i], attrs[i] == "cswitch" ? CswitchText(D(ex.Values[i]), refused, ex) : Num(D(ex.Values[i]))));
                if (ex.Model == "SEXS")
                {
                    ep.Add(("ExcSEXS.kc", "0"));
                    ep.Add(("ExcSEXS.tc", "0"));
                    ep.Add(("ExcSEXS.efdmin", Num(D(ex.Values[4]))));
                    ep.Add(("ExcSEXS.efdmax", Num(D(ex.Values[5]))));
                }
                ep.Add(("ExcitationSystemDynamics.SynchronousMachineDynamics", ExtraDocument.Ref(smd)));
                doc.New(ecls, doc.NewId(machine + "/exciter"), ep.ToArray());
                written++;
            }
            if (roles.TryGetValue("governor", out PsseDyrRecord? gov))
            {
                var gp = new List<(string, string)> { ("IdentifiedObject.name", $"{gov.Model} {keys[k].Bus} {keys[k].Id}"), ("DynamicsFunctionBlock.enabled", "true"),
                    ("GovSteam0.mwbase", Num(model.Generators[k].MBase)) };
                for (int i = 0; i < Tgov1Attrs.Length; i++) gp.Add(("GovSteam0." + Tgov1Attrs[i], Num(D(gov.Values[i]))));
                gp.Add(("TurbineGovernorDynamics.SynchronousMachineDynamics", ExtraDocument.Ref(smd)));
                doc.New("GovSteam0", doc.NewId(machine + "/governor"), gp.ToArray());
                written++;
            }
        }
        if (refused.Count > 0) return;
        omissions.Add(new CgmesOmission(CgmesProfileKind.DY, "every DYR model outside GENROU, GENSAL, GENCLS, SEXS, SCRX and TGOV1",
            "refused by name: their CGMES attribute mapping is not checked against two public sources in this release."));
        notes.Add($"DY: {written} dynamic model(s) of {byMachine.Count} machine(s) written.");
    }

    private static string CswitchText(double v, List<string> refused, PsseDyrRecord r)
    {
        if (v == 1.0) return "true";
        if (v == 0.0) return "false";
        refused.Add($"--profiles dy: {r.Model} for machine '{r.Id}' at bus {r.Bus}: CSWITCH {Num(v)} is not 0 (bus fed) or 1 (solid fed).");
        return "false";
    }

    private static string Num(double v) => CgmesExtraProfiles.Num(v);

    /// <summary>
    /// Turn the DY documents among <paramref name="documents"/> into DYR records. <paramref name="machines"/> maps a
    /// SynchronousMachine mRID to its PSS/E bus, ID and MBase (<see cref="MachinesOf"/> for a CgmesWriter export). Records
    /// are written machine by machine in document order (machine, exciter, governor), one per line, with numbers in
    /// round-trip form. Throws <see cref="InvalidDataException"/> listing every refusal.
    /// </summary>
    public static CgmesDynamicsImport Read(IEnumerable<string> documents, IReadOnlyDictionary<string, CgmesMachineKey> machines)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(machines);
        CgmesObjectSet set = CgmesObjectSet.Parse(documents);
        var refused = new List<string>();
        var notes = new List<string>();
        var dynLinks = new[] { "SynchronousMachineDynamics.SynchronousMachine", "ExcitationSystemDynamics.SynchronousMachineDynamics",
            "TurbineGovernorDynamics.SynchronousMachineDynamics", "PowerSystemStabilizerDynamics.ExcitationSystemDynamics" };
        var mappedClasses = new HashSet<string>(StringComparer.Ordinal) { "SynchronousMachineTimeConstantReactance", "SynchronousMachineSimplified", "ExcSEXS", "ExcSCRX", "GovSteam0" };

        // Every dynamics object (a function block linked to a machine, an exciter or another block, or any class the
        // profile defines that is not mapped) is classified; an unmapped one is refused by name.
        var blocks = set.Objects.Where(o => o.Defined && (dynLinks.Any(o.Has) || o.Has("DynamicsFunctionBlock.enabled"))).ToList();
        foreach (CgmesObject o in blocks)
        {
            string cls = o.Class ?? "?";
            if (mappedClasses.Contains(cls))
            {
                if (o.Text("DynamicsFunctionBlock.enabled") == "false") refused.Add($"{cls} '{o.Mrid}': enabled = false (a disabled block has no PSS/E form).");
                continue;
            }
            string why = RefusedClasses.TryGetValue(cls, out string? r) ? r
                : cls.Contains("UserDefined", StringComparison.Ordinal) || cls.Contains("Proprietary", StringComparison.Ordinal) ? "user-defined and proprietary models are not read"
                : "not in the WP10 DY mapping (mapped: SynchronousMachineTimeConstantReactance, SynchronousMachineSimplified, ExcSEXS, ExcSCRX, GovSteam0)";
            refused.Add($"{cls} '{o.Mrid}': {why}.");
        }

        var lines = new List<string>();
        var records = new List<PsseDyrRecord>();
        bool extended = false;
        double Need(CgmesObject o, string prop)
        {
            double? v = CgmesObjectSet.Double(o, prop);
            if (v is null) { refused.Add($"{o.Class} '{o.Mrid}': {prop} is missing."); return 0; }
            return v.Value;
        }
        void Zero(CgmesObject o, string prop, string why)
        {
            if (CgmesObjectSet.Double(o, prop) is double v && v != 0) refused.Add($"{o.Class} '{o.Mrid}': {prop} = {Num(v)}; {why}.");
        }
        void SameRa(CgmesObject o, CgmesMachineKey key, string model)
        {
            double ra = CgmesObjectSet.Double(o, "RotatingMachineDynamics.statorResistance") ?? 0.0;
            if (!CgmesExtraProfiles.Same(ra, key.Ra))
                refused.Add($"{o.Class} '{o.Mrid}': RotatingMachineDynamics.statorResistance = {Num(ra)} differs from the machine's armature resistance {Num(key.Ra)} (the RAW file's ZR); PSS/E {model} takes it from the RAW file.");
        }
        void Emit(CgmesMachineKey key, string model, IReadOnlyList<double> values)
        {
            var sb = new StringBuilder();
            sb.Append(key.Bus.ToString(Inv)).Append(" '").Append(model).Append("' '").Append(key.Id).Append('\'');
            foreach (double v in values) sb.Append(' ').Append(Num(v));
            sb.Append(" /");
            lines.Add(sb.ToString());
            records.Add(new PsseDyrRecord(key.Bus, model, key.Id, values.Select(Num).ToList(), lines.Count));
        }

        foreach (CgmesObject smd in set.Objects.Where(o => o.Defined && o.Has("SynchronousMachineDynamics.SynchronousMachine")))
        {
            if (!mappedClasses.Contains(smd.Class ?? "")) continue;
            string sm = smd.Ref("SynchronousMachineDynamics.SynchronousMachine")!;
            if (!machines.TryGetValue(sm, out CgmesMachineKey? key)) { refused.Add($"{smd.Class} '{smd.Mrid}': its SynchronousMachine '{sm}' is not a machine of the case."); continue; }
            int before = refused.Count;
            string model;
            var vals = new List<double>();
            if (smd.Class == "SynchronousMachineSimplified")
            {
                model = "GENCLS";
                extended = true;
                vals.Add(Need(smd, "RotatingMachineDynamics.inertia"));
                vals.Add(CgmesObjectSet.Double(smd, "RotatingMachineDynamics.damping") ?? 0.0);
                Zero(smd, "RotatingMachineDynamics.statorLeakageReactance", "PSS/E GENCLS takes its source reactance from the RAW file");
                SameRa(smd, key, "GENCLS");
            }
            else
            {
                const string c = "SynchronousMachineTimeConstantReactance";
                string rotor = smd.Ref(c + ".rotorType") is string rt ? CgmesObjectSet.EnumLeaf(rt) : "";
                string mt = smd.Ref(c + ".modelType") is string mtr ? CgmesObjectSet.EnumLeaf(mtr) : "";
                if (mt != "subtransient") refused.Add($"{c} '{smd.Mrid}': modelType {(mt.Length == 0 ? "missing" : mt)}; only subtransient (GENROU, GENSAL) is mapped.");
                if (rotor is not ("roundRotor" or "salientPole")) refused.Add($"{c} '{smd.Mrid}': rotorType {(rotor.Length == 0 ? "missing" : rotor)} is not roundRotor or salientPole.");
                model = rotor == "salientPole" ? "GENSAL" : "GENROU";
                string[] attrs = model == "GENROU" ? GenrouAttrs : GensalAttrs;
                foreach (string a in attrs) vals.Add(Need(smd, Owner(c, a) + "." + a));
                double xd2 = CgmesObjectSet.Double(smd, c + ".xDirectSubtrans") ?? 0, xq2 = CgmesObjectSet.Double(smd, c + ".xQuadSubtrans") ?? xd2;
                if (!CgmesExtraProfiles.Same(xd2, xq2)) refused.Add($"{c} '{smd.Mrid}': xQuadSubtrans {Num(xq2)} differs from xDirectSubtrans {Num(xd2)}; PSS/E {model} has one subtransient reactance.");
                Zero(smd, c + ".tc", $"PSS/E {model} has no Canay time constant");
                Zero(smd, c + ".ks", $"PSS/E {model} has no saturation loading correction");
                SameRa(smd, key, model);
                Zero(smd, "SynchronousMachineDetailed.saturationFactorQAxis", $"PSS/E {model} has no separate q-axis saturation");
                Zero(smd, "SynchronousMachineDetailed.saturationFactor120QAxis", $"PSS/E {model} has no separate q-axis saturation");
            }
            if (refused.Count > before) continue;
            Emit(key, model, vals);

            foreach (CgmesObject ex in set.Objects.Where(o => o.Defined && o.Ref("ExcitationSystemDynamics.SynchronousMachineDynamics") == smd.Mrid && mappedClasses.Contains(o.Class ?? "")))
            {
                int b0 = refused.Count;
                var ev = new List<double>();
                if (ex.Class == "ExcSEXS")
                {
                    foreach (string a in SexsAttrs) ev.Add(Need(ex, "ExcSEXS." + a));
                    Zero(ex, "ExcSEXS.kc", "PSS/E SEXS has no PI block");
                    Zero(ex, "ExcSEXS.tc", "PSS/E SEXS has no PI block");
                    if (refused.Count == b0)
                    {
                        if (CgmesObjectSet.Double(ex, "ExcSEXS.efdmin") is double fmin && !CgmesExtraProfiles.Same(fmin, ev[4])) refused.Add($"ExcSEXS '{ex.Mrid}': efdmin {Num(fmin)} differs from emin {Num(ev[4])}; PSS/E SEXS has one field limit pair.");
                        if (CgmesObjectSet.Double(ex, "ExcSEXS.efdmax") is double fmax && !CgmesExtraProfiles.Same(fmax, ev[5])) refused.Add($"ExcSEXS '{ex.Mrid}': efdmax {Num(fmax)} differs from emax {Num(ev[5])}; PSS/E SEXS has one field limit pair.");
                    }
                    if (refused.Count == b0) Emit(key, "SEXS", ev);
                }
                else if (ex.Class == "ExcSCRX")
                {
                    foreach (string a in ScrxAttrs)
                    {
                        if (a == "cswitch")
                        {
                            string? t = ex.Text("ExcSCRX.cswitch");
                            if (t is not ("true" or "false")) refused.Add($"ExcSCRX '{ex.Mrid}': cswitch is '{t}', not true or false.");
                            ev.Add(t == "true" ? 1.0 : 0.0);
                        }
                        else ev.Add(Need(ex, "ExcSCRX." + a));
                    }
                    extended = true;
                    if (refused.Count == b0) Emit(key, "SCRX", ev);
                }
                else refused.Add($"{ex.Class} '{ex.Mrid}': an exciter class that is not ExcSEXS or ExcSCRX.");
            }
            foreach (CgmesObject gov in set.Objects.Where(o => o.Defined && o.Ref("TurbineGovernorDynamics.SynchronousMachineDynamics") == smd.Mrid && mappedClasses.Contains(o.Class ?? "")))
            {
                if (gov.Class != "GovSteam0") { refused.Add($"{gov.Class} '{gov.Mrid}': a governor class that is not GovSteam0."); continue; }
                int b0 = refused.Count;
                double mw = Need(gov, "GovSteam0.mwbase");
                if (refused.Count == b0 && !CgmesExtraProfiles.Same(mw, key.MBase))
                    refused.Add($"GovSteam0 '{gov.Mrid}': mwbase {Num(mw)} MW differs from the machine's rated {Num(key.MBase)} MVA; PSS/E TGOV1 is on the machine base.");
                var gv = Tgov1Attrs.Select(a => Need(gov, "GovSteam0." + a)).ToList();
                if (refused.Count == b0) Emit(key, "TGOV1", gv);
            }
        }
        if (refused.Count > 0)
            throw new InvalidDataException($"The CGMES DY data cannot be read: {refused.Count} item(s) GridSim does not map:" + Environment.NewLine + "  - "
                + string.Join(Environment.NewLine + "  - ", refused));
        notes.Add($"DY: {records.Count} DYR record(s) from the DY documents.");
        return new CgmesDynamicsImport(string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""), records, extended, notes);
    }
}
