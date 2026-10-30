using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Tests.Ui;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests.Editor;

/// <summary>
/// Phase 5b (2026-10-20): editing a v2 case's asset overlay with the case editor's engine (CaseSession, CaseOps,
/// OverlayOps, ReferenceIndex, SaveGate, CaseCommitter), on temporary copies only (the UiOverlayFixture: data/uk projected
/// to v2 with a twenty-record overlay in two files). Pinned:
/// <list type="bullet">
/// <item>the overlay files open as session documents, after the network files, and a case without an overlay opens with
/// exactly its manifest and network files;</item>
/// <item>LOSSLESS: an overlay parameter edit saved changes exactly that value token (the object form's v only, its unit
/// and provenance kept), every other byte of every file as before; undo gives the original bytes back;</item>
/// <item>BIT IDENTITY: after any overlay-only edits saved, the reloaded model is equal to the original in every field,
/// doubles by their bits, and the network and manifest files are byte for byte as before;</item>
/// <item>the reference rules, with exact messages: deleting a model record an overlay record names is refused, listing
/// the overlay referrers; renaming a bus, line or transformer cascades into the overlay's references; deleting an overlay
/// record with children or record references pointing at it is refused, listing them; renaming an overlay id cascades
/// into parents and record references; a generator's name an overlay record names cannot change;</item>
/// <item>the kind spec's rules for parameters, references and parents, and adding a record by kind;</item>
/// <item>the save gate refuses an overlay that fails V2AssetChecker, with every problem the checker finds listed (and
/// nothing else), and Check reports the same; a good overlay passes and the v2 loader reads the staged case with it.</item>
/// </list>
/// </summary>
public sealed class OverlayEditingTests
{
    private const string FileA = "site.assets.json", FileB = "lines.assets.json", Network = V1Projector.NetworkFileName;

    private static CaseSession Open(UiOverlayFixture f) => CaseSession.OpenV2(f.ManifestPath, CaseSessionTests.Unlimited());

    private static Dictionary<string, byte[]> Files(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    // The overlay record of an id (its document, "/records", index).
    private static RecordRef Rec(CaseSession s, string id) => ReferenceIndex.Build(s).OverlayRecords[id];

    // A model record of a role by its name (or a bus by its id), as the engine addresses it.
    private static RecordRef Model(CaseSession s, string role, string? name = null, int? bus = null)
    {
        foreach (RecordCollection c in s.CollectionsOf(role))
        {
            JsonSourceNode array = s.ArrayOf(c)!;
            for (int i = 0; i < array.Elements.Count; i++)
            {
                JsonSourceNode r = array.Elements[i];
                if ((name is not null && RecordRead.Str(r, "name", StringComparer.Ordinal) == name) || (bus is not null && RecordRead.Int(r, "id", StringComparer.Ordinal) == bus))
                    return new RecordRef(c.Document, c.ArrayPointer, i);
            }
        }
        throw new InvalidOperationException($"no {role} record {name ?? bus?.ToString(CultureInfo.InvariantCulture)}.");
    }

    // The original bytes with the value token at <pointer> replaced by <literal>, nothing else changed.
    private static byte[] Spliced(byte[] original, string name, string pointer, string literal)
    {
        JsonSourceText t = JsonSourceText.Parse(original, name, StringComparer.Ordinal);
        JsonSourceNode value = t.Resolve(pointer)!;
        byte[] lit = Encoding.UTF8.GetBytes(literal);
        var result = new byte[original.Length - value.Length + lit.Length];
        original.AsSpan(0, value.Start).CopyTo(result);
        lit.CopyTo(result.AsSpan(value.Start));
        original.AsSpan(value.Start + value.Length).CopyTo(result.AsSpan(value.Start + lit.Length));
        return result;
    }

    private static V2CaseLoadResult Load(string manifest) => V2CaseLoader.LoadWithNotes(manifest, V2TestSupport.SchemasDir, dataRoot: null);

    private static JsonElement Param(CaseAssetOverlay o, string id, string name) => o.Find(id)!.Params.GetProperty(name);

    // =================================================================================================================
    // Opening
    // =================================================================================================================

    [Fact]
    public void The_overlay_files_open_as_session_documents_after_the_network_files_and_a_case_without_one_is_unchanged()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        Assert.Equal(new[] { "uk.case.json", Network, FileA, FileB }, s.Documents.Select(d => d.FileName));
        Assert.Equal(new[] { CaseSession.V2CaseSchema, CaseSession.V2NetworkSchema, CaseSession.V2AssetsSchema, CaseSession.V2AssetsSchema },
            s.Documents.Select(d => d.SchemaFile));
        Assert.True(s.HasOverlay);
        Assert.Equal(new[] { 2, 3 }, s.CollectionsOf(CaseRoles.Overlay).Select(c => c.Document));
        Assert.All(s.CollectionsOf(CaseRoles.Overlay), c => Assert.Equal("/records", c.ArrayPointer));
        Assert.Equal(new[] { 13, 7 }, s.CollectionsOf(CaseRoles.Overlay).Select(c => s.ArrayOf(c)!.Elements.Count));
        Assert.Equal(UiOverlayFixture.RecordCount, ReferenceIndex.Build(s).OverlayRecords.Count);
        Assert.Null(s.Preflight.LoaderError);   // the good overlay passes the checker at open
        Assert.False(s.IsDirty);

        using var plain = new UiOverlayFixture(withOverlay: false);
        CaseSession p = Open(plain);
        Assert.Equal(new[] { "uk.case.json", Network }, p.Documents.Select(d => d.FileName));
        Assert.False(p.HasOverlay);
        Assert.Empty(p.CollectionsOf(CaseRoles.Overlay));
        Assert.Empty(ReferenceIndex.Build(p).OverlayRecords);
        // The model collections are the same with or without the overlay.
        Assert.Equal(p.Collections.Select(c => (c.Document, c.ArrayPointer, c.Role)),
            s.Collections.Where(c => c.Role != CaseRoles.Overlay).Select(c => (c.Document, c.ArrayPointer, c.Role)));
    }

    [Fact]
    public void Check_of_a_good_overlay_passes_and_the_v2_loader_reads_the_staged_case_with_it()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        SaveReport r = SaveGate.Run(Open(f), new SaveOptions(CheckOnly: true));
        Assert.Equal(SaveOutcome.CheckPassed, r.Outcome);
        Assert.Empty(r.Errors);
        Assert.Contains("the v2 loader reads the case (staged copy, dispatch on, as every verb loads it).", r.Notes);
    }

    // =================================================================================================================
    // Lossless edits and bit identity
    // =================================================================================================================

    [Fact]
    public void An_overlay_param_edit_saved_changes_only_that_token_and_the_reloaded_overlay_and_model_are_as_expected()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        V2CaseLoadResult original = Load(f.ManifestPath);
        CaseSession s = Open(f);

        // A bare value: its token replaced.
        RecordRef cbl = Rec(s, "ov.cbl");
        OpResult r = OverlayOps.SetParam(s, cbl, "ratedCurrentA", "4500");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("params.ratedCurrentA 4000 -> 4500 (U to undo).", r.Message);
        // The object form: only its v (unit and provenance kept).
        RecordRef bb1 = Rec(s, "ov.bb1");
        Assert.Equal("params.ratedCurrentA 4000 -> 4100 (U to undo).", OverlayOps.SetParam(s, bb1, "ratedCurrentA", "4100").Message);
        // Typing the same value again is no change and no undo step.
        int depth = s.UndoDepth;
        Assert.False(OverlayOps.SetParam(s, cbl, "ratedCurrentA", "4500.0").Changed);
        Assert.Equal(depth, s.UndoDepth);

        SaveReport saved = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(new[] { FileA }, saved.FilesWritten);

        byte[] expected = Spliced(before["assets/site.assets.json"], FileA, cbl.Pointer + "/params/ratedCurrentA", "4500");
        expected = Spliced(expected, FileA, bb1.Pointer + "/params/ratedCurrentA/v", "4100");
        Dictionary<string, byte[]> after = Files(f.CaseDir);
        Assert.Equal(expected, after["assets/site.assets.json"]);
        foreach (string other in before.Keys.Where(k => k != "assets/site.assets.json"))
            Assert.Equal(before[other], after[other]);

        V2CaseLoadResult reloaded = Load(f.ManifestPath);
        Assert.Equal(4500, Param(reloaded.Overlay!, "ov.cbl", "ratedCurrentA").GetDouble());
        JsonElement obj = Param(reloaded.Overlay!, "ov.bb1", "ratedCurrentA");
        Assert.Equal(4100, obj.GetProperty("v").GetDouble());
        Assert.Equal("A", obj.GetProperty("unit").GetString());
        Assert.Equal("Synthetic", obj.GetProperty("prov").GetProperty("tag").GetString());
        Assert.Empty(ModelBits.Differences(original.Model, reloaded.Model));
        Assert.Equal(original.Notes, reloaded.Notes);
    }

    [Fact]
    public void Undo_of_overlay_edits_gives_the_original_bytes_back()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        byte[] a = s.Doc(FileA).Text.ToArray(), b = s.Doc(FileB).Text.ToArray();
        Assert.True(OverlayOps.SetParam(s, Rec(s, "ov.cbl"), "breakingCurrentA", "50000").Ok);
        Assert.True(OverlayOps.SetRef(s, Rec(s, "ov.cbl"), "line", "L2-3 B6 boundary").Ok);
        Assert.True(OverlayOps.ResetParam(s, Rec(s, "ov.l6"), "heightM").Ok);
        Assert.True(CaseOps.RenameKey(s, Rec(s, "ov.zebra"), "ov.zebra2").Ok);
        Assert.True(s.IsDirty);
        while (s.CanUndo) s.Undo();
        Assert.Equal(a, s.Doc(FileA).Text.ToArray());
        Assert.Equal(b, s.Doc(FileB).Text.ToArray());
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void The_model_is_bit_identical_after_any_overlay_only_edits_saved()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        V2CaseLoadResult original = Load(f.ManifestPath);
        CaseSession s = Open(f);

        Assert.True(OverlayOps.SetParam(s, Rec(s, "ov.sub"), "latitudeDeg", "57.6").Ok);
        Assert.True(OverlayOps.SetParam(s, Rec(s, "ov.cpl"), "normalOpen", "true").Ok);
        Assert.True(OverlayOps.SetRef(s, Rec(s, "ov.bb2"), "bus", "102").Ok);
        Assert.True(OverlayOps.SetRef(s, Rec(s, "ov.cbg"), "generator", "Torness Nuclear").Ok);
        Assert.True(OverlayOps.ResetRef(s, Rec(s, "ov.cpl"), "to").Ok);
        Assert.True(OverlayOps.SetParent(s, Rec(s, "ov.span1"), "ov.route").Changed == false);   // already its parent
        Assert.True(CaseOps.SetField(s, Rec(s, "ov.sub").Document, Rec(s, "ov.sub").Pointer, "note", "edited").Ok);
        Assert.True(CaseOps.DuplicateRecord(s, Rec(s, "ov.ds")).Ok);
        Assert.True(CaseOps.DeleteRecord(s, Rec(s, "ov.es")).Ok);
        Assert.True(CaseOps.RenameKey(s, Rec(s, "ov.soil"), "ov.soil.north").Ok);
        Assert.True(OverlayOps.AddRecord(s, Rec(s, "ov.sub").Document, "busbarSection", "ov.bb3", "ov.sub",
            new[] { new KeyValuePair<string, string>("bus", "102") }, new[] { new KeyValuePair<string, string>("label", "Reserve") }).Ok);

        SaveReport saved = SaveGate.Run(s);
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        Assert.Equal(new[] { FileA, FileB }, saved.FilesWritten);
        Dictionary<string, byte[]> after = Files(f.CaseDir);
        Assert.Equal(before["uk.case.json"], after["uk.case.json"]);
        Assert.Equal(before[Network], after[Network]);

        V2CaseLoadResult reloaded = Load(f.ManifestPath);
        Assert.Empty(ModelBits.Differences(original.Model, reloaded.Model));
        Assert.Equal(UiOverlayFixture.RecordCount + 1, reloaded.Overlay!.Records.Count);   // + a copy + a new one - a deleted one
        Assert.Equal("ov.soil.north", reloaded.Overlay.Find("ov.earth")!.Refs.Single(x => x.Role == "soil").Key);
        Assert.Equal(102, reloaded.Overlay.Find("ov.bb3")!.Refs.Single().Bus);
        Assert.NotNull(reloaded.Overlay.Find("ov.ds-copy"));
        Assert.Null(reloaded.Overlay.Find("ov.es"));
    }

    // =================================================================================================================
    // Deletes and renames of model records an overlay names
    // =================================================================================================================

    [Fact]
    public void Deleting_a_model_record_an_overlay_record_names_is_refused_listing_the_overlay_referrers()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        byte[] net = s.Doc(Network).Text.ToArray();

        RecordRef demand = Model(s, CaseRoles.Demands, "North Scotland");
        OpResult d = CaseOps.DeleteRecord(s, demand);
        Assert.False(d.Ok);
        Assert.Equal($"Delete refused: 1 record(s) refer to {Network} demands #{demand.Index.ToString(CultureInfo.InvariantCulture)} 'North Scotland': "
            + $"{FileA} record 'ov.cbd' (switchingDevice) (refs.demand)", d.Message);

        RecordRef moyle = Model(s, CaseRoles.Generators, "Moyle");
        OpResult g = CaseOps.DeleteRecord(s, moyle);
        Assert.Equal($"Delete refused: 1 record(s) refer to {Network} generators #{moyle.Index.ToString(CultureInfo.InvariantCulture)} 'Moyle': "
            + $"{FileA} record 'ov.hvdc' (hvdcLink) (refs.sendingConverter)", g.Message);

        // A bus: the overlay's referrers listed FIRST (a bus can have more model referrers than a refusal shows).
        RecordRef bus1 = Model(s, CaseRoles.Buses, bus: 1);
        int model = ReferenceIndex.Build(s).BusReferrers(1).Count;
        OpResult b = CaseOps.DeleteRecord(s, bus1);
        Assert.False(b.Ok);
        string what = RecordRead.Describe(s.Documents[bus1.Document], s.Doc(Network).Text.Resolve(bus1.Pointer)!, bus1.Index, bus1.ArrayPointer);
        Assert.StartsWith($"Delete refused: {(model + 2).ToString(CultureInfo.InvariantCulture)} record(s) refer to {what}: "
            + $"{FileA} record 'ov.bb1' (busbarSection) (refs.bus), {FileA} record 'ov.earth' (earthPath) (refs.bus)", b.Message, StringComparison.Ordinal);

        // A line and a transformer: their overlay referrers among the rest.
        Assert.Contains($"{FileA} record 'ov.cbl' (switchingDevice) (refs.line)", CaseOps.DeleteRecord(s, Model(s, CaseRoles.Lines, "L1-2 N-S Scotland")).Message, StringComparison.Ordinal);
        Assert.Contains($"{FileB} record 'ov.route' (lineRoute) (refs.line)", CaseOps.DeleteRecord(s, Model(s, CaseRoles.Lines, "L2-3 B6 boundary")).Message, StringComparison.Ordinal);
        Assert.Contains($"{FileA} record 'ov.t3w' (transformer3W) (refs.hv)", CaseOps.DeleteRecord(s, Model(s, CaseRoles.Transformers, "T2 GSP S Scotland")).Message, StringComparison.Ordinal);

        Assert.Equal(net, s.Doc(Network).Text.ToArray());
        Assert.False(s.IsDirty);

        // The where-used list of the model record includes them too.
        Assert.Equal(new[] { "refs.demand" }, CaseOps.ReferrersOf(s, demand).Select(x => x.Field));
    }

    [Fact]
    public void A_generator_name_an_overlay_record_names_cannot_change_and_says_why()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        RecordRef moyle = Model(s, CaseRoles.Generators, "Moyle");
        string title = s.CollectionsOf(CaseRoles.Generators).First().Shape.Field("name")!.Title;
        OpResult r = CaseOps.SetField(s, moyle.Document, moyle.Pointer, "name", "Moyle 2");
        Assert.False(r.Ok);
        Assert.Equal($"{title}: 1 asset overlay record(s) refer to this generator by its name, so it cannot change (a generator name is not a key and has no Rename); "
            + $"change or remove those references first. Referrers: {FileA} record 'ov.hvdc' (hvdcLink) (refs.sendingConverter)", r.Message);
        Assert.False(s.IsDirty);
        // A generator nothing in the overlay names renames freely, exactly as before.
        ReferenceIndex ix = ReferenceIndex.Build(s);
        RecordCollection gens = s.CollectionsOf(CaseRoles.Generators).First();
        int free = s.ArrayOf(gens)!.Elements.Select((g, i) => (g, i))
            .First(x => ix.OverlayNameReferrers(AssetRefTarget.Generator, RecordRead.Str(x.g, "name", StringComparer.Ordinal) ?? "").Count == 0).i;
        OpResult renamed = CaseOps.SetField(s, gens.Document, gens.ArrayPointer + "/" + free.ToString(CultureInfo.InvariantCulture), "name", "A new station name");
        Assert.True(renamed.Ok && renamed.Changed, renamed.Message);
    }

    [Fact]
    public void Renaming_a_bus_a_line_or_a_transformer_cascades_into_the_overlay_references_in_one_step()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        int depth = s.UndoDepth;

        OpResult bus = CaseOps.RenameKey(s, Model(s, CaseRoles.Buses, bus: 1), "9001");
        Assert.True(bus.Ok, bus.Message);
        Assert.Equal(depth + 1, s.UndoDepth);   // one step across the network and the overlay files
        JsonSourceText a = s.Doc(FileA).Text;
        Assert.Equal("9001", a.RawText(a.Resolve(Rec(s, "ov.bb1").Pointer + "/refs/bus/bus")!));
        Assert.Equal("9001", a.RawText(a.Resolve(Rec(s, "ov.earth").Pointer + "/refs/bus/bus")!));

        Assert.True(CaseOps.RenameKey(s, Model(s, CaseRoles.Lines, "L1-2 N-S Scotland"), "L1-2 renamed").Ok);
        Assert.True(CaseOps.RenameKey(s, Model(s, CaseRoles.Lines, "L2-3 B6 boundary"), "L2-3 renamed").Ok);
        Assert.True(CaseOps.RenameKey(s, Model(s, CaseRoles.Transformers, "T2 GSP S Scotland"), "T2 renamed").Ok);
        a = s.Doc(FileA).Text;
        JsonSourceText b = s.Doc(FileB).Text;
        Assert.Equal("\"L1-2 renamed\"", a.RawText(a.Resolve(Rec(s, "ov.cbl").Pointer + "/refs/line/line")!));
        Assert.Equal("\"L2-3 renamed\"", b.RawText(b.Resolve(Rec(s, "ov.route").Pointer + "/refs/line/line")!));
        Assert.Equal("\"T2 renamed\"", a.RawText(a.Resolve(Rec(s, "ov.t3w").Pointer + "/refs/hv/transformer")!));

        // The case still checks (every reference still resolves), and saves with all of it.
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.True(check.Outcome == SaveOutcome.CheckPassed, string.Join(Environment.NewLine, check.Lines()));
        Assert.True(SaveGate.Run(s, new SaveOptions(ConfirmNewWarnings: true)).Saved);
        CaseAssetOverlay o = Load(f.ManifestPath).Overlay!;
        Assert.Equal(9001, o.Find("ov.bb1")!.Refs.Single().Bus);
        Assert.Equal("L1-2 renamed", o.Find("ov.cbl")!.Refs.Single(x => x.Role == "line").Key);
    }

    // =================================================================================================================
    // Overlay records: delete, rename, duplicate
    // =================================================================================================================

    [Fact]
    public void Deleting_an_overlay_record_with_children_or_record_refs_pointing_at_it_is_refused_listing_them()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);

        OpResult soil = CaseOps.DeleteRecord(s, Rec(s, "ov.soil"));
        Assert.Equal($"Delete refused: 1 record(s) refer to {FileB} record 'ov.soil' (soilZone): {FileA} record 'ov.earth' (earthPath) (refs.soil)", soil.Message);

        OpResult bb2 = CaseOps.DeleteRecord(s, Rec(s, "ov.bb2"));
        Assert.Equal($"Delete refused: 3 record(s) refer to {FileA} record 'ov.bb2' (busbarSection): "
            + $"{FileA} record 'ov.cpl' (switchingDevice) (refs.to), {FileA} record 'ov.cbd' (switchingDevice) (refs.from), {FileA} record 'ov.es' (switchingDevice) (refs.from)", bb2.Message);

        OpResult sub = CaseOps.DeleteRecord(s, Rec(s, "ov.sub"));
        Assert.StartsWith($"Delete refused: 11 record(s) refer to {FileA} record 'ov.sub' (substation): {FileA} record 'ov.bb1' (busbarSection) (parent), ", sub.Message, StringComparison.Ordinal);

        OpResult route = CaseOps.DeleteRecord(s, Rec(s, "ov.route"));
        Assert.Equal($"Delete refused: 2 record(s) refer to {FileB} record 'ov.route' (lineRoute): {FileB} record 'ov.span1' (span) (parent), {FileB} record 'ov.ew' (earthWire) (parent)", route.Message);
        Assert.False(s.IsDirty);

        // A leaf goes, in one step; its referrers then let the next one go.
        OpResult es = CaseOps.DeleteRecord(s, Rec(s, "ov.es"));
        Assert.Equal($"Deleted {FileA} record 'ov.es' (switchingDevice) (U to undo).", es.Message);
        Assert.False(ReferenceIndex.Build(s).OverlayRecords.ContainsKey("ov.es"));
    }

    [Fact]
    public void Renaming_an_overlay_id_cascades_into_parents_and_record_refs_and_a_clash_or_a_bad_id_is_refused()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);

        OpResult dry = CaseOps.RenameKey(s, Rec(s, "ov.bb2"), "ov.bb2x", dryRun: true);
        Assert.Equal($"Rename id \"ov.bb2\" -> \"ov.bb2x\": 3 reference(s) follow: {FileA} record 'ov.cpl' (switchingDevice) (refs.to), "
            + $"{FileA} record 'ov.cbd' (switchingDevice) (refs.from), {FileA} record 'ov.es' (switchingDevice) (refs.from)", dry.Message);
        Assert.False(s.IsDirty);

        OpResult r = CaseOps.RenameKey(s, Rec(s, "ov.bb2"), "ov.bb2x");
        Assert.Equal("Rename id \"ov.bb2\" -> \"ov.bb2x\": 3 reference(s) follow (U to undo).", r.Message);
        Assert.Equal(3, ReferenceIndex.Build(s).OverlayRecordReferrers("ov.bb2x").Count);
        Assert.Empty(ReferenceIndex.Build(s).OverlayRecordReferrers("ov.bb2"));

        // Parents follow too (a rename across files: ov.soil is in file B, its referrer in file A).
        OpResult sub = CaseOps.RenameKey(s, Rec(s, "ov.sub"), "sub.north");
        Assert.Equal("Rename id \"ov.sub\" -> \"sub.north\": 11 reference(s) follow (U to undo).", sub.Message);
        Assert.True(CaseOps.RenameKey(s, Rec(s, "ov.soil"), "soil.1").Ok);
        Assert.Equal(SaveOutcome.CheckPassed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);

        Assert.Equal($"Rename refused: overlay record id 'ov.bb1' is already used by {FileA} /records/0; an id must be unique across every overlay file of the case.",
            CaseOps.RenameKey(s, Rec(s, "ov.cpl"), "ov.bb1").Message);
        OpResult bad = CaseOps.RenameKey(s, Rec(s, "ov.cpl"), "bad id");
        Assert.False(bad.Ok);
        Assert.StartsWith("Rename refused, the record would not be valid: ", bad.Message, StringComparison.Ordinal);
        // The id field with referrers changes only by Rename (the key rule), listing them.
        RecordRef bb1 = Rec(s, "ov.bb1");
        OpResult set = CaseOps.SetField(s, bb1.Document, bb1.Pointer, "id", "ov.bb9");
        Assert.False(set.Ok);
        Assert.Contains("record(s) refer to this id; use Rename (R) to change it everywhere in one step.", set.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicating_an_overlay_record_gives_the_copy_a_fresh_id()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        RecordRef ds = Rec(s, "ov.ds");
        OpResult r = CaseOps.DuplicateRecord(s, ds);
        Assert.Equal($"Duplicated {FileA} record 'ov.ds-copy' (switchingDevice). The copy is record 'ov.ds-copy' (its children, if any, are not copied). (U to undo)", r.Message);
        Assert.Equal(ds.WithIndex(ds.Index + 1), r.Select);
        Assert.True(CaseOps.DuplicateRecord(s, ds).Ok);   // a second copy: -copy is taken, so -copy-2
        Assert.True(ReferenceIndex.Build(s).OverlayRecords.ContainsKey("ov.ds-copy-2"));
        Assert.Equal(SaveOutcome.CheckPassed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);
    }

    // =================================================================================================================
    // Parameters, references and parents by the kind spec
    // =================================================================================================================

    [Fact]
    public void Params_refs_and_parents_follow_the_kind_spec_with_exact_refusals()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        RecordRef cbl = Rec(s, "ov.cbl"), cpl = Rec(s, "ov.cpl"), sub = Rec(s, "ov.sub"), bb2 = Rec(s, "ov.bb2");

        Assert.Equal("params.device: 'fuse' is not one of: breaker, disconnector, earthSwitch.", OverlayOps.SetParam(s, cbl, "device", "fuse").Message);
        OpResult lots = OverlayOps.SetParam(s, cbl, "ratedCurrentA", "lots");
        Assert.False(lots.Ok);
        Assert.StartsWith("params.ratedCurrentA: ", lots.Message, StringComparison.Ordinal);   // the strict number parser's own reason follows
        Assert.Equal($"params.ratedCurrentA: refused, the record would not be valid: {cbl.Pointer}/params/ratedCurrentA: -1 A is below the minimum 0 for 'ratedCurrentA'.",
            OverlayOps.SetParam(s, cbl, "ratedCurrentA", "-1").Message);
        Assert.Equal("record 'ov.cbl': kind 'switchingDevice' has no parameter 'colour' (declared: device, normalOpen, ratedCurrentA, breakingCurrentA).",
            OverlayOps.SetParam(s, cbl, "colour", "red").Message);
        Assert.Equal("params.device is required by kind 'switchingDevice' and cannot be reset to absent.", OverlayOps.ResetParam(s, cbl, "device").Message);
        Assert.Equal("params.device \"breaker\" -> \"disconnector\" (U to undo).", OverlayOps.SetParam(s, cbl, "device", "disconnector").Message);

        Assert.Equal("refs.line: no line of the case is named 'nope' (names must match exactly).", OverlayOps.SetRef(s, cbl, "line", "nope").Message);
        Assert.Equal("refs.to: role 'to' must point at a record of kind busbarSection, but 'ov.sub' is a 'substation'.", OverlayOps.SetRef(s, cpl, "to", "ov.sub").Message);
        Assert.Equal("refs.to: a record cannot point at itself.", OverlayOps.SetRef(s, cpl, "to", "ov.cpl").Message);
        Assert.Equal("refs.bus: bus 99999 is not a bus of the case.", OverlayOps.SetRef(s, bb2, "bus", "99999").Message);
        Assert.Equal("record 'ov.cbl': kind 'switchingDevice' has no role 'bus' (declared: from, to, line, transformer, generator, demand).", OverlayOps.SetRef(s, cbl, "bus", "1").Message);
        Assert.Equal("refs.from is required by kind 'switchingDevice' (a record) and cannot be reset to absent.", OverlayOps.ResetRef(s, cpl, "from").Message);
        Assert.Equal("refs.to \"ov.bb2\" -> \"ov.bb1\" (U to undo).", OverlayOps.SetRef(s, cpl, "to", "ov.bb1").Message);

        Assert.Equal("parent: a 'substation' record sits at the top level and takes no parent.", OverlayOps.SetParent(s, sub, "ov.l6").Message);
        Assert.Equal("parent: the parent of a 'busbarSection' record must be of kind substation, but 'ov.route' is a 'lineRoute'.", OverlayOps.SetParent(s, bb2, "ov.route").Message);
        Assert.Equal("parent: 'ov.none' is not a record of the overlay.", OverlayOps.SetParent(s, bb2, "ov.none").Message);
        Assert.Equal("parent is required: a 'busbarSection' record must sit under a record of kind substation.", OverlayOps.ResetParent(s, bb2).Message);
        Assert.Equal(SaveOutcome.CheckPassed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);
    }

    [Fact]
    public void Adding_a_record_by_kind_appends_it_in_schema_order_and_refuses_what_the_kind_spec_refuses()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        CaseSession s = Open(f);
        int docA = s.Doc(FileA).Id;
        static KeyValuePair<string, string>[] Kv(params (string K, string V)[] x) => x.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToArray();

        OpResult ok = OverlayOps.AddRecord(s, docA, "switchingDevice", "ov.cb2", "ov.sub", Kv(("from", "ov.bb2"), ("demand", "North Scotland")),
            Kv(("device", "breaker"), ("normalOpen", "n"), ("ratedCurrentA", "2000")));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal($"Added {FileA} record 'ov.cb2' (switchingDevice) (U to undo).", ok.Message);
        JsonSourceText a = s.Doc(FileA).Text;
        string written = a.RawText(a.Resolve(ok.Select!.Value.Pointer)!);
        Assert.True(JsonElement.DeepEquals(EditorTestSupport.Element(
            "{ \"id\": \"ov.cb2\", \"kind\": \"switchingDevice\", \"parent\": \"ov.sub\", \"refs\": { \"from\": { \"record\": \"ov.bb2\" }, \"demand\": { \"demand\": \"North Scotland\" } }, "
            + "\"params\": { \"device\": \"breaker\", \"normalOpen\": false, \"ratedCurrentA\": 2000 } }"), EditorTestSupport.Element(written)), written);
        // Members in schema order, refs and params in the kind's order, on one line like its neighbours.
        Assert.True(written.IndexOf("\"id\"", StringComparison.Ordinal) < written.IndexOf("\"kind\"", StringComparison.Ordinal)
                    && written.IndexOf("\"parent\"", StringComparison.Ordinal) < written.IndexOf("\"refs\"", StringComparison.Ordinal)
                    && written.IndexOf("\"from\"", StringComparison.Ordinal) < written.IndexOf("\"demand\"", StringComparison.Ordinal)
                    && written.IndexOf("\"refs\"", StringComparison.Ordinal) < written.IndexOf("\"params\"", StringComparison.Ordinal), written);
        Assert.DoesNotContain('\n', written);

        Assert.Equal("A new 'switchingDevice' record needs: role from (a record), parameter normalOpen.",
            OverlayOps.AddRecord(s, docA, "switchingDevice", "ov.x", "ov.sub", Kv(), Kv(("device", "breaker"))).Message);
        Assert.Equal($"id: overlay record id 'ov.sub' is already used by {FileA} /records/1; an id must be unique across every overlay file of the case.",
            OverlayOps.AddRecord(s, docA, "substation", "ov.sub", null, Kv(), Kv()).Message);
        Assert.StartsWith("'fuseBox' is not an asset overlay kind (known: busbarSection, ", OverlayOps.AddRecord(s, docA, "fuseBox", "ov.x", null, Kv(), Kv()).Message, StringComparison.Ordinal);
        Assert.Equal("parent: a 'busbarSection' record must have a parent (of kind substation).",
            OverlayOps.AddRecord(s, docA, "busbarSection", "ov.x", null, Kv(("bus", "1")), Kv()).Message);
        Assert.Equal("refs.bus: bus 4242 is not a bus of the case.", OverlayOps.AddRecord(s, docA, "busbarSection", "ov.x", "ov.sub", Kv(("bus", "4242")), Kv()).Message);
        OpResult badId = OverlayOps.AddRecord(s, docA, "substation", "-bad", null, Kv(), Kv());
        Assert.StartsWith("A new 'substation' record would not be valid: ", badId.Message, StringComparison.Ordinal);
        Assert.Equal("an overlay record is added to an asset overlay file of the case (one the manifest lists in \"assets\").",
            OverlayOps.AddRecord(s, s.Doc(Network).Id, "substation", "ov.x", null, Kv(), Kv()).Message);
        Assert.Equal(1, s.UndoDepth);   // only the successful add changed anything
        Assert.Equal(SaveOutcome.CheckPassed, SaveGate.Run(s, new SaveOptions(CheckOnly: true)).Outcome);
    }

    // =================================================================================================================
    // The save gate and the overlay checker
    // =================================================================================================================

    /// <summary>File A with three problems the schema cannot see: an enum value the kind refuses, a bus the case does not
    /// have, a required role missing.</summary>
    internal static string BadOverlayA()
    {
        string bad = UiOverlayFixture.OverlayA
            .Replace("\"device\": \"breaker\", \"normalOpen\": false, \"ratedCurrentA\": 4000", "\"device\": \"fuse\", \"normalOpen\": false, \"ratedCurrentA\": 4000", StringComparison.Ordinal)
            .Replace("\"refs\": { \"bus\": { \"bus\": 101 } }", "\"refs\": { \"bus\": { \"bus\": 99999 } }", StringComparison.Ordinal)
            .Replace("{ \"id\": \"ov.ds\", \"kind\": \"switchingDevice\", \"parent\": \"ov.sub\", \"refs\": { \"from\": { \"record\": \"ov.bb1\" } }",
                     "{ \"id\": \"ov.ds\", \"kind\": \"switchingDevice\", \"parent\": \"ov.sub\", \"refs\": { }", StringComparison.Ordinal);
        Assert.NotEqual(UiOverlayFixture.OverlayA, bad);
        return bad;
    }

    [Fact]
    public void The_save_gate_refuses_an_overlay_the_checker_fails_listing_every_problem_and_nothing_else()
    {
        using var f = new UiOverlayFixture(withOverlay: true, overlayA: BadOverlayA());
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseSession s = Open(f);
        Assert.NotNull(s.Preflight.LoaderError);   // pre-flight found them at open (they do not block editing)

        // What the checker itself says, as the save gate's problems.
        var docs = s.AssetDocuments.Select(d => (d.FileName, d.Text.ToElement())).ToList();
        AssetOverlayTargets targets = AssetOverlayTargets.FromNetworks(new[] { s.Doc(Network).Text.ToElement() });
        IReadOnlyList<SchemaError> checker = V2AssetChecker.Check(docs, new SchemaValidator(V2TestSupport.RealSchemas()),
            OverlayKindSet.LoadDirectory(Path.Combine(V2TestSupport.KindsDir, OverlayKindSet.FolderName), V2TestSupport.RealSchemas()), targets);
        var expected = checker.Select(e => new SaveProblem(e.File!, e.Pointer, e.Message)).ToList();
        Assert.Equal(3, expected.Count);
        Assert.Contains(expected, p => p.Message == "record 'ov.cbl': value \"fuse\" is not one of the allowed values: breaker, disconnector, earthSwitch.");
        Assert.Contains(expected, p => p.Message == "record 'ov.bb2': role 'bus' points at bus 99999, which is not a bus of the case.");
        Assert.Contains(expected, p => p.Message == "record 'ov.ds': a 'switchingDevice' record requires role 'from' (a record), which is missing.");

        // A good edit elsewhere (so there is something to save): refused with exactly the checker's problems.
        Assert.True(OverlayOps.SetParam(s, Rec(s, "ov.l6"), "heightM", "55").Ok);
        SaveReport save = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, save.Outcome);
        Assert.Equal("NOT SAVED: 3 error(s). The files on disk are unchanged.", save.Headline);
        Assert.Equal(expected, save.Errors);
        Assert.Equal(before, Files(f.CaseDir));

        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.Equal(SaveOutcome.CheckFailed, check.Outcome);
        Assert.Equal(expected, check.Errors);
    }

    [Fact]
    public void An_overlay_broken_in_the_session_is_refused_by_the_save_gate_too()
    {
        using var f = new UiOverlayFixture(withOverlay: true);
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseSession s = Open(f);
        // The generic field edit can change a kind (the Author grid locks it); the checker then refuses the record.
        RecordRef hvdc = Rec(s, "ov.hvdc");
        Assert.True(CaseOps.SetField(s, hvdc.Document, hvdc.Pointer, "kind", "soilZone").Ok);
        SaveReport save = SaveGate.Run(s);
        Assert.Equal(SaveOutcome.Refused, save.Outcome);
        Assert.All(save.Errors, e => Assert.Equal(FileA, e.File));
        Assert.Contains(save.Errors, e => e.Message == "record 'ov.hvdc': kind 'soilZone' declares no roles, so role 'sendingConverter' cannot be given.");
        Assert.Equal(before, Files(f.CaseDir));
    }
}
