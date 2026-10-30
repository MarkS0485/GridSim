using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R056 (October 2026 review): replay-event and scenario simulate with an event's recorded response services and an
/// offset pre-event start, but the frequency export carried neither, so crossval_freq.py re-integrated different
/// physics (no service tranches, nominal start) and reported a mismatch or a coincidental pass. The export now carries
/// both when present, and only then, so a service-free nominal-start export is unchanged; crossval_freq.py refuses to
/// compare (exit 77, NOT CHECKED) when they are present.
/// </summary>
public class FrequencyExportServicesTests
{
    private static FrequencyExportMeta Meta() =>
        new("services-test", "deadbeef", "2026-10-02T00:00:00Z", 1.0, "test-host", ProvenanceTag.Synthetic);

    private static FrequencyEvent[] Ev() => [new FrequencyEvent(1.0, -1000)];

    private static readonly SystemResponseService Dc = new(Mw: 500, DeadbandHz: 0.015, FullDeliveryDeviationHz: 0.5,
        TimeConstantS: 0.5, KneeDeviationHz: 0.2, KneeFraction: 0.05);

    private static JsonElement ExportOf(DynamicsConfig cfg)
    {
        FrequencyResponse resp = FrequencyDynamics.Simulate(120_000, 30_000, Ev(), cfg);
        return JsonDocument.Parse(FrequencyExport.ToJson(120_000, 30_000, Ev(), cfg, resp, Meta())).RootElement.Clone();
    }

    [Fact]
    public void Recorded_services_and_offset_start_are_exported_exactly()
    {
        JsonElement root = ExportOf(new DynamicsConfig { SystemResponseServices = [Dc], InitialFrequencyHz = 49.95 });

        JsonElement svc = Assert.Single(root.GetProperty("systemResponseServices").EnumerateArray().ToArray());
        Assert.Equal(500.0, svc.GetProperty("mw").GetDouble());
        Assert.Equal(0.015, svc.GetProperty("deadbandHz").GetDouble());
        Assert.Equal(0.5, svc.GetProperty("fullDeliveryDeviationHz").GetDouble());
        Assert.Equal(0.5, svc.GetProperty("timeConstantS").GetDouble());
        Assert.Equal(0.2, svc.GetProperty("kneeDeviationHz").GetDouble());
        Assert.Equal(0.05, svc.GetProperty("kneeFraction").GetDouble());
        Assert.Equal(49.95, root.GetProperty("config").GetProperty("initialFrequencyHz").GetDouble());
    }

    [Fact]
    public void A_service_free_nominal_start_export_has_neither_key()
    {
        // The original schema: no systemResponseServices block and exactly the seven original config keys, so every
        // export written before this change (and every service-free one after) reads the same bytes.
        JsonElement root = ExportOf(new DynamicsConfig());
        Assert.False(root.TryGetProperty("systemResponseServices", out _));
        string[] keys = root.GetProperty("config").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "nominalHz", "droopPu", "dampingPctPerHz", "responsiveFraction", "governorTimeConstantS",
            "stepSeconds", "durationSeconds" }, keys);
    }

    [Fact]
    public void Services_alone_or_offset_alone_each_appear_independently()
    {
        JsonElement onlySvc = ExportOf(new DynamicsConfig { SystemResponseServices = [Dc] });
        Assert.True(onlySvc.TryGetProperty("systemResponseServices", out _));
        Assert.False(onlySvc.GetProperty("config").TryGetProperty("initialFrequencyHz", out _));

        JsonElement onlyOffset = ExportOf(new DynamicsConfig { InitialFrequencyHz = 49.9 });
        Assert.False(onlyOffset.TryGetProperty("systemResponseServices", out _));
        Assert.Equal(49.9, onlyOffset.GetProperty("config").GetProperty("initialFrequencyHz").GetDouble());
    }

    [Fact]
    public void Crossval_freq_refuses_to_compare_unmodelled_physics()
    {
        // Source-level contract on the sidecar (the test run has no scipy): the guard runs before any integration and
        // exits with the distinct NOT CHECKED code.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string py = File.ReadAllText(Path.Combine(dir!.FullName, "tools", "crossval", "crossval_freq.py"));
        Assert.Contains("EXIT_NOT_CHECKED = 77", py);
        Assert.Contains("doc.get(\"systemResponseServices\")", py);
        Assert.Contains("get(\"initialFrequencyHz\")", py);
        int guard = py.IndexOf("reasons = unmodelled_physics(doc)", StringComparison.Ordinal);
        int integrate = py.IndexOf("peak_rocof, nadir = integrate(doc)", StringComparison.Ordinal);
        Assert.True(guard > 0 && integrate > guard, "the unmodelled-physics guard must run before the re-integration");
    }
}
