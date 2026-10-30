using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Replay;

namespace GridSim.Core.Scenarios;

/// <summary>One evaluated cell of the sweep: a fixed (inertia, device, corner) triple and its frequency outcome.</summary>
public sealed record ScenarioCell(
    double InertiaGvaS, string Device, Corner Corner,
    double RocofHzPerS, double NadirHz, double SettleHz,
    bool LfddAtRisk, string Assessment);

/// <summary>One inertia level's device-vs-no-device comparison at the nominal corner.</summary>
public sealed record RiskPoint(
    double InertiaGvaS,
    double RocofNoDevice, double NadirNoDevice, bool LfddNoDevice,
    double RocofWithDevice, double NadirWithDevice, bool LfddWithDevice,
    bool LfddAvoidedByDevice);

/// <summary>The inertia risk curve for one device across the inertia trajectory (nominal corner).</summary>
public sealed record InertiaRiskCurve(string DeviceName, IReadOnlyList<RiskPoint> Points);

/// <summary>The full scenario result: the passed baseline gate, every cell, and the headline risk curve.</summary>
public sealed record ScenarioMatrix(
    string EventId,
    ReplayVerdict BaselineVerdict,
    double LfddThresholdHz,
    IReadOnlyList<ScenarioCell> Cells,
    InertiaRiskCurve RiskCurve);

/// <summary>
/// Runs the counterfactual sweep - <b>Inertia x Devices x corners</b> - on a recorded event, but only after the
/// no-device baseline <see cref="ReplayValidator">reproduces the record</see>: <see cref="Run"/> throws
/// <see cref="BaselineNotReproducedException"/> otherwise. Each cell replays the event's EXOGENOUS losses (never
/// the recorded LFDD - the device is meant to make LFDD unnecessary) at a swept system inertia with the device
/// applied, bracketed by best/worst corners. Emits every cell plus an <see cref="InertiaRiskCurve"/> that pairs
/// each inertia level with/without the device so the "device lifts the nadir out of LFDD" story is one table.
/// </summary>
public static class ScenarioEngine
{
    public static ScenarioMatrix Run(ScenarioSpec spec)
    {
        // The inertia axis must be finite and above 0: 0, a negative or a NaN level passed to the simulator (which floors
        // the energy) and came back as a cell graded on whatever it produced.
        foreach (double h in spec.Axes.InertiaGvaS)
            if (!(double.IsFinite(h) && h > 0))
                throw new ArgumentOutOfRangeException(nameof(spec), h, "Every inertia level on the scenario axis must be a finite number of GVA.s above 0.");

        // ---- credibility gate: reproduce reality (with the recorded LFDD) before any counterfactual ----
        ReplayResult baseline = ReplayValidator.Validate(spec.Event, spec.BaseConfig, spec.Tolerance);
        if (baseline.Verdict != ReplayVerdict.Reproduced)
            throw new BaselineNotReproducedException(baseline);

        double demand = spec.Event.PreEventDemandMw;
        var losses = spec.Event.ExogenousLossEvents();
        double lfddHz = FrequencyLimits.LfddFirstStageHz;

        // Counterfactual cells keep the event's RECORDED response services (the real system's DC/DM/DR)
        // - the device under test is additional to them, not a replacement for them.
        // The window must cover the WHOLE staged loss sequence, exactly as the credibility gate
        // (ReplayValidator) and H0StudyEngine do: the default 30 s clips events that arrive later
        // (9 Aug 2019 loses -1,247 MW between 40-90 s), so every cell/risk point would replay a
        // milder event than the baseline the gate just validated - silently overstating security
        // and mis-stating the device benefit.
        ScenarioSpec sweep = spec with { BaseConfig = spec.Event.ApplyRecordedServices(spec.BaseConfig) };
        // The ONE post-event tail rule shared with ReplayValidator and EvidenceReport (R176, R194).
        sweep = sweep with { BaseConfig = ReplayValidator.ExtendToCover(sweep.BaseConfig, losses) };

        var cells = new List<ScenarioCell>();
        foreach (double inertiaGva in sweep.Axes.InertiaGvaS)
            foreach (DeviceConfig device in sweep.Axes.Devices)
                foreach (Corner corner in sweep.Axes.Corners)
                {
                    DynamicsConfig cfg = ScenarioCorners.Apply(device.Apply(sweep.BaseConfig), corner);
                    FrequencyResponse r = RequireFiniteNadir(
                        FrequencyDynamics.Simulate(inertiaGva * 1000.0, demand, losses, cfg), inertiaGva, device.Name);
                    FrequencyAssessment a = FrequencyLimits.Assess(r);
                    cells.Add(new ScenarioCell(
                        inertiaGva, device.Name, corner,
                        r.InitialRocofHzPerS, r.NadirHz, r.SettleHz,
                        r.NadirHz <= lfddHz, a.Summary));
                }

        InertiaRiskCurve curve = BuildRiskCurve(sweep, demand, losses, lfddHz);
        return new ScenarioMatrix(spec.Event.Id, baseline.Verdict, lfddHz, cells, curve);
    }

    private static InertiaRiskCurve BuildRiskCurve(
        ScenarioSpec spec, double demand, IReadOnlyList<FrequencyEvent> losses, double lfddHz)
    {
        // The device = the one non-"none" device on the axis (the one under test). GR-858: the curve is built for one
        // device; an axis with several used to take the first and ignore the rest without saying so, so it is refused.
        var underTest = spec.Axes.Devices.Where(d => d.RotationalInertiaMwS > 0 || d.FastResponseMw > 0).ToList();
        if (underTest.Count > 1)
            throw new ArgumentException(
                $"The inertia risk curve compares one device with none, but the axis has {underTest.Count} devices " +
                $"({string.Join(", ", underTest.Select(d => d.Name))}); run one scenario per device.", nameof(spec));
        DeviceConfig device = underTest.Count == 1 ? underTest[0] : DeviceConfig.None;

        DynamicsConfig nominalNo = ScenarioCorners.Apply(DeviceConfig.None.Apply(spec.BaseConfig), Corner.Nominal);
        DynamicsConfig nominalWith = ScenarioCorners.Apply(device.Apply(spec.BaseConfig), Corner.Nominal);

        var pts = new List<RiskPoint>();
        foreach (double inertiaGva in spec.Axes.InertiaGvaS)
        {
            double eMwS = inertiaGva * 1000.0;
            FrequencyResponse no = RequireFiniteNadir(FrequencyDynamics.Simulate(eMwS, demand, losses, nominalNo), inertiaGva, DeviceConfig.None.Name);
            FrequencyResponse with = RequireFiniteNadir(FrequencyDynamics.Simulate(eMwS, demand, losses, nominalWith), inertiaGva, device.Name);
            bool lfddNo = no.NadirHz <= lfddHz, lfddWith = with.NadirHz <= lfddHz;
            pts.Add(new RiskPoint(
                inertiaGva,
                no.InitialRocofHzPerS, no.NadirHz, lfddNo,
                with.InitialRocofHzPerS, with.NadirHz, lfddWith,
                LfddAvoidedByDevice: lfddNo && !lfddWith));
        }
        return new InertiaRiskCurve(device.Name, pts);
    }

    /// <summary>A diverged or undefined run (NaN nadir) is a failure, not a cell: <c>NaN &lt;= lfddHz</c> is false, so it
    /// used to be scored "no LFDD risk".</summary>
    private static FrequencyResponse RequireFiniteNadir(FrequencyResponse r, double inertiaGva, string device)
    {
        if (!double.IsFinite(r.NadirHz))
            throw new InvalidOperationException(
                $"Scenario run at {inertiaGva.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} GVA.s with device '{device}' has a nadir of " +
                $"{r.NadirHz.ToString(System.Globalization.CultureInfo.InvariantCulture)} Hz; the run diverged or is undefined, so it cannot be graded.");
        return r;
    }
}
