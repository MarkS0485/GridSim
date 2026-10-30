using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Simulation;

namespace GridSim.Core.Scenarios;

/// <summary>One untransformed generator's inertia contribution, so a sidecar can recompute E for any scale.</summary>
public sealed record H0StudyGeneratorInertia(string Name, int Bus, string Type, double HMwsPerMva, double MBaseMva);

/// <summary>
/// One evaluated cell of the H=0 sweep: a fixed (sync-inertia scale, GFM fraction, corner) triple and
/// the outcome. The real and virtual inertia are kept in SEPARATE fields - <see cref="SystemInertiaGvaS"/>
/// is REAL (1/2)Jw^2 that enters E; <see cref="VirtualInertiaMwS"/> is the grid-forming fleet's virtual
/// control inertia Ev, which enters ONLY the forming denominator (<see cref="EffectiveFormingInertiaMwS"/>),
/// never the real budget. RoCoF/nadir fields are <c>NaN</c> (an explicit "undefined" sentinel) when the
/// cell is <see cref="FormingDependent"/> - a genuine H=0 grid with no grid-forming source forms nothing,
/// so those quantities are not physical numbers to be plotted.
/// </summary>
public sealed record H0StudyCell(
    double SyncInertiaScale,
    double GfmFraction,                // the REQUESTED grid-forming fraction (the sweep axis value)
    double RealisedGfmFraction,        // R289: the share actually tagged (whole units: can exceed the request by one unit)
    Corner Corner,
    double SystemInertiaGvaS,          // REAL (1/2)Jw^2, transformed model; EXCLUDES Ev
    double VirtualInertiaMwS,          // Ev = sum(synInertia*f0/2) over the GFM fleet; virtual control inertia
    double FormingDampingMwPerHz,      // Dv from the GFM fleet
    double EffectiveFormingInertiaMwS, // E_real + Ev (NO Math.Max floor); == E_real when Ev == 0
    double BareInertiaRocofHzPerS,     // f0*dP/(2*E_real), dP = the RoCoF-setting loss (first 5 s, R287); fleet-free, analytic; NaN when E_real == 0
    double BareFormedRocofHzPerS,      // fleet-FREE forming swing peak RoCoF (validates the forming ODE); NaN if un-formable
    double BareFormedNadirHz,          // fleet-FREE forming swing nadir; NaN if un-formable
    double RocofHzPerS,                // fleet-coupled peak RoCoF (COI basis); NaN if un-formable
    double NadirHz,                    // NaN if un-formable
    double NadirSeconds,
    double SettleHz,                   // NaN if un-formable
    int TrippedCount,
    double TotalTrippedMw,
    double NetFleetMwAtNadir,
    bool FormingDependent,             // true iff E_real == 0 AND Ev == 0 (nothing forms the frequency)
    string FormedBy,                   // "Synchronous" | "GridForming" | "None"
    bool LfddAtRisk,
    bool CascadeLossOfMains,
    string Assessment);

/// <summary>The full H=0 study result: the passed baseline gate, the untransformed inertia table, and every cell.
/// <para>R287 (B4, October 2026): the loss figures. <see cref="TotalExogenousLossMw"/> is |sum of every exogenous loss
/// step| (it was called <c>LargestLossMw</c>, which it is not); <see cref="RocofSettingLossMw"/> is |sum of the loss steps
/// in the first <see cref="Events.GridEvent.RocofSettingWindowSeconds"/>|, the dP of every cell's
/// <see cref="H0StudyCell.BareInertiaRocofHzPerS"/>; <see cref="LargestSingleLossMw"/> is the largest single step.</para>
/// </summary>
public sealed record H0StudyResult(
    string EventId,
    ReplayVerdict BaselineVerdict,
    double LfddThresholdHz,
    double NominalHz,
    double TotalExogenousLossMw,
    double RocofSettingLossMw,
    double LargestSingleLossMw,
    double DemandMw,
    DynamicsConfig BaseConfigResolved,        // corner-independent base config (recorded services applied)
    IReadOnlyList<FrequencyEvent> Losses,     // exogenous losses (the swing forcing a sidecar re-integrates)
    IReadOnlyList<double> SyncInertiaScales,
    IReadOnlyList<double> GfmFractions,
    IReadOnlyList<Corner> Corners,
    IReadOnlyList<H0StudyGeneratorInertia> InertiaTable,
    IReadOnlyList<H0StudyCell> Cells)
{
    /// <summary>True if any cell is forming-dependent (a genuine H=0 grid that only a grid-forming source could hold).</summary>
    public bool AnyFormingDependent => Cells.Any(c => c.FormingDependent);
}

/// <summary>
/// The fleet-routed twin of <see cref="ScenarioEngine"/> for the H=0 counterfactual. Where the scenario
/// sweep floors a scalar E, this routes every cell through the authored per-asset fleet
/// (<see cref="AssetFrequencyDynamics"/>) over a RECONSTRUCTED grid model, applying two model-level
/// transforms per cell: scale the real synchronous inertia (<see cref="GridInertiaTransforms.ScaleSynchronousInertia"/>)
/// and set the grid-forming fraction of the converter fleet (<see cref="GridInertiaTransforms.SetGfmFraction"/>).
///
/// <para>Wiring Workstream A's grid-forming FORMATION path is what makes "GFM holds at H=0" a real
/// physical result rather than a flag: the GFM fleet is re-interpreted as a virtual former (Ev/Dv, via
/// <see cref="FormingAggregate.FromFleet"/>) that enters the forming denominator, and is EXCLUDED from
/// the active fleet so its contribution is counted exactly once. The grid-following units stay in the
/// fleet and keep tripping into the Loss-of-Mains cascade. At scale==0 with an adequate GFM fleet the
/// forming denominator is 0(real)+Ev(virtual) &gt; 0, so a bounded RoCoF is FORMED (GFM holds); at
/// scale==0 with an all-GFL fleet nothing forms the frequency and the cell is
/// <see cref="H0StudyCell.FormingDependent"/> with NaN RoCoF (GFL cannot hold) - the real bifurcation.</para>
///
/// <para>The transforms and this engine are ADDITIVE: <see cref="ScenarioEngine"/>,
/// <see cref="FrequencyDynamics"/> and <see cref="AssetFrequencyDynamics"/> are untouched, so every
/// existing validated path stays byte-identical.</para>
/// </summary>
public static class H0StudyEngine
{
    public static H0StudyResult Run(H0StudySpec spec)
    {
        // ---- credibility gate: reproduce reality before any counterfactual (reused verbatim) ----
        ReplayResult baseline = ReplayValidator.Validate(spec.Event, spec.BaseConfig, spec.Tolerance);
        if (baseline.Verdict != ReplayVerdict.Reproduced)
            throw new BaselineNotReproducedException(baseline);

        double f0 = spec.BaseConfig.NominalHz;
        double demand = spec.Event.PreEventDemandMw;
        var losses = spec.Event.ExogenousLossEvents();
        double lfddHz = FrequencyLimits.LfddFirstStageHz;
        // D-DOC-034: since ExogenousLossEvents keeps the operator's recovery actions (gb-2023-12-22's BM ramp), the dP of the
        // study is summed over the LOSSES alone (GridEvent.LossEvents, the same time-ordered steps the old method gave), so
        // the recovery is never netted off the loss. For an event whose only actions are LFDD the sum is bit-identical.
        double sumLossMw = spec.Event.LossEvents().Sum(l => l.DeltaPMw);     // signed (negative = deficit)
        double totalExogenousLossMw = Math.Abs(sumLossMw);
        // R287 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; source: the swing equation
        // df/dt = f0 dP / (2 E) at the loss, and the Ofgem 9 August 2019 report paras 2.4.2-2.4.15 for the staged
        // sequence): the ANALYTIC initial RoCoF used the sum of EVERY staged loss, including steps tens of seconds later
        // that cannot shape the first slope (gb-2019-08-09: 2,768 MW over 90 s, giving 0.32 Hz/s at scale 1.0 against the
        // recorded 0.17 Hz/s and the cell's own simulated InitialRocof). It now uses the losses that set the initial
        // RoCoF: those within GridEvent.RocofSettingWindowSeconds (5 s), the same rule the replay's inertia cross-check
        // has always used (gb-2019-08-09: 1,521 MW, 50 x 1521 / (2 x 215000) = 0.1769 Hz/s; gb-2023-12-22: the 1,000 MW
        // IFA trip at 0.5 s, not 1,345 MW with the Cottam trip at 9.5 s). For an event whose losses all fall in the first
        // 5 s (gb-largest-loss-1320) the figure is bit-identical: the same steps are summed in the same (time) order.
        double rocofSettingSignedLossMw = spec.Event.RocofSettingSignedLossMw();
        double rocofSettingLossMw = Math.Abs(rocofSettingSignedLossMw);
        double largestSingleLossMw = spec.Event.LargestSingleLossMw();

        // Keep the event's RECORDED response services (DC/DM/DR) and cover the whole staged sequence.
        DynamicsConfig baseCfg = spec.Event.ApplyRecordedServices(spec.BaseConfig);
        baseCfg = ReplayValidator.ExtendToCover(baseCfg, losses);   // the ONE post-event tail rule (R176, R194)

        var cells = new List<H0StudyCell>();
        foreach (double gfm in spec.GfmFractions)
        {
            // Tag the CONVERTER fleet on the UNSCALED base model (converter detection keys off H==0,
            // valid only before any scaling), then aggregate the GFM fleet's virtual former (scale-
            // invariant: scaling never touches a converter's H==0).
            // R289 (B4, October 2026): keep the share the whole-unit fill REALISED beside the requested axis value.
            GridModel mTagged = GridInertiaTransforms.SetGfmFraction(spec.BaseModel, gfm, spec.GfmClass, spec.GflClass,
                out double gfmRealised);
            FormingAggregate agg = FormingAggregate.FromFleet(mTagged, f0);
            double ev = agg.VirtualInertiaMwS, dv = agg.DampingMwPerHz;

            // The ACTIVE fleet excludes the GFM units (their contribution is the forming denominator, not
            // a second fleet injection); the grid-FOLLOWING units stay and keep tripping.
            GridModel mFleetTagged = ExcludeClass(mTagged, spec.GfmClass);
            (int gflCount, double gflMw) = FleetInfeed(mFleetTagged);

            foreach (double s in spec.SyncInertiaScales)
            {
                GridModel mScaled = GridInertiaTransforms.ScaleSynchronousInertia(mTagged, s);
                GridModel mFleet = GridInertiaTransforms.ScaleSynchronousInertia(mFleetTagged, s);
                double eReal = mScaled.SystemInertiaGvaS * 1000.0;
                double sysGvaS = mScaled.SystemInertiaGvaS;
                // R287: the RoCoF-setting loss (first 5 s), not the whole staged sum.
                double bareRocof = eReal > 0.0 ? f0 * rocofSettingSignedLossMw / (2.0 * eReal) : double.NaN;
                bool formable = eReal > 0.0 || ev > 0.0;
                bool formingDependent = !formable;
                string formedBy = ev > 0.0 ? "GridForming" : eReal > 0.0 ? "Synchronous" : "None";
                double eForm = eReal + ev;

                foreach (Corner corner in spec.Corners)
                {
                    if (formingDependent)
                    {
                        // Genuine H=0 with no grid-forming source: nothing forms df/dt. Sentinel the
                        // RoCoF/nadir as NaN (not a huge finite E-floor artifact) and record the collapse:
                        // every grid-following unit is lost to the Loss-of-Mains cascade.
                        cells.Add(new H0StudyCell(
                            s, gfm, gfmRealised, corner, sysGvaS, ev, dv, eForm,
                            bareRocof, double.NaN, double.NaN,
                            double.NaN, double.NaN, double.NaN, double.NaN,
                            gflCount, gflMw, double.NaN,
                            FormingDependent: true, formedBy, LfddAtRisk: true, CascadeLossOfMains: true,
                            Assessment: "UN-FORMABLE: H=0 with no grid-forming source - frequency cannot be formed."));
                        continue;
                    }

                    DynamicsConfig cfgCorner = ScenarioCorners.Apply(baseCfg, corner);
                    DynamicsConfig cfgForming = ev > 0.0
                        ? cfgCorner with
                        {
                            Forming = FormingMode.Vsm,
                            VirtualInertiaMwS = ev,
                            FormingDampingMwPerHz = dv,
                            FormingSetpointMw = 0.0,
                        }
                        : cfgCorner;   // Ev == 0, E_real > 0: synchronous mass forms it (standard COI)

                    // Fleet-FREE forming reference (the pure new-physics trajectory a sidecar re-integrates).
                    FrequencyResponse bare = FrequencyDynamics.Simulate(eReal, demand, losses, cfgForming);
                    // Fleet-coupled: GFL injection + Loss-of-Mains trip-deficit cascade on top of the former.
                    AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(mFleet, demand, losses, cfgForming);

                    bool lfddAtRisk = r.Response.NadirHz <= lfddHz;
                    bool cascade = r.TrippedCount > 0 && lfddAtRisk;
                    string assessment = FrequencyLimits.Assess(r.Response).Summary;

                    cells.Add(new H0StudyCell(
                        s, gfm, gfmRealised, corner, sysGvaS, ev, dv, eForm,
                        bareRocof, bare.InitialRocofHzPerS, bare.NadirHz,
                        r.Response.InitialRocofHzPerS, r.Response.NadirHz, r.Response.NadirSeconds, r.Response.SettleHz,
                        r.TrippedCount, r.TotalTrippedMw, r.NetFleetMwAtNadir,
                        FormingDependent: false, formedBy, lfddAtRisk, cascade, assessment));
                }
            }
        }

        var inertiaTable = spec.BaseModel.Generators
            .Select(g => new H0StudyGeneratorInertia(g.Name ?? "", g.Bus, g.Type, g.H, g.MBase))
            .ToList();

        return new H0StudyResult(
            spec.Event.Id, baseline.Verdict, lfddHz, f0, totalExogenousLossMw, rocofSettingLossMw, largestSingleLossMw,
            demand, baseCfg, losses,
            spec.SyncInertiaScales, spec.GfmFractions, spec.Corners, inertiaTable, cells);
    }

    /// <summary>Copy of <paramref name="m"/> with every generator inline-tagged <paramref name="className"/> removed from the fleet.</summary>
    private static GridModel ExcludeClass(GridModel m, string className)
    {
        var gens = m.Generators
            .Where(g => !string.Equals(g.DynamicsClass, className, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return GridInertiaTransforms.With(m, gens);
    }

    /// <summary>The count and total dispatched MW of the grid-following converter fleet (the infeed a cascade loses).</summary>
    private static (int Count, double Mw) FleetInfeed(GridModel m)
    {
        if (m.AssetDynamics is not { } spec) return (0, 0.0);
        int count = 0; double mw = 0.0;
        foreach (Generator g in m.Generators)
        {
            string? cn = spec.Bindings.Resolve(g.DynamicsClass, g.Type);
            if (cn is null) continue;
            if (spec.Library.Get(cn).Kind != AssetKind.GridFollowingInverter) continue;
            count++;
            mw += Math.Abs(g.Pg);
        }
        return (count, mw);
    }
}
