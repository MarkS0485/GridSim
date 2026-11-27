using System.Text;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.127), pure Core: which dynamics verbs <c>breaking-change validate</c> re-runs (each verb classified with its
/// reason), the files a dynamics input names inside itself (so the journal can hash and keep them), the plug-in pin check
/// made before any re-run starts, and the fields of the dynamics result files that carry identity and not results.
/// </summary>
public sealed class Rc5DynamicsReplayPolicyTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("dynpolicy");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static readonly IReadOnlySet<string> Writer = new HashSet<string>(StringComparer.Ordinal) { "pf", "freq", "modal-full", "emt3" };

    private string W(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Every_dynamics_verb_is_classified_with_its_reason()
    {
        // Re-run: stability, dynamics-study, long-term (with --out), trip and modal.
        foreach (string v in new[] { "stability", "dynamics-study", "long-term", "trip", "modal" })
        {
            string[] argv = v == "long-term" ? [v, "case9", "lt.json", "--out", "lt.csv"] : [v, "case9"];
            Assert.Null(ReplayPolicy.Refusal(v, argv, Writer));
        }
        Assert.Equal(new[] { "stability", "long-term", "trip", "modal" }, ReplayPolicy.DynamicsVerbs);
        Assert.Contains("dynamics-study", ReplayPolicy.StudyVerbs);
        // long-term without --out writes its CSV beside its input, outside the output flags.
        Assert.Equal("long-term without --out writes its CSV beside its input, outside the output flags (give --out to make it re-runnable)",
            ReplayPolicy.Refusal("long-term", ["long-term", "case9", "lt.json"], Writer));
        // Never re-run, each with its own reason (not "not yet classified").
        foreach ((string v, string starts) in new[]
        {
            ("transient", "its timeseries.csv carries each slice's solve time"),
            ("emt", "its summary.md records the wall time"),
            ("replay-event", "its summary.md carries the run header"),
            ("scenario", "its summary.md carries the run header"),
            ("h0-study", "its summary.md carries the run header"),
            ("evidence", "its summary.md carries the run header"),
            ("impedance-stability", "its export records the time and host"),
            ("replay", "it plays a recorded trace on the console"),
            ("fit-frequency", "it prints its fit on the console"),
            ("fleet", "not yet shown to give the same result twice"),
        })
        {
            string? why = ReplayPolicy.Refusal(v, [v], Writer);
            Assert.NotNull(why);
            Assert.StartsWith(starts, why, StringComparison.Ordinal);
        }
        // The writer and study verbs that are dynamics already (freq, modal-full, emt3, motor-start, emt-line) keep their
        // class; a verb in no list is still "not yet classified".
        Assert.Null(ReplayPolicy.Refusal("modal-full", ["modal-full", "case9"], Writer));
        Assert.Null(ReplayPolicy.Refusal("motor-start", ["motor-start", "s.json"], Writer));
        Assert.Equal("not yet classified", ReplayPolicy.Refusal("no-such-verb", ["no-such-verb"], Writer));
        // stability --events-out names an output folder outside the output policy's flags: rewritten on a re-run.
        Assert.Contains(("stability", "--events-out"), ReplayPolicy.OutputPathFlags);
    }

    [Fact]
    public void A_study_file_names_its_raw_dyr_unit_protection_devices_and_curves()
    {
        W(Path.Combine("s", "case.raw"), "raw");
        W(Path.Combine("d", "case.dyr"), "dyr");
        W(Path.Combine("s", "units.json"), "{}");
        W(Path.Combine("s", "devices.json"), "{}");
        W(Path.Combine("s", "curves", "a.json"), "{}");
        W(Path.Combine("s", "curves", "sub", "b.json"), "{}");
        W(Path.Combine("s", "curves", "notes.txt"), "not read by the curve library");
        string study = W(Path.Combine("s", "study.json"), """
            { "schemaVersion": 1, "raw": { "path": "case.raw", "sha256": "x" }, "dyr": { "path": "../d/case.dyr", "sha256": "y" },
              "adaptive": { "unitProtection": { "path": "units.json", "sha256": "z" }, "devices": { "path": "devices.json", "sha256": "w" }, "curves": "curves" } }
            """);
        IReadOnlyList<DynamicsInputs.NamedFile> named = DynamicsInputs.Expand(study, File.ReadAllBytes(study));
        Assert.Equal(new[]
        {
            ("study:raw", "case.raw"), ("study:dyr", "../d/case.dyr"), ("study:adaptive.unitProtection", "units.json"),
            ("study:adaptive.devices", "devices.json"), (DynamicsInputs.CurvesFolderLabel, "curves"),
            (DynamicsInputs.CurvesLabel + "a.json", "curves/a.json"), (DynamicsInputs.CurvesLabel + "sub/b.json", "curves/sub/b.json"),
        }, named.Select(n => (n.Label, n.Written)));
        Assert.Equal(Path.Combine(_dir, "d", "case.dyr"), named[1].Full);
        // 2.1.130: the curves folder itself is named by its file list, never kept in the store (it is a list, not a file).
        Assert.All(named, n => Assert.Equal(n.Label != DynamicsInputs.CurvesFolderLabel, n.Snapshot));
        Assert.Equal("a.json\nsub/b.json\n", Encoding.UTF8.GetString(DynamicsInputs.FolderListing(Path.Combine(_dir, "s", "curves"))));
        // Not a study, an assignments file or a plug-in configuration: nothing.
        string other = W("other.json", """{ "schema": "gridsim-case/2", "network": ["n.json"] }""");
        Assert.Empty(DynamicsInputs.Expand(other, File.ReadAllBytes(other)));
        Assert.Empty(DynamicsInputs.Expand(Path.Combine(_dir, "x.dyr"), Encoding.ASCII.GetBytes("1 'GENCLS' 1 5.0 0.0 /\n")));
    }

    [Fact]
    public void An_assignments_file_names_its_user_models_and_a_plugin_configuration_its_assemblies_never_kept()
    {
        string abs = Path.Combine(_dir, "elsewhere", "m2.json");
        string escaped = abs.Replace("\\", "\\\\", StringComparison.Ordinal);
        string assign = W(Path.Combine("a", "assign.json"), $$"""
            { "schema": "gridsim-model-assignments/1", "userModels": [ { "machine": { "bus": 1, "id": "1" }, "model": "SHIPPED" },
              { "machine": { "bus": 2, "id": "1" }, "file": "models/m1.json", "sha256": "x" }, { "machine": { "bus": 3, "id": "1" }, "file": "{{escaped}}", "sha256": "y" } ] }
            """);
        IReadOnlyList<DynamicsInputs.NamedFile> a = DynamicsInputs.Expand(assign, File.ReadAllBytes(assign));
        Assert.Equal(new[] { ("assignments:userModels[1].file", "models/m1.json"), ("assignments:userModels[2].file", abs) }, a.Select(n => (n.Label, n.Written)));

        string config = W(Path.Combine("p", "plugins.json"), """
            { "schema": "gridsim-plugins/1", "root": "bin", "assemblies": [ { "path": "one.dll", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "models": ["ONE"] } ] }
            """);
        DynamicsInputs.NamedFile dll = Assert.Single(DynamicsInputs.Expand(config, File.ReadAllBytes(config)));
        Assert.Equal((DynamicsInputs.PluginAssemblyLabel + "assemblies[0]", "bin/one.dll", Path.Combine(_dir, "p", "bin", "one.dll"), false),
            (dll.Label, dll.Written, dll.Full, dll.Snapshot));
        Assert.True(DynamicsInputs.IsPluginConfiguration(File.ReadAllBytes(config)));
        Assert.False(DynamicsInputs.IsPluginConfiguration(File.ReadAllBytes(assign)));
    }

    [Fact]
    public void The_plugin_pin_check_names_a_missing_or_changed_assembly_and_passes_the_pinned_one()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("not really an assembly: only its hash is checked, nothing is loaded");
        string pin = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        string configPath = Path.Combine(_dir, "p", "plugins.json");
        byte[] config = Encoding.UTF8.GetBytes($$"""{ "schema": "gridsim-plugins/1", "assemblies": [ { "path": "one.dll", "sha256": "{{pin}}", "models": ["ONE"] } ] }""");
        string dll = Path.Combine(_dir, "p", "one.dll");
        Assert.Null(DynamicsInputs.VerifyPluginPins(config, configPath, p => p == dll ? bytes : null));
        Assert.Equal($"plug-in assembly missing: {dll}", DynamicsInputs.VerifyPluginPins(config, configPath, _ => null));
        byte[] other = Encoding.ASCII.GetBytes("another assembly");
        string now = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(other));
        Assert.Equal($"plug-in assembly changed: {dll} sha256 {now} != pinned {pin}", DynamicsInputs.VerifyPluginPins(config, configPath, _ => other));
        // A path that leaves the root is refused by the loader's own rule, before any bytes are read.
        byte[] escape = Encoding.UTF8.GetBytes($$"""{ "schema": "gridsim-plugins/1", "assemblies": [ { "path": "../one.dll", "sha256": "{{pin}}", "models": ["ONE"] } ] }""");
        Assert.StartsWith("plug-in assembly refused: ../one.dll:", DynamicsInputs.VerifyPluginPins(escape, configPath, _ => bytes), StringComparison.Ordinal);
    }

    /// <summary>The run-metadata sidecar and the CSV run header carry host, build and time identity only (RunInfo.WriteJson
    /// and RunInfo.HeaderLines). Two runs on different hosts at different times compare IDENTICAL; the run description, the
    /// provenance line and every data column are still compared.</summary>
    [Fact]
    public void Run_metadata_and_the_csv_run_header_are_left_out_and_everything_else_compared()
    {
        static byte[] Meta(string host, string time, string run) => Encoding.UTF8.GetBytes(
            $$"""{"timestampUtc":"{{time}}","backend":"cpu","gitCommit":"{{host}}c","buildConfig":"Release","coreVersion":"2.1.{{host.Length}}","runtime":".NET {{host}}","arch":"X64","serverGc":false,"simdTier":"Avx{{host.Length}}","simdLanes":{{host.Length}},"simdFma":true,"machineName":"{{host}}","os":"os {{host}}","cpu":"cpu {{host}}","logicalCores":{{host.Length}},"ramGb":{{host.Length}}.5,"run":"{{run}}"}""");
        VolatileFields mask = VolatileFields.Default;
        FileComparison same = ResultCompare.Compare("run-metadata.json", Meta("hostA", "2026-01-01T00:00:00Z", "stability x"), Meta("hostBB", "2026-02-02T00:00:00Z", "stability x"),
            "stability", s => s, s => s, mask);
        Assert.Empty(same.Fields);
        FileComparison run = ResultCompare.Compare("run-metadata.json", Meta("a", "t1", "verdict=STABLE"), Meta("a", "t1", "verdict=UNSTABLE"), "stability", s => s, s => s, mask);
        Assert.Equal("$.run", Assert.Single(run.Fields).Field);
        // The report sidecar of a report verb has the same fields (<report>.run-metadata.json).
        Assert.Empty(ResultCompare.Compare("bench.run-metadata.json", Meta("hostA", "t1", "r"), Meta("hostBB", "t2", "r"), "x", s => s, s => s, mask).Fields);
        // In another file the same names are results and are compared.
        Assert.NotEmpty(ResultCompare.Compare("other.json", Meta("hostA", "t1", "r"), Meta("hostBB", "t2", "r"), "x", s => s, s => s, mask).Fields);

        static byte[] Csv(string host, string time, string verdict, string value) => Encoding.UTF8.GetBytes(
            $"# GridSim  -  {time} UTC\n# code:    git {host}  |  GridSim.Core v2.1.127  |  build Release\n# runtime: .NET {host}\n# machine: {host}  os\n"
            + $"# cpu:     cpu {host}  -  8 logical cores  -  16 GB RAM\n# simd:    Avx2  (4 doubles/step)  FMA=True\n# backend: cpu\n# run:     stability  verdict={verdict}\n"
            + "# provenance: Synthetic (a model trajectory)\nt_s,trace,delta_rad\n0,0,0.5\n0.01,0," + value + "\n");
        Assert.Empty(ResultCompare.Compare("rms-events-machines.csv", Csv("a", "2026-01-01 00:00:00", "STABLE", "0.25"), Csv("bb", "2026-02-02 10:00:00", "STABLE", "0.25"),
            "stability", s => s, s => s, mask).Fields);
        FileComparison verdict = ResultCompare.Compare("rms-events-machines.csv", Csv("a", "t", "STABLE", "0.25"), Csv("a", "t", "UNSTABLE", "0.25"), "stability", s => s, s => s, mask);
        FieldDifference line = Assert.Single(verdict.Fields);
        Assert.Equal(("(comment line)", "string"), (line.Field, line.Kind));
        string oneUlp = Math.BitIncrement(0.25).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        FileComparison moved = ResultCompare.Compare("rms-events-machines.csv", Csv("a", "t", "STABLE", "0.25"), Csv("bb", "t2", "STABLE", oneUlp), "stability", s => s, s => s, mask);
        FieldDifference d = Assert.Single(moved.Fields);
        Assert.Equal(("delta_rad", "number", (ulong?)1UL), (d.Field, d.Kind, d.MaxUlps));
        // A CSV with no comment lines is compared exactly as before.
        (List<string> comments, string body) = ResultCompare.SplitCommentLines("a,b\n1,2\n");
        Assert.Empty(comments);
        Assert.Equal("a,b\n1,2\n", body);
    }
}
