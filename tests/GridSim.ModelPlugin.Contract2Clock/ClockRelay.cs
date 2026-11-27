using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(2)]

namespace GridSim.ModelPlugin.Contract2Clock;

/// <summary>CLOCK_RELAY: a relay that reads the wall clock. The metadata lint refuses the whole assembly, naming
/// System.DateTime.get_Now and the method that calls it, before anything is loaded.</summary>
public sealed class ClockRelayFactory : IUserModelFactory
{
    public string Id => "CLOCK_RELAY";

    public UserModelRole Role => UserModelRole.Relay;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new ClockRelay();
}

public sealed class ClockRelay : IUserRelay
{
    public void Initialise()
    {
    }

    public RelayDecision Evaluate(in RelayInputs inputs) => DateTime.Now.Second == 0 ? RelayDecision.TripWith(1) : RelayDecision.None;
}
