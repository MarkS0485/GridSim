using System;
using System.IO;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R095: the overcurrent grading and sequence-of-events studies are reachable from the CLI (`grade`,
/// `protection-check --cascade --alarms`). Covers the Core pieces the CLI relies on (the relay spec parser, the strict
/// alarm CSV reader, the new fail-loud input checks) and, at source level, the CLI wiring (the test project does not
/// reference the CLI).
/// </summary>
public sealed class GradeAndSoeCliTests
{
    // ---- relay spec parser ----

    [Theory]
    [InlineData("iec-si:400:0.1", OvercurrentCurve.IecStandardInverse, 400.0, 0.1)]
    [InlineData("IEC-VI:1.5:0.25", OvercurrentCurve.IecVeryInverse, 1.5, 0.25)]
    [InlineData("iec-ei:100:1", OvercurrentCurve.IecExtremelyInverse, 100.0, 1.0)]
    [InlineData("iec-lti:100:0.5", OvercurrentCurve.IecLongTimeInverse, 100.0, 0.5)]
    [InlineData("ieee-mi:200:2", OvercurrentCurve.IeeeModeratelyInverse, 200.0, 2.0)]
    [InlineData("ieee-vi:200:3.5", OvercurrentCurve.IeeeVeryInverse, 200.0, 3.5)]
    [InlineData(" ieee-ei : 200 : 1e-1 ", OvercurrentCurve.IeeeExtremelyInverse, 200.0, 0.1)]
    public void Idmt_specs_parse_to_the_exact_element(string spec, OvercurrentCurve curve, double pickup, double td)
    {
        Assert.Equal(new OvercurrentElement(curve, pickup, TimeDial: td), OvercurrentCoordination.ParseElement(spec));
    }

    [Fact]
    public void A_definite_time_spec_carries_its_delay()
    {
        Assert.Equal(new OvercurrentElement(OvercurrentCurve.DefiniteTime, 1200.0, DefiniteTimeSeconds: 0.9),
            OvercurrentCoordination.ParseElement("dt:1200:0.9"));
        Assert.Equal(0.0, OvercurrentCoordination.ParseElement("dt:1200:0").DefiniteTimeSeconds);
    }

    [Theory]
    [InlineData("iec-xx:400:0.1")]   // unknown curve
    [InlineData("iec-si:400")]       // missing field
    [InlineData("iec-si:400:0.1:9")] // extra field
    [InlineData("iec-si:0:0.1")]     // pickup not > 0
    [InlineData("iec-si:-5:0.1")]
    [InlineData("iec-si:400:0")]     // time dial not > 0
    [InlineData("iec-si:400:-0.1")]
    [InlineData("iec-si:NaN:0.1")]   // not finite
    [InlineData("iec-si:400:Infinity")]
    [InlineData("iec-si:4,00:0.1")]  // not invariant culture
    [InlineData("dt:400:-0.1")]      // negative delay
    [InlineData("")]
    public void Bad_specs_fail_loud_naming_the_spec(string spec)
    {
        FormatException ex = Assert.Throws<FormatException>(() => OvercurrentCoordination.ParseElement(spec));
        Assert.Contains($"'{spec}'", ex.Message, StringComparison.Ordinal);
    }

    // ---- fail-loud inputs in the curve library (valid inputs are unchanged; see OvercurrentCoordinationTests) ----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void A_meaningless_fault_current_is_refused(double current)
    {
        var e = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.1);
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.OperateTimeSeconds(e, current));
    }

    [Fact]
    public void Zero_current_never_operates_rather_than_failing()
    {
        var e = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.1);
        Assert.True(double.IsPositiveInfinity(OvercurrentCoordination.OperateTimeSeconds(e, 0.0)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_meaningless_time_dial_is_refused(double td)
    {
        var e = new OvercurrentElement(OvercurrentCurve.IeeeVeryInverse, 100.0, td);
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.OperateTimeSeconds(e, 500.0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_meaningless_definite_time_delay_is_refused(double delay)
    {
        var e = new OvercurrentElement(OvercurrentCurve.DefiniteTime, 100.0, DefiniteTimeSeconds: delay);
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.OperateTimeSeconds(e, 500.0));
    }

    [Fact]
    public void An_infinite_pickup_and_a_meaningless_cti_are_refused()
    {
        var ok = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.1);
        var inf = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, double.PositiveInfinity, 0.1);
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.OperateTimeSeconds(inf, 500.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.Grade(ok, ok, 500.0, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.Grade(ok, ok, 500.0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => OvercurrentCoordination.GradeChain(new[] { ok, ok }, 500.0, double.PositiveInfinity));
    }

    // ---- alarm log CSV ----

    private static IReadOnlyList<AlarmEvent> Csv(string text) => AlarmLogCsv.Parse(new StringReader(text), "test.csv");

    [Fact]
    public void A_well_formed_log_parses_every_row_exactly()
    {
        var alarms = Csv(
            "# GB event, times from fault inception\n" +
            "at_s,device,from,to,description\n" +
            "0.12,51,4,5,phase OC trip, feeder A\n" +
            "\n" +
            "0.5,27,0,0,undervoltage at bus 7\n" +
            "1.25e0,21-Z1,9,4,\n");
        Assert.Equal(3, alarms.Count);
        Assert.Equal(new AlarmEvent(0.12, "51", 4, 5, "phase OC trip, feeder A"), alarms[0]);   // commas kept
        Assert.Equal(new AlarmEvent(0.5, "27", 0, 0, "undervoltage at bus 7"), alarms[1]);
        Assert.Equal(new AlarmEvent(1.25, "21-Z1", 9, 4, ""), alarms[2]);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("time,device,from,to,description\n", "header")]
    [InlineData("at_s,device,from,to,description\n0.1,51,4\n", "line 2")]
    [InlineData("at_s,device,from,to,description\nNaN,51,4,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\nInfinity,51,4,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\n0.1, ,4,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\n0.1,51,4.5,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\n0.1,51,-4,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\n0.1,51,0,5,x\n", "line 2")]
    [InlineData("at_s,device,from,to,description\n0.1,51,4,5,x\n0.2,51,4,0,x\n", "line 3")]
    public void A_malformed_log_is_refused_with_its_line_never_dropped(string text, string where)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Csv(text));
        Assert.Contains(where, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_fails_loud()
    {
        Assert.Throws<FileNotFoundException>(() => AlarmLogCsv.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.csv")));
    }

    [Fact]
    public void A_parsed_log_cross_references_a_cascade()
    {
        var cascade = new CascadeResult(new[]
        {
            new CascadeStep(1, "51", "OC", "A", 4, 5, 0.1, "overload", true, 1),
            new CascadeStep(2, "49", "thermal", "B", 9, 4, 2.0, "overload", true, 0),
        }, true, "nothing else trips");
        var alarms = Csv("at_s,device,from,to,description\n0.12,51,5,4,x\n2.4,49,4,9,y\n0.3,27,0,0,uv\n");
        SoeCrossReference x = SequenceOfEvents.CrossReference(cascade, alarms);
        Assert.True(x.FullyConsistent);
        Assert.Equal(2, x.Agreements);
    }

    // ---- CLI wiring (source level) ----

    private static string CliSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
    }

    [Fact]
    public void Cli_grade_and_alarms_are_wired_with_their_exit_codes()
    {
        string src = CliSource();
        foreach (string flag in new[] { "--chain", "--current", "--cti", "--alarms" })
            Assert.Contains($"    \"{flag}\",\n", src);
        Assert.Contains("rest[0].Equals(\"grade\", StringComparison.OrdinalIgnoreCase)", src);
        Assert.Contains("OvercurrentCoordination.GradeChain(chain, i, cti)", src);
        Assert.Contains("Environment.ExitCode = allCoordinated ? 0 : 3;", src);
        Assert.Contains("GridSim.Core.Protection.AlarmLogCsv.Load(alarmsPath)", src);
        Assert.Contains("SequenceOfEvents.CrossReference(cas, alarms)", src);
        Assert.Contains("if (!x.FullyConsistent) Environment.ExitCode = 3;", src);
        Assert.Contains("error: --alarms cross-references the predicted cascade; add --cascade.", src);
    }
}
