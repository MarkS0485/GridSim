namespace GridSim.Core.Presentation;

/// <summary>
/// Display thresholds for the MECHANICAL / machine-engineer view, shared by the WPF <c>MachineEngineerVm</c>
/// and the web <c>MechanicalService</c> so both list the same fleet of spinning machines.
/// </summary>
public static class MechanicalThresholds
{
    /// <summary>Minimum synchronous-machine output (MW) to list - real transmission units, not sub-MW embedded plant.</summary>
    public const double MinSyncMachineMw = 10.0;
}
