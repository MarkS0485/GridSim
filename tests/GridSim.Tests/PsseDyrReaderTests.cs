using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): the PSS/E DYR reader. case14.dyr (tests/GridSim.Tests/fixtures/psse) is hand-written for the
/// IEEE 14-bus RAW fixture: GENROU and GENSAL machines with every mapped controller (SEXS, IEEET1, PSS1A, TGOV1, IEEEG1,
/// HYGOV), one record split over two lines, one written with commas. The document the reader makes must load through the
/// same loader as any gridsim-dynamics/2 file, with every value where the DYR put it.
/// </summary>
public class PsseDyrReaderTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static PsseParseResult Case14() => PsseRawParser.ParseFile(Fixture("case14.raw"));

    private static string Dyr() => File.ReadAllText(Fixture("case14.dyr")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static MachineDynamicsSet Load(PsseDyrResult r, PsseParseResult raw)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-dyr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "case14.dyr.dynamics.json");
            File.WriteAllText(path, r.Json);
            return MachineDynamicsLoader.Load(path, Path.Combine(CliProcess.Root(), "schemas", "v2"), raw.Model);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Case14_dyr_maps_every_machine_and_controller_and_loads()
    {
        PsseParseResult raw = Case14();
        PsseDyrResult r = PsseDyrReader.Read(Dyr(), raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        Assert.Equal(5, r.Machines);
        Assert.Equal(11, r.Records.Count);
        Assert.Empty(r.Notes);
        MachineDynamicsSet set = Load(r, raw);
        Assert.Equal(5, set.Entries.Count);
        Assert.True(set.AllRecorded);

        // Bus 1: GENROU as written, X''q = X''d, saturation through S(1.0) and S(1.2); SEXS lead = TA/TB x TB; TGOV1.
        MachineDynamicsEntry g1 = set.Entries.Single(e => raw.Model.Generators[e.GeneratorIndex].Bus == 1);
        Assert.Equal((5.148, 0.0, 1.80, 0.30, 0.23, 1.75, 0.65, 0.23), (g1.Data.H, g1.Data.Damping, g1.Data.Xd, g1.Data.Xdp, g1.Data.Xdpp, g1.Data.Xq, g1.Data.Xqp, g1.Data.Xqpp));
        Assert.Equal((7.4, 0.03, 0.40, 0.05), (g1.Data.Tdop, g1.Data.Tdopp, g1.Data.Tqop, g1.Data.Tqopp));
        Assert.Equal((0.09, 0.38), (g1.Data.Saturation!.SatAt1Pu, g1.Data.Saturation.SatAt12Pu));
        Assert.Equal("SEXS", g1.Exciter!.Model);
        Assert.Equal(0.1 * 10.0, g1.Exciter.Params["sexsLeadS"]);
        Assert.Equal((10.0, 100.0, 0.05, 0.0, 4.0), (g1.Exciter.Params["sexsLagS"], g1.Exciter.Params["sexsGain"], g1.Exciter.Params["sexsExciterTimeS"], g1.Exciter.Params["sexsFieldMinPu"], g1.Exciter.Params["sexsFieldMaxPu"]));
        Assert.IsType<GridSim.Core.Dynamics.Models.Tgov1Data>(g1.Data.Governor);

        // Bus 2: GENSAL (the record split over two lines): X'q = Xq and T'qo = T''qo; IEEET1 with PSS1A; HYGOV.
        MachineDynamicsEntry g2 = set.Entries.Single(e => raw.Model.Generators[e.GeneratorIndex].Bus == 2);
        Assert.Equal((6.54, 1.25, 0.31, 0.25, 0.70, 0.70, 0.25), (g2.Data.H, g2.Data.Xd, g2.Data.Xdp, g2.Data.Xdpp, g2.Data.Xq, g2.Data.Xqp, g2.Data.Xqpp));
        Assert.Equal((6.1, 0.05, 0.06, 0.06), (g2.Data.Tdop, g2.Data.Tdopp, g2.Data.Tqop, g2.Data.Tqopp));
        Assert.Null(g2.Data.Saturation);
        Assert.Equal("IEEET1", g2.Exciter!.Model);
        Assert.Equal((400.0, 0.04, 1.0, 0.80, 0.03, 1.0, -7.3, 7.3), (g2.Exciter.Params["ieeet1RegulatorGain"], g2.Exciter.Params["ieeet1RegulatorTimeS"],
            g2.Exciter.Params["ieeet1ExciterConstant"], g2.Exciter.Params["ieeet1ExciterTimeS"], g2.Exciter.Params["ieeet1FeedbackGain"],
            g2.Exciter.Params["ieeet1FeedbackTimeS"], g2.Exciter.Params["ieeet1RegulatorMinPu"], g2.Exciter.Params["ieeet1RegulatorMaxPu"]));
        Assert.Equal((20.0, 10.0, 0.15, 0.05, 0.15, 0.05, -0.1, 0.1), (g2.Exciter.Pss!["gain"], g2.Exciter.Pss["washoutS"], g2.Exciter.Pss["lead1S"],
            g2.Exciter.Pss["lag1S"], g2.Exciter.Pss["lead2S"], g2.Exciter.Pss["lag2S"], g2.Exciter.Pss["outputMinPu"], g2.Exciter.Pss["outputMaxPu"]));
        Assert.IsType<GridSim.Core.Dynamics.Models.HydroGovernorData>(g2.Data.Governor);

        // Bus 3: the record written with commas; IEEEG1 with droop 1 / K.
        MachineDynamicsEntry g3 = set.Entries.Single(e => raw.Model.Generators[e.GeneratorIndex].Bus == 3);
        Assert.Equal((4.0, 1.30, 0.14), (g3.Data.H, g3.Data.Xd, g3.Data.Xdpp));
        Assert.IsType<GridSim.Core.Dynamics.Models.Ieeeg1Data>(g3.Data.Governor);
        Assert.Null(g3.Exciter);
    }

    [Fact]
    public void The_document_tags_derived_values_and_names_the_file_and_line()
    {
        PsseDyrResult r = PsseDyrReader.Read(Dyr(), Case14(), "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        Assert.Contains("\"schema\": \"gridsim-dynamics/2\"", r.Json, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"gridsim.psse.b2.m1.machine\"", r.Json, StringComparison.Ordinal);
        Assert.Contains("PSS/E DYR case14.dyr, line 7: GENSAL record.", r.Json, StringComparison.Ordinal);
        Assert.Contains("PSS/E DYR case14.dyr, line 7: GENSAL has no q-axis transient circuit: X'q = Xq, so that circuit carries nothing.", r.Json, StringComparison.Ordinal);
        Assert.Contains("PSS/E DYR case14.dyr, line 13: R = 1 / K.", r.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_models_are_refused_with_the_full_list()
    {
        // Tests (2.0.182): the unmapped example was ESST4B until the reader learned to map it; ESST1A, still unmapped,
        // takes its place with the same counts and lines, so the assertion is exactly as strong as before.
        string text = Dyr() + "     6 'ESST1A' 1  0.0 1 2 3 /\n     8 'GENCLS' 1  3.0 0.0 /\n     1 'USRMDL' 1 'MYAVR' 1 2 0 0 0 0 /\n     8 'ESST1A' 1 0.0 /\n";
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, Case14(), "case14", new PsseDyrOptions { SourceName = "x.dyr" }));
        Assert.Contains("models this reader does not map: ESST1A x2 (first at line 16), GENCLS x1 (first at line 17), USRMDL MYAVR x1 (first at line 18)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-unmodelled", ex.Message, StringComparison.Ordinal);

        PsseDyrResult allowed = PsseDyrReader.Read(text, Case14(), "case14", new PsseDyrOptions { AllowUnmodelled = true });
        Assert.Equal(5, allowed.Machines);
        Assert.Contains(allowed.Notes, n => n.StartsWith("left out (unmodelled dynamics allowed): models this reader does not map: ESST1A x2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("     2 'IEEET1' 1   0.0  400.0", "     2 'IEEET1' 1   0.02  400.0", "IEEET1 for machine '1' at bus 2 (line 9): a voltage transducer lag TR")]
    [InlineData("10.0  10.0  20.0  0.1  -0.1  0.0  0.0 /", "10.0  5.0  20.0  0.1  -0.1  0.0  0.0 /", "PSS1A for machine '1' at bus 2 (line 10): a washout sT5/(1+sT6) with T5 not equal to T6")]
    [InlineData("     2 'PSS1A'  1   1  0  0.0  0.0", "     2 'PSS1A'  1   1  0  0.06  0.0", "PSS1A for machine '1' at bus 2 (line 10): the A1 / A2 filter")]
    [InlineData("2.0  1.0  0.0  0.0 /", "2.0  1.2  0.0  0.08 /", "HYGOV for machine '1' at bus 2 (line 11): a turbine gain At other than 1")]
    [InlineData("     3 'IEEEG1' 1   0  0", "     3 'IEEEG1' 1   4  0", "IEEEG1 for machine '1' at bus 3 (line 13): a second (low-pressure) shaft at another bus (JBUS not 0)")]
    [InlineData("0.25  0.12  0.0  0.0 /", "0.25  0.12  0.1  0.3 /", "GENSAL saturation for machine '1' at bus 2 (line 7)")]
    public void Values_gridsim_cannot_honour_are_refused_unless_allowed(string find, string replace, string expected)
    {
        string text = Dyr().Replace(find, replace, StringComparison.Ordinal);
        Assert.NotEqual(Dyr(), text);
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, Case14(), "case14"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        PsseDyrResult allowed = PsseDyrReader.Read(text, Case14(), "case14", new PsseDyrOptions { AllowUnmodelled = true });
        Assert.Contains(allowed.Notes, n => n.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(5, allowed.Machines);
    }

    [Fact]
    public void A_controller_with_no_machine_record_and_a_converter_are_refused_unless_allowed()
    {
        string text = Dyr().Replace("     6 'GENROU' 1   7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0 /\n",
            "     6 'SEXS'   1   0.1  10.0  100.0  0.05  0.0  4.0 /\n", StringComparison.Ordinal)
            + "     8 'REGC_A' 1  1  0.02  10.0  0.9  0.4  0.9  1.2  0.5  0.0  1.3  0.02  0.7  1.5  -1.5  0.7 /\n";
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, Case14(), "case14"));
        Assert.Contains("SEXS for machine '1' at bus 6 (line 14) has no GENROU or GENSAL record for its unit", ex.Message, StringComparison.Ordinal);
        // WECC expansion (justified strengthening): REGC_A used to be refused outright ("GridSim's converter parameters live in
        // the case's asset classes"). It is now modelled (WeccRenewableNetworkTests), so this record is judged as a model: the
        // unit also has a GENROU record, which a unit cannot carry together with a renewable converter model, and its
        // Iolim = 1.3 is out of range (the HVRCM lower limit on the exported current must be 0 or negative). Both are listed.
        Assert.Contains("REGC_A for machine '1' at bus 8 (line 16): the unit also has a GENROU machine record (line 15); a unit takes either a synchronous machine model or a renewable converter model, not both", ex.Message, StringComparison.Ordinal);
        Assert.Contains("REGC_A Iolim = 1.3: must be 0 or negative", ex.Message, StringComparison.Ordinal);
        PsseDyrResult allowed = PsseDyrReader.Read(text, Case14(), "case14", new PsseDyrOptions { AllowUnmodelled = true });
        Assert.Equal(4, allowed.Machines);   // bus 6 has no machine model; bus 8 keeps its GENROU
    }

    [Theory]
    [InlineData("     8 'GENROU' 1", "     9 'GENROU' 1", "PSS/E DYR line 15: GENROU names machine '1' at bus 9, which the RAW case does not hold.")]
    [InlineData("     8 'GENROU' 1", "     8 'GENROU' 2", "PSS/E DYR line 15: GENROU names machine '2' at bus 8, which the RAW case does not hold.")]
    [InlineData("     6 'GENROU' 1", "     8 'GENROU' 1", "PSS/E DYR line 15: machine '1' at bus 8 already has a machine record (GENROU, line 14)")]
    [InlineData("0.12  0.0  0.0 /\n     8", "0.12  0.0  0.0\n     8", "PSS/E DYR line 14: GENROU needs exactly 14 values after the machine ID; the record has 31.")]
    [InlineData("1.55  0.25  0.40  0.18  0.12  0.0  0.0 /\n", "1.55  0.25  0.40  0.18  0.12  0.0  0.0\n", "the record is not ended by a slash before the end of the file")]
    [InlineData("     1 'TGOV1'  1   0.05", "     1 'TGOV1'  1   fast", "PSS/E DYR line 5: TGOV1 R is 'fast'; it must be a finite number.")]
    public void Malformed_records_are_refused_naming_the_line(string find, string replace, string expected)
    {
        string text = Dyr().Replace("\r\n", "\n", StringComparison.Ordinal);
        int at = expected.Contains("already has", StringComparison.Ordinal) ? text.IndexOf(find, StringComparison.Ordinal) : text.LastIndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture text to change was not found");
        text = text[..at] + replace + text[(at + find.Length)..];
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, Case14(), "case14", new PsseDyrOptions { AllowUnmodelled = true }));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
