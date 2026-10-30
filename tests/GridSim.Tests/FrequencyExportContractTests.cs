using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The frequency-export schema contract, incl. the optional grid-forming block. Locks that the forming
/// block appears ONLY under a forming run (so Forming==None exports stay byte-identical), that the new
/// unfloored <c>effectiveFormingInertiaMwS</c> is what the crossval sidecar reads, and - the real-vs-
/// synthetic invariant - that the REAL <c>effectiveInertiaMwS</c> never absorbs virtual inertia Ev.
/// </summary>
public class FrequencyExportContractTests
{
    private static FrequencyExportMeta Meta() =>
        new("forming-test", "deadbeef", "2026-07-20T00:00:00Z", 1.0, "test-host", ProvenanceTag.Synthetic);

    private static FrequencyEvent[] Ev() => new[] { new FrequencyEvent(1.0, -1000) };

    private static JsonElement ExportOf(DynamicsConfig cfg, double systemInertiaMwS = 0.0)
    {
        FrequencyResponse resp = FrequencyDynamics.Simulate(systemInertiaMwS, 30_000, Ev(), cfg);
        string json = FrequencyExport.ToJson(systemInertiaMwS, 30_000, Ev(), cfg, resp, Meta());
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Schema_tag_is_locked()
    {
        Assert.Equal("gridsim-frequency-export/1", ExportOf(new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000 }, 120_000)
            .GetProperty("schema").GetString());
    }

    [Fact]
    public void Forming_block_is_absent_when_forming_is_off()
    {
        JsonElement root = ExportOf(new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000 }, 120_000);
        Assert.False(root.TryGetProperty("forming", out _), "the forming block must not appear under Forming==None");

        // The recurrence array is exactly the 6-line classic form (no forming lines leaked in).
        string[] rec = root.GetProperty("recurrence").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(6, rec.Length);
        Assert.DoesNotContain(rec, s => s.Contains("forming"));
    }

    [Fact]
    public void Forming_off_export_is_byte_identical_regardless_of_unused_forming_params()
    {
        // Setting the forming fields while Forming==None must not change a single byte of the export.
        FrequencyResponse resp = FrequencyDynamics.Simulate(120_000, 30_000, Ev(), new DynamicsConfig());
        string plain = FrequencyExport.ToJson(120_000, 30_000, Ev(), new DynamicsConfig(), resp, Meta());
        string withParams = FrequencyExport.ToJson(120_000, 30_000, Ev(),
            new DynamicsConfig { Forming = FormingMode.None, VirtualInertiaMwS = 77_000, FormingDampingMwPerHz = 55 }, resp, Meta());
        Assert.Equal(plain, withParams);
    }

    [Fact]
    public void Forming_block_carries_the_unfloored_denominator_and_new_recurrence()
    {
        const double sys = 0.0, dev = 0.0, ev = 30_000, dv = 400;
        JsonElement root = ExportOf(new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = ev, FormingDampingMwPerHz = dv,
            DeviceRotationalInertiaMwS = dev, DampingPctPerHz = 0, ResponsiveFraction = 0,
        }, sys);

        Assert.True(root.TryGetProperty("forming", out JsonElement forming));
        Assert.Equal("Vsm", forming.GetProperty("mode").GetString());
        Assert.Equal(ev, forming.GetProperty("virtualFormingInertiaMwS").GetDouble());
        Assert.Equal(dv, forming.GetProperty("formingDampingMwPerHz").GetDouble());
        // The UNFLOORED forming denominator the crossval sidecar reads: sys + dev + Ev, NO Math.Max(1,.).
        Assert.Equal(sys + dev + ev, forming.GetProperty("effectiveFormingInertiaMwS").GetDouble());

        string[] rec = root.GetProperty("recurrence").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(8, rec.Length);   // 6 classic + 2 forming
        Assert.Contains(rec, s => s.Contains("f0/(2*(Ereal+Ev))"));
        Assert.Contains(rec, s => s.Contains("semi-implicit"));
    }

    [Fact]
    public void Real_effective_inertia_never_absorbs_virtual_inertia()
    {
        // The REAL denominator must be a pure function of system + device rotational inertia, invariant to Ev.
        JsonElement lo = ExportOf(new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = 1_000, FormingDampingMwPerHz = 400,
        }, 150_000);
        JsonElement hi = ExportOf(new DynamicsConfig
        {
            Forming = FormingMode.Vsm, VirtualInertiaMwS = 500_000, FormingDampingMwPerHz = 400,
        }, 150_000);

        double eLo = lo.GetProperty("inertia").GetProperty("effectiveInertiaMwS").GetDouble();
        double eHi = hi.GetProperty("inertia").GetProperty("effectiveInertiaMwS").GetDouble();
        Assert.Equal(150_000, eLo);          // = system + device (device 0), NOT + Ev
        Assert.Equal(eLo, eHi);              // varying Ev by 500x leaves real E untouched

        // ...while the forming denominator DID move with Ev.
        Assert.NotEqual(
            lo.GetProperty("forming").GetProperty("effectiveFormingInertiaMwS").GetDouble(),
            hi.GetProperty("forming").GetProperty("effectiveFormingInertiaMwS").GetDouble());
    }
}
