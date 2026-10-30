using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F23: the machine dynamic data file (gridsim-dynamics/2) feeding SubtransientInit.MachineData, exciters and governors.
/// Pinned: the fixture validates under the v2 schemas and kind specs, binds to case9 and yields exactly the hand-written
/// MachineData / controller objects; the study runs on it and is stationary undisturbed; every fail-loud rule (missing
/// required parameter, ignored parameter, unknown or ambiguous generator, double binding, converter binding, lumped
/// governor, PSS without exciter, bad IEEEG1 fractions, placeholder machine) is refused with a message naming it.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    private static string F23SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string F23Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    /// <summary>Write a variant of the fixture (one exact text replacement) to a temp file and return its path.</summary>
    private static string F23Variant(string find, string replace)
    {
        string text = File.ReadAllText(F23Fixture);
        Assert.Contains(find, text, StringComparison.Ordinal);   // the variant really edits the fixture
        string path = Path.Combine(Path.GetTempPath(), "gridsim-b3-f23-" + Guid.NewGuid().ToString("N") + ".dynamics.json");
        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
        return path;
    }

    private static string F23LoadError(GridModel model, string find, string replace)
    {
        string path = F23Variant(find, replace);
        try { return Assert.Throws<InvalidDataException>(() => MachineDynamicsLoader.Load(path, F23SchemasDir, model)).Message; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void F23_the_fixture_validates_as_a_v2_document()
    {
        V2DocumentReport report = V2DocumentValidator.ValidateFiles(new[] { F23Fixture }, F23SchemasDir)[0];
        Assert.Equal("gridsim-dynamics/2", report.SchemaTag);
        Assert.True(report.Valid, string.Join("\n", report.Errors));
    }

    [Fact]
    public void F23_the_fixture_binds_to_case9_and_gives_exactly_the_written_data()
    {
        GridModel model = IeeeCases.Case9();
        MachineDynamicsSet set = MachineDynamicsLoader.Load(F23Fixture, F23SchemasDir, model);
        Assert.Equal("WSCC 9-bus GENROU test machines", set.Name);
        Assert.Equal(3, set.Entries.Count);
        Assert.False(set.AllRecorded);   // Synthetic values: a study on them stays INDICATIVE

        // Bus 1: no inertiaH in the template, so the case's own H, said in a note; saturation and leakage; the hydro governor.
        MachineDynamicsEntry e1 = set.For(0)!;
        var expected1 = new SubtransientInit.MachineData(model.Generators[0].H, 0.0, 1.0, 0.30, 0.22, 0.65, 0.40, 0.22,
            8.0, 0.03, 0.40, 0.05, new SubtransientMachine.SaturationCurve(0.10, 0.30),
            new HydroGovernorData(0.04, 0.4, 5.0, 0.2, 2.0, 0.0, 1.2), 0.15);
        Assert.Equal(expected1, e1.Data);
        Assert.Null(e1.Exciter);
        Assert.Contains(e1.Notes, n => n.Contains("from the case", StringComparison.Ordinal));

        // Bus 2: SEXS with PSS1A, TGOV1.
        MachineDynamicsEntry e2 = set.For(1)!;
        var expected2 = new SubtransientInit.MachineData(6.4, 0.5, 1.8, 0.30, 0.25, 1.7, 0.55, 0.25, 6.0, 0.04, 0.40, 0.06,
            null, new Tgov1Data(0.05, 0.5, 1.0, 5.0, 0.0, 0.0, 1.5), 0.0);
        Assert.Equal(expected2, e2.Data);
        RmsExciter x2 = e2.Exciter!.Create();
        Assert.Equal("SEXS", x2.Kind);
        Assert.NotNull(x2.Stabiliser);
        Assert.NotSame(x2, e2.Exciter.Create());   // a fresh instance every time

        // Bus 3: IEEET1, IEEEG1 with rate limits; the inertia carries its own DerivedFromRecorded tag.
        MachineDynamicsEntry e3 = set.For(2)!;
        var expected3 = new SubtransientInit.MachineData(3.01, 0.0, 1.3, 0.18, 0.14, 1.25, 0.25, 0.14, 5.9, 0.03, 0.60, 0.05,
            null, new Ieeeg1Data(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.4, 0.3, 0.0, 0.0, 1.5, 0.0, 0.0, 0.0, 0.0, 0.1, -1.0), 0.0);
        Assert.Equal(expected3, e3.Data);
        Assert.Equal("IEEET1", e3.Exciter!.Create().Kind);
        Assert.Equal(new[] { ProvenanceTag.DerivedFromRecorded, ProvenanceTag.Synthetic }, e3.Provenance);
    }

    [Fact]
    public void F23_a_study_on_the_file_data_is_stationary_undisturbed_and_reacts_to_a_fault()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(F23Fixture, F23SchemasDir, model);
        var data = model.Generators.Select((g, k) => set.For(k)!.Data).ToList();
        var exciters = model.Generators.Select((g, k) => set.For(k)!.Exciter?.Create()).ToList();

        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0, exciters: exciters);
        Assert.True(still.StayedInSynchronism);
        foreach (MultiMachineSubtransient.MachineTrace m in still.Machines)
            Assert.True(Math.Abs(m.SpeedPu[^1]) < 1e-7, $"speed drifted to {m.SpeedPu[^1]:R}");

        var exciters2 = model.Generators.Select((g, k) => set.For(k)!.Exciter?.Create()).ToList();
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 3.0, exciters: exciters2);
        Assert.True(fault.Machines.Any(m => Math.Abs(m.SpeedPu[^1]) > 1e-6), "a fault must move the fleet");
    }

    [Theory]
    [InlineData("\"xqpPu\": 0.40, \"xqppPu\": 0.22,", "\"xqpPu\": 0.40,", "'xqppPu' is required")]
    [InlineData("\"damping\": 0.0,", "\"damping\": 0.0, \"unitTransformerXPu\": 0.1,", "unitTransformerXPu: not read")]
    [InlineData("{ \"bus\": 1, \"template\"", "{ \"bus\": 99, \"template\"", "no in-service generator at bus 99")]
    [InlineData("{ \"bus\": 2, \"template\"", "{ \"bus\": 1, \"template\"", "already bound")]
    [InlineData("\"model\": \"TGOV1\", \"droopPu\": 0.05,", "\"model\": \"lumped\", \"droopPu\": 0.05,", "lumped governor")]
    [InlineData("\"exciter\": \"gridsim.test.wscc9.sexs@1\", ", "", "a pss is given with no exciter")]
    [InlineData("\"ieeeg1K1\": 0.3,", "\"ieeeg1K1\": 0.5,", "must sum to 1")]
    [InlineData("\"tgov1ReheatLagS\": 5.0,", "\"tgov1ReheatLagS\": 5.0, \"hygovGateTimeS\": 0.2,", "hygovGateTimeS: not read")]
    [InlineData("{ \"bus\": 3, \"unit\": 0,", "{ \"bus\": 3, \"unit\": 1,", "there is no unit 1")]
    public void F23_bad_dynamic_data_is_refused_naming_the_problem(string find, string replace, string fragment)
    {
        string message = F23LoadError(IeeeCases.Case9(), find, replace);
        Assert.Contains(fragment, message, StringComparison.Ordinal);
    }

    [Fact]
    public void F23_schema_and_kind_spec_errors_are_reported_before_any_binding()
    {
        // A unit the kind does not declare, and an unknown member in a machine entry: the v2 layers refuse both.
        string m1 = F23LoadError(IeeeCases.Case9(), "\"sexsGain\": 100.0,", "\"sexsGain\": { \"v\": 100.0, \"unit\": \"s\" },");
        Assert.Contains("unit 's' does not match", m1, StringComparison.Ordinal);
        string m2 = F23LoadError(IeeeCases.Case9(), "{ \"bus\": 2, \"template\"", "{ \"bus\": 2, \"generator\": \"x\", \"template\"");
        Assert.Contains("/machines/1", m2, StringComparison.Ordinal);   // oneOf: name OR bus, not both
    }

    [Fact]
    public void F23_converter_plant_cannot_be_bound()
    {
        GridModel model = IeeeCases.Case9();
        GridModel withPv = model.CopyWith(generators: model.Generators.Select((g, i) => i == 2 ? g with { Type = "solar" } : g).ToList());
        string path = F23Variant("\"name\": \"WSCC", "\"name\": \"WSCC");   // the fixture as it is
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => MachineDynamicsLoader.Load(path, F23SchemasDir, withPv));
            Assert.Contains("converter plant", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
