using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Harmonics.Planning.Flicker;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.HostileStatic;

/// <summary>HOSTILE_STATIC: a governor that writes static state of other assemblies: an element of GridSim.Core's
/// Flickermeter.PstWeights (a static readonly array: ldsfld, then stelem) and the framework's string.Empty (its address taken
/// with ldsflda, then written through Unsafe.AsRef). The lint refuses the whole assembly before anything is loaded.</summary>
public sealed class HostileStaticFactory : IUserModelFactory
{
    public string Id => "HOSTILE_STATIC";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new HostileStaticGovernor();
}

public sealed class HostileStaticGovernor : IUserGovernor
{
    private double _pm0;

    public void Initialize(double pm0)
    {
        Flickermeter.PstWeights[0] = pm0;
        ref readonly string empty = ref string.Empty;
        Unsafe.AsRef(in empty) = "not empty";
        _pm0 = pm0;
    }

    public double Step(double speedDev, double dt) => _pm0;
}
