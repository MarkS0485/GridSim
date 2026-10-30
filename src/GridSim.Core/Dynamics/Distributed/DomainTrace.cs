using System.Collections.Generic;
using GridSim.Core.Frequency;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// One speed domain's frequency trajectory from a distributed solve - the same shape as the system
/// <see cref="FrequencyResponse"/>, but for a single coherent mass. Per-domain nadirs/RoCoFs diverge
/// from the COI (that is the whole point of the model); their inertia-weighted mean tracks the COI.
/// </summary>
/// <param name="InitialRocofHzPerS">This domain's PEAK RoCoF (Hz/s, sign kept) over the run: the step value with the
/// largest magnitude. N141: the name says "initial" for historical reasons (see
/// <see cref="FrequencyResponse.InitialRocofHzPerS"/>).</param>
/// <param name="SettleHz">This domain's frequency (Hz) one step after its last trace point, the swing family's settle
/// convention (see <see cref="FrequencyResponse.SettleHz"/>): the frequency at Duration is <c>Points[^1].Hz</c>.</param>
public sealed record DomainTrace(
    int DomainId,
    double InitialRocofHzPerS,
    double NadirHz,
    double NadirSeconds,
    double SettleHz,
    IReadOnlyList<FrequencyPoint> Points);
