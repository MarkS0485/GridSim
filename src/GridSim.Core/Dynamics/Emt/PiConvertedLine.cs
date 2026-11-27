namespace GridSim.Core.Dynamics.Emt;

/// <summary>DYN-18 (ruling 14): a travelling-wave line the EMT engine modelled as a pi branch because its travel time
/// <paramref name="TauSeconds"/> is shorter than the step <paramref name="StepSeconds"/> (a Bergeron line needs at least one
/// step of delay; stretching it to one step would model a longer line).</summary>
/// <param name="Id">The line's element id.</param>
/// <param name="From">The line's from node (an EMT node index, not a bus id).</param>
/// <param name="To">The line's to node (an EMT node index, not a bus id).</param>
/// <param name="TauSeconds">The line's travel time, s.</param>
/// <param name="StepSeconds">The run's step, s.</param>
/// <param name="Label">R7-03: the branch as the case names it (its position in the branch list, its end buses and its
/// name; <see cref="EmtNetwork.ElementLabels"/>), or null for a network not built from a case.</param>
public sealed record PiConvertedLine(int Id, int From, int To, double TauSeconds, double StepSeconds, string? Label = null)
{
    /// <summary>The line as a message names it: <see cref="Label"/> when known, else its element id and nodes.</summary>
    public string Describe() => Label ?? $"travelling-wave line {Id} (nodes {From}->{To})";
}
