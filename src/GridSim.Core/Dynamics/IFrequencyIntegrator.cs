namespace GridSim.Core.Dynamics;

/// <summary>
/// The stateful swing-integrator contract shared by <see cref="FrequencyIntegrator"/> (the untouched
/// baseline) and <see cref="AssetAwareFrequencyIntegrator"/> (with the authored fleet), so
/// <see cref="Simulation.LiveSim"/> can drive either without branching on the concrete type.
/// </summary>
public interface IFrequencyIntegrator
{
    double FrequencyHz { get; }
    double RocofHzPerS { get; }
    void Step(double dtSeconds, double eventMw);
}
