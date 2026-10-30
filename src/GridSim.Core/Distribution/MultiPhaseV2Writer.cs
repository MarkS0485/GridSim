using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Distribution;

/// <summary>
/// N339 (October 2026): writes a <see cref="MultiPhaseFeeder"/> as a <c>gridsim-dist3ph/2</c> document
/// (<c>schemas/v2/dist3ph.schema.json</c>) that <see cref="MultiPhaseJsonLoader.ParseV2"/> reads back to the same model,
/// bit for bit.
///
/// <para><b>Byte-stable.</b> The text is a pure function of the feeder and the provenance: members in a fixed order,
/// every number in its shortest round-trip form (System.Text.Json), two-space indentation, "\n" line ends on every OS
/// and one final newline. Writing the loaded-back feeder again gives the same bytes (pinned by tests).</para>
///
/// <para><b>Exact.</b> Every per-unit double the model holds is written as that double; the order of buses, branches,
/// capacitors and loads is the model's (the solver sums in that order). Optional members are written only when they
/// differ from the reader's absence rule (phases abc, no shunt, no regulator, transformer ratio 1), so absence and
/// presence read back to the same record. A model the v2 form cannot hold is REFUSED by name
/// (<see cref="InvalidDataException"/>), never written in part: a non-finite number, a branch with two transformer
/// flags, a transformer ratio on a branch that is not a transformer element, or a branch kind the format does not know.
/// Whatever the reader would refuse (for example a line with a regulator) is refused by the reader on the round trip,
/// which is why the CLI and the tests always read the written text back and compare (<see cref="FirstDifference"/>).</para>
///
/// <para><b>Provenance.</b> A feeder read from a v2 file carries its provenance and it is written back as read. A
/// feeder from a v1 file or a lifted case carries none, and the caller MUST then say what it is
/// (<paramref name="provenanceForUnprovenanced"/> of <see cref="ToJson"/>): GridSim never invents a tag. Giving one for a
/// feeder that already has its own is refused too (which one would win would be a guess).</para>
/// </summary>
public static class MultiPhaseV2Writer
{
    // Indented, "\n" line ends on every OS (byte-stable output; the default follows Environment.NewLine).
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary>The provenance <c>gridsim dist3ph ... to-v2</c> gives a feeder that carries none (a v1 file or a lifted
    /// positive-sequence case): honest about what is known, so a CuratedPlaceholder with the [VERIFY] token naming the
    /// source file.</summary>
    public static AssetClassProvenance ConvertedWithoutProvenance(string sourceFile) => new(ProvenanceTag.CuratedPlaceholder,
        $"Converted by GridSim from {Path.GetFileName(sourceFile.TrimEnd('/', '\\'))}, a source that carries no provenance; " +
        "the data source is the description above. [VERIFY] the tag before relying on it.");

    /// <summary>
    /// The v2 document text of <paramref name="feeder"/>. <paramref name="provenanceForUnprovenanced"/> is required
    /// when the feeder has no <see cref="MultiPhaseFeeder.Provenance"/> and refused when it has one.
    /// </summary>
    /// <exception cref="ArgumentException">Provenance missing, or given twice.</exception>
    /// <exception cref="InvalidDataException">The model holds something the v2 form cannot (named).</exception>
    public static string ToJson(MultiPhaseFeeder feeder, AssetClassProvenance? provenanceForUnprovenanced = null)
    {
        ArgumentNullException.ThrowIfNull(feeder);
        // N354 (CF5) refused a feeder with converter units here, because the v2 form had no place for them. N360 (CF6):
        // the v2 form now holds them (the "converters" section below, read back by ParseV2 to the same doubles), so the
        // refusal is gone; a converter list that is not aligned with its ids is still refused (below), never written in part.
        Dist3phProvenance prov;
        if (feeder.Provenance is { } own)
        {
            if (provenanceForUnprovenanced is not null)
                throw new ArgumentException($"feeder '{feeder.Name}' already carries its own provenance (read from a v2 file); a second one is refused rather than guessed between.", nameof(provenanceForUnprovenanced));
            prov = own;
        }
        else
        {
            if (provenanceForUnprovenanced is null)
                throw new ArgumentException($"feeder '{feeder.Name}' carries no provenance (a v1 file or a lifted case) and none was given; a v2 document needs one and GridSim does not invent a tag.", nameof(provenanceForUnprovenanced));
            prov = new Dist3phProvenance(provenanceForUnprovenanced, new Dictionary<string, AssetClassProvenance>());
        }
        CheckProvenance(prov.Default, "the default provenance");
        foreach (KeyValuePair<string, AssetClassProvenance> kv in prov.ByElement) CheckProvenance(kv.Value, $"the provenance of {kv.Key}");

        MultiPhaseModel m = feeder.Model;
        if (feeder.BranchIds.Count != m.Branches.Count || feeder.BranchKinds.Count != m.Branches.Count || feeder.LoadIds.Count != m.Loads.Count
            || feeder.CapacitorIds.Count != m.Capacitors.Count)
            throw new InvalidDataException($"feeder '{feeder.Name}': its id lists are not aligned with its model ({feeder.BranchIds.Count} branch ids / {feeder.BranchKinds.Count} kinds for {m.Branches.Count} branches, {feeder.LoadIds.Count} load ids for {m.Loads.Count} loads, {feeder.CapacitorIds.Count} capacitor ids for {m.Capacitors.Count} capacitors).");
        // N360 (CF6): the converter units and their ids are two aligned lists too.
        if (feeder.ConverterIds.Count != feeder.Converters.Count)
            throw new InvalidDataException($"feeder '{feeder.Name}': {feeder.ConverterIds.Count} converter ids for {feeder.Converters.Count} converter units; the lists are not aligned.");

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("schema", MultiPhaseJsonLoader.SchemaV2);
            w.WriteString("name", feeder.Name);
            w.WriteString("description", feeder.Description);
            Num(w, "baseMva", feeder.BaseMva, "baseMva");
            w.WritePropertyName("provenance");
            w.WriteStartObject();
            WriteProv(w, "_default", prov.Default);
            w.WriteEndObject();

            w.WritePropertyName("buses");
            w.WriteStartArray();
            foreach (PhaseBus b in m.Buses)
            {
                w.WriteStartObject();
                w.WriteNumber("id", b.Id);
                Num(w, "baseKvLL", feeder.BaseKvLLByBus[b.Id], $"bus {b.Id} baseKvLL");
                if (feeder.BusNames.TryGetValue(b.Id, out string? nm)) w.WriteString("name", nm);
                if (b.IsSource)
                {
                    if (b.SourceV is null || b.SourceV.Length != 3)
                        throw new InvalidDataException($"feeder '{feeder.Name}': source bus {b.Id} has no three-phase source voltage.");
                    ComplexVector(w, "sourceVPu", b.SourceV, $"bus {b.Id} source voltage");
                }
                ElementProv(w, prov, $"bus:{b.Id}");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("branches");
            w.WriteStartArray();
            for (int k = 0; k < m.Branches.Count; k++)
                WriteBranch(w, feeder, k, prov);
            w.WriteEndArray();

            if (feeder.OpenSwitchIds.Count > 0)
            {
                w.WritePropertyName("openSwitches");
                w.WriteStartArray();
                foreach (string id in feeder.OpenSwitchIds) w.WriteStringValue(id);
                w.WriteEndArray();
            }

            if (m.Capacitors.Count > 0)
            {
                w.WritePropertyName("capacitors");
                w.WriteStartArray();
                for (int k = 0; k < m.Capacitors.Count; k++)
                {
                    PhaseCapacitor c = m.Capacitors[k];
                    string id = feeder.CapacitorIds[k];
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteNumber("bus", c.Bus);
                    Vector(w, "qPu", c.QPerPhase, $"capacitor {id} qPu");
                    ElementProv(w, prov, $"capacitor:{id}");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (m.Loads.Count > 0)
            {
                w.WritePropertyName("loads");
                w.WriteStartArray();
                for (int k = 0; k < m.Loads.Count; k++)
                {
                    PhaseLoad l = m.Loads[k];
                    string id = feeder.LoadIds[k];
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteNumber("bus", l.Bus);
                    w.WriteString("connection", l.Connection switch
                    {
                        PhaseLoadConnection.Wye => "wye",
                        PhaseLoadConnection.Delta => "delta",
                        _ => throw new InvalidDataException($"load {id}: connection {l.Connection} has no v2 spelling."),
                    });
                    w.WriteString("model", l.Model switch
                    {
                        PhaseLoadModel.ConstantPower => "constant-power",
                        PhaseLoadModel.ConstantCurrent => "constant-current",
                        PhaseLoadModel.ConstantImpedance => "constant-impedance",
                        _ => throw new InvalidDataException($"load {id}: model {l.Model} has no v2 spelling."),
                    });
                    ComplexVector(w, "sPu", l.SPerPhase, $"load {id} sPu");
                    ElementProv(w, prov, $"load:{id}");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            // N360 (CF6): the active-front-end converter units (the v1 "converters" section), written last and only when
            // the feeder has any, so every feeder without them writes exactly the bytes it wrote before. Each number is
            // the double the model holds (no arithmetic), so ParseV2 reads back the same units bit for bit. rOnRating is
            // written only when it is not exactly +0.0, the reader's absence rule (so -0.0 survives the round trip too).
            if (feeder.Converters.Count > 0)
            {
                w.WritePropertyName("converters");
                w.WriteStartArray();
                for (int k = 0; k < feeder.Converters.Count; k++)
                {
                    GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd c = feeder.Converters[k];
                    string id = feeder.ConverterIds[k];
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteNumber("bus", c.Bus);
                    Num(w, "ratingMva", c.RatingMva, $"converter {id} ratingMva");
                    Num(w, "xOnRating", c.ReactancePu, $"converter {id} xOnRating");
                    if (BitConverter.DoubleToInt64Bits(c.ResistancePu) != BitConverter.DoubleToInt64Bits(0.0))
                        Num(w, "rOnRating", c.ResistancePu, $"converter {id} rOnRating");
                    ElementProv(w, prov, $"converter:{id}");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteBranch(Utf8JsonWriter w, MultiPhaseFeeder feeder, int k, Dist3phProvenance prov)
    {
        PhaseBranch br = feeder.Model.Branches[k];
        string id = feeder.BranchIds[k];
        string kind = feeder.BranchKinds[k];
        if (kind is not ("line" or "transformer" or "switch" or "regulator"))
            throw new InvalidDataException($"branch {id}: kind \"{kind}\" is not one the v2 form knows (line, transformer, switch, regulator).");
        int flags = br.TransformerFlagCount;   // N334 (CF5): the open banks count too
        if (flags > 1) throw new InvalidDataException($"branch {id}: more than one transformer flag is set; the model is contradictory.");
        string? element = br.DeltaWyeStepDown ? "delta-wye-step-down" : br.DeltaWyeStepUp ? "delta-wye-step-up"
            : br.WyeDeltaStepDown ? "wye-delta-step-down" : br.WyeDeltaStepUp ? "wye-delta-step-up" : br.DeltaDelta ? "delta-delta"
            : br.OpenWyeOpenDelta ? "open-wye-open-delta" : br.OpenDeltaOpenDelta ? "open-delta-open-delta" : null;
        if (element is null && BitConverter.DoubleToInt64Bits(br.TransformerRatio) != BitConverter.DoubleToInt64Bits(1.0))
            throw new InvalidDataException($"branch {id}: a transformer ratio of {R(br.TransformerRatio)} on a branch that is not a transformer element cannot be written (the reader would ignore it).");

        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("kind", kind);
        w.WriteNumber("from", br.From);
        w.WriteNumber("to", br.To);
        if (br.Phases != Phase.Abc) w.WriteString("phases", PhaseIndex.Name(br.Phases));
        ComplexMatrix(w, "zPu", br.Z, $"branch {id} zPu");
        if (br.ShuntY is not null) ComplexMatrix(w, "shuntYPu", br.ShuntY, $"branch {id} shuntYPu");
        if (br.RegulatorRatio is not null)
        {
            if (br.RegulatorRatio.Length != 3) throw new InvalidDataException($"branch {id}: the regulator ratio has {br.RegulatorRatio.Length} entries, not 3.");
            Vector(w, "regulatorRatio", br.RegulatorRatio, $"branch {id} regulatorRatio");
        }
        if (element is not null)
        {
            w.WriteString("transformerElement", element);
            // Written only when it is not exactly 1: the reader's absence rule gives 1.0.
            if (BitConverter.DoubleToInt64Bits(br.TransformerRatio) != BitConverter.DoubleToInt64Bits(1.0))
                Num(w, "transformerRatio", br.TransformerRatio, $"branch {id} transformerRatio");
        }
        ElementProv(w, prov, $"branch:{id}");
        w.WriteEndObject();
    }

    // ---- small writers, each refusing a non-finite number by name -------------------------------------------------

    private static void Num(Utf8JsonWriter w, string name, double v, string what)
    {
        w.WritePropertyName(name);
        NumValue(w, v, what);
    }

    private static void NumValue(Utf8JsonWriter w, double v, string what)
    {
        if (!double.IsFinite(v)) throw new InvalidDataException($"{what} is {R(v)}; JSON (and the v2 form) holds finite numbers only.");
        w.WriteNumberValue(v);
    }

    private static void Vector(Utf8JsonWriter w, string name, double[] v, string what)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        for (int i = 0; i < 3; i++) NumValue(w, v[i], what);
        w.WriteEndArray();
    }

    private static void ComplexVector(Utf8JsonWriter w, string name, Complex[] v, string what)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WritePropertyName("re");
        w.WriteStartArray();
        for (int i = 0; i < 3; i++) NumValue(w, v[i].Real, what);
        w.WriteEndArray();
        w.WritePropertyName("im");
        w.WriteStartArray();
        for (int i = 0; i < 3; i++) NumValue(w, v[i].Imaginary, what);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void ComplexMatrix(Utf8JsonWriter w, string name, PhaseImpedance m, string what)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        foreach (bool real in new[] { true, false })
        {
            w.WritePropertyName(real ? "re" : "im");
            w.WriteStartArray();
            for (int i = 0; i < 3; i++)
            {
                w.WriteStartArray();
                for (int j = 0; j < 3; j++) NumValue(w, real ? m[i, j].Real : m[i, j].Imaginary, what);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteProv(Utf8JsonWriter w, string name, AssetClassProvenance p)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteString("tag", p.Tag.ToString());
        w.WriteString("note", p.Note);
        w.WriteEndObject();
    }

    private static void ElementProv(Utf8JsonWriter w, Dist3phProvenance prov, string key)
    {
        if (prov.ByElement.TryGetValue(key, out AssetClassProvenance? p)) WriteProv(w, "prov", p);
    }

    private static void CheckProvenance(AssetClassProvenance p, string what)
    {
        if (!Enum.IsDefined(p.Tag)) throw new ArgumentException($"{what}: {(int)p.Tag} is not a provenance tag.", nameof(p));
        if (p.Note is null) throw new ArgumentException($"{what}: the note is missing.", nameof(p));
        if (p.Tag == ProvenanceTag.CuratedPlaceholder && !p.Note.Contains("[VERIFY]", StringComparison.Ordinal))
            throw new ArgumentException($"{what}: a CuratedPlaceholder note must contain the literal token [VERIFY].", nameof(p));
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ---- the round-trip proof ---------------------------------------------------------------------------------------

    /// <summary>
    /// The first difference between two feeders, compared bit for bit (every double by its 64 bits, so -0 and +0 differ
    /// and nothing is compared with a tolerance): base, buses (id, source flag, source voltage, kV base, name), branches
    /// (ends, phases, every Z and shunt entry, regulator ratios, transformer flags and ratio, id, kind), open switches,
    /// capacitors and loads (with their ids), and (N360, CF6) the converter units with their ids, in model order. Name and
    /// description are compared too; the source file
    /// and the provenance are not (a written file has another path, and a v1 feeder has no provenance). Null when the
    /// feeders are the same.
    /// </summary>
    public static string? FirstDifference(MultiPhaseFeeder a, MultiPhaseFeeder b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static bool Same(double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);
        static bool SameC(Complex x, Complex y) => Same(x.Real, y.Real) && Same(x.Imaginary, y.Imaginary);

        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return $"name: \"{a.Name}\" vs \"{b.Name}\"";
        if (!string.Equals(a.Description, b.Description, StringComparison.Ordinal)) return "description differs";
        if (!Same(a.BaseMva, b.BaseMva) || !Same(a.Model.BaseMva, b.Model.BaseMva)) return $"baseMva: {R(a.BaseMva)} vs {R(b.BaseMva)}";
        MultiPhaseModel ma = a.Model, mb = b.Model;

        if (ma.Buses.Count != mb.Buses.Count) return $"bus count: {ma.Buses.Count} vs {mb.Buses.Count}";
        for (int i = 0; i < ma.Buses.Count; i++)
        {
            PhaseBus x = ma.Buses[i], y = mb.Buses[i];
            if (x.Id != y.Id || x.IsSource != y.IsSource) return $"bus {i}: id / source {x.Id}/{x.IsSource} vs {y.Id}/{y.IsSource}";
            if ((x.SourceV is null) != (y.SourceV is null)) return $"bus {x.Id}: source voltage present on one side only";
            if (x.SourceV is not null)
                for (int p = 0; p < 3; p++)
                    if (!SameC(x.SourceV[p], y.SourceV![p])) return $"bus {x.Id}: source voltage phase {"abc"[p]} {x.SourceV[p]} vs {y.SourceV[p]}";
            if (!Same(a.BaseKvLLByBus[x.Id], b.BaseKvLLByBus[y.Id])) return $"bus {x.Id}: baseKvLL {R(a.BaseKvLLByBus[x.Id])} vs {R(b.BaseKvLLByBus[y.Id])}";
            a.BusNames.TryGetValue(x.Id, out string? na);
            b.BusNames.TryGetValue(y.Id, out string? nb);
            if (!string.Equals(na, nb, StringComparison.Ordinal)) return $"bus {x.Id}: name \"{na}\" vs \"{nb}\"";
        }
        if (a.BaseKvLLByBus.Count != b.BaseKvLLByBus.Count || a.BusNames.Count != b.BusNames.Count) return "bus base or name tables differ in size";

        if (ma.Branches.Count != mb.Branches.Count) return $"branch count: {ma.Branches.Count} vs {mb.Branches.Count}";
        for (int k = 0; k < ma.Branches.Count; k++)
        {
            PhaseBranch x = ma.Branches[k], y = mb.Branches[k];
            string at = $"branch {k} ({a.BranchIds[k]})";
            if (!string.Equals(a.BranchIds[k], b.BranchIds[k], StringComparison.Ordinal)) return $"{at}: id vs \"{b.BranchIds[k]}\"";
            if (!string.Equals(a.BranchKinds[k], b.BranchKinds[k], StringComparison.Ordinal)) return $"{at}: kind {a.BranchKinds[k]} vs {b.BranchKinds[k]}";
            if (x.From != y.From || x.To != y.To || x.Phases != y.Phases) return $"{at}: ends or phases differ";
            if (x.DeltaWyeStepDown != y.DeltaWyeStepDown || x.DeltaWyeStepUp != y.DeltaWyeStepUp || x.WyeDeltaStepDown != y.WyeDeltaStepDown
                || x.WyeDeltaStepUp != y.WyeDeltaStepUp || x.DeltaDelta != y.DeltaDelta
                || x.OpenWyeOpenDelta != y.OpenWyeOpenDelta || x.OpenDeltaOpenDelta != y.OpenDeltaOpenDelta) return $"{at}: transformer flags differ";
            if (!Same(x.TransformerRatio, y.TransformerRatio)) return $"{at}: transformer ratio {R(x.TransformerRatio)} vs {R(y.TransformerRatio)}";
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (!SameC(x.Z[i, j], y.Z[i, j])) return $"{at}: Z[{i}][{j}] {x.Z[i, j]} vs {y.Z[i, j]}";
            if ((x.ShuntY is null) != (y.ShuntY is null)) return $"{at}: shunt present on one side only";
            if (x.ShuntY is not null)
                for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (!SameC(x.ShuntY[i, j], y.ShuntY![i, j])) return $"{at}: shunt[{i}][{j}] differs";
            if ((x.RegulatorRatio is null) != (y.RegulatorRatio is null)) return $"{at}: regulator present on one side only";
            if (x.RegulatorRatio is not null)
                for (int p = 0; p < 3; p++)
                    if (!Same(x.RegulatorRatio[p], y.RegulatorRatio![p])) return $"{at}: regulator ratio phase {"abc"[p]} differs";
        }

        if (!a.OpenSwitchIds.SequenceEqual(b.OpenSwitchIds, StringComparer.Ordinal)) return "open switches differ";

        if (ma.Capacitors.Count != mb.Capacitors.Count) return $"capacitor count: {ma.Capacitors.Count} vs {mb.Capacitors.Count}";
        for (int k = 0; k < ma.Capacitors.Count; k++)
        {
            if (!string.Equals(a.CapacitorIds[k], b.CapacitorIds[k], StringComparison.Ordinal) || ma.Capacitors[k].Bus != mb.Capacitors[k].Bus)
                return $"capacitor {k}: id or bus differs";
            for (int p = 0; p < 3; p++)
                if (!Same(ma.Capacitors[k].QPerPhase[p], mb.Capacitors[k].QPerPhase[p])) return $"capacitor {a.CapacitorIds[k]}: phase {"abc"[p]} differs";
        }

        if (ma.Loads.Count != mb.Loads.Count) return $"load count: {ma.Loads.Count} vs {mb.Loads.Count}";
        for (int k = 0; k < ma.Loads.Count; k++)
        {
            PhaseLoad x = ma.Loads[k], y = mb.Loads[k];
            if (!string.Equals(a.LoadIds[k], b.LoadIds[k], StringComparison.Ordinal) || x.Bus != y.Bus || x.Model != y.Model || x.Connection != y.Connection)
                return $"load {k} ({a.LoadIds[k]}): id, bus, model or connection differs";
            for (int p = 0; p < 3; p++)
                if (!SameC(x.SPerPhase[p], y.SPerPhase[p])) return $"load {a.LoadIds[k]}: phase {"abc"[p]} {x.SPerPhase[p]} vs {y.SPerPhase[p]}";
        }

        // N360 (CF6): the converter units, bit for bit (a feeder without converters on both sides compares as before).
        if (a.Converters.Count != b.Converters.Count) return $"converter count: {a.Converters.Count} vs {b.Converters.Count}";
        for (int k = 0; k < a.Converters.Count; k++)
        {
            GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd x = a.Converters[k], y = b.Converters[k];
            string cid = k < a.ConverterIds.Count ? a.ConverterIds[k] : k.ToString(CultureInfo.InvariantCulture);
            if (k >= a.ConverterIds.Count || k >= b.ConverterIds.Count || !string.Equals(a.ConverterIds[k], b.ConverterIds[k], StringComparison.Ordinal))
                return $"converter {k} ({cid}): id differs";
            if (x.Bus != y.Bus) return $"converter {cid}: bus {x.Bus} vs {y.Bus}";
            if (!Same(x.RatingMva, y.RatingMva)) return $"converter {cid}: rating {R(x.RatingMva)} vs {R(y.RatingMva)} MVA";
            if (!Same(x.ReactancePu, y.ReactancePu)) return $"converter {cid}: reactance {R(x.ReactancePu)} vs {R(y.ReactancePu)}";
            if (!Same(x.ResistancePu, y.ResistancePu)) return $"converter {cid}: resistance {R(x.ResistancePu)} vs {R(y.ResistancePu)}";
        }
        return null;
    }
}
