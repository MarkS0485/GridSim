using System;
using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Storage behind the inverter asset models (<see cref="IDcSource"/>): a chemistry pack bounds what a
/// frequency-response service can actually deliver. A 2C service (2 MW from 1 MWh) is held by NMC (2C-rated) but
/// current-limited on LFP (1C); energy runs out at the SoC floor; the bucket path is unchanged.
/// </summary>
public class BatteryAssetTests
{
    private static AssetClass Bess(double chemistryCode, double capacityMwh = 1.0) => new(
        "bess", AssetKind.GridFollowingInverter, "",
        new Dictionary<string, double>
        {
            [AssetParams.CapacityMwh] = capacityMwh,
            [AssetParams.SocInit] = 0.9,
            [AssetParams.HeadroomUpFraction] = 1.0,
            [AssetParams.HeadroomDownFraction] = 1.0,
            [AssetParams.DroopGainMwPerHzPerMw] = 5.0,
            [AssetParams.DeadbandHz] = 0.015,
            [AssetParams.ActuateTauS] = 0.1,
            [AssetParams.BatteryChemistry] = chemistryCode,
        },
        new Dictionary<string, AssetClassProvenance>());

    /// <summary>Run a sustained 49.5 Hz event (full droop) for <paramref name="seconds"/>; returns the output
    /// trace (MW) sampled each second.</summary>
    private static List<double> Deliver(GridFollowingInverter inv, double seconds)
    {
        var trace = new List<double>();
        const double dt = 0.1;
        for (int k = 0; k * dt < seconds; k++)
        {
            double t = k * dt;
            AssetActuation a = inv.Update(new AssetStepContext(49.5, 0.0, 0.0, dt, t));
            if (k % 10 == 0) trace.Add(a.ActivePowerMw);
        }
        return trace;
    }

    [Fact]
    public void A_two_c_service_is_held_by_nmc_but_current_limited_on_lfp()
    {
        var nmc = new GridFollowingInverter(Bess(2), bus: 1, pgMw: 0, ratedMw: 2.0);
        var lfp = new GridFollowingInverter(Bess(1), bus: 1, pgMw: 0, ratedMw: 2.0);
        Assert.IsType<BatteryDcSource>(nmc.DcSource);
        List<double> n = Deliver(nmc, 60), l = Deliver(lfp, 60);
        Assert.InRange(n[^1], 1.85, 2.0);            // 2C cell, 98 % converter
        Assert.InRange(l[^1], 0.9, 1.15);            // held at the LFP 1C BMS limit
    }

    [Fact]
    public void Delivery_stops_at_the_soc_floor_after_the_usable_energy()
    {
        var nmc = new GridFollowingInverter(Bess(2), bus: 1, pgMw: 0, ratedMw: 2.0);
        List<double> trace = Deliver(nmc, 1800);
        double deliveredMwh = 0;
        foreach (double p in trace) deliveredMwh += p / 3600.0;
        int stop = trace.FindIndex(p => p < 0.05);
        Assert.True(stop > 0, "delivery should stop inside 30 minutes at 2C");
        Assert.InRange(stop / 60.0, 20.0, 30.0);     // ~0.85 MWh window at ~2 MW, less losses
        Assert.InRange(deliveredMwh, 0.70, 0.90);
        Assert.True(nmc.SocFraction <= BatteryChemistryLibrary.Nmc.SocMin + 0.01);
    }

    [Fact]
    public void Reset_restores_the_pack()
    {
        var inv = new GridFollowingInverter(Bess(1), bus: 1, pgMw: 0, ratedMw: 1.0);
        Deliver(inv, 300);
        Assert.True(inv.SocFraction < 0.9);
        inv.Reset();
        Assert.Equal(0.9, inv.SocFraction, 12);
    }

    [Fact]
    public void Without_a_chemistry_the_soc_bucket_path_is_unchanged()
    {
        var inv = new GridFollowingInverter(Bess(0), bus: 1, pgMw: 0, ratedMw: 2.0);
        Assert.IsType<SocBucket>(inv.DcSource);
        List<double> trace = Deliver(inv, 60);
        Assert.InRange(trace[^1], 1.999, 2.001);     // bucket: no rate limit, no losses
        Assert.Equal(0.9 - 2.0 * (60.0 - 0.1) / 3600.0, inv.SocFraction, 2);
    }
}
