using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.LeakSample;

/// <summary>LEAK_INIT: a governor that keeps its set-point in a static field written by Initialize and read by Step, so every
/// instance runs on the set-point of the instance initialised last. It passes the lint (a plug-in may write its own statics);
/// two instances started at the same point agree, so only a replay from different points shows the leak.</summary>
public sealed class LeakInitFactory : IUserModelFactory
{
    public string Id => "LEAK_INIT";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new LeakInitGovernor();
}

public sealed class LeakInitGovernor : IUserGovernor
{
    private static double s_pm0;

    public void Initialize(double pm0) => s_pm0 = pm0;

    public double Step(double speedDev, double dt) => s_pm0 - speedDev / 0.05;
}
