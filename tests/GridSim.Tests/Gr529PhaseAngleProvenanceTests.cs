using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-529: phase-angle rows used to carry their frequency and instant inside Source, so every row was a noise-floor
/// class of its own and a distrust of "phaseangle" matched none of them. The rows now share the Source class and carry
/// the per-row detail in the optional Measurement.Provenance, which the measurement-set JSON and the estimate export
/// write when present and read as null when absent.
/// </summary>
public class Gr529PhaseAngleProvenanceTests
{
    private static IReadOnlyList<Measurement> AngleRows(GridModel m)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        var t0 = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var obs = new List<PhaseAngleObs>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            obs.Add(new PhaseAngleObs(truth.BusIds[i], truth.VaDeg[i], t0.AddMilliseconds(20 * i), 50.0 + 0.001 * i));
        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(
            m, obs, new PhaseAngleOptions { Mode = PhaseAngleReference.Raw });
        return rows;
    }

    [Fact]
    public void Gr529_every_phase_angle_row_shares_one_source_and_keeps_its_detail_as_provenance()
    {
        IReadOnlyList<Measurement> rows = AngleRows(IeeeCases.Case14());
        Assert.True(rows.Count > 1);
        Assert.All(rows, r => Assert.Equal("phaseangle", r.Source));
        Assert.All(rows, r => Assert.NotNull(r.Provenance));
        Assert.All(rows, r => Assert.Matches(@"^f=[0-9.]+Hz@2026-05-01T20:00:", r.Provenance!));
        Assert.Equal(rows.Count, rows.Select(r => r.Provenance).Distinct().Count());   // the detail is per row
        Assert.True(new DistrustedSources(new[] { "phaseangle" }).IsDistrusted(rows[0].Source));
    }

    [Fact]
    public void Gr529_phase_angle_residuals_form_one_noise_floor_class()
    {
        IReadOnlyList<Measurement> rows = AngleRows(IeeeCases.Case14());
        var residuals = rows.Select((r, i) => new MeasurementResidual(r, r.Value, 0.0, 0.1 * (i % 5))).ToList();
        NoiseFloorReport report = NoiseFloorAnalysis.Characterise(residuals);
        NoiseFloorClass only = Assert.Single(report.Classes);
        Assert.Equal("phaseangle", only.Source);
        Assert.Equal(rows.Count, only.Count);
    }

    [Fact]
    public void Gr529_the_measurement_set_json_round_trips_provenance_and_omits_it_when_null()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr529-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var with = new Measurement(MeasurementKind.VaDeg, 1.5, 2.0, Bus: 2, Source: "phaseangle", Provenance: "f=50Hz@2026-05-01T20:00:00.0000000Z");
            var without = new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: 2);
            var set = new MeasurementSet
            {
                Case = "case14", BaseMva = 100, TakenUtc = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc),
                Measurements = new[] { with, without },
            };
            string path = Path.Combine(dir, "set.json");
            set.Save(path);
            string text = File.ReadAllText(path);
            Assert.Equal(1, CountOf(text, "\"provenance\""));   // written for the row that has it only

            MeasurementSet back = MeasurementSet.Load(path, utcNow: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(with.Provenance, back.Measurements[0].Provenance);
            Assert.Null(back.Measurements[1].Provenance);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Gr529_the_estimate_export_carries_provenance_and_the_residual_reader_reads_it_back()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet full = WlsEstimatorTests.ExactFullSet(m, truth);
        var ms = full.Measurements.ToList();
        ms.AddRange(AngleRows(m).Select(r => r with { Sigma = 0.5 }));
        MeasurementSet set = full with { Measurements = ms };
        EstimationResult est = new WlsStateEstimator().Estimate(m, set);
        Assert.True(est.Converged);

        string dir = Path.Combine(Path.GetTempPath(), "gr529x-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "estimate-export.json");
            File.WriteAllText(path, EstimateExport.ToJson(m, set, est,
                new SolveExportMeta("case14", "cpu", "c", "2026-11-01T00:00:00Z", 1.0, "test")));
            EstimateExportResidualTable table = EstimateExportResiduals.Load(path);
            var angle = table.Residuals.Where(r => r.Measurement.Kind == MeasurementKind.VaDeg).ToList();
            Assert.NotEmpty(angle);
            Assert.All(angle, r => Assert.Equal("phaseangle", r.Measurement.Source));
            Assert.All(angle, r => Assert.StartsWith("f=", r.Measurement.Provenance, StringComparison.Ordinal));
            Assert.All(table.Residuals.Where(r => r.Measurement.Kind != MeasurementKind.VaDeg),
                r => Assert.Null(r.Measurement.Provenance));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOf(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
