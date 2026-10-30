using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>One record of a PSS/E DYR file as written: bus, model name, machine ID and the values after them.</summary>
/// <param name="Bus">IBUS.</param>
/// <param name="Model">The model name (for example GENROU), upper case.</param>
/// <param name="Id">The machine ID, trimmed.</param>
/// <param name="Values">The fields after the ID: integer parameters (ICONs) first where the model has them, then CONs.</param>
/// <param name="Line">The 1-based line the record starts on.</param>
public sealed record PsseDyrRecord(int Bus, string Model, string Id, IReadOnlyList<string> Values, int Line);

/// <summary>How <see cref="PsseDyrReader"/> treats data GridSim cannot honour exactly.</summary>
public sealed record PsseDyrOptions
{
    /// <summary>
    /// False (the default): a record of a model this reader does not map, a value GridSim's model has no place for
    /// (a non-zero transducer lag, a cross-compound shaft, a PSS filter, the HYGOV turbine gain, a renewable value out of range, ...)
    /// or a controller with no machine model refuses the file, listing every one. True: each is left out and listed in
    /// the notes, so the rest of the data can be used.
    /// </summary>
    public bool AllowUnmodelled { get; init; }

    /// <summary>The file name the provenance notes name (the DYR file's name). Null: "the DYR file".</summary>
    public string? SourceName { get; init; }
}

/// <summary>The machine dynamic data a DYR file gives, as a <c>gridsim-dynamics/2</c> document, with the notes.</summary>
/// <param name="Json">The <c>gridsim-dynamics/2</c> document (UTF-8 JSON text) that <see cref="Json.MachineDynamicsLoader"/>
/// loads against the RAW case.</param>
/// <param name="Notes">What was derived, approximated or left out.</param>
/// <param name="Records">Every record of the file, in file order.</param>
/// <param name="Machines">How many machines the document binds.</param>
public sealed record PsseDyrResult(string Json, IReadOnlyList<string> Notes, IReadOnlyList<PsseDyrRecord> Records, int Machines)
{
    /// <summary>
    /// WECC (expansion): the converter units the DYR gives a WECC second-generation model chain (REGC_A with REEC_A,
    /// REEC_B or REEC_C), keyed by the unit's index in <c>model.Generators</c> (the in-service units, the index the RMS
    /// study's per-generator data uses). Empty when the file has none. These are not part of <see cref="Json"/> (the
    /// <c>gridsim-dynamics/2</c> document describes synchronous machines); the CLI carries them to the RMS study.
    /// </summary>
    public IReadOnlyDictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> Renewables { get; init; } =
        new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>();
}

/// <summary>
/// Reads a PSS/E dynamic data (DYR) file and maps its records onto GridSim's machine dynamic data, written as a
/// <c>gridsim-dynamics/2</c> document bound to the case read from the matching RAW file (Tranche A, October 2026).
/// </summary>
/// <remarks>
/// <para><b>Records.</b> Free format: <c>IBUS 'MODEL' ID values... /</c>, over as many lines as needed, ended by a slash
/// (the rest of that line is ignored). Fields are separated by blanks or commas; <c>@!</c> heading lines are skipped.</para>
/// <para><b>What maps.</b> Machines: GENROU and GENSAL to the subtransient machine. Exciters: SEXS, IEEET1, EXAC1 and ESST4B (ESAC1A only from a gridsim-dynamics/2 file: its PSS/E CON order is not in public text). Stabiliser:
/// PSS1A. Governors: TGOV1, IEEEG1, HYGOV and GAST. Each value goes to the GridSim parameter of the same meaning; where GridSim
/// needs a value PSS/E does not give, or gives in another form, the derivation is exact and tagged DerivedFromRecorded
/// with a note (SEXS lead time = TA/TB x TB; IEEEG1 droop = 1 / K; GENSAL, which has no q-axis transient circuit,
/// X'q = Xq so that circuit carries nothing, and T'qo = T''qo, which then has no effect). Every other value is tagged
/// Recorded, naming the file and line.</para>
/// <para><b>Renewables</b> (WECC expansion): the WECC second-generation models REGC_A with REEC_A, REEC_B or REEC_C
/// (PSS/E REGCA1, REECA1, REECB1, REECCU1, their V33 user-model names, or the WECC names; see
/// <see cref="PsseDyrRenewables"/>) map onto a <see cref="GridSim.Core.Dynamics.Models.Wecc.WeccPlant"/> per unit, returned
/// in <see cref="PsseDyrResult.Renewables"/> for the RMS study (not in the JSON document). Every value is range-checked; a
/// unit with both a machine record and a renewable chain, an electrical control without its REGC_A, or an exciter,
/// stabiliser or governor on a renewable unit is refused.</para>
/// <para><b>What does not map is refused</b> unless <see cref="PsseDyrOptions.AllowUnmodelled"/> is set, and every case is
/// listed: models this reader does not know (the message lists each with its count and first line), renewable data with
/// a problem (above), and values a GridSim model has no
/// block for: IEEET1 with TR, SWITCH or KE = 0 (PSS/E then computes KE) or exciter saturation (PSS/E's curve is not
/// GridSim's exponential one); PSS1A with an input other than speed, a remote bus, the A1 / A2 filter, a washout with T5
/// not equal to T6, or the VCU / VCL cut-offs; IEEEG1 driving a second shaft (JBUS); HYGOV with a filter Tf, a gate
/// rate limit VELM, a turbine gain At other than 1, turbine damping or no-load flow; GENSAL saturation (PSS/E saturates
/// its d axis only); and a controller on a unit with no machine record. A record for a machine the RAW case does not
/// hold, or a second record of the same kind for one unit, is always refused.</para>
/// </remarks>
public static class PsseDyrReader
{
    /// <summary>The models this reader maps, by role.</summary>
    public static readonly IReadOnlyDictionary<string, string> MappedModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GENROU"] = "machine", ["GENSAL"] = "machine", ["SEXS"] = "exciter", ["IEEET1"] = "exciter", ["EXAC1"] = "exciter", ["ESST4B"] = "exciter", ["PSS1A"] = "pss",
        ["TGOV1"] = "governor", ["IEEEG1"] = "governor", ["HYGOV"] = "governor", ["GAST"] = "governor",
    };

    /// <summary>The number of values (ICONs then CONs) each mapped model's record holds after the machine ID.</summary>
    private static readonly IReadOnlyDictionary<string, int> ValueCount = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GENROU"] = 14, ["GENSAL"] = 12, ["SEXS"] = 6, ["IEEET1"] = 14, ["PSS1A"] = 15, ["TGOV1"] = 7, ["IEEEG1"] = 22,
        ["HYGOV"] = 12, ["GAST"] = 9, ["EXAC1"] = 17, ["ESST4B"] = 17,
    };

    /// <summary>Split DYR text into records. A record not ended by a slash before the end of the text, or one too short
    /// to hold a bus, a model and an ID, is refused naming its line.</summary>
    public static IReadOnlyList<PsseDyrRecord> ReadRecords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = PsseText.Lines(text);
        var records = new List<PsseDyrRecord>();
        var fields = new List<string?>();
        int start = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (PsseText.IsHeading(line)) continue;
            (List<string?> f, string? comment) = PsseText.Split(line, i + 1, "DYR");
            if (fields.Count == 0 && f.Count > 0) start = i + 1;
            fields.AddRange(f);
            if (comment is null) continue;   // no slash yet: the record goes on
            if (fields.Count == 0) continue;  // a line that is only a comment
            var r = new PsseRecord(fields.ToList(), start, "DYR", null);
            if (fields.Count < 3) throw r.Error($"a record needs IBUS, the model name and the machine ID; this one has {fields.Count} field(s)");
            int bus = r.Int(0, "IBUS");
            string model = (fields[1] ?? "").Trim().ToUpperInvariant();
            if (model.Length == 0) throw r.Error("the model name is empty");
            records.Add(new PsseDyrRecord(bus, model, (fields[2] ?? "").Trim(), fields.Skip(3).Select(x => x ?? "").ToList(), start));
            fields.Clear();
        }
        if (fields.Count > 0) throw new InvalidDataException($"PSS/E DYR line {start} (DYR data): the record is not ended by a slash before the end of the file.");
        return records;
    }

    /// <summary>Map the DYR text onto the machines of <paramref name="raw"/> (the case read from the matching RAW file).</summary>
    public static PsseDyrResult Read(string text, PsseParseResult raw, string name, PsseDyrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(name);
        options ??= new PsseDyrOptions();
        string source = options.SourceName ?? "the DYR file";
        IReadOnlyList<PsseDyrRecord> records = ReadRecords(text);
        var notes = new List<string>();
        var issues = new List<string>();

        // The case's units: the RAW record key of each generator, and its 0-based index among the in-service units of its bus.
        GridModel model = raw.Model;
        if (raw.MachineKeys.Count != model.AllGenerators.Count)
            throw new ArgumentException("the RAW parse result's machine keys do not line up with its model's generators.", nameof(raw));
        var unitOf = new Dictionary<(int, string), (Generator Gen, int Unit)>();
        var perBus = new Dictionary<int, int>();
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            Generator g = model.AllGenerators[k];
            int unit = -1;
            if (g.InService) { unit = perBus.TryGetValue(g.Bus, out int c) ? c : 0; perBus[g.Bus] = unit + 1; }
            unitOf[(raw.MachineKeys[k].Bus, raw.MachineKeys[k].Id)] = (g, unit);
        }

        // Group the records by unit and role; refuse duplicates and machines the case does not hold.
        var byUnit = new Dictionary<(int, string), Dictionary<string, PsseDyrRecord>>();
        var unknown = new SortedDictionary<string, (int Count, int First)>(StringComparer.Ordinal);
        int outOfService = 0;
        foreach (PsseDyrRecord r in records)
        {
            // WECC (expansion): the renewable models (REGC_A, REEC_A/B/C, under their PSS/E, V33 user-model or WECC names, or
            // as a USRMDL record) take their role from PsseDyrRenewables; their value counts are checked there.
            string? wecc = PsseDyrRenewables.ModelOf(r);
            string refusedKey = r.Model == "USRMDL" && r.Values.Count > 0 ? r.Values[0].Trim().Trim('\'').ToUpperInvariant() : r.Model;
            if (PsseDyrRenewables.RefusedOrder.TryGetValue(refusedKey, out string? modelled))
            {
                issues.Add($"{refusedKey} for machine '{r.Id}' at bus {r.Bus} (line {r.Line}): GridSim models {modelled}, but the order of the PSS/E {refusedKey} record "
                    + $"is not in any public source, so it is not read with a guessed order; write the unit as a '{modelled}' record "
                    + (modelled switch
                    {
                        "REGC_B" => "with the 11 values of Table 1 of the WECC REMWG memo Rev. 27b (2/16/23), in that order",
                        "REEC_D" => "with the six flags PfFlag VFlag QFlag PFlag PqFlag Vcmpflag and then the 78 values of Table 5 of the EPRI REGC_B/REEC_D test protocol (8/17/2020), in that order",
                        "REGFM_A1" => "with the 22 data values of Table 1 of PNNL-35110 (September 2023), in that order, without the three set points the power flow initialises",
                        _ => "with the 48 values of the WECC DER_A specification's Table 1, in that order",
                    }));
                continue;
            }
            if (!MappedModels.TryGetValue(r.Model, out string? role) && wecc is null)
            {
                string key = r.Model == "USRMDL" && r.Values.Count > 0 ? $"USRMDL {r.Values[0].Trim()}" : r.Model;
                unknown.TryGetValue(key, out var u);
                unknown[key] = (u.Count + 1, u.First == 0 ? r.Line : u.First);
                continue;
            }
            if (wecc is not null) role = PsseDyrRenewables.Roles[wecc];
            if (role is null) throw new InvalidOperationException("internal error: a mapped record has no role");
            if (!unitOf.TryGetValue((r.Bus, r.Id), out var unit))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} names machine '{r.Id}' at bus {r.Bus}, which the RAW case does not hold.");
            // Each model's record has a fixed number of values: fewer, or more (two records run together because a slash is
            // missing), is refused rather than read short or long.
            if (ValueCount.TryGetValue(r.Model, out int need) && r.Values.Count != need)
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} needs exactly {need} values after the machine ID; the record has {r.Values.Count}.");
            if (wecc is not null) PsseDyrRenewables.ValuesOf(r, wecc);   // the same count rule for the renewable records
            if (unit.Unit < 0) { outOfService++; continue; }
            if (!byUnit.TryGetValue((r.Bus, r.Id), out var roles)) byUnit[(r.Bus, r.Id)] = roles = new Dictionary<string, PsseDyrRecord>();
            if (roles.TryGetValue(role, out PsseDyrRecord? prior))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: machine '{r.Id}' at bus {r.Bus} already has a {role} record ({prior.Model}, line {prior.Line}); a unit takes one of each.");
            roles[role] = r;
        }
        if (unknown.Count > 0)
            issues.Add("models this reader does not map: " + string.Join(", ", unknown.Select(kv => $"{kv.Key} x{kv.Value.Count} (first at line {kv.Value.First})")));
        if (outOfService > 0) notes.Add($"{outOfService} record(s) for out-of-service machines left out (the RMS study does not integrate them).");

        // Build the document.
        using var ms = new MemoryStream();
        int machines = 0;
        var renewables = new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>();
        // Names and notes hold quotes (X'd): written as they are, not escaped, since the document is a data file, not HTML; LF
        // line ends so the same records give the same bytes on every OS.
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "gridsim-dynamics/2");
            w.WriteString("name", "PSS/E DYR " + name);
            w.WriteString("description", $"Machine dynamic data read from the PSS/E dynamic data file {source} for the case {model.Name}.");
            w.WriteStartArray("templates");
            var bindings = new List<(int Bus, int Unit, string Ref)>();
            foreach (Generator g in model.AllGenerators)
            {
                int k = IndexOf(model.AllGenerators, g);
                PsseMachineKey key = raw.MachineKeys[k];
                if (!byUnit.TryGetValue((key.Bus, key.Id), out var roles)) continue;
                string unitName = $"machine '{key.Id}' at bus {key.Bus}";
                string idBase = $"gridsim.psse.b{key.Bus.ToString(CultureInfo.InvariantCulture)}.m{Segment(key.Id)}";
                // WECC (expansion): a renewable model chain (REGC_A + REEC_A/B/C) for a converter unit.
                bool renewable = PsseDyrRenewables.AllRoles.Any(roles.ContainsKey);
                if (renewable)
                {
                    Renewable(roles, unitName, raw, g, source, renewables, notes, issues);
                    if (!roles.ContainsKey("machine")) continue;   // a renewable unit's controllers were judged there
                }
                if (!roles.TryGetValue("machine", out PsseDyrRecord? mrec))
                {
                    foreach (var kv in roles)
                        issues.Add($"{kv.Value.Model} for {unitName} (line {kv.Value.Line}) has no GENROU or GENSAL record for its unit; GridSim binds controllers to a machine model");
                    continue;
                }
                int before = issues.Count;
                var controllers = new List<(string Slot, string Ref)>();
                foreach (string slot in new[] { "exciter", "pss", "governor" })
                {
                    if (!roles.TryGetValue(slot, out PsseDyrRecord? crec)) continue;
                    var p = new List<(string Name, object Value)>();
                    var derived = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!Controller(crec, unitName, p, derived, issues)) continue;
                    if (slot == "pss" && !roles.ContainsKey("exciter"))
                    {
                        issues.Add($"PSS1A for {unitName} (line {crec.Line}) has no exciter record; the stabiliser's output joins the AVR summing junction");
                        continue;
                    }
                    string id = $"{idBase}.{slot}";
                    WriteTemplate(w, id, slot, $"{crec.Model}, {unitName}", p, derived, source, crec);
                    controllers.Add((slot, id + "@1"));
                }
                var mp = new List<(string Name, object Value)>();
                var mderived = new Dictionary<string, string>(StringComparer.Ordinal);
                Machine(mrec, unitName, mp, mderived, issues);
                if (issues.Count > before && !options.AllowUnmodelled)
                    continue;   // refused below; nothing more is written for this unit
                string mid = $"{idBase}.machine";
                WriteTemplate(w, mid, "synchronousMachine", $"{mrec.Model}, {unitName}", mp, mderived, source, mrec, controllers);
                bindings.Add((g.Bus, unitOf[(key.Bus, key.Id)].Unit, mid + "@1"));
                machines++;
            }
            w.WriteEndArray();
            w.WriteStartArray("machines");
            foreach ((int bus, int unit, string tref) in bindings)
            {
                w.WriteStartObject();
                w.WriteNumber("bus", bus);
                if ((perBus.TryGetValue(bus, out int n) ? n : 0) > 1) w.WriteNumber("unit", unit);
                w.WriteString("template", tref);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        // WECC (expansion): with unmodelled dynamics allowed a renewable chain that had a problem is left out (listed below);
        // the unit then runs on the case's own converter model.
        if (issues.Count > 0 && !options.AllowUnmodelled)
            throw new InvalidDataException($"PSS/E DYR {source}: {issues.Count} item(s) GridSim cannot represent exactly:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", issues) + Environment.NewLine +
                "Read it with unmodelled dynamics allowed (gridsim: --allow-unmodelled) to leave them out and use the rest.");
        foreach (string issue in issues) notes.Add("left out (unmodelled dynamics allowed): " + issue + ".");
        if (machines == 0 && renewables.Count == 0)
            throw new InvalidDataException($"PSS/E DYR {source}: no machine of the case has a GENROU or GENSAL record or a renewable model that GridSim can use, so there is no dynamic data.");
        return new PsseDyrResult(Encoding.UTF8.GetString(ms.ToArray()), notes, records, machines) { Renewables = renewables };
    }

    /// <summary>
    /// WECC (expansion): map one unit's renewable records onto a <see cref="GridSim.Core.Dynamics.Models.Wecc.WeccPlant"/>
    /// (every value range-checked by the parameter sets), or add the reasons it cannot be used to <paramref name="issues"/>.
    /// A unit takes a synchronous machine model or a renewable chain, not both; the chain is anchored on its REGC_A; a
    /// renewable unit has no exciter, stabiliser or governor.
    /// </summary>
    private static void Renewable(Dictionary<string, PsseDyrRecord> roles, string unitName, PsseParseResult raw, Generator g,
        string source, Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> renewables, List<string> notes, List<string> issues)
    {
        GridModel model = raw.Model;
        roles.TryGetValue(PsseDyrRenewables.ConverterRole, out PsseDyrRecord? conv);
        roles.TryGetValue(PsseDyrRenewables.ElectricalRole, out PsseDyrRecord? elec);
        roles.TryGetValue(PsseDyrRenewables.PlantRole, out PsseDyrRecord? plantRec);
        roles.TryGetValue(PsseDyrRenewables.DriveTrainRole, out PsseDyrRecord? driveRec);
        roles.TryGetValue(PsseDyrRenewables.AeroRole, out PsseDyrRecord? aeroRec);
        roles.TryGetValue(PsseDyrRenewables.PitchRole, out PsseDyrRecord? pitchRec);
        roles.TryGetValue(PsseDyrRenewables.TorqueRole, out PsseDyrRecord? torqueRec);
        roles.TryGetValue(PsseDyrRenewables.DerRole, out PsseDyrRecord? derRec);
        roles.TryGetValue(PsseDyrRenewables.GfmRole, out PsseDyrRecord? gfmRec);
        PsseDyrRecord first = gfmRec ?? derRec ?? conv ?? elec ?? plantRec ?? driveRec ?? aeroRec ?? pitchRec ?? torqueRec!;
        string firstName = PsseDyrRenewables.ModelOf(first)!;
        int before = issues.Count;
        bool hasMachine = roles.TryGetValue("machine", out PsseDyrRecord? mrec);
        if (hasMachine)
            issues.Add($"{firstName} for {unitName} (line {first.Line}): the unit also has a {mrec!.Model} machine record (line {mrec.Line}); "
                + "a unit takes either a synchronous machine model or a renewable converter model, not both");
        else
            foreach (string slot in new[] { "exciter", "pss", "governor" })
                if (roles.TryGetValue(slot, out PsseDyrRecord? c))
                    issues.Add($"{c.Model} for {unitName} (line {c.Line}): a renewable converter unit ({firstName}) has no field or shaft for a {slot}");
        if (gfmRec is not null)
        {
            // REGFM_A1 stands alone (a voltage source with its own controls).
            foreach (PsseDyrRecord? o in new[] { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec, derRec })
                if (o is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(o)} for {unitName} (line {o.Line}): the unit also has a REGFM_A1 record (line {gfmRec.Line}); REGFM_A1 stands alone");
            GridSim.Core.Dynamics.Models.Wecc.RegfmA1Parameters gp = PsseDyrRenewables.RegfmA1(gfmRec);
            foreach (string problem in gp.Check()) issues.Add($"{problem}, for {unitName} (line {gfmRec.Line})");
            if (issues.Count > before) return;
            renewables[IndexOf(model.Generators, g)] = GridSim.Core.Dynamics.Models.Wecc.WeccPlant.RegfmA1(gp,
                $"PSS/E DYR {source}, line(s) {gfmRec.Line.ToString(CultureInfo.InvariantCulture)}");
            return;
        }
        if (derRec is not null)
        {
            // DER_A stands alone.
            foreach (PsseDyrRecord? o in new[] { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec })
                if (o is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(o)} for {unitName} (line {o.Line}): the unit also has a DER_A record (line {derRec.Line}); DER_A stands alone");
            GridSim.Core.Dynamics.Models.Wecc.DeraParameters dp = PsseDyrRenewables.Dera(derRec);
            var der = GridSim.Core.Dynamics.Models.Wecc.WeccPlant.DerA(dp, $"PSS/E DYR {source}, line(s) {derRec.Line.ToString(CultureInfo.InvariantCulture)}");
            foreach (string problem in dp.Check()) issues.Add($"{problem}, for {unitName} (line {derRec.Line})");
            if (issues.Count > before) return;
            int kd = -1;
            for (int i = 0; i < model.Generators.Count; i++) if (ReferenceEquals(model.Generators[i], g)) { kd = i; break; }
            if (kd < 0) throw new InvalidOperationException("internal error: an in-service unit of the DYR is not among the model's generators");
            renewables[kd] = der;
            notes.Add($"DER_A for {unitName} (line {derRec.Line}): Xe = {dp.Xe.ToString("R", CultureInfo.InvariantCulture)} is the model's numerical network-interface "
                + "reactance; GridSim solves the current-source interface to its fixed point (1e-10 pu), so it has no effect on the answer");
            return;
        }
        if (conv is null)
        {
            issues.Add($"{firstName} for {unitName} (line {first.Line}) has no REGC_A record for its unit (nor REGC_B); the controls drive a generator/converter model");
            return;
        }
        bool isB = PsseDyrRenewables.ModelOf(conv) == "REGC_B";
        GridSim.Core.Dynamics.Models.Wecc.RegcaParameters? regc = isB ? null : PsseDyrRenewables.Regca(conv);
        GridSim.Core.Dynamics.Models.Wecc.RegcbParameters? regcb = isB ? PsseDyrRenewables.Regcb(conv) : null;
        string? elecName = elec is null ? null : PsseDyrRenewables.ModelOf(elec);
        GridSim.Core.Dynamics.Models.Wecc.ReecParameters? reec = elec is null || elecName == "REEC_D" ? null : PsseDyrRenewables.Reec(elec, elecName!);
        GridSim.Core.Dynamics.Models.Wecc.ReecdParameters? reecd = elecName == "REEC_D" ? PsseDyrRenewables.Reecd(elec!) : null;
        if (reecd is not null && reecd.Mvab != 0.0 && Math.Abs(reecd.Mvab - GridSim.Core.Dynamics.Models.MachineBase.EffectiveMva(g, model.BaseMva)) > 1e-9 * reecd.Mvab)
            issues.Add($"REEC_D for {unitName} (line {elec!.Line}): mvab = {reecd.Mvab.ToString("R", CultureInfo.InvariantCulture)} differs from the unit's MBASE "
                + $"{GridSim.Core.Dynamics.Models.MachineBase.EffectiveMva(g, model.BaseMva).ToString("R", CultureInfo.InvariantCulture)} MVA; GridSim works on MBASE, so mvab must be 0 or MBASE");
        GridSim.Core.Dynamics.Models.Wecc.RepcaParameters? repc = null;
        if (plantRec is not null)
        {
            repc = PsseDyrRenewables.Repca(plantRec);
            // The regulated bus and the flow branch must be the RAW case's (the branch by its buses and circuit identifier).
            if (repc.RegulatedBus != 0 && !model.Buses.Any(b => b.Id == repc.RegulatedBus))
                issues.Add($"REPC_A for {unitName} (line {plantRec.Line}): regulated bus {repc.RegulatedBus} is not a bus of the RAW case");
            if (repc.FlowFromBus != 0 && repc.FlowToBus != 0)
            {
                int bi = -1;
                for (int i = 0; i < raw.BranchKeys.Count; i++)
                {
                    PsseBranchKey bk = raw.BranchKeys[i];
                    bool ends = (bk.FromBus == repc.FlowFromBus && bk.ToBus == repc.FlowToBus) || (bk.FromBus == repc.FlowToBus && bk.ToBus == repc.FlowFromBus);
                    if (ends && bk.ThirdBus == 0 && string.Equals(bk.Circuit.Trim(), repc.FlowCircuit, StringComparison.OrdinalIgnoreCase)) { bi = i; break; }
                }
                if (bi < 0)
                    issues.Add($"REPC_A for {unitName} (line {plantRec.Line}): flow branch {repc.FlowFromBus}-{repc.FlowToBus} circuit '{repc.FlowCircuit}' is not an in-service two-terminal branch of the RAW case");
                else repc = repc with { FlowBranchIndex = bi };
            }
        }
        GridSim.Core.Dynamics.Models.Wecc.WindParameters? wind = null;
        if (driveRec is not null)
            wind = new GridSim.Core.Dynamics.Models.Wecc.WindParameters(PsseDyrRenewables.Wtgta(driveRec),
                aeroRec is null ? null : PsseDyrRenewables.Wtgara(aeroRec), pitchRec is null ? null : PsseDyrRenewables.Wtgpta(pitchRec),
                torqueRec is null ? null : PsseDyrRenewables.Wtgtrqa(torqueRec));
        else
            foreach (PsseDyrRecord? w in new[] { aeroRec, pitchRec, torqueRec })
                if (w is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(w)} for {unitName} (line {w.Line}) has no WTGT_A drive train record for its unit; the turbine models act on its speeds");
        PsseDyrRecord?[] all = { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec };
        string lines = string.Join(", ", all.Where(x => x is not null).Select(x => x!.Line.ToString(CultureInfo.InvariantCulture)));
        var plant = new GridSim.Core.Dynamics.Models.Wecc.WeccPlant(regc, reec, $"PSS/E DYR {source}, line(s) {lines}") { Repc = repc, Wind = wind, RegcB = regcb, ReecD = reecd };
        foreach (string problem in plant.Check())
        {
            // The record a problem belongs to, by the model name it starts with (the chain's own rules go to the REGC_A line).
            PsseDyrRecord? owner = all.FirstOrDefault(x => x is not null && problem.StartsWith(PsseDyrRenewables.ModelOf(x)! + " ", StringComparison.Ordinal));
            issues.Add($"{problem}, for {unitName} (line {(owner ?? conv).Line})");
        }
        if (issues.Count > before) return;
        int k = -1;
        for (int i = 0; i < model.Generators.Count; i++) if (ReferenceEquals(model.Generators[i], g)) { k = i; break; }
        if (k < 0) throw new InvalidOperationException("internal error: an in-service unit of the DYR is not among the model's generators");
        renewables[k] = plant;
        if (regc is not null)
        notes.Add($"{plant.Chain} for {unitName} (line(s) {lines}): REGC_A Accel = {regc.Accel.ToString("R", CultureInfo.InvariantCulture)} is a PSS/E "
            + "network-solution acceleration factor; GridSim solves the converter / network fixed point to 1e-10 pu, so it has no effect on the answer");
    }

    private static int IndexOf(IReadOnlyList<Generator> list, Generator g)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], g)) return i;
        throw new InvalidOperationException("generator not in the fleet");
    }

    /// <summary>A machine ID as a template id segment: letters, digits, '_' and '-' kept, anything else as _xx (hex).</summary>
    private static string Segment(string id)
    {
        var sb = new StringBuilder();
        foreach (char c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
            else sb.Append('_').Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.ToString() : "_";
    }

    private static double V(PsseDyrRecord r, int k, string name)
    {
        string s = r.Values[k].Replace('D', 'E').Replace('d', 'e');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} {name} is '{r.Values[k]}'; it must be a finite number.");
        return v;
    }

    private static void Machine(PsseDyrRecord r, string unit, List<(string, object)> p, Dictionary<string, string> derived, List<string> issues)
    {
        if (r.Model == "GENROU")
        {
            // T'do T''do T'qo T''qo H D Xd Xq X'd X'q X''d Xl S(1.0) S(1.2); X''q = X''d.
            p.Add(("tdopS", V(r, 0, "T'do"))); p.Add(("tdoppS", V(r, 1, "T''do"))); p.Add(("tqopS", V(r, 2, "T'qo")));
            p.Add(("tqoppS", V(r, 3, "T''qo"))); p.Add(("inertiaH", V(r, 4, "H"))); p.Add(("damping", V(r, 5, "D")));
            p.Add(("xdPu", V(r, 6, "Xd"))); p.Add(("xqPu", V(r, 7, "Xq"))); p.Add(("xdpPu", V(r, 8, "X'd")));
            p.Add(("xqpPu", V(r, 9, "X'q"))); p.Add(("xdppPu", V(r, 10, "X''d"))); p.Add(("xqppPu", V(r, 10, "X''d")));
            p.Add(("xlPu", V(r, 11, "Xl"))); p.Add(("saturationS10", V(r, 12, "S(1.0)"))); p.Add(("saturationS12", V(r, 13, "S(1.2)")));
            derived["xqppPu"] = "GENROU takes X''q = X''d.";
            return;
        }
        // GENSAL: T'do T''do T''qo H D Xd Xq X'd X''d Xl S(1.0) S(1.2).
        double tqopp = V(r, 2, "T''qo"), xq = V(r, 6, "Xq"), s10 = V(r, 10, "S(1.0)"), s12 = V(r, 11, "S(1.2)");
        p.Add(("tdopS", V(r, 0, "T'do"))); p.Add(("tdoppS", V(r, 1, "T''do"))); p.Add(("tqopS", tqopp)); p.Add(("tqoppS", tqopp));
        p.Add(("inertiaH", V(r, 3, "H"))); p.Add(("damping", V(r, 4, "D"))); p.Add(("xdPu", V(r, 5, "Xd"))); p.Add(("xqPu", xq));
        p.Add(("xdpPu", V(r, 7, "X'd"))); p.Add(("xqpPu", xq)); p.Add(("xdppPu", V(r, 8, "X''d"))); p.Add(("xqppPu", V(r, 8, "X''d")));
        p.Add(("xlPu", V(r, 9, "Xl")));
        derived["xqpPu"] = "GENSAL has no q-axis transient circuit: X'q = Xq, so that circuit carries nothing.";
        derived["tqopS"] = "GENSAL has no q-axis transient circuit: T'qo = T''qo, which has no effect because X'q = Xq.";
        derived["xqppPu"] = "GENSAL takes X''q = X''d.";
        if (s10 != 0 || s12 != 0)
            issues.Add($"GENSAL saturation for {unit} (line {r.Line}): PSS/E saturates the d axis only, GridSim's machine both axes");
    }

    /// <summary>A controller's parameters; false (with the reason in <paramref name="issues"/>) when it cannot be mapped.</summary>
    private static bool Controller(PsseDyrRecord r, string unit, List<(string, object)> p, Dictionary<string, string> derived, List<string> issues)
    {
        int before = issues.Count;
        string at = $"{r.Model} for {unit} (line {r.Line})";
        void Need(bool ok, string what) { if (!ok) issues.Add($"{at}: {what}"); }
        switch (r.Model)
        {
            case "SEXS":
            {
                // TA/TB TB K TE EMIN EMAX
                double ratio = V(r, 0, "TA/TB"), tb = V(r, 1, "TB");
                p.Add(("model", "SEXS")); p.Add(("sexsLeadS", ratio * tb)); p.Add(("sexsLagS", tb)); p.Add(("sexsGain", V(r, 2, "K")));
                p.Add(("sexsExciterTimeS", V(r, 3, "TE"))); p.Add(("sexsFieldMinPu", V(r, 4, "EMIN"))); p.Add(("sexsFieldMaxPu", V(r, 5, "EMAX")));
                derived["sexsLeadS"] = "TA = (TA/TB) x TB.";
                break;
            }
            case "IEEET1":
            {
                // TR KA TA VRMAX VRMIN KE TE KF TF SWITCH E1 SE(E1) E2 SE(E2)
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's IEEET1 has none)");
                Need(V(r, 5, "KE") != 0, "KE = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 9, "SWITCH") == 0, "SWITCH not 0");
                Need(V(r, 11, "SE(E1)") == 0 && V(r, 13, "SE(E2)") == 0, "exciter saturation (PSS/E's curve is not GridSim's exponential one)");
                p.Add(("model", "IEEET1")); p.Add(("ieeet1RegulatorGain", V(r, 1, "KA"))); p.Add(("ieeet1RegulatorTimeS", V(r, 2, "TA")));
                p.Add(("ieeet1RegulatorMaxPu", V(r, 3, "VRMAX"))); p.Add(("ieeet1RegulatorMinPu", V(r, 4, "VRMIN")));
                p.Add(("ieeet1ExciterConstant", V(r, 5, "KE"))); p.Add(("ieeet1ExciterTimeS", V(r, 6, "TE")));
                p.Add(("ieeet1FeedbackGain", V(r, 7, "KF"))); p.Add(("ieeet1FeedbackTimeS", V(r, 8, "TF")));
                break;
            }
            case "ESST4B":
                // TR KPR KIR VRMAX VRMIN TA KPM KIM VMMAX VMMIN KG KP KI VBMAX KC XL THETAP
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's ESST4B has none)");
                p.Add(("model", "ESST4B")); p.Add(("st4bKpr", V(r, 1, "KPR"))); p.Add(("st4bKir", V(r, 2, "KIR")));
                p.Add(("st4bVrMaxPu", V(r, 3, "VRMAX"))); p.Add(("st4bVrMinPu", V(r, 4, "VRMIN"))); p.Add(("st4bTaS", V(r, 5, "TA")));
                p.Add(("st4bKpm", V(r, 6, "KPM"))); p.Add(("st4bKim", V(r, 7, "KIM"))); p.Add(("st4bVmMaxPu", V(r, 8, "VMMAX")));
                p.Add(("st4bVmMinPu", V(r, 9, "VMMIN"))); p.Add(("st4bKg", V(r, 10, "KG"))); p.Add(("st4bKp", V(r, 11, "KP")));
                p.Add(("st4bKi", V(r, 12, "KI"))); p.Add(("st4bVbMaxPu", V(r, 13, "VBMAX"))); p.Add(("st4bKc", V(r, 14, "KC")));
                p.Add(("st4bXlPu", V(r, 15, "XL"))); p.Add(("st4bThetapDeg", V(r, 16, "THETAP")));
                break;
            case "EXAC1":
                // TR TB TC KA TA VRMAX VRMIN TE KF TF KC KD KE E1 SE(E1) E2 SE(E2)
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's EXAC1 has none)");
                p.Add(("model", "EXAC1")); p.Add(("acLagS", V(r, 1, "TB"))); p.Add(("acLeadS", V(r, 2, "TC")));
                p.Add(("acRegulatorGain", V(r, 3, "KA"))); p.Add(("acRegulatorTimeS", V(r, 4, "TA")));
                p.Add(("acRegulatorMaxPu", V(r, 5, "VRMAX"))); p.Add(("acRegulatorMinPu", V(r, 6, "VRMIN")));
                p.Add(("acExciterTimeS", V(r, 7, "TE"))); p.Add(("acFeedbackGain", V(r, 8, "KF"))); p.Add(("acFeedbackTimeS", V(r, 9, "TF")));
                p.Add(("acRectifierKc", V(r, 10, "KC"))); p.Add(("acDemagnetisingKd", V(r, 11, "KD"))); p.Add(("acExciterConstant", V(r, 12, "KE")));
                p.Add(("acSatE1", V(r, 13, "E1"))); p.Add(("acSatSe1", V(r, 14, "SE(E1)"))); p.Add(("acSatE2", V(r, 15, "E2")));
                p.Add(("acSatSe2", V(r, 16, "SE(E2)")));
                break;
            case "PSS1A":
            {
                // ICS IB A1 A2 T1 T2 T3 T4 T5 T6 KS LSMAX LSMIN VCU VCL
                Need(V(r, 0, "ICS") == 1, "an input other than rotor speed deviation (ICS not 1)");
                Need(V(r, 1, "IB") == 0, "a remote bus input (IB not 0)");
                Need(V(r, 2, "A1") == 0 && V(r, 3, "A2") == 0, "the A1 / A2 filter (GridSim's PSS1A has none)");
                double t5 = V(r, 8, "T5"), t6 = V(r, 9, "T6");
                Need(t5 == t6, "a washout sT5/(1+sT6) with T5 not equal to T6 (GridSim's washout is sTw/(1+sTw))");
                Need(V(r, 13, "VCU") == 0 && V(r, 14, "VCL") == 0, "the VCU / VCL voltage cut-offs");
                p.Add(("model", "PSS1A")); p.Add(("gain", V(r, 10, "KS"))); p.Add(("washoutS", t5));
                p.Add(("lead1S", V(r, 4, "T1"))); p.Add(("lag1S", V(r, 5, "T2"))); p.Add(("lead2S", V(r, 6, "T3"))); p.Add(("lag2S", V(r, 7, "T4")));
                p.Add(("outputMaxPu", V(r, 11, "LSMAX"))); p.Add(("outputMinPu", V(r, 12, "LSMIN")));
                break;
            }
            case "TGOV1":
                // R T1 VMAX VMIN T2 T3 Dt
                p.Add(("model", "TGOV1")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("tgov1ValveTimeS", V(r, 1, "T1")));
                p.Add(("valveMaxPu", V(r, 2, "VMAX"))); p.Add(("valveMinPu", V(r, 3, "VMIN"))); p.Add(("tgov1ReheatLeadS", V(r, 4, "T2")));
                p.Add(("tgov1ReheatLagS", V(r, 5, "T3"))); p.Add(("turbineDampingPu", V(r, 6, "Dt")));
                break;
            case "IEEEG1":
            {
                // JBUS M K T1 T2 T3 Uo Uc PMAX PMIN T4 K1 K2 T5 K3 K4 T6 K5 K6 T7 K7 K8
                Need(V(r, 0, "JBUS") == 0, "a second (low-pressure) shaft at another bus (JBUS not 0)");
                double k = V(r, 2, "K");
                Need(k > 0, "K not above 0, so there is no droop R = 1 / K");
                p.Add(("model", "IEEEG1")); p.Add(("droopPu", k > 0 ? 1.0 / k : 0.0));
                p.Add(("ieeeg1LagS", V(r, 3, "T1"))); p.Add(("ieeeg1LeadS", V(r, 4, "T2"))); p.Add(("ieeeg1ValveTimeS", V(r, 5, "T3")));
                p.Add(("ieeeg1UoPuPerS", V(r, 6, "Uo"))); p.Add(("ieeeg1UcPuPerS", V(r, 7, "Uc")));
                p.Add(("valveMaxPu", V(r, 8, "PMAX"))); p.Add(("valveMinPu", V(r, 9, "PMIN"))); p.Add(("ieeeg1ChestS", V(r, 10, "T4")));
                p.Add(("ieeeg1K1", V(r, 11, "K1"))); p.Add(("ieeeg1K2", V(r, 12, "K2"))); p.Add(("ieeeg1ReheatS", V(r, 13, "T5")));
                p.Add(("ieeeg1K3", V(r, 14, "K3"))); p.Add(("ieeeg1K4", V(r, 15, "K4"))); p.Add(("ieeeg1CrossoverS", V(r, 16, "T6")));
                p.Add(("ieeeg1K5", V(r, 17, "K5"))); p.Add(("ieeeg1K6", V(r, 18, "K6"))); p.Add(("ieeeg1LpS", V(r, 19, "T7")));
                p.Add(("ieeeg1K7", V(r, 20, "K7"))); p.Add(("ieeeg1K8", V(r, 21, "K8")));
                derived["droopPu"] = "R = 1 / K.";
                break;
            }
            case "GAST":
                // R T1 T2 T3 AT KT VMAX VMIN DTURB (every value has a GridSim parameter of the same meaning)
                p.Add(("model", "GAST")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("gastValveTimeS", V(r, 1, "T1")));
                p.Add(("gastTurbineTimeS", V(r, 2, "T2"))); p.Add(("gastExhaustTimeS", V(r, 3, "T3"))); p.Add(("gastAmbientLimitPu", V(r, 4, "AT")));
                p.Add(("gastTemperatureGain", V(r, 5, "KT"))); p.Add(("valveMaxPu", V(r, 6, "VMAX"))); p.Add(("valveMinPu", V(r, 7, "VMIN")));
                p.Add(("turbineDampingPu", V(r, 8, "DTURB")));
                break;
            case "HYGOV":
                // R r Tr Tf Tg VELM GMAX GMIN TW At Dturb qNL
                Need(V(r, 3, "Tf") == 0, "a filter time Tf (GridSim's hydro governor has none)");
                Need(V(r, 5, "VELM") == 0, "a gate rate limit VELM");
                Need(V(r, 9, "At") == 1, "a turbine gain At other than 1");
                Need(V(r, 10, "Dturb") == 0, "turbine damping Dturb");
                Need(V(r, 11, "qNL") == 0, "a no-load flow qNL");
                p.Add(("model", "HYGOV")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("hygovTransientDroopPu", V(r, 1, "r")));
                p.Add(("hygovResetTimeS", V(r, 2, "Tr"))); p.Add(("hygovGateTimeS", V(r, 4, "Tg"))); p.Add(("valveMaxPu", V(r, 6, "GMAX")));
                p.Add(("valveMinPu", V(r, 7, "GMIN"))); p.Add(("hygovWaterTimeS", V(r, 8, "TW")));
                break;
            default:
                throw new InvalidOperationException($"internal error: no mapping for {r.Model}");
        }
        return issues.Count == before;
    }

    private static void WriteTemplate(Utf8JsonWriter w, string id, string kind, string title, List<(string Name, object Value)> p,
        Dictionary<string, string> derived, string source, PsseDyrRecord rec, List<(string Slot, string Ref)>? controllers = null)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteNumber("rev", 1);
        w.WriteString("kind", kind);
        w.WriteString("status", "active");
        w.WriteString("title", title);
        w.WriteStartObject("params");
        foreach ((string name, object value) in p)
        {
            if (value is string s) w.WriteString(name, s);
            else w.WriteNumber(name, (double)value);
        }
        w.WriteEndObject();
        if (controllers is { Count: > 0 })
        {
            w.WriteStartObject("controllers");
            foreach ((string slot, string tref) in controllers) w.WriteString(slot, tref);
            w.WriteEndObject();
        }
        w.WriteStartObject("provenance");
        w.WriteStartObject("_default");
        w.WriteString("tag", "Recorded");
        w.WriteString("note", $"PSS/E DYR {source}, line {rec.Line.ToString(CultureInfo.InvariantCulture)}: {rec.Model} record.");
        w.WriteEndObject();
        foreach (var kv in derived)
        {
            w.WriteStartObject(kv.Key);
            w.WriteString("tag", "DerivedFromRecorded");
            w.WriteString("note", $"PSS/E DYR {source}, line {rec.Line.ToString(CultureInfo.InvariantCulture)}: {kv.Value}");
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
