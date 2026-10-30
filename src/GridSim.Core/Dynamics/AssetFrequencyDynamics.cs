using GridSim.Core.Assets;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Frequency;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics;

/// <summary>The excursion plus what the authored-asset fleet did: trips, net FFR at the nadir, per-class rollups.</summary>
/// <remarks>D-DOC-161 (October 2026): the trips are also split by who tripped - the inverters bound to generators
/// (Loss-of-Mains RoCoF / vector shift, or under-voltage ride-through) and the placed protection devices - so a report can
/// label them truthfully (the trip verb called every infeed trip "lost to LoM"). Counts are the end-of-run tripped state,
/// as <see cref="TrippedCount"/> is; the two MW shares are accumulated beside <see cref="TotalTrippedMw"/> and add to it up
/// to rounding. D-DOC-038: <see cref="NetworkStageAssetCount"/> assets carry voltage or loading stages that this lumped
/// solve (no network) does not evaluate.</remarks>
public sealed record AssetFrequencyResult(
    FrequencyResponse Response,
    int TrippedCount,
    double TotalTrippedMw,
    double NetFleetMwAtNadir,
    IReadOnlyList<AssetClassRollup> Rollups,
    int InverterTrippedCount = 0,
    double InverterTrippedMw = 0.0,
    int ProtectionTrippedCount = 0,
    double ProtectionTrippedMw = 0.0,
    int NetworkStageAssetCount = 0);

/// <summary>
/// The batch (pre-computed-trace) swing solve WITH the authored per-asset dynamics fleet - the
/// centre-of-inertia twin of <see cref="FrequencyDynamics.Simulate"/> plus the <c>pAssetFleet</c> term.
/// It is a faithful copy of <c>FrequencyDynamics.Simulate</c> with two additions appended <b>last</b>
/// in the imbalance sum: the fleet's net swing power, and the cumulative Loss-of-Mains trip deficit
/// (lost dispatched Pg). With an empty fleet both are identically <c>0.0</c>, so the trajectory is
/// bit-for-bit the baseline (<c>x + 0.0 - 0.0 == x</c>) - the reduction gate.
///
/// <para>Local frequency is sampled from the COI at the PREVIOUS step's RoCoF/phase (a one-step
/// measurement latency), which also avoids an algebraic loop with the very term it forms.</para>
/// </summary>
public static class AssetFrequencyDynamics
{
    public static AssetFrequencyResult Simulate(
        GridModel model, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
    {
        // N275 (October 2026, stream B1): a model-based entry point runs at the model's nominal (no config: the defaults at it;
        // a config at another nominal: refused). For a 50 Hz model with no config this is the default config, bit for bit.
        DynamicsConfig cfg = SwingKernel.ConfigForModel(model, config, "AssetFrequencyDynamics.Simulate");
        AssetDynamicsFleet fleet = cfg.EnableAssetDynamics && (model.AssetDynamics is not null || model.Protections is not null)
            ? AssetDynamicsFleet.Build(model, cfg.NominalHz)
            : AssetDynamicsFleet.Empty;
        fleet.Reset();

        // R080 (October 2026, stream B1): the responders and their sums are the one swing kernel's (SwingResponders), the
        // same copy FrequencyDynamics.Simulate uses, so an empty fleet still reduces to it bit for bit.
        var resp = new SwingResponders(cfg, baseDemandMw);
        double systemInertiaMwS = model.SystemInertiaGvaS * 1000.0;
        double f0 = resp.NominalHz;
        (double e, double eForming) = SwingKernel.SwingInertia(systemInertiaMwS + cfg.DeviceRotationalInertiaMwS, cfg);
        double dt = cfg.StepSeconds;
        int steps = SwingKernel.BatchStepCount(cfg);
        FrequencyEvent[] ev = SwingKernel.SortedEvents(events);

        var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<FrequencyPoint>(steps + 1);

        double f = resp.StartFrequencyHz;
        double nadir = f, nadirT = 0;
        double peakRocof = 0;
        double phaseDeg = 0.0;
        double netFleetAtNadir = 0.0;
        double finalTripDeficit = 0.0;

        for (int k = 0; k <= steps; k++)
        {
            double t = k * dt;
            double df = f - f0;
            double pEvent = SwingKernel.ActiveEventMw(ev, t, dt);

            double pAssetFleet = 0.0, assetTripDeficit = 0.0;
            if (!resp.Forming)
            {
                // Explicit (None) integrator: responder states advance at the outer step.
                resp.Advance(df, dt);

                // Authored per-asset fleet (predictor-corrector): form the imbalance without the fleet,
                // get the predicted RoCoF, sample every controller, then close the loop. Empty fleet =>
                // pAssetFleet and the deficit are identically 0.0 (reduction gate).
                double pBase = resp.ImbalanceMw(pEvent, df);
                if (!fleet.IsEmpty)
                {
                    double rocofPredict = f0 / (2.0 * e) * pBase;
                    AssetFleetStep fs = fleet.StepCoi(f, rocofPredict, phaseDeg, dt, t);
                    pAssetFleet = fs.NetActivePowerMw;
                    assetTripDeficit = fs.TrippedDeficitMw;
                    finalTripDeficit = assetTripDeficit;
                }

                double pImbalance = pBase + pAssetFleet - assetTripDeficit;
                double rocof = f0 / (2.0 * e) * pImbalance;

                if (!double.IsFinite(f) || !double.IsFinite(rocof))   // fail loud: never grade a diverged run
                    throw new InvalidOperationException(System.FormattableString.Invariant(
                        $"Asset frequency swing diverged at t = {t:R} s (f = {f:R} Hz, RoCoF = {rocof:R} Hz/s); no result."));
                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), f, rocof));
                if (f < nadir) { nadir = f; nadirT = t; netFleetAtNadir = pAssetFleet; }
                if (Math.Abs(rocof) > Math.Abs(peakRocof)) peakRocof = rocof;

                phaseDeg += 360.0 * (f - f0) * dt;   // accumulated angle vs nominal (vector-shift proxy input)
                f += rocof * dt;
            }
            else
            {
                // Forming: the (f-f0) damping is integrated implicitly (A-stable) via
                // FormingKernel.SemiImplicitStep, and the outer step is sub-divided by
                // FormingKernel.SubStepCount so the explicit high-gain responders stay stable at small
                // E+Ev - identical to FrequencyDynamics, so an empty fleet reduces bit-for-bit. The
                // fleet predictor keys off the UNFLOORED forming denominator and start-of-step states.
                double fStart = f;
                double dampTotal = resp.FormingDampingTotalMwPerHz;
                // Bug hunt 2026-10-29, finding 13 (decided, documented): under Forming with a non-empty fleet this batch
                // engine and the live AssetAwareFrequencyIntegrator are NOT the same discretisation. Here the fleet is
                // sampled once per outer step, from the responder states of the previous step, and its power is held
                // across the sub-steps; the live engine advances the responders first and samples the fleet every
                // sub-step. Both converge to the same continuous model as the step shrinks; only the non-forming branch
                // is step-for-step identical. They are kept as they are because both have pinned results; a caller that
                // needs the two to agree to rounding must run non-forming, or the same engine.

                if (!fleet.IsEmpty)
                {
                    // Start-of-step forcing: the kernel's ServicesMw is the sum of the service states as they stand
                    // (the same left-to-right sum the old pSvc.Sum() gave).
                    double pForcingPredict = resp.FormingForcingMw(pEvent);
                    double rocofPredict = f0 / (2.0 * eForming) * (pForcingPredict - dampTotal * df);
                    AssetFleetStep fs = fleet.StepCoi(f, rocofPredict, phaseDeg, dt, t);
                    pAssetFleet = fs.NetActivePowerMw;
                    assetTripDeficit = fs.TrippedDeficitMw;
                    finalTripDeficit = assetTripDeficit;
                }

                int nSub = FormingKernel.SubStepCount(dt, f0, eForming);
                double h = dt / nSub;
                double rocofReported = 0.0;
                for (int sub = 0; sub < nSub; sub++)
                {
                    double dfSub = f - f0;
                    resp.Advance(dfSub, h);
                    double pForcing = resp.FormingForcingMw(pEvent) + pAssetFleet - assetTripDeficit;
                    (double fNext, double rocofForming) = FormingKernel.SemiImplicitStep(f, f0, eForming, pForcing, dampTotal, h);
                    if (sub == 0) rocofReported = rocofForming;   // start-of-step RoCoF, matching the None convention
                    f = fNext;
                }

                if (!double.IsFinite(fStart) || !double.IsFinite(rocofReported))
                    throw new InvalidOperationException(System.FormattableString.Invariant(
                        $"Asset grid-forming swing diverged at t = {t:R} s (f = {fStart:R} Hz); no result."));
                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), fStart, rocofReported));
                if (fStart < nadir) { nadir = fStart; nadirT = t; netFleetAtNadir = pAssetFleet; }
                if (Math.Abs(rocofReported) > Math.Abs(peakRocof)) peakRocof = rocofReported;

                phaseDeg += 360.0 * (fStart - f0) * dt;
            }
        }

        var response = new FrequencyResponse(peakRocof, nadir, nadirT, f, points);
        int trippedCount = fleet.Assets.Count(a => a.Tripped);
        var rollups = fleet.Rollups();
        int protectionTripped = fleet.Assets.Count(a => a.Tripped && a.Kind == AssetKind.Protection);
        return new AssetFrequencyResult(response, trippedCount, finalTripDeficit, netFleetAtNadir, rollups,
            InverterTrippedCount: trippedCount - protectionTripped, InverterTrippedMw: fleet.InverterTrippedMw,
            ProtectionTrippedCount: protectionTripped, ProtectionTrippedMw: fleet.ProtectionTrippedMw,
            NetworkStageAssetCount: fleet.NetworkStageAssetCount);
    }
}
