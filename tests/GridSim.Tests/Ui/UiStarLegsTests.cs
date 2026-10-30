using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 5f (2026-10-20): the three-winding transformer of the asset overlay and its "derive star legs"
/// helper.
/// <list type="bullet">
/// <item>The pure maths (StarLegsMath): hand-worked cases with exact expected doubles, each derived HERE from the formula
/// (Z1 = (Z12 + Z13 - Z23) / 2 and cyclic; Z_new = Z_old * S_new / S_old; times (V_rated / V_base)^2), not copied from the
/// implementation: equal ratings, unequal pair bases referred to a common base, a voltage referral, and a negative derived
/// reactance (physically possible for a star leg: shown and written, never refused). The values are chosen with short
/// binary fractions, so every product and sum is exact and the expected doubles are exact whatever the order of
/// evaluation.</item>
/// <item>Refusals, with the reason: no MVA base, a missing pairwise impedance, a leg that does not touch the star bus, a
/// rated voltage with no bus base voltage.</item>
/// <item>Author mode: the six proposed edits (old -> new); apply = ONE undo step that changes exactly the six leg values
/// (every other byte of every document unchanged, the new values written losslessly); undo restores every document bit
/// for bit; a stale proposal is refused; nothing is written without the confirmation.</item>
/// <item>Run mode: the OVERVIEW tables (nameplate, pairwise as authored, the legs authored beside derived) and the CHECK
/// consistency note (display only), and the solved frames bit-identical with and without the overlay.</item>
/// </list>
/// The case is data/uk with a three-winding transformer added as its authored star equivalent: a star bus 201 and three
/// legs '3W HV' (2 -> 201), '3W MV' (201 -> 102) and '3W LV' (201 -> 202, a new 13 kV bus), each r 0.0017, x 0.05.
/// </summary>
public sealed class UiStarLegsTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static void Same(double expected, double actual, string what) =>
        Assert.True(Bits(expected) == Bits(actual), $"{what}: expected {expected:R} (bits {Bits(expected)}), got {actual:R} (bits {Bits(actual)})");

    // The hand-worked pairwise impedances (per unit on 100 MVA): short binary fractions, so every step is exact.
    private const double R12 = 0.0078125, X12 = 0.125, R13 = 0.015625, X13 = 0.25, R23 = 0.01171875, X23 = 0.1875;

    // =================================================================================================================
    // The pure maths
    // =================================================================================================================

    [Fact]
    public void Equal_ratings_give_the_star_legs_of_the_formula_exactly()
    {
        var z12 = new SeriesImpedance(R12, X12);
        var z13 = new SeriesImpedance(R13, X13);
        var z23 = new SeriesImpedance(R23, X23);
        (SeriesImpedance z1, SeriesImpedance z2, SeriesImpedance z3) = StarLegsMath.Star(z12, z13, z23);
        // Independently from the formula.
        Same((R12 + R13 - R23) / 2, z1.R, "R1");
        Same((X12 + X13 - X23) / 2, z1.X, "X1");
        Same((R12 + R23 - R13) / 2, z2.R, "R2");
        Same((X12 + X23 - X13) / 2, z2.X, "X2");
        Same((R13 + R23 - R12) / 2, z3.R, "R3");
        Same((X13 + X23 - X12) / 2, z3.X, "X3");
        // And by hand: 3/512, 3/32; 1/512, 1/32; 5/512, 5/32.
        Same(0.005859375, z1.R, "R1 by hand");
        Same(0.09375, z1.X, "X1 by hand");
        Same(0.001953125, z2.R, "R2 by hand");
        Same(0.03125, z2.X, "X2 by hand");
        Same(0.009765625, z3.R, "R3 by hand");
        Same(0.15625, z3.X, "X3 by hand");
        // The legs add back to the pairs: Z1 + Z2 = Z12, Z1 + Z3 = Z13, Z2 + Z3 = Z23 (exact here).
        Same(X12, z1.X + z2.X, "X1 + X2");
        Same(X13, z1.X + z3.X, "X1 + X3");
        Same(X23, z2.X + z3.X, "X2 + X3");
        // Equal ratings: a referral to the same base changes nothing (factor exactly 1).
        Assert.Equal(z12, StarLegsMath.Rebase(z12, 100, 100));
    }

    [Fact]
    public void Unequal_pair_bases_are_referred_to_the_common_base_before_the_star_and_then_to_the_case_base()
    {
        // HV-MV quoted on 200 MVA, HV-LV on 100 MVA, MV-LV on 50 MVA; the common base is 100 MVA; the case base 50 MVA.
        double s12 = 200, s13 = 100, s23 = 50, common = 100, system = 50;
        SeriesImpedance a12 = StarLegsMath.Rebase(new SeriesImpedance(R12, X12), s12, common);
        SeriesImpedance a13 = StarLegsMath.Rebase(new SeriesImpedance(R13, X13), s13, common);
        SeriesImpedance a23 = StarLegsMath.Rebase(new SeriesImpedance(R23, X23), s23, common);
        // Z_new = Z_old * S_new / S_old.
        Same(X12 * common / s12, a12.X, "X12 on 100 MVA");   // 0.0625
        Same(R12 * common / s12, a12.R, "R12 on 100 MVA");
        Same(X13, a13.X, "X13 on 100 MVA (same base)");
        Same(X23 * common / s23, a23.X, "X23 on 100 MVA");   // 0.375
        Same(0.0625, a12.X, "X12 by hand");
        Same(0.375, a23.X, "X23 by hand");
        (SeriesImpedance z1, SeriesImpedance z2, SeriesImpedance z3) = StarLegsMath.Star(a12, a13, a23);
        double x12 = X12 * common / s12, x13 = X13 * common / s13, x23 = X23 * common / s23;
        Same((x12 + x13 - x23) / 2, z1.X, "X1");   // -0.03125
        Same((x12 + x23 - x13) / 2, z2.X, "X2");   // 0.09375
        Same((x13 + x23 - x12) / 2, z3.X, "X3");   // 0.28125
        Same(-0.03125, z1.X, "X1 by hand");
        // To the case base: Z * S_system / S_common, voltage factor 1.
        SeriesImpedance onCase = StarLegsMath.ToSystem(z3, common, system, 1.0);
        Same(((x13 + x23 - x12) / 2) * system / common, onCase.X, "X3 on the case base");   // 0.140625
        Same(0.140625, onCase.X, "X3 on the case base by hand");
    }

    [Fact]
    public void The_voltage_referral_is_the_square_of_rated_over_base()
    {
        // A winding rated 264 kV on a 132 kV bus base: (264 / 132)^2 = 4; rated 66 kV on 132 kV: 1/4.
        Same(264.0 / 132.0 * (264.0 / 132.0), StarLegsMath.VoltageFactor(264, 132), "factor 4");
        Same(4.0, StarLegsMath.VoltageFactor(264, 132), "factor 4 by hand");
        Same(0.25, StarLegsMath.VoltageFactor(66, 132), "factor 1/4 by hand");
        SeriesImpedance z = StarLegsMath.ToSystem(new SeriesImpedance(R12, X12), 100, 10000, 4.0);
        Same(R12 * (10000.0 / 100.0) * 4.0, z.R, "R on the case base");
        Same(X12 * (10000.0 / 100.0) * 4.0, z.X, "X on the case base");
        Same(50.0, z.X, "X on the case base by hand");
        // A base that is not a finite power above zero is refused loudly.
        Assert.Throws<ArgumentOutOfRangeException>(() => StarLegsMath.Rebase(z, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarLegsMath.ToSystem(z, 100, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarLegsMath.VoltageFactor(132, -1));
    }

    [Fact]
    public void A_negative_derived_reactance_is_shown_and_not_refused()
    {
        // X13 larger than X12 + X23: the MV leg's reactance comes out negative, which a star equivalent allows.
        double x12 = 0.125, x13 = 0.5, x23 = 0.25;
        (_, SeriesImpedance z2, _) = StarLegsMath.Star(new SeriesImpedance(0, x12), new SeriesImpedance(0, x13), new SeriesImpedance(0, x23));
        Same((x12 + x23 - x13) / 2, z2.X, "X2");
        Same(-0.0625, z2.X, "X2 by hand");

        StarLegsDerivation d = StarLegsHelper.Derive(Record(x13: "0.5", x23: "0.25"), 201, Legs(), Array.Empty<string>(), 10000);
        Assert.True(d.Ok, d.Refusal);
        Same(-0.0625 * 100, d.Legs[1].Derived.X, "MV x on the case base");
        Assert.Contains(d.Lines, l => l.StartsWith("A derived leg value is negative: that is physically possible", StringComparison.Ordinal));
    }

    // A transformer3W record as the overlay carries it (params as JSON text; any member may be dropped or replaced).
    private static AssetOverlayRecord Record(string? baseMva = "100", string? r12 = "0.0078125", string? x13 = "0.25", string? x23 = "0.1875",
        string? hvKv = "400", string? mvKv = "132", string? lvKv = "13", int? star = 201)
    {
        var p = new List<string>();
        void Add(string name, string? v) { if (v is not null) p.Add("\"" + name + "\": " + v); }
        Add("baseMva", baseMva);
        Add("hvKv", hvKv);
        Add("mvKv", mvKv);
        Add("lvKv", lvKv);
        Add("r12Pu", r12);
        Add("x12Pu", "0.125");
        Add("r13Pu", "0.015625");
        Add("x13Pu", x13);
        Add("r23Pu", "0.01171875");
        Add("x23Pu", x23);
        using JsonDocument doc = JsonDocument.Parse("{" + string.Join(", ", p) + "}");
        var refs = new List<AssetOverlayRef>
        {
            new("hv", AssetRefTarget.Transformer, null, "3W HV"),
            new("mv", AssetRefTarget.Transformer, null, "3W MV"),
            new("lv", AssetRefTarget.Transformer, null, "3W LV"),
        };
        if (star is int s) refs.Insert(0, new AssetOverlayRef("starBus", AssetRefTarget.Bus, s, null));
        return new AssetOverlayRecord
        {
            Id = "ov.t3w", Kind = "transformer3W", Refs = refs, Params = doc.RootElement.Clone(), Document = "site.assets.json", Pointer = "/records/1",
        };
    }

    // The three legs of the test case (star bus 201), as the helper reads them.
    private static StarLegCase?[] Legs(double? mvBaseKv = 132) => new StarLegCase?[]
    {
        new("hv", "3W HV", 2, 201, 0.0017, 0.05, 400),
        new("mv", "3W MV", 201, 102, 0.0017, 0.05, mvBaseKv),
        new("lv", "3W LV", 201, 202, 0.0017, 0.05, 13),
    };

    [Fact]
    public void The_derivation_of_a_record_follows_the_formula_on_its_base_and_states_the_bases_and_formulas()
    {
        StarLegsDerivation d = StarLegsHelper.Derive(Record(), 201, Legs(), Array.Empty<string>(), 10000);
        Assert.True(d.Ok, d.Refusal);
        Same(100, d.CommonMva, "common base");
        Same(10000, d.SystemMva, "case base");
        double f = 10000.0 / 100.0;   // S_system / S_common; every rated voltage equals its bus base, so (V_r / V_b)^2 = 1
        double[] r = { (R12 + R13 - R23) / 2 * f, (R12 + R23 - R13) / 2 * f, (R13 + R23 - R12) / 2 * f };
        double[] x = { (X12 + X13 - X23) / 2 * f, (X12 + X23 - X13) / 2 * f, (X13 + X23 - X12) / 2 * f };
        Assert.Equal(new[] { "hv", "mv", "lv" }, d.Legs.Select(l => l.Role));
        for (int i = 0; i < 3; i++)
        {
            Same(r[i], d.Legs[i].Derived.R, d.Legs[i].Role + " r");
            Same(x[i], d.Legs[i].Derived.X, d.Legs[i].Role + " x");
            Same(1.0, d.Legs[i].VoltageFactor, d.Legs[i].Role + " voltage factor");
            Same(0.0017, d.Legs[i].Authored.R, d.Legs[i].Role + " authored r");
        }
        Same(0.5859375, d.Legs[0].Derived.R, "HV r by hand");
        Same(9.375, d.Legs[0].Derived.X, "HV x by hand");
        Same(3.125, d.Legs[1].Derived.X, "MV x by hand");
        Same(15.625, d.Legs[2].Derived.X, "LV x by hand");
        Assert.Equal("Common base: baseMva = 100 MVA. The transformer3W kind spec states that r12Pu ... x23Pu are on baseMva, so each pair is referred by Z * (S_common / S_pair) = Z * 1.", d.Lines[0]);
        Assert.Equal("Star equivalent (r and x separately): Z1 = (Z12 + Z13 - Z23) / 2, Z2 = (Z12 + Z23 - Z13) / 2, Z3 = (Z13 + Z23 - Z12) / 2.", d.Lines[1]);
        Assert.Equal("Case base: Z_leg = Z * (S_system / S_common) * (V_rated / V_base)^2 with S_system = 10000 MVA (the case's /system/baseMva) and V_base the leg's outer bus.", d.Lines[2]);
        Assert.Equal("HV leg '3W HV': on the common base r 0.005859375, x 0.09375; voltage factor (hvKv / baseKv of bus 2)^2 = (400 / 400)^2 = 1; on the case base r 0.5859375, x 9.375.", d.Lines[3]);
        Assert.Equal("Nothing is automatic: the solver uses the authored leg values, now and after any change to the overlay record.", d.Lines[^1]);

        // A rated voltage twice the bus base: the leg is four times larger on the case base.
        StarLegsDerivation v = StarLegsHelper.Derive(Record(mvKv: "264"), 201, Legs(), Array.Empty<string>(), 10000);
        Same(4.0, v.Legs[1].VoltageFactor, "MV voltage factor");
        Same((X12 + X23 - X13) / 2 * f * 4.0, v.Legs[1].Derived.X, "MV x with the voltage referral");
        // No rated voltage: the winding is taken as rated at its bus's base voltage (factor 1, said so).
        StarLegsDerivation nv = StarLegsHelper.Derive(Record(lvKv: null), 201, Legs(), Array.Empty<string>(), 10000);
        Same(1.0, nv.Legs[2].VoltageFactor, "LV voltage factor");
        Assert.Equal("lvKv not given: the winding is taken as rated at the base voltage of bus 202 (voltage factor 1)", nv.Legs[2].VoltageNote);
    }

    [Fact]
    public void The_derivation_is_refused_with_every_reason_when_the_bases_or_values_are_not_given()
    {
        StarLegsDerivation noBase = StarLegsHelper.Derive(Record(baseMva: null), 201, Legs(), Array.Empty<string>(), 10000);
        Assert.False(noBase.Ok);
        Assert.Empty(noBase.Legs);
        Assert.Equal("Derive star legs refused for 'ov.t3w': the MVA base of the pairwise impedances is not given: the transformer3W kind spec says r12Pu ... x23Pu are on baseMva, and baseMva is absent. Nothing was changed.",
            noBase.Refusal);
        Assert.Contains("baseMva is not a finite power above 0 MVA (0)", StarLegsHelper.Derive(Record(baseMva: "0"), 201, Legs(), Array.Empty<string>(), 10000).Refusal, StringComparison.Ordinal);

        StarLegsDerivation missing = StarLegsHelper.Derive(Record(r12: null, x23: null), 201, Legs(), Array.Empty<string>(), 10000);
        Assert.Equal("Derive star legs refused for 'ov.t3w': the pairwise impedances r12Pu, x23Pu are not given. Nothing was changed.", missing.Refusal);

        StarLegsDerivation noSystem = StarLegsHelper.Derive(Record(), 201, Legs(), Array.Empty<string>(), null);
        Assert.Contains("the case's system base (/system/baseMva) is not a finite power above 0 MVA", noSystem.Refusal, StringComparison.Ordinal);

        StarLegsDerivation offStar = StarLegsHelper.Derive(Record(star: 102), 102, Legs(), Array.Empty<string>(), 10000);
        Assert.Equal("Derive star legs refused for 'ov.t3w': the HV leg '3W HV' (2 -> 201) does not touch the star bus 102; the LV leg '3W LV' (201 -> 202) does not touch the star bus 102. Nothing was changed.",
            offStar.Refusal);

        StarLegsDerivation noKv = StarLegsHelper.Derive(Record(), 201, Legs(mvBaseKv: null), Array.Empty<string>(), 10000);
        Assert.Equal("Derive star legs refused for 'ov.t3w': bus 102 (the outer end of the MV leg) has no base voltage, so mvKv = 132 kV cannot be referred to it. Nothing was changed.", noKv.Refusal);

        StarLegsDerivation noStar = StarLegsHelper.Derive(Record(star: null), null, Legs(), new[] { "the LV leg '3W LV' is not a transformer of the case" }, 10000);
        Assert.Equal("Derive star legs refused for 'ov.t3w': the star bus (reference 'starBus') is not given; the LV leg '3W LV' is not a transformer of the case. Nothing was changed.", noStar.Refusal);
    }

    // =================================================================================================================
    // The test case: data/uk with a three-winding transformer as its authored star equivalent
    // =================================================================================================================

    private const string Overlay = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "South Scotland" } },
          { "id": "ov.t3w", "kind": "transformer3W", "parent": "ov.sub", "refs": { "starBus": { "bus": 201 }, "hv": { "transformer": "3W HV" }, "mv": { "transformer": "3W MV" }, "lv": { "transformer": "3W LV" } }, "params": { "baseMva": 100, "hvKv": 400, "mvKv": 132, "lvKv": 13, "r12Pu": 0.0078125, "x12Pu": 0.125, "r13Pu": 0.015625, "x13Pu": 0.25, "r23Pu": 0.01171875, "x23Pu": 0.1875 } }
        ] }
        """;

    private const string OverlayB = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 100 } }
        ] }
        """;

    /// <summary>data/uk with the star bus 201, the LV bus 202 and the three legs added (each leg r <paramref name="legR"/>,
    /// x <paramref name="legX"/>), projected to v2 into a fresh temporary folder, with the overlay (or without).</summary>
    private sealed class ThreeWindingCase : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-starlegs-" + Guid.NewGuid().ToString("N"));

        public ThreeWindingCase(bool withOverlay = true, string? legR = null, string? legX = null, string overlay = Overlay)
        {
            string v1 = Path.Combine(_root, "v1");
            Directory.CreateDirectory(v1);
            foreach (string f in Directory.EnumerateFiles(Path.Combine(EditorTestSupport.DataDir, "uk")))
                File.Copy(f, Path.Combine(v1, Path.GetFileName(f)));
            JsonArray buses = JsonNode.Parse(File.ReadAllText(Path.Combine(v1, "buses.json")))!.AsArray();
            buses.Add(JsonNode.Parse("""{ "id": 201, "name": "3W star", "baseKv": 400 }"""));
            buses.Add(JsonNode.Parse("""{ "id": 202, "name": "3W LV 13kV", "baseKv": 13 }"""));
            File.WriteAllText(Path.Combine(v1, "buses.json"), buses.ToJsonString());
            JsonArray tx = JsonNode.Parse(File.ReadAllText(Path.Combine(v1, "transformer.json")))!.AsArray();
            string r = legR ?? "0.0017", x = legX ?? "0.05";
            string[] rs = r.Split(','), xs = x.Split(',');
            string R(int i) => rs[Math.Min(i, rs.Length - 1)];
            string X(int i) => xs[Math.Min(i, xs.Length - 1)];
            tx.Add(JsonNode.Parse("{ \"name\": \"3W HV\", \"from\": 2, \"to\": 201, \"rPu\": " + R(0) + ", \"xPu\": " + X(0) + ", \"tapRatio\": 1.0, \"ratingMva\": 100 }"));
            tx.Add(JsonNode.Parse("{ \"name\": \"3W MV\", \"from\": 201, \"to\": 102, \"rPu\": " + R(1) + ", \"xPu\": " + X(1) + ", \"tapRatio\": 1.0, \"ratingMva\": 100 }"));
            tx.Add(JsonNode.Parse("{ \"name\": \"3W LV\", \"from\": 201, \"to\": 202, \"rPu\": " + R(2) + ", \"xPu\": " + X(2) + ", \"tapRatio\": 1.0, \"ratingMva\": 100 }"));
            File.WriteAllText(Path.Combine(v1, "transformer.json"), tx.ToJsonString());
            V2Projection p = V1Projector.Project(V1Projector.ReadV1(v1));
            string dir = Path.Combine(_root, "case");
            Directory.CreateDirectory(dir);
            ManifestPath = Path.Combine(dir, "uk3w.case.json");
            File.WriteAllBytes(ManifestPath, p.CaseJson);
            File.WriteAllBytes(Path.Combine(dir, V1Projector.NetworkFileName), p.NetworkJson);
            if (withOverlay) UiOverlayFixture.AddOverlay(ManifestPath, overlay, OverlayB);
        }

        public string ManifestPath { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
            catch (UnauthorizedAccessException) { /* likewise */ }
        }
    }

    private static CaseAuthoring Open(string manifest) => CaseAuthoring.Open(AssetCaseSource.V2(manifest), CaseSessionTests.Unlimited(), Array.Empty<string>());

    private static CaseRef Ov(CaseAuthoring a, string id) => a.Collections().Where(c => c.Role == CaseRoles.Overlay).SelectMany(a.Records)
        .Select(r => r.Ref).First(r => a.OverlayIdentity(r).Id == id);

    // Every existing document's bytes, by index.
    private static byte[]?[] Snapshot(CaseAuthoring a) => a.Session.Documents.Select(d => d.Exists ? d.Text.ToArray() : null).ToArray();

    [Fact]
    public void Author_mode_proposes_the_six_leg_edits_old_to_new_and_changes_nothing_until_applied()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        Assert.Null(a.OverlayRefusal);
        byte[]?[] before = Snapshot(a);
        int depth = a.UndoDepth;
        long revision = a.Revision;

        StarLegsProposal p = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        Assert.True(p.CanApply, p.Refusal);
        Assert.Equal("ov.t3w", p.OverlayId);
        Assert.Equal(revision, p.Revision);
        // Proposing changes nothing.
        Assert.Equal(depth, a.UndoDepth);
        Assert.Equal(revision, a.Revision);
        Assert.False(a.IsDirty);
        Assert.Equal(before, Snapshot(a));

        double f = 10000.0 / 100.0;
        double[] expected =
        {
            (R12 + R13 - R23) / 2 * f, (X12 + X13 - X23) / 2 * f,
            (R12 + R23 - R13) / 2 * f, (X12 + X23 - X13) / 2 * f,
            (R13 + R23 - R12) / 2 * f, (X13 + X23 - X12) / 2 * f,
        };
        Assert.Equal(new[] { ("hv", "3W HV", "rPu"), ("hv", "3W HV", "xPu"), ("mv", "3W MV", "rPu"), ("mv", "3W MV", "xPu"), ("lv", "3W LV", "rPu"), ("lv", "3W LV", "xPu") },
            p.Edits.Select(e => (e.Role, e.Transformer, e.Field)));
        for (int i = 0; i < 6; i++)
        {
            StarLegEdit e = p.Edits[i];
            Same(expected[i], e.NewValue, e.Transformer + " " + e.Field);
            Same(i % 2 == 0 ? 0.0017 : 0.05, e.OldValue, e.Transformer + " " + e.Field + " old");
            Assert.Equal(JsonLiteral.FormatNumber(expected[i]), e.NewText);
            Same(expected[i], double.Parse(e.NewText, System.Globalization.CultureInfo.InvariantCulture), "the new text reads back to the derived double");
            // The old text is exactly what the file writes now.
            CaseDocument d = a.Session.Documents[e.Record.Document];
            Assert.Equal(d.Text.RawText(d.Text.Resolve(e.Record.Pointer + "/" + e.Field)!), e.OldText);
        }
        Assert.Equal(new[] { "0.5859375", "9.375", "0.1953125", "3.125", "0.9765625", "15.625" }, p.Edits.Select(e => e.NewText));
        Assert.Equal("HV leg '3W HV' xPu: " + p.Edits[1].OldText + " -> 9.375", p.Edits[1].Text);
        Assert.Equal(p.Edits.Select(e => e.Text), p.EditLines);
    }

    [Fact]
    public void Apply_is_one_undo_step_that_changes_exactly_the_six_leg_values_and_undo_restores_every_byte()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        byte[]?[] before = Snapshot(a);
        int depth = a.UndoDepth;
        StarLegsProposal p = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        Assert.True(p.CanApply, p.Refusal);
        int net = p.Edits[0].Record.Document;
        Assert.All(p.Edits, e => Assert.Equal(net, e.Record.Document));

        // The bytes the network file must have after: the six value tokens replaced, nothing else (spliced back to front).
        CaseDocument doc = a.Session.Documents[net];
        var spans = p.Edits.Select(e => (Node: doc.Text.Resolve(e.Record.Pointer + "/" + e.Field)!, e.NewText)).OrderByDescending(x => x.Node.Start).ToList();
        var expectedBytes = new List<byte>(before[net]!);
        foreach ((JsonSourceNode node, string text) in spans)
        {
            expectedBytes.RemoveRange(node.Start, node.End - node.Start);
            expectedBytes.InsertRange(node.Start, Encoding.UTF8.GetBytes(text));
        }

        AuthoringResult r = StarLegsHelper.Apply(a, p);
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.Equal("derive star legs of 'ov.t3w': 6 value(s) set as one change (U to undo).", r.Message);
        Assert.Equal(depth + 1, a.UndoDepth);
        Assert.Equal("derive star legs of 'ov.t3w'", a.UndoDescription);
        byte[]?[] after = Snapshot(a);
        Assert.Equal(expectedBytes.ToArray(), after[net]);
        for (int i = 0; i < before.Length; i++)
            if (i != net) Assert.Equal(before[i], after[i]);
        Assert.Equal(new[] { a.Session.Documents[net].FileName }, a.DirtyFiles);
        // Lossless: each written value reads back to the derived double, bit for bit.
        foreach (StarLegEdit e in p.Edits)
            Same(e.NewValue, a.Session.Documents[net].Text.Resolve(e.Record.Pointer + "/" + e.Field)!.NumberValue!.Value, e.Transformer + " " + e.Field + " written");

        // A second proposal now finds every value already derived: no edit changes anything, and nothing is recorded.
        StarLegsProposal again = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        Assert.All(again.Edits, e => Assert.EndsWith("  (unchanged)", e.Text, StringComparison.Ordinal));
        AuthoringResult none = StarLegsHelper.Apply(a, again);
        Assert.True(none.Ok);
        Assert.False(none.Changed);
        Assert.Equal(depth + 1, a.UndoDepth);

        // Undo: every document exactly as before, bit for bit; redo puts the six back.
        AuthoringResult u = a.Undo();
        Assert.True(u.Ok, u.Message);
        Assert.Equal(depth, a.UndoDepth);
        Assert.Equal(before, Snapshot(a));
        Assert.False(a.IsDirty);
        a.Redo();
        Assert.Equal(after, Snapshot(a));
    }

    [Fact]
    public void A_negative_derived_leg_reactance_is_proposed_and_written_as_derived()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        FieldGridModel g = a.FieldGrid(Ov(a, "ov.t3w"));
        Assert.True(g.Edit("params.x13Pu", "0.5").Ok);
        Assert.True(g.Edit("params.x23Pu", "0.25").Ok);
        StarLegsProposal p = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        Assert.True(p.CanApply, p.Refusal);
        StarLegEdit mvX = p.Edits.Single(e => e.Role == "mv" && e.Field == "xPu");
        double expected = (0.125 + 0.25 - 0.5) / 2 * (10000.0 / 100.0);
        Same(expected, mvX.NewValue, "MV x");
        Same(-6.25, mvX.NewValue, "MV x by hand");
        Assert.Equal("MV leg '3W MV' xPu: " + mvX.OldText + " -> -6.25  (negative: possible for a star leg; written as derived)", mvX.Text);
        AuthoringResult r = StarLegsHelper.Apply(a, p);
        Assert.True(r.Ok && r.Changed, r.Message);
        CaseDocument d = a.Session.Documents[mvX.Record.Document];
        Same(-6.25, d.Text.Resolve(mvX.Record.Pointer + "/xPu")!.NumberValue!.Value, "MV x written");
    }

    [Fact]
    public void A_stale_or_refused_proposal_is_refused_and_changes_nothing()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        StarLegsProposal p = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        // Any change after the proposal makes it stale.
        Assert.True(a.FieldGrid(Ov(a, "ov.t3w")).Edit("params.hvKv", "400.5").Ok);
        int depth = a.UndoDepth;
        byte[]?[] mid = Snapshot(a);
        AuthoringResult stale = StarLegsHelper.Apply(a, p);
        Assert.False(stale.Ok);
        Assert.Equal("Derive star legs refused: the case has changed since the edits were proposed. Derive them again to see the edits for the case as it is now. Nothing was changed.", stale.Message);
        Assert.Equal(depth, a.UndoDepth);
        Assert.Equal(mid, Snapshot(a));

        // A missing pairwise value (an optional parameter reset to absent): refused with the reason, nothing to apply.
        Assert.True(a.FieldGrid(Ov(a, "ov.t3w")).Reset("params.r12Pu").Ok);
        StarLegsProposal refused = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        Assert.False(refused.CanApply);
        Assert.Empty(refused.Edits);
        Assert.Equal("Derive star legs refused for 'ov.t3w': the pairwise impedance r12Pu is not given. Nothing was changed.", refused.Refusal);
        int d2 = a.UndoDepth;
        Assert.False(StarLegsHelper.Apply(a, refused).Ok);
        Assert.Equal(d2, a.UndoDepth);

        // A record of another kind has no star legs.
        Assert.Equal("Derive star legs refused: 'ov.soil' is a soilZone record, not a transformer3W, so there are no star legs to derive. Nothing was changed.",
            StarLegsHelper.Propose(a, Ov(a, "ov.soil")).Refusal);
    }

    [Fact]
    public void The_Author_model_derives_on_request_shows_the_proposal_and_applies_only_on_confirm_as_one_undo_step()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        var m = new AssetAuthorModel(a);
        Assert.True(m.SelectRecord(Ov(a, "ov.t3w")));
        Assert.True(m.CanDeriveStarLegs);
        Assert.True(m.RecordPage!.CanDeriveStarLegs);
        Assert.False(m.RecordPage.HasStarLegs);   // nothing is proposed until asked
        int depth = a.UndoDepth;
        byte[]?[] before = Snapshot(a);

        StarLegsProposal p = m.DeriveStarLegs();
        Assert.Same(p, m.StarLegs);
        Assert.Same(p, m.RecordPage!.StarLegs);
        Assert.True(m.RecordPage.CanApplyStarLegs);
        Assert.False(m.RecordPage.HasStarLegsRefusal);
        Assert.Equal("Derive star legs: 6 leg edits proposed for 'ov.t3w' (not applied: Apply writes them as one change, one undo step; the solver keeps the authored values until then).", m.Status);
        Assert.Equal(depth, a.UndoDepth);
        Assert.Equal(before, Snapshot(a));

        // Cancel drops it, nothing changed.
        m.CancelStarLegs();
        Assert.Null(m.StarLegs);
        Assert.False(m.RecordPage!.HasStarLegs);
        Assert.Equal(before, Snapshot(a));

        // Derive again and confirm: one undo step; the page drops the spent proposal.
        m.DeriveStarLegs();
        AuthoringResult r = m.ApplyStarLegs();
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.Equal(depth + 1, a.UndoDepth);
        Assert.Null(m.StarLegs);
        Assert.False(m.RecordPage!.HasStarLegs);
        Assert.False(m.ApplyStarLegs().Ok);   // nothing left to apply
        Assert.Equal(depth + 1, a.UndoDepth);
        m.Undo();
        Assert.Equal(before, Snapshot(a));

        // A selection elsewhere does not offer it; asking anyway is a programming error, refused loudly.
        Assert.True(m.SelectRecord(Ov(a, "ov.soil")));
        Assert.False(m.CanDeriveStarLegs);
        Assert.False(m.RecordPage!.CanDeriveStarLegs);
        Assert.Throws<InvalidOperationException>(() => m.DeriveStarLegs());
    }

    [Fact]
    public void Set_fields_refuses_the_whole_group_on_one_bad_edit_and_leaves_every_document_as_it_was()
    {
        using var c = new ThreeWindingCase();
        CaseAuthoring a = Open(c.ManifestPath);
        StarLegsProposal p = StarLegsHelper.Propose(a, Ov(a, "ov.t3w"));
        byte[]?[] before = Snapshot(a);
        int depth = a.UndoDepth;
        var edits = p.Edits.Select(e => new CaseOps.FieldEdit(e.Record.Document, e.Record.Pointer, e.Field, e.NewText)).ToList();
        edits.Insert(3, new CaseOps.FieldEdit(edits[0].Document, edits[0].ObjectPointer, "xPu", "not a number"));
        OpResult r = CaseOps.SetFields(a.Session, edits, "group");
        Assert.False(r.Ok);
        Assert.StartsWith("group: refused, nothing changed. ", r.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(a));
        Assert.Equal(depth, a.UndoDepth);
    }

    // =================================================================================================================
    // Run mode: the OVERVIEW and CHECK of the transformer3W record, and the solver untouched
    // =================================================================================================================

    // Two solved frames of a v2 case, from the live host exactly as the app runs it (as UiAssetOverlayTests), with the
    // dashboard clock each was shown at.
    private static (LiveLoadResult Loaded, (SimFrame Frame, string Clock)[] Frames) Run(string manifest)
    {
        var host = new SimHost();
        var dash = new DashboardModel();
        LiveLoadResult loaded = host.LoadLive(ModelCatalog.V2CaseSource(manifest), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        var frames = new (SimFrame, string)[2];
        for (int i = 0; i < frames.Length; i++)
        {
            HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            frames[i] = (hf.Frame!, dash.Clock);
        }
        return (loaded, frames);
    }

    private static AssetNavigatorModel Navigator(string manifest, CaseAssetOverlay? overlay, (SimFrame Frame, string Clock)[] frames)
    {
        var nav = new AssetNavigatorModel { Active = true };
        nav.SetSource(AssetCaseSource.From(ModelCatalog.V2CaseSource(manifest)), CaseSessionTests.Unlimited(), overlay, null);
        foreach (var (f, c) in frames) nav.Update(f, c);
        Assert.NotNull(nav.Index);
        return nav;
    }

    [Fact]
    public void The_Run_overview_shows_the_star_bus_legs_nameplate_pairwise_values_and_the_legs_authored_beside_derived()
    {
        using var c = new ThreeWindingCase();
        (LiveLoadResult loaded, var frames) = Run(c.ManifestPath);
        AssetNavigatorModel nav = Navigator(c.ManifestPath, loaded.Overlay, frames);
        OverlayIndex ov = nav.Index!.Overlay!;
        GridModel m = nav.Index.Model;
        nav.Select(nav.Tree.NodeFor(ov.Find("ov.t3w")!.Key)!);
        AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
        Assert.Equal("Three-winding transformer  -  carried, not used by the solver", page.Sub);
        Assert.Equal(OverlayIndex.SolverNote, page.Note);
        string star = "bus 201 '3W star'";
        Assert.Equal(new[]
        {
            new OverviewFact("Star bus", star),
            new OverviewFact("Common MVA base", "100 MVA (the pairwise impedances are on it, as the kind spec states)"),
            new OverviewFact("Rated voltages", "HV 400 kV, MV 132 kV, LV 13 kV"),
            new OverviewFact("Leg values", "the solver uses the legs' authored r and x; Derive star legs (Author mode) proposes the derived values below and writes them only on confirm"),
            new OverviewFact("Derived star legs", "derived from the nameplate on the case base 10000 MVA (display only)"),
        }, page.Facts.TakeLast(5));

        int Leg(string name) => Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].IsTransformer && m.Branches[i].Name == name);
        string Resolved(string name) => "transformer #" + Leg(name) + "  " + m.Branches[Leg(name)].From + " -> " + m.Branches[Leg(name)].To;
        Assert.Equal(new[] { OverlayKindPages.NameplateTable, OverlayKindPages.PairwiseTable, OverlayKindPages.StarLegsTable }, page.Tables.Select(t => t.Title));
        Assert.Equal(new[]
        {
            new[] { "HV", "400", "transformer '3W HV'", Resolved("3W HV") },
            new[] { "MV", "132", "transformer '3W MV'", Resolved("3W MV") },
            new[] { "LV", "13", "transformer '3W LV'", Resolved("3W LV") },
        }, page.Tables[0].Rows.Select(r => r.Cells.ToArray()));
        Assert.Equal(new[] { "Winding", "Rated voltage (kV)", "Leg transformer", "Leg resolves to" }, page.Tables[0].Columns);
        Assert.Equal(new[]
        {
            new[] { "HV-MV (12)", "0.0078125", "0.125" },
            new[] { "HV-LV (13)", "0.015625", "0.25" },
            new[] { "MV-LV (23)", "0.01171875", "0.1875" },
        }, page.Tables[1].Rows.Select(r => r.Cells.ToArray()));
        Assert.Equal(new[] { "Pair", "r (pu on baseMva)", "x (pu on baseMva)" }, page.Tables[1].Columns);
        OverviewTable legs = page.Tables[2];
        Assert.Equal(new[] { "Leg", "Transformer", "Buses (from -> to)", "Authored r (pu, case base)", "Authored x (pu, case base)", "Derived r (pu, case base)", "Derived x (pu, case base)" }, legs.Columns);
        Assert.Equal(new[]
        {
            new[] { "HV", "3W HV", "2 -> 201", JsonLiteral.FormatNumber(m.Branches[Leg("3W HV")].R), JsonLiteral.FormatNumber(m.Branches[Leg("3W HV")].X), "0.5859375", "9.375" },
            new[] { "MV", "3W MV", "201 -> 102", JsonLiteral.FormatNumber(m.Branches[Leg("3W MV")].R), JsonLiteral.FormatNumber(m.Branches[Leg("3W MV")].X), "0.1953125", "3.125" },
            new[] { "LV", "3W LV", "201 -> 202", JsonLiteral.FormatNumber(m.Branches[Leg("3W LV")].R), JsonLiteral.FormatNumber(m.Branches[Leg("3W LV")].X), "0.9765625", "15.625" },
        }, legs.Rows.Select(r => r.Cells.ToArray()));
        Same(0.0017, m.Branches[Leg("3W MV")].R, "the solver's MV leg r is the authored one");
        Assert.StartsWith("Common base: baseMva = 100 MVA.", legs.Note, StringComparison.Ordinal);

        // The links (generic reference rows) select the star bus and each leg's circuit node.
        foreach (OverviewRow row in page.Rows.Where(r => r.Detail.StartsWith("reference '", StringComparison.Ordinal)).ToList())
        {
            Assert.True(nav.OpenKey(row.Target!), row.Name);
            Assert.Same(nav.Tree.NodeFor(row.Target!), nav.Selected);
            nav.GoBack();
        }
        Assert.Equal(new[] { AssetKey.Substation(201), AssetKey.Branch(m, Leg("3W HV")), AssetKey.Branch(m, Leg("3W MV")), AssetKey.Branch(m, Leg("3W LV")) },
            page.Rows.Where(r => r.Detail.StartsWith("reference '", StringComparison.Ordinal)).Select(r => r.Target));

        // CHECK: a display-only consistency note (the authored legs are not the derivation); no problem, no warning.
        Assert.Contains(AssetNavigatorModel.CheckTab, nav.SubTabs);
        OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
        Assert.Empty(check.Rows);
        Assert.Empty(check.Warnings);
        OverlayCheckRow note = Assert.Single(check.Notes);
        Assert.True(check.HasNotes);
        Assert.Equal("/records/1", note.Pointer);
        Assert.StartsWith("The authored legs are not the nameplate's star derivation (the solver uses the authored values; Derive star legs in Author mode proposes the derived ones): HV leg '3W HV' authored r 0.0017, x 0.05; derived r 0.5859375, x 9.375;",
            note.Message, StringComparison.Ordinal);
        Assert.Equal("1 consistency note on transformer3W 'ov.t3w' (display only, never an error)", check.Heading);
    }

    [Fact]
    public void Legs_authored_as_the_derivation_give_no_note_and_a_record_that_cannot_be_derived_says_why()
    {
        // The legs written as the derived values: no consistency note, so no CHECK sub-tab.
        using (var c = new ThreeWindingCase(legR: "0.5859375,0.1953125,0.9765625", legX: "9.375,3.125,15.625"))
        {
            (LiveLoadResult loaded, var frames) = Run(c.ManifestPath);
            AssetNavigatorModel nav = Navigator(c.ManifestPath, loaded.Overlay, frames);
            OverlayEntry t3w = nav.Index!.Overlay!.Find("ov.t3w")!;
            Assert.Empty(t3w.Notes);
            nav.Select(nav.Tree.NodeFor(t3w.Key)!);
            Assert.DoesNotContain(AssetNavigatorModel.CheckTab, nav.SubTabs);
        }
        // A star bus none of the legs touches: the page and the note say why nothing was derived.
        string off = Overlay.Replace("\"starBus\": { \"bus\": 201 }", "\"starBus\": { \"bus\": 102 }", StringComparison.Ordinal);
        Assert.NotEqual(Overlay, off);
        using (var c = new ThreeWindingCase(overlay: off))
        {
            (LiveLoadResult loaded, var frames) = Run(c.ManifestPath);
            AssetNavigatorModel nav = Navigator(c.ManifestPath, loaded.Overlay, frames);
            nav.Select(nav.Tree.NodeFor(nav.Index!.Overlay!.Find("ov.t3w")!.Key)!);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            string why = "Derive star legs refused for 'ov.t3w': the HV leg '3W HV' (2 -> 201) does not touch the star bus 102; the LV leg '3W LV' (201 -> 202) does not touch the star bus 102. Nothing was changed.";
            Assert.Equal(new OverviewFact("Derived star legs", why), page.Facts[^1]);
            Assert.All(page.Tables[2].Rows, r => Assert.Equal(new[] { "not derived", "not derived" }, r.Cells.TakeLast(2)));
            Assert.Equal(why, page.Tables[2].Note);
            Assert.Equal("The star legs cannot be derived from the nameplate over the running model: " + why, Assert.Single(nav.OverlayCheck!.Notes).Message);
        }
    }

    [Fact]
    public void The_solved_frames_are_bit_identical_with_and_without_the_three_winding_overlay()
    {
        using var with = new ThreeWindingCase();
        using var without = new ThreeWindingCase(withOverlay: false);
        (LiveLoadResult a, var ra) = Run(with.ManifestPath);
        (LiveLoadResult b, var rb) = Run(without.ManifestPath);
        SimFrame[] fa = ra.Select(x => x.Frame).ToArray(), fb = rb.Select(x => x.Frame).ToArray();
        Assert.NotNull(a.Overlay);
        Assert.Null(b.Overlay);
        for (int i = 0; i < fa.Length; i++)
        {
            PowerFlowResult x = fa[i].Result, y = fb[i].Result;
            Assert.Equal(x.BusIds, y.BusIds);
            Assert.Equal(x.Vm.Select(Bits), y.Vm.Select(Bits));
            Assert.Equal(x.VaDeg.Select(Bits), y.VaDeg.Select(Bits));
            Assert.Equal(x.PInjMw.Select(Bits), y.PInjMw.Select(Bits));
            Assert.Equal(fa[i].Model.Generators.Select(g => Bits(g.Pg)), fb[i].Model.Generators.Select(g => Bits(g.Pg)));
            Assert.Equal(fa[i].Model.Branches.Select(br => (Bits(br.R), Bits(br.X))), fb[i].Model.Branches.Select(br => (Bits(br.R), Bits(br.X))));
        }
    }
}
