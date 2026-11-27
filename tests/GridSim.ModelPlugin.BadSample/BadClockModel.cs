using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.BadSample;

/// <summary>BAD_CLOCK: a governor that reads the clock. The metadata lint refuses the whole assembly, naming
/// System.DateTime.get_UtcNow and the method that calls it, before anything is loaded.</summary>
public sealed class BadClockFactory : IUserModelFactory
{
    public string Id => "BAD_CLOCK";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new BadClockGovernor();
}

public sealed class BadClockGovernor : IUserGovernor
{
    private double _pm0;

    public void Initialize(double pm0) => _pm0 = pm0;

    public double Step(double speedDev, double dt) => _pm0 + (DateTime.UtcNow.Ticks % 2) * 1e-12;
}
