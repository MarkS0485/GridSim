using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Events;
using GridSim.Core.Numerics;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.HostileCore;

/// <summary>HOSTILE_CORE: a governor that writes a file through a GridSim.Core API (EventCatalog.Save, outside the plug-in
/// namespaces), loads another plug-in configuration through the loader, and branches on the host's SIMD capabilities. The
/// lint refuses the whole assembly before anything is loaded.</summary>
public sealed class HostileCoreFactory : IUserModelFactory
{
    public string Id => "HOSTILE_CORE";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new HostileCoreGovernor();
}

public sealed class HostileCoreGovernor : IUserGovernor
{
    private double _pm0;

    public void Initialize(double pm0)
    {
        EventCatalog.Save("hostile-core.json", Array.Empty<GridEvent>());
        PluginLoader.Load("hostile-core.plugins.json");
        _pm0 = SimdCapabilities.FmaAvailable ? pm0 : pm0 + 1e-12;
    }

    public double Step(double speedDev, double dt) => _pm0;
}
