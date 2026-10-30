using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GridSim.Core.Gda;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R270 / R264: <see cref="GridModel.CopyWith"/> carries every property, and the copy sites that used to drop
/// <see cref="GridModel.AssetDynamics"/> and <see cref="GridModel.Protections"/> now keep them.
/// </summary>
public class GridModelCopyTests
{
    // gb-synthetic ships both an asset-dynamics bundle and a protection bundle.
    private static GridModel Synthetic() =>
        JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-synthetic"));

    // The full list of GridModel's settable properties. If a property is added, this test fails until both
    // CopyWith and this list are updated - which is the point: a new property cannot be silently dropped.
    private static readonly string[] Known =
    {
        "AssetDynamics", "BaseMva", "Boundaries", "Branches", "Buses", "DroppedGeneratorRows", "FactsDevices", "FrequencyHz",
        "Generators", "InductionMachines", "LccLinks", "MtdcLinks", "Name", "Oltcs", "Protections", "StaticVarCompensators", "TransformerFlowControls", "VscLinks", "ZeroSequenceMutuals",   // LccLinks, MtdcLinks, VscLinks added 2026-10-25 (HVDC links); FactsDevices, StaticVarCompensators 2026-10-25 (FACTS); InductionMachines, TransformerFlowControls, ZeroSequenceMutuals 2026-10-25; CopyWith carries them
    };

    private static PropertyInfo[] Settable() => typeof(GridModel)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void CopyWith_knows_every_settable_property()
    {
        Assert.Equal(Known, Settable().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void CopyWith_with_no_arguments_carries_every_property_by_reference()
    {
        GridModel m = Synthetic();
        Assert.NotNull(m.AssetDynamics);
        Assert.NotNull(m.Protections);

        GridModel copy = m.CopyWith();
        foreach (PropertyInfo p in Settable())
        {
            object? a = p.GetValue(m), b = p.GetValue(copy);
            if (a is null || a.GetType().IsValueType || a is string) Assert.Equal(a, b);
            else Assert.Same(a, b);
        }
    }

    [Fact]
    public void CopyWith_replaces_only_what_it_is_given()
    {
        GridModel m = Synthetic();
        var branches = m.Branches.Take(3).ToList();
        GridModel copy = m.CopyWith(branches: branches, name: "renamed");
        Assert.Same(branches, copy.Branches);
        Assert.Equal("renamed", copy.Name);
        Assert.Same(m.Buses, copy.Buses);
        Assert.Same(m.AssetDynamics, copy.AssetDynamics);
        Assert.Same(m.Protections, copy.Protections);
    }

    [Fact]
    public void The_gda_per_bus_path_keeps_asset_dynamics_and_protections()
    {
        // R264: the per-bus path rebuilt the model without them, while the system-factor path kept them.
        GridModel m = Synthetic();
        var perBus = new Dictionary<int, double> { [m.Buses.First(b => b.Pd > 0).Id] = 1.1 };
        GridModel perBusCase = new GdaTimeStep(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.0, perBus).ApplyTo(m);
        GridModel systemCase = new GdaTimeStep(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.0).ApplyTo(m);

        Assert.Same(m.AssetDynamics, perBusCase.AssetDynamics);
        Assert.Same(m.Protections, perBusCase.Protections);
        Assert.Same(systemCase.AssetDynamics, perBusCase.AssetDynamics);   // both paths now agree
        Assert.Same(systemCase.Protections, perBusCase.Protections);
    }
}
