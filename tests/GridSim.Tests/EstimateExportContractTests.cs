using System.Text.Json;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Locks the <c>gridsim-estimate-export/1</c> shape the Python sidecars
/// (<c>crossval_estimation.py</c>, <c>se_residual_check.py</c>) parse, so a rename here fails CI
/// without needing Python - the same pattern as <see cref="CrossvalContractTests"/>.
/// </summary>
public class EstimateExportContractTests
{
    private static JsonElement Export(bool detectBadData = true)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, set, new EstimationOptions { DetectBadData = detectBadData });
        var meta = new SolveExportMeta("case9", "test", "abc123", "2026-07-14T00:00:00Z", 0, "test-host");
        string json = EstimateExport.ToJson(model, set, est, meta);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Export_carries_the_versioned_schema_and_meta()
    {
        JsonElement doc = Export();
        Assert.Equal("gridsim-estimate-export/1", doc.GetProperty("schema").GetString());
        Assert.Equal("case9", doc.GetProperty("meta").GetProperty("Case").GetString());
    }

    [Fact]
    public void Model_block_has_everything_a_sidecar_needs_to_rebuild_the_network()
    {
        JsonElement model = Export().GetProperty("model");
        Assert.Equal(100.0, model.GetProperty("baseMva").GetDouble());
        Assert.Equal(9, model.GetProperty("busCount").GetInt32());

        JsonElement bus = model.GetProperty("buses").EnumerateArray().First();
        foreach (string field in new[] { "id", "type", "baseKv", "pd", "qd", "gs", "bs" })
            Assert.True(bus.TryGetProperty(field, out _), $"bus.{field} missing");

        JsonElement branch = model.GetProperty("branches").EnumerateArray().First();
        foreach (string field in new[] { "from", "to", "r", "x", "b", "tap", "phaseShiftDeg" })
            Assert.True(branch.TryGetProperty(field, out _), $"branch.{field} missing");

        JsonElement gen = model.GetProperty("generators").EnumerateArray().First();
        foreach (string field in new[] { "bus", "pg", "vg", "qmin", "qmax" })
            Assert.True(gen.TryGetProperty(field, out _), $"generator.{field} missing");
    }

    [Fact]
    public void Measurement_items_carry_kind_location_value_and_sigma()
    {
        JsonElement measurements = Export().GetProperty("measurements");
        Assert.Equal("gridsim-measurements/1", measurements.GetProperty("measurementSchema").GetString());
        Assert.True(measurements.GetProperty("count").GetInt32() > 0);

        var items = measurements.GetProperty("items").EnumerateArray().ToList();
        JsonElement busMeas = items.First(i => i.GetProperty("kind").GetString() == "VmPu");
        Assert.True(busMeas.GetProperty("bus").GetInt32() > 0);
        Assert.True(busMeas.GetProperty("sigma").GetDouble() > 0);
        JsonElement flowMeas = items.First(i => i.GetProperty("kind").GetString() == "PFlowMw");
        Assert.True(flowMeas.TryGetProperty("from", out _));
        Assert.True(flowMeas.TryGetProperty("to", out _));
        Assert.True(flowMeas.TryGetProperty("end", out _));
    }

    [Fact]
    public void Estimate_block_carries_state_fit_and_verdicts()
    {
        JsonElement estimate = Export().GetProperty("estimate");
        Assert.Equal("Converged", estimate.GetProperty("status").GetString());
        Assert.True(estimate.GetProperty("converged").GetBoolean());
        foreach (string field in new[] { "iterations", "maxStateUpdate", "objectiveJ", "degreesOfFreedom", "chiSquareThreshold", "chiSquarePassed" })
            Assert.True(estimate.TryGetProperty(field, out _), $"estimate.{field} missing");

        JsonElement bus = estimate.GetProperty("buses").EnumerateArray().First();
        Assert.True(bus.TryGetProperty("id", out _));
        Assert.True(bus.TryGetProperty("vm", out _));
        Assert.True(bus.TryGetProperty("vaDeg", out _));
    }

    [Fact]
    public void Residuals_observability_and_removals_are_present()
    {
        JsonElement doc = Export();

        JsonElement residual = doc.GetProperty("residuals").EnumerateArray().First();
        foreach (string field in new[] { "measurement", "predicted", "residual", "normalizedResidual", "critical" })
            Assert.True(residual.TryGetProperty(field, out _), $"residual.{field} missing");

        JsonElement obs = doc.GetProperty("observability");
        Assert.True(obs.GetProperty("observable").GetBoolean());
        Assert.Equal(obs.GetProperty("stateSize").GetInt32(), obs.GetProperty("rank").GetInt32());

        Assert.Equal(JsonValueKind.Array, doc.GetProperty("removed").ValueKind);
    }

    [Fact]
    public void Nan_normalized_residuals_serialize_without_throwing()
    {
        // --no-bad-data leaves every normalized residual NaN; the export must carry that honestly
        // (AllowNamedFloatingPointLiterals), not crash the run.
        JsonElement doc = Export(detectBadData: false);
        JsonElement rn = doc.GetProperty("residuals").EnumerateArray().First().GetProperty("normalizedResidual");
        Assert.Equal("NaN", rn.GetString());
    }
}
