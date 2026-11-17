using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Study;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P3: CGMES as a normal study job over the constructed four-profile bundle in fixtures/cgmes/p3-coupler (EQ, TP,
/// SSH, SV; CGMES 2.4.15 style headers; no third-party content). The full job (import, topology, controls, AC solve,
/// export and read-back), switch actions, the optional P1 stage and each refused input.
/// </summary>
public class CgmesStudyJobTests
{
    internal static string FixtureDir => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "cgmes", "p3-coupler");

    internal static CgmesProfileInput Profile(string type, string file, byte[]? bytes = null, string version = "2.4.15")
    {
        byte[] b = bytes ?? File.ReadAllBytes(Path.Combine(FixtureDir, file));
        return new CgmesProfileInput(type, version, file, Convert.ToHexStringLower(SHA256.HashData(b)), b);
    }

    internal static List<CgmesProfileInput> Bundle() =>
    [
        Profile("EQ", "p3_EQ.xml"), Profile("TP", "p3_TP.xml"), Profile("SSH", "p3_SSH.xml"), Profile("SV", "p3_SV.xml"),
    ];

    private static byte[] Edited(string file, string from, string to)
    {
        string text = File.ReadAllText(Path.Combine(FixtureDir, file));
        Assert.Contains(from, text);
        return Encoding.UTF8.GetBytes(text.Replace(from, to));
    }

    private static readonly CimImportOptions Options = new(StartingVoltages: true, ApparentPowerRatings: true, RequireSshSetPoints: true);

    private static CgmesStudyRequest Request(IReadOnlyList<CgmesProfileInput>? profiles = null, CimImportOptions? options = null,
        IReadOnlyList<CgmesSwitchAction>? actions = null, CgmesSecurityRequest? security = null, bool apply = true) =>
        new(profiles ?? Bundle(), options ?? Options, 0.95, 1.05, ApplyControls: apply, SwitchActions: actions, Security: security);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ---- the full job ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_full_job_imports_controls_solves_exports_and_reads_back_equivalently()
    {
        CgmesStudyRun run = CgmesStudyRunner.Run(Request());
        CgmesStudyResult r = run.Result;
        Assert.True(r.Status == "SECURE", JsonSerializer.Serialize(r, Json));
        Assert.Equal("Verified", r.Outcome);

        // Stages in order, none skipped.
        Assert.Equal(new[] { "bundle", "import", "bridge", "solve", "intact", "exchange", "verdict" }, r.Stages.Select(s => s.Stage));
        Assert.Equal(Enumerable.Range(1, r.Stages.Count), r.Stages.Select(s => s.Order));

        // Profiles: declared version, header identity and dependencies, consumption.
        Assert.Equal(new[] { "EQ", "TP", "SSH", "SV" }, r.Bundle.Profiles.Select(p => p.Profile));
        Assert.All(r.Bundle.Profiles, p => Assert.Equal("2.4.15", p.DeclaredVersion));
        Assert.All(r.Bundle.Profiles, p => Assert.True(p.Consumed));
        CgmesProfileRecord sv = r.Bundle.Profiles[3];
        Assert.Equal("c0a80001-0000-4000-8000-0000000000b1", sv.HeaderModelId);
        Assert.Equal(new[] { "c0a80001-0000-4000-8000-0000000000f1", "c0a80001-0000-4000-8000-0000000000a1" }, sv.DependentOn);
        Assert.Equal(CgmesBundleInspector.BundleIdentity(Bundle()), r.Bundle.BundleSha256);
        Assert.Contains(r.Bundle.OmittedProfiles, o => o.StartsWith("DY:", StringComparison.Ordinal));

        // Visible starting voltages (SV), switching state (the closed coupler joins both A sections into one TopologicalNode).
        CgmesBusIdentity a = r.Identity.Buses.Single(b => b.Name == "A");
        Assert.Equal(112.2 / 110.0, a.StartVm);
        Assert.Equal(3, r.Import.BusesWithStartingVoltage);
        Assert.Equal(3, r.Identity.Buses.Count);

        // One real monitored rating with its source: L3's permanent limit (its TATL is not read, and said).
        CgmesBranchIdentity l3 = r.Identity.Branches.Single(b => b.CimMrid == "L3");
        Assert.Equal(80.0, l3.RateAMva);
        Assert.StartsWith("PATL", l3.RatingSource);
        Assert.Contains(r.Import.ImporterNotes, n => n.Contains("not a permanent (PATL) limit"));
        Assert.Contains("falls back to A", r.Identity.PostFaultRatingOrigin);
        Assert.Contains(r.Intact.Limits, l => l.Kind == "BranchFrom" && l.Index == l3.Index && l.Upper == 80.0 && l.Source == "A");
        Assert.Equal((3, 3), (r.Identity.LiveBranches, r.Identity.RatedBranches));

        // One controlled device: the SVC holds B at 111.1 kV (1.01 pu); the plain model would hold it at its SSH q = 0.
        CgmesRepresentedControl svc = r.Controls.Represented.Single(c => c.Kind.StartsWith("susceptance", StringComparison.Ordinal));
        Assert.True(r.Controls.Settled);
        Assert.Equal(111.1 / 110.0, svc.TargetPu);
        Assert.True(svc.Realised > 0 && svc.Realised < 50.0, $"SVC susceptance {svc.Realised} MVAr");
        Assert.False(svc.AtLimit);
        int bBus = r.Identity.Buses.Single(b => b.Name == "B").BusId;
        LimitEvidence vB = r.Intact.Limits.Single(l => l.Kind == "Voltage" && l.Index == bBus);
        Assert.Equal(111.1 / 110.0, vB.Observed, 7);   // to the control tolerance (1e-8 pu)
        Assert.Contains(r.Controls.Represented, c => c.Kind == "local voltage" && c.Treatment.StartsWith("slack"));

        // Exchange: identity, topology, ratings and controlled output, through the read-back.
        CgmesExchangeReport x = r.Exchange;
        Assert.Equal("EVALUATED", x.Evaluated);
        Assert.True(x.IdentityEquivalent, string.Join("; ", x.IdentityDifferences));
        Assert.True(x.TopologyEquivalent, string.Join("; ", x.TopologyDifferences));
        Assert.True(x.RatingsEquivalent, string.Join("; ", x.RatingDifferences));
        Assert.True(x.ControlledOutputEquivalent, JsonSerializer.Serialize(x.ReadBackCheck, Json) + JsonSerializer.Serialize(x.FoldCheck, Json));
        Assert.Equal(PowerFlowStatus.Converged, x.ReadBackStatus);
        Assert.Equal(r.Intact.Disposition, x.ReadBackDisposition);
        Assert.Equal(CgmesBundleInspector.Hash(Encoding.UTF8.GetBytes(run.ExportDocument!)), x.DocumentSha256);
        Assert.Contains(x.Identity, e => e.Kind == "branch" && e.SourceMrid == "L3" && e.ExportMrid == "L" + (l3.Index + 1));
        Assert.Contains(x.Identity, e => e.Kind == "generator" && e.SourceMrid == "G1");
        Assert.Contains(r.Deferred, d => d.Contains("not claimed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_exported_document_reads_back_to_the_same_ratings_and_topology_independently()
    {
        // Independent of the runner's own comparison: read the exported document with the incumbent reader and check the
        // monitored rating and the bus count by hand.
        CgmesStudyRun run = CgmesStudyRunner.Run(Request());
        GridModel back = CgmesWriter.ReadBack(run.ExportDocument!, 100.0, "back");
        Assert.Equal(3, back.Buses.Count);
        Assert.Equal(new[] { 120.0, 120.0, 80.0 }, back.Branches.Select(b => b.RateAMva).OrderByDescending(v => v));
        Assert.Equal(2, back.Generators.Count);
        // The SVC's realised susceptance is a fixed shunt at B in the exchange (MVAr at 1 pu = svc.Realised).
        CgmesRepresentedControl svc = run.Result.Controls.Represented.Single(c => c.Kind.StartsWith("susceptance", StringComparison.Ordinal));
        Assert.Equal(svc.Realised!.Value, back.Buses.Single(b => b.Bs != 0).Bs, 9);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        CgmesStudyRun first = CgmesStudyRunner.Run(Request());
        CgmesStudyRun second = CgmesStudyRunner.Run(Request());
        Assert.Equal(JsonSerializer.Serialize(first.Result, Json), JsonSerializer.Serialize(second.Result, Json));
        Assert.Equal(first.ExportDocument, second.ExportDocument);
    }

    [Fact]
    public void Controls_not_applied_are_recorded_and_never_stand_in_for_the_controlled_result()
    {
        CgmesStudyResult held = CgmesStudyRunner.Run(Request(apply: false)).Result;
        CgmesStudyResult controlled = CgmesStudyRunner.Run(Request()).Result;
        Assert.False(held.Controls.Applied);
        Assert.Contains("held", held.Controls.Solver);
        CgmesRepresentedControl svc = held.Controls.Represented.Single(c => c.Kind.StartsWith("susceptance", StringComparison.Ordinal));
        Assert.Null(svc.Realised);
        Assert.Contains("not applied", svc.Treatment);
        int bBus = held.Identity.Buses.Single(b => b.Name == "B").BusId;
        double vHeld = held.Intact.Limits.Single(l => l.Kind == "Voltage" && l.Index == bBus).Observed;
        double vCtl = controlled.Intact.Limits.Single(l => l.Kind == "Voltage" && l.Index == bBus).Observed;
        Assert.True(Math.Abs(vHeld - vCtl) > 1e-4, $"held {vHeld} vs controlled {vCtl}");
    }

    // ---- switch actions --------------------------------------------------------------------------------------------

    private static readonly CimImportOptions Switches = Options with { Topology = CimTopology.Switches };

    [Fact]
    public void Opening_the_coupler_splits_substation_A_and_both_sections_stay_supplied()
    {
        CgmesStudyResult closed = CgmesStudyRunner.Run(Request(options: Switches)).Result;
        CgmesStudyResult open = CgmesStudyRunner.Run(Request(options: Switches, actions: [new CgmesSwitchAction("_BRK_A", true)])).Result;
        Assert.Equal(3, closed.Identity.Buses.Count);
        CgmesSwitchingReport s = Assert.IsType<CgmesSwitchingReport>(open.Switching);
        CgmesSwitchActionRecord action = Assert.Single(s.Actions);
        Assert.Equal(("BRK_A", "Breaker", (bool?)false, true), (action.Mrid, action.Class, action.AsFoundOpen, action.RequestedOpen));
        Assert.Equal((3, 4), (s.BusesBefore, s.BusesAfter));
        // Old and new groups: as found, every A terminal is on the bus named by the smallest ConnectivityNode (CN_A1);
        // after opening, the A2 section's terminals (L2's from end, Load A2) move to CN_A2.
        Assert.Equal(new[] { "L2|from", "LD_A2|node" }, s.Moves.Select(m => m.Equipment + "|" + m.Terminal));
        Assert.All(s.Moves, m => Assert.Equal(("CN_A1", "CN_A2"), (m.BeforeBus, m.AfterBus)));
        Assert.Equal(4, open.Identity.Buses.Count);
        Assert.True(open.Status == "SECURE", open.Intact.Reason + " " + open.Exchange.Reason);
        Assert.True(open.Exchange.ControlledOutputEquivalent);
        // The TopologicalNodes (TP) still say one A node: the importer lists the disagreement rather than hiding it.
        Assert.Contains(open.Import.ImporterNotes, n => n.Contains("Topology check", StringComparison.Ordinal));
    }

    [Fact]
    public void Reclosing_restores_the_same_groups_and_identities()
    {
        CgmesStudyResult closed = CgmesStudyRunner.Run(Request(options: Switches)).Result;
        CgmesStudyResult reclosed = CgmesStudyRunner.Run(Request(options: Switches, actions: [new CgmesSwitchAction("#_BRK_A", false)])).Result;
        Assert.Empty(reclosed.Switching!.Moves);
        Assert.Equal(JsonSerializer.Serialize(closed.Identity, Json), JsonSerializer.Serialize(reclosed.Identity, Json));
        Assert.Equal(JsonSerializer.Serialize(closed.Intact, Json), JsonSerializer.Serialize(reclosed.Intact, Json));
    }

    [Fact]
    public void A_switch_action_needs_the_switch_topology_and_a_real_switch()
    {
        Assert.Throws<ArgumentException>(() => CgmesStudyRunner.Run(Request(actions: [new CgmesSwitchAction("_BRK_A", true)])));
        var notSwitch = Assert.Throws<CgmesStudyInputException>(() =>
            CgmesStudyRunner.Run(Request(options: Switches, actions: [new CgmesSwitchAction("_L1", true)])));
        Assert.Contains("not a switch", notSwitch.Message);
        Assert.Throws<CgmesStudyInputException>(() =>
            CgmesStudyRunner.Run(Request(options: Switches, actions: [new CgmesSwitchAction("_NOPE", true)])));
    }

    // ---- P1 over the imported model ----------------------------------------------------------------------------------

    [Fact]
    public void The_P1_verification_runs_over_the_imported_model_by_source_identity()
    {
        CgmesStudyResult r = CgmesStudyRunner.Run(Request(security: new CgmesSecurityRequest(["_L1", "_L2"], PostFaultRating.RateB))).Result;
        TransmissionSecurityResult s = Assert.IsType<TransmissionSecurityResult>(r.Security);
        Assert.Equal(2, s.Outages.Count);
        int l1 = r.Identity.Branches.Single(b => b.CimMrid == "L1").Index;
        Assert.Equal(l1, s.Outages[0].OutageIndex);
        // Rate B is absent from a CGMES import (TATL not read): every post-fault thermal row cites fallback A.
        Assert.All(s.Outages.SelectMany(o => o.Limits).Where(l => l.Kind.StartsWith("Branch", StringComparison.Ordinal)),
            l => Assert.Equal("fallback A", l.Source));
        Assert.Contains(r.Stages, st => st.Stage == "security");
        Assert.Equal(s.Status == "SECURE" ? "SECURE" : "NOT SECURE", r.Status);
    }

    [Fact]
    public void A_radial_outage_is_an_islanded_row_and_the_study_is_incomplete()
    {
        CgmesStudyResult r = CgmesStudyRunner.Run(Request(security: new CgmesSecurityRequest(["_L3"], PostFaultRating.RateA))).Result;
        Assert.Equal("ISLANDED", r.Security!.Outages.Single().Disposition);
        Assert.Equal(("NOT SECURE", "Incomplete"), (r.Status, r.Outcome));
        Assert.Throws<ArgumentException>(() => CgmesStudyRunner.Run(Request(security: new CgmesSecurityRequest(["_G1"], PostFaultRating.RateA))));
    }

    // ---- refused inputs ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_missing_required_profile_is_refused_by_name()
    {
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(Bundle().Where(p => p.Profile != "SSH").ToList())));
        Assert.Equal("SSH", e.Profile);
        Assert.Contains("required profile is missing", e.Message);
        var tp = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(Bundle().Where(p => p.Profile != "TP").ToList())));
        Assert.Equal("TP", tp.Profile);
        var sv = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(Bundle().Where(p => p.Profile != "SV").ToList())));
        Assert.Equal("SV", sv.Profile);
    }

    [Fact]
    public void An_absent_optional_profile_is_only_listed()
    {
        CgmesStudyResult r = CgmesStudyRunner.Run(Request(Bundle().Where(p => p.Profile != "SV").ToList(),
            Options with { StartingVoltages = false })).Result;
        Assert.Equal("SECURE", r.Status);
        Assert.Contains(r.Bundle.OmittedProfiles, o => o.StartsWith("SV:", StringComparison.Ordinal));
        Assert.Equal(0, r.Import.BusesWithStartingVoltage);
    }

    [Fact]
    public void A_duplicated_profile_type_is_refused_naming_both()
    {
        List<CgmesProfileInput> b = Bundle();
        b.Add(Profile("SSH", "p3_SSH.xml") with { Path = "copy_SSH.xml" });
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(b)));
        Assert.Contains("copy_SSH.xml", e.Message);
        Assert.Contains("p3_SSH.xml", e.Message);
        Assert.Contains("duplicated profile", e.Message);
    }

    [Fact]
    public void A_conflicting_duplicate_record_is_refused_naming_both_profiles_and_the_record()
    {
        byte[] tp = Edited("p3_TP.xml", "</rdf:RDF>",
            "<cim:ConformLoad rdf:about=\"#_LD_B\"><cim:EnergyConsumer.p>55</cim:EnergyConsumer.p></cim:ConformLoad></rdf:RDF>");
        List<CgmesProfileInput> b = Bundle();
        b[1] = Profile("TP", "p3_TP.xml", tp);
        // TP comes before SSH in the manifest, so SSH is the one that disagrees.
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(b)));
        Assert.Equal("SSH (p3_SSH.xml)", e.Profile);
        Assert.Equal("ConformLoad LD_B EnergyConsumer.p", e.Record);
        Assert.Contains("TP (p3_TP.xml) gives '55'", e.Message);
    }

    [Fact]
    public void A_mis_referenced_profile_is_refused_naming_the_profile_and_the_dependency()
    {
        byte[] ssh = Edited("p3_SSH.xml", "c0a80001-0000-4000-8000-0000000000e1", "c0a80001-0000-4000-8000-0000000000e9");
        List<CgmesProfileInput> b = Bundle();
        b[2] = Profile("SSH", "p3_SSH.xml", ssh);
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(b)));
        Assert.Equal("SSH (p3_SSH.xml)", e.Profile);
        Assert.Equal("md:Model.DependentOn c0a80001-0000-4000-8000-0000000000e9", e.Record);
    }

    [Fact]
    public void An_invalid_terminal_reference_is_refused_naming_the_record()
    {
        byte[] tp = Edited("p3_TP.xml", "<cim:Terminal rdf:about=\"#_T_LD_C\"><cim:Terminal.TopologicalNode rdf:resource=\"#_TN_C\"/>",
            "<cim:Terminal rdf:about=\"#_T_LD_C\"><cim:Terminal.TopologicalNode rdf:resource=\"#_TN_X\"/>");
        List<CgmesProfileInput> b = Bundle();
        b[1] = Profile("TP", "p3_TP.xml", tp);
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(b)));
        Assert.Equal("Terminal T_LD_C Terminal.TopologicalNode", e.Record);
    }

    [Fact]
    public void A_digest_mismatch_and_a_header_contradicting_the_declared_type_are_refused()
    {
        List<CgmesProfileInput> b = Bundle();
        b[0] = b[0] with { Sha256 = new string('0', 64) };
        Assert.Contains("SHA-256 mismatch", Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(b))).Message);
        List<CgmesProfileInput> c = Bundle();
        c[3] = Profile("DY", "p3_SV.xml");
        var e = Assert.Throws<CgmesStudyInputException>(() => CgmesStudyRunner.Run(Request(c, Options with { StartingVoltages = false })));
        Assert.Equal("md:Model.profile", e.Record);
    }

    [Fact]
    public void A_required_missing_set_point_is_refused_by_the_importer()
    {
        byte[] ssh = Edited("p3_SSH.xml", "<cim:EnergyConsumer.q>15</cim:EnergyConsumer.q>", "");
        List<CgmesProfileInput> b = Bundle();
        b[2] = Profile("SSH", "p3_SSH.xml", ssh);
        var e = Assert.Throws<InvalidDataException>(() => CgmesStudyRunner.Run(Request(b)));
        Assert.Contains("Load B", e.Message);
        // Without the strict option the same bundle imports, the missing q taken as 0 (the incumbent default), and the
        // study still says what it solved.
        CgmesStudyResult lax = CgmesStudyRunner.Run(Request(b, Options with { RequireSshSetPoints = false })).Result;
        Assert.False(lax.Import.Options.RequireSshSetPoints);
    }

    [Fact]
    public void Automatic_tap_control_in_the_bundle_is_listed_as_unrepresented()
    {
        const string head = "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:cim=\"http://iec.ch/TC57/2013/CIM-schema-cim16#\">";
        byte[] eq = Encoding.UTF8.GetBytes(head + "<cim:RatioTapChanger rdf:ID=\"_RTC1\"><cim:TapChanger.neutralStep>0</cim:TapChanger.neutralStep></cim:RatioTapChanger></rdf:RDF>");
        byte[] ssh = Encoding.UTF8.GetBytes(head + "<cim:RatioTapChanger rdf:about=\"#_RTC1\"><cim:TapChanger.controlEnabled>true</cim:TapChanger.controlEnabled></cim:RatioTapChanger></rdf:RDF>");
        CgmesBundleReport report = CgmesBundleInspector.Inspect(
            [new CgmesProfileInput("EQ", "3.0", "eq.xml", CgmesBundleInspector.Hash(eq), eq),
             new CgmesProfileInput("SSH", "3.0", "ssh.xml", CgmesBundleInspector.Hash(ssh), ssh)],
            topologyFromSwitches: true, needsStateVariables: false);
        CgmesUnrepresentedControl c = Assert.Single(report.UnrepresentedControls);
        Assert.Equal(("RTC1", "RatioTapChanger"), (c.Mrid, c.Class));
        Assert.Contains(report.Diagnostics, d => d.Contains("no md:FullModel header"));
    }
}
