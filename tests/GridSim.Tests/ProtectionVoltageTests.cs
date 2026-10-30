using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Phase 2 - voltage/loading-domain protections. These need the network context feed (per-bus voltage +
/// branch loading), which the pure lumped COI model does not carry: so they are proven INERT there and
/// ACTIVE only once a context supplies the quantity. Exercised at the controller level (the feed itself is
/// wired in LiveSim from the solved model).
/// </summary>
public class ProtectionVoltageTests
{
    private static AssetClass ProtClass(string name, params (string key, double val)[] p)
    {
        var pd = new Dictionary<string, double>();
        foreach (var (k, v) in p) pd[k] = v;
        return new AssetClass(name, AssetKind.Protection, "protection", pd,
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY]") });
    }

    private static AssetStepContext Ctx(double voltagePu = 1.0, double loading = 0.0, double clockS = 0.0)
        => new(LocalHz: 50, LocalRocofHzPerS: 0, LocalPhaseDeg: 0, DtSeconds: 0.02, ClockSeconds: clockS,
               LocalVoltagePu: voltagePu, LoadingFraction: loading);

    [Fact]
    public void Under_voltage_protection_trips_when_fed_a_low_voltage_and_is_inert_at_nominal()
    {
        var c = new ProtectionController(ProtClass("uv", (AssetParams.UvTripPu, 0.8), (AssetParams.UvTripDwellS, 0.0)),
            bus: 1, capacityMw: 300);

        // At nominal voltage: inert (the default LocalVoltagePu is 1.0 in the lumped model).
        AssetActuation nominal = c.Update(Ctx(voltagePu: 1.0, clockS: 0.0));
        Assert.False(nominal.TrippedNow);

        c.Reset();
        AssetActuation low = c.Update(Ctx(voltagePu: 0.7, clockS: 0.0));   // 0.7 < 0.8 => operate
        Assert.True(low.TrippedNow);
        Assert.Equal(300.0, low.TrippedInfeedMw, 6);
    }

    [Fact]
    public void Over_voltage_and_thermal_overload_trip_on_their_fed_quantities()
    {
        var ov = new ProtectionController(ProtClass("ov", (AssetParams.OvTripPu, 1.10), (AssetParams.OvTripDwellS, 0.0)), 1, 200);
        Assert.True(ov.Update(Ctx(voltagePu: 1.15)).TrippedNow);

        var th = new ProtectionController(ProtClass("thermal", (AssetParams.OverloadTripFraction, 1.0), (AssetParams.OverloadTripDwellS, 0.0)), 1, 500);
        Assert.False(th.Update(Ctx(loading: 0.5)).TrippedNow);   // 50% loaded: fine
        th.Reset();
        Assert.True(th.Update(Ctx(loading: 1.2)).TrippedNow);    // 120% loaded: trips
    }

    [Fact]
    public void Voltage_protection_is_inert_in_the_pure_lumped_model()
    {
        // With the default context (LocalVoltagePu 1.0), a UV stage set below nominal never operates -
        // codifying that voltage protections need the network feed, they are not silently mis-firing.
        // Moved 2026-10 (B4 R137, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
        // 10.1): the dwell must be authored. Before: no uvTripDwellS (read as 0 s). After: uvTripDwellS = 0.0, the same.
        var c = new ProtectionController(ProtClass("uv", (AssetParams.UvTripPu, 0.9), (AssetParams.UvTripDwellS, 0.0)), 1, 300);
        for (int i = 0; i < 100; i++)
        {
            AssetActuation a = c.Update(Ctx(clockS: i * 0.02));   // default voltage 1.0
            Assert.False(a.TrippedNow);
        }
        Assert.False(c.Tripped);
    }
}
