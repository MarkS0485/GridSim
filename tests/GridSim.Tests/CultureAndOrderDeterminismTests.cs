using System.Globalization;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Outputs and input choices that depended on the machine (its culture, its file system's enumeration order, a lenient
/// date parse) are now fixed: a MATPOWER file written under any culture has the same bytes; a replay run directory with
/// two snapshots of one index, a repeated telemetry key, a culture-shaped date or a non-numeric cell is refused instead
/// of being read in an order or a meaning that differs between machines.
/// </summary>
public class CultureAndOrderDeterminismTests
{
    [Fact]
    public void A_matpower_file_written_under_a_comma_decimal_culture_has_invariant_numbers_in_its_notes()
    {
        GridModel m = IeeeCases.Case9();
        // case9's buses carry no per-bus limits in the hand-coded model, so the operational-band note is written.
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            MatpowerWriteResult de = MatpowerWriter.Write(m);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            MatpowerWriteResult inv = MatpowerWriter.Write(m);
            Assert.Equal(inv.Text, de.Text);
            Assert.Equal(inv.Notes, de.Notes);
            Assert.DoesNotContain(de.Notes, n => n.Contains("1,06", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void A_frequency_assessment_summary_under_a_comma_decimal_culture_has_invariant_numbers()
    {
        // GR-543: a 0.3 Hz/s RoCoF that stays inside the operational band, so the summary names the RoCoF limit.
        var r = new GridSim.Core.Dynamics.FrequencyResponse(0.3, 49.9, 1.0, 49.95, Array.Empty<GridSim.Core.Frequency.FrequencyPoint>());
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string legacyDe = GridSim.Core.Dynamics.FrequencyLimits.Assess(r).Summary;
            string activeDe = GridSim.Core.Dynamics.FrequencyLimits.Assess(r, 0.25).Summary;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(GridSim.Core.Dynamics.FrequencyLimits.Assess(r).Summary, legacyDe);
            Assert.Equal(GridSim.Core.Dynamics.FrequencyLimits.Assess(r, 0.25).Summary, activeDe);
            Assert.Equal("RoCoF OVER legacy 0.125 Hz/s", legacyDe);
            Assert.Equal("RoCoF OVER 0.25 Hz/s", activeDe);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static string MakeRun(string csv, params string[] snapshotNames)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (string name in snapshotNames)
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", name), model, r, trace, meta);
        }
        File.WriteAllText(Path.Combine(dir, "state-estimate.csv"), csv);
        return dir;
    }

    private static void WithRun(string csv, string[] snapshots, Action<string> body)
    {
        string dir = MakeRun(csv, snapshots);
        try { body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    [Fact]
    public void Two_snapshots_with_one_index_are_refused_not_ordered_by_the_file_system()
    {
        WithRun("index,time_utc\n1,2024-06-01T09:00:00Z\n", new[] { "slice-000001.json", "state-000001.json" }, dir =>
        {
            var ex = Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir));
            Assert.Contains("same index 1", ex.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("01/06/2024 09:00")]
    [InlineData("1 Jun 2024 09:00")]
    [InlineData("2024-06-01 09:00:00")]
    public void A_culture_shaped_time_utc_is_refused(string stamp)
    {
        WithRun($"index,time_utc\n0,{stamp}\n", new[] { "state-000000.json" }, dir =>
            Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir)));
    }

    [Fact]
    public void A_repeated_telemetry_key_is_refused()
    {
        WithRun("index,time_utc,gen_mw\n0,2024-06-01T09:00:00Z,320\n0,2024-06-01T09:30:00Z,321\n", new[] { "state-000000.json" }, dir =>
        {
            var ex = Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir));
            Assert.Contains("appears twice", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_present_but_non_numeric_cell_is_refused_and_an_empty_one_is_no_value()
    {
        WithRun("index,time_utc,gen_mw\n0,2024-06-01T09:00:00Z,abc\n", new[] { "state-000000.json" }, dir =>
        {
            var ex = Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir));
            Assert.Contains("'gen_mw' value 'abc' is not a number", ex.Message, StringComparison.Ordinal);
        });
        WithRun("index,time_utc,gen_mw\n0,2024-06-01T09:00:00Z,\n", new[] { "state-000000.json" }, dir =>
            Assert.Single(PresolvedReplay.Load(dir).Frames));
    }
}
