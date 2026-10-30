using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Abstractions;

/// <summary>
/// Solves the steady-state power flow: given generation and demand, find every
/// bus voltage and angle that satisfies Kirchhoff's laws across the network.
/// </summary>
public interface IPowerFlowSolver
{
    PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null);
}
