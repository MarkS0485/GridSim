using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Visuals;

namespace GridSim.Web.Sessions;

/// <summary>
/// Per-session MECHANICAL projection. Stateful because per-machine <b>ramp</b> is a frame-to-frame delta
/// (needs the previous MW), so it can't be a pure static projection. Mirrors the WPF
/// <c>MachineEngineerVm</c>: risk-sorted synchronous machines with loading / inertia / ramp, and the dual
/// RoCoF-relay margin (legacy 0.125 vs modern 1.0 Hz/s). Reactive/thermal/torsional detail the presolved
/// feed doesn't carry is left out rather than fabricated.
/// </summary>
public sealed class MechanicalService
{
    // Per-unit previous output, keyed by generator INDEX (checked against bus + name), not by name: gb-full names
    // are not unique, and name keying diffed one "- chp" unit against another every frame (R054).
    private readonly UnitRampHistory _history = new();
    private double _dtMinutes;

    public MechanicalService(double dtMinutes) => _dtMinutes = dtMinutes > 0 ? dtMinutes : 1.0;

    /// <summary>The frame-to-frame interval (minutes) the MW/min ramp divides by.</summary>
    public double StepMinutes => _dtMinutes;

    /// <summary>
    /// Follow a change of the live sim's step (the transport STEP picker, via <c>SimSession.SetStepMinutes</c>).
    /// The NEXT frame is integrated over the new step, so the next ramp (Pg now minus Pg at the previous frame)
    /// spans exactly <paramref name="minutes"/>; the history is therefore kept, not reset. Before this the divisor
    /// stayed at the construction-time step (R067): after switching 20 min -> 1 s every MW/min ramp was
    /// understated 1,200 times. A non-finite or non-positive step is a caller bug and throws.
    /// </summary>
    public void SetStepMinutes(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Step must be a finite, positive number of minutes.");
        _dtMinutes = minutes;
    }

    /// <summary>Drop the ramp history - call on a replay seek / timeline discontinuity.</summary>
    public void Reset() => _history.Reset();

    public MechanicalDto Project(SimFrame f)
    {
        var rows = new List<MachineRowDto>();
        double sumInertia = 0;

        IReadOnlyList<Generator> gens = f.Model.Generators;
        for (int gi = 0; gi < gens.Count; gi++)
        {
            Generator g = gens[gi];
            FuelInfo fi = Fuels.Classify(g.Type);
            if (!fi.Synchronous || g.Pg < MechanicalThresholds.MinSyncMachineMw) continue;

            string key = g.Name ?? $"{fi.Name}@{g.Bus}";
            double pmax = g.Pmax is > 0 and < double.PositiveInfinity ? g.Pmax : Math.Max(g.Pg, g.MBase);
            double loading = pmax > 0 ? g.Pg / pmax * 100.0 : 0;

            // P-Q capability limits for the protection-envelope diagram: real when the model carries
            // finite reactive limits; fall back to +/-Pg (a unit-PF square) when infinite, mirroring the
            // WPF MachineEngineerVm so PqDiagram never receives +/-Infinity. (Qg itself is 0 in the
            // presolved feed - reactive DISPATCH isn't carried - so the operating dot sits on Q=0.)
            double qminLim = double.IsInfinity(g.Qmin) ? -g.Pg : g.Qmin;
            double qmaxLim = double.IsInfinity(g.Qmax) ? g.Pg : g.Qmax;
            double inertia = g.H * g.MBase / 1000.0;
            sumInertia += inertia;

            // Ramp against THIS unit's own previous output (index identity), 0 when it has no history yet.
            double ramp = 0;
            if (_history.Exchange(gi, g) is double prev) ramp = (g.Pg - prev) / _dtMinutes;

            // Stress = worst of real-power loading and reactive-headroom proximity (when limits are finite).
            double stress = pmax > 0 ? g.Pg / pmax : 0;
            if (g.Qmax is > 0 and < double.PositiveInfinity && g.Qg > 0) stress = Math.Max(stress, g.Qg / g.Qmax);

            rows.Add(new MachineRowDto(
                key, fi.Name, fi.Color,
                g.Pg, pmax, loading,
                g.Qg, qminLim, qmaxLim,
                inertia, g.H,
                ramp, Math.Clamp(stress, 0, 1) * 100.0, fi.Synchronous));
        }

        rows.Sort((a, b) => b.StressPercent.CompareTo(a.StressPercent));   // risk-sorted

        double rocof = Math.Abs(f.RocofHzPerS);
        double legacy = FrequencyLimits.RocofLimitLegacyHzPerS;
        double modern = FrequencyLimits.RocofLimitModernHzPerS;

        return new MechanicalDto(
            f.RocofHzPerS,
            (legacy - rocof) / legacy * 100.0, rocof > legacy,
            (modern - rocof) / modern * 100.0, rocof > modern,
            sumInertia, rows.Count, rows);
    }
}
