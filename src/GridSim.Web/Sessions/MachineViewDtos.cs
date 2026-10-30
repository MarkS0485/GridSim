namespace GridSim.Web.Projection;

/// <summary>
/// Additive instrument-dial fields for THE MACHINE tab that are NOT carried by the shared
/// <see cref="MachineDto"/> (which lives in the shared TabDtos and must stay stable across every consumer).
/// Produced per-frame by <see cref="GridSim.Web.Sessions.MachineService"/> alongside the MachineDto and
/// delivered on the frame envelope (<c>FrameEnvelope.MachineView</c>).
/// <para>
/// Currently this is the OLTC-fleet regulation strain read by the ELECTRICAL instrument bank's fourth dial:
/// the worse of the fraction of taps at travel limit and the average bus error versus deadband. Faithful to
/// WPF <c>MachineVm.UpdateTaps</c> (which the compact <see cref="MachineDto"/> tap fields can't reconstruct,
/// since they omit the per-tap regulation error and deadband).
/// </para>
/// </summary>
public sealed record MachineViewDto(double RegStressPercent);
