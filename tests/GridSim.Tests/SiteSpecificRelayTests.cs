using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The opt-in site-specific relay layer: for named critical assets, the cascade-driving branch relays
/// (49/50/51/21) read a site-specific <see cref="ProtectionSettings"/> instead of the generics, while every
/// other asset keeps the generic settings. Null overrides (the default) reproduce the generic assessment
/// exactly.
/// </summary>
public class SiteSpecificRelayTests
{
    // Two lines from the slack, each loaded to ~130% of a 100 MVA rating (over the generic 100% thermal
    // pickup). "CRITICAL" gets a site-specific record; "NORMAL" does not.
    private static GridModel Model() => new()
    {
        BaseMva = 100,
        Name = "site-relay",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 128, Qd: 30, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 128, Qd: 30, BaseKv: 132),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -300, Qmax: 300) },
        Branches = new[]
        {
            new Branch(1, 2, 0.005, 0.03, 0.0, RateAMva: 100, Name: "CRITICAL"),
            new Branch(1, 3, 0.005, 0.03, 0.0, RateAMva: 100, Name: "NORMAL"),
        },
    };

    private static ProtectionReport Assess(GridModel m, ProtectionSettings? s)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return ProtectionAssessment.Assess(m, r, s);
    }

    private static bool ThermalOperatesOn(ProtectionReport report, string asset)
        => report.Operating.Any(v => v.Device == "49" && v.LocationName == asset);

    [Fact]
    public void Generic_settings_trip_both_overloaded_lines()
    {
        ProtectionReport generic = Assess(Model(), null);
        Assert.True(ThermalOperatesOn(generic, "CRITICAL"), "generic 100% pickup should trip the overloaded CRITICAL line");
        Assert.True(ThermalOperatesOn(generic, "NORMAL"));
    }

    [Fact]
    public void A_site_specific_override_suppresses_only_the_named_asset()
    {
        // The site record for CRITICAL raises its thermal pickup above the actual loading, so it no longer
        // trips; NORMAL, with no override, still trips on the generic pickup.
        var siteSettings = new ProtectionSettings
        {
            PerAsset = new Dictionary<string, ProtectionSettings>
            {
                ["CRITICAL"] = new ProtectionSettings { ThermalPickupFraction = 1.5 }, // site test record: higher pickup
            },
        };

        ProtectionReport report = Assess(Model(), siteSettings);
        Assert.False(ThermalOperatesOn(report, "CRITICAL"), "the site-specific pickup should keep CRITICAL from tripping");
        Assert.True(ThermalOperatesOn(report, "NORMAL"), "NORMAL has no override, so the generic pickup still trips it");
    }

    [Fact]
    public void A_null_override_map_is_identical_to_the_generics()
    {
        ProtectionReport generic = Assess(Model(), new ProtectionSettings());
        ProtectionReport nullMap = Assess(Model(), new ProtectionSettings { PerAsset = null });
        Assert.Equal(generic.Operating.Count, nullMap.Operating.Count);
        Assert.Equal(
            generic.Operating.Select(v => (v.Device, v.LocationName)).OrderBy(x => x),
            nullMap.Operating.Select(v => (v.Device, v.LocationName)).OrderBy(x => x));
    }
}
