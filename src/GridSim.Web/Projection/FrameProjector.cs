using GridSim.Core.Analysis;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Visuals;

namespace GridSim.Web.Projection;

/// <summary>
/// Projects Core physics types (<see cref="GridModel"/>, <see cref="SimFrame"/>) into the raw-number DTOs the
/// browser binds to. Pure and stateless - it maps, it does not simulate. Mirrors how the WPF
/// <c>FastSchematic</c>/<c>DashboardViewModel</c> read a solved frame, minus the WPF presentation types.
/// </summary>
public static class FrameProjector
{
    /// <summary>
    /// Build the one-time topology. Runs <see cref="GraphLayout.EnsureLayout"/> first so flat IEEE/MATPOWER
    /// cases (X/Y all zero - which would stack every node in the Cornwall corner) get a synthetic layout,
    /// exactly as the WPF replay path does.
    /// </summary>
    public static TopologyDto BuildTopology(string caseId, string caseLabel, GridModel model, bool replay, int frameCount)
    {
        // Geolocated iff the model already carried real coordinates (GB models ship them); a case that had no
        // layout gets synthetic coords from EnsureLayout and is NOT geolocated, so the geography overlays stay off.
        bool geolocated = !GraphLayout.HasNoLayout(model);
        GridModel laid = GraphLayout.EnsureLayout(model);

        var nodes = new List<NodeDto>(laid.Buses.Count);
        foreach (Bus b in laid.Buses)
            nodes.Add(new NodeDto(b.Id, b.X, b.Y, b.BaseKv, b.Name ?? $"Bus {b.Id}", b.Type.ToString()));

        var edges = new List<EdgeDto>(laid.Branches.Count);
        for (int i = 0; i < laid.Branches.Count; i++)
        {
            Branch br = laid.Branches[i];
            edges.Add(new EdgeDto(i, br.From, br.To, br.IsTransformer, br.Name ?? $"{br.From}-{br.To}", br.RateAMva));
        }

        var fleet = new List<FleetDto>(laid.Generators.Count);
        foreach (Generator g in laid.Generators)
        {
            FuelInfo fi = Fuels.Classify(g.Type);
            // Nameplate, for marker size and the fly-out capacity row only (see FleetDto). Power-flow-only devices
            // are flagged so the map skips them: gb-full's slack balancer alone carries a 266,425 MVA nameplate.
            double mw = g.MBase > 0 ? g.MBase : Math.Abs(g.Pg);
            fleet.Add(new FleetDto(g.Bus, mw, fi.Name, fi.Color, fi.Synchronous, g.Name ?? fi.Name, g.PowerFlowOnly));
        }

        // Boundary cuts -> the edge indices whose flows sum across them (for the map's boundary overlay).
        var edgeIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < laid.Branches.Count; i++)
            if (laid.Branches[i].Name is { Length: > 0 } bn) edgeIndexByName[bn] = i;
        var boundaries = new List<BoundaryCutDto>(laid.Boundaries.Count);
        foreach (Boundary b in laid.Boundaries)
        {
            var idxs = new List<int>();
            foreach (BoundaryBranch bb in b.Branches)
                if (edgeIndexByName.TryGetValue(bb.Branch, out int ei)) idxs.Add(ei);
            if (idxs.Count > 0) boundaries.Add(new BoundaryCutDto(b.Name, b.LimitMw, idxs));
        }

        return new TopologyDto("topology", caseId, caseLabel, laid.BaseMva, laid.BusCount,
            replay, frameCount, nodes, edges, fleet, boundaries, geolocated);
    }

    /// <summary>Project one solved instant into a browser frame envelope.</summary>
    public static FrameEnvelope Project(SimFrame f, string? label = null)
    {
        PowerFlowResult r = f.Result;

        // GR-728: every per-bus array must cover every bus. A shorter one used to be padded with 1.0 pu / 0 and graded by
        // the lamp as if it were a solved voltage; it is a malformed result and is refused.
        int nb = r.BusIds.Count;
        if (r.Vm.Length < nb || r.VaDeg.Length < nb || r.PInjMw.Length < nb || r.QInjMvar.Length < nb)
            throw new InvalidOperationException(
                $"frame {f.Tick}: the solved state has {r.Vm.Length} voltages, {r.VaDeg.Length} angles, {r.PInjMw.Length} P and "
                + $"{r.QInjMvar.Length} Q injections for {nb} buses; it cannot be shown.");
        var nodes = new List<NodeStateDto>(r.BusIds.Count);
        double vSum = 0;
        // Q10 (2026-10): the lowest and highest bus voltage, for the VOLTAGE ALERT lamp (StatusLampRules.IsVoltageAlert).
        double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            double vm = r.Vm[i];
            double va = r.VaDeg[i];
            double p = r.PInjMw[i];
            double q = r.QInjMvar[i];
            nodes.Add(new NodeStateDto(r.BusIds[i], vm, va, p, q));
            vSum += vm;
            if (vm < vMin) vMin = vm;
            if (vm > vMax) vMax = vm;
        }
        double avgV = r.BusIds.Count > 0 ? vSum / r.BusIds.Count : 1.0;
        // No buses: the same 1.0 pu placeholder the average has always used.
        if (r.BusIds.Count == 0) { vMin = 1.0; vMax = 1.0; }

        // Per-edge state, index-aligned to the topology edges (LineFlows is index-aligned with Branches).
        var edges = new List<EdgeStateDto>(r.LineFlows.Count);
        for (int i = 0; i < r.LineFlows.Count; i++)
        {
            LineFlow lf = r.LineFlows[i];
            double frac = lf.RatingMva > 0
                ? lf.LoadingPercent / 100.0
                : Math.Min(1.0, Math.Abs(lf.PFromMw) / PresentationLimits.UnratedBranchNominalMw);
            edges.Add(new EdgeStateDto(i, lf.PFromMw, lf.QFromMvar, lf.PLossMw, frac));
        }

        var boundaries = new List<BoundaryStateDto>();
        // D-DOC-087: BoundaryAnalysis refuses a non-converged state (the convergence contract), so a frame that did not
        // converge shows no boundary rows rather than flows read from a transient iterate.
        foreach (BoundaryFlow bf in r.Converged ? BoundaryAnalysis.Evaluate(f.Model, r) : Array.Empty<BoundaryFlow>())
            boundaries.Add(new BoundaryStateDto(bf.Name, bf.FlowMw, bf.LimitMw, bf.LoadingPercent, bf.Overloaded));

        var headline = new HeadlineDto(
            FrequencyHz: f.FrequencyHz,
            RocofHzPerS: f.RocofHzPerS,
            GenerationMw: f.GenerationMw,
            DemandMw: f.DemandMw,
            LossMw: r.TotalLossMw,
            InertiaGvaS: f.Model.SystemInertiaGvaS,
            AvgVoltagePu: avgV,
            Converged: r.Converged,
            MinVoltagePu: vMin,
            MaxVoltagePu: vMax);

        InverterFleetDto? inverterFleet = BuildInverterFleet(f);

        return new FrameEnvelope("state", f.Tick, f.Hour, FormatClock(f.Hour), label,
            headline, nodes, edges, boundaries, InverterFleet: inverterFleet,
            Conformance: BuildConformance(f.Model, r),
            Experimental: BuildExperimental(f.Model, r),
            // HUMAN SCALE is a cheap re-framing of the solved frame - wire it every frame. HARMONICS is a
            // heavy per-order study, so the per-frame path carries only the guarded skipped rollup (the tab
            // runs the real study on demand via HarmonicsProjection.FromCore). See HarmonicsDto.
            HumanScale: HumanScaleProjection.Project(f),
            Harmonics: HarmonicsProjection.Project(f.Model, r));
    }

    /// <summary>
    /// Project a RAW replay tick into a LEAN envelope. The raw frame is a degenerate no-solve frame (a coords-only
    /// model and an empty result), so this skips everything <see cref="Project"/> derives from a solve: no per-bus
    /// node/edge state (the map renders the static topology backdrop tinted nationally, not solved state), no
    /// boundary flows, and none of the physics tabs (CONFORMANCE / EXPERIMENTAL wave field / HUMAN SCALE / HARMONICS)
    /// - which would be meaningless on an empty result, are hidden in the replay-only UI, and would be an
    /// unnecessary per-frame cost on a 3,539-bus model. The headline is built from the RAW per-tick scalars
    /// (frequency, RoCoF, generation, demand, measured inertia). The raw-fed GEN&amp;INERTIA / OPERATOR / MARKET DTOs
    /// are attached by the caller (<see cref="Sessions.SimSession.BuildEnvelope"/>).
    /// </summary>
    public static FrameEnvelope ProjectRaw(SimFrame f, double inertiaGvaS, string? label = null)
    {
        var headline = new HeadlineDto(
            FrequencyHz: f.FrequencyHz,
            RocofHzPerS: f.RocofHzPerS,
            GenerationMw: f.GenerationMw,
            DemandMw: f.DemandMw,
            LossMw: 0.0,
            InertiaGvaS: inertiaGvaS,
            AvgVoltagePu: 1.0,
            Converged: true,
            // A raw replay tick has no solved bus voltages: the same 1.0 pu placeholder as the average.
            MinVoltagePu: 1.0,
            MaxVoltagePu: 1.0);

        return new FrameEnvelope("state", f.Tick, f.Hour, FormatClock(f.Hour), label,
            headline,
            new List<NodeStateDto>(),      // no solved per-bus state - the map shows the static backdrop
            new List<EdgeStateDto>(),
            new List<BoundaryStateDto>());
    }

    /// <summary>
    /// Whether <see cref="GridSim.Core.Physics.Experimental.ContinuumWaveModel.FromSolvedCase"/> can possibly succeed
    /// for this frame, checked BEFORE calling it (R018, October 2026). That call goes through
    /// <c>ClassicalInit.FromSolvedCase</c> with every generator treated as a machine, which throws
    /// <see cref="InvalidOperationException"/> when the solve did not converge, when there are no generators, or when
    /// any generator has no inertia constant (H not &gt; 0). The first two throw at once; the H check, however, only
    /// runs AFTER it has built a dense admittance matrix and the dense (n + g)^2 augmented admittance. On gb-full
    /// (n = 3,539, g = 5,318, 4,366 units with H = 0) that is about 1.45 GB allocated and filled per frame just to
    /// throw. This predicate reads the same three conditions off the model and result directly (no matrices), so a
    /// frame that is bound to throw skips the call and gets exactly the result the catch used to give it.
    /// <para>It is a guard, not a model: it never decides an answer the Core call would have given differently. When it
    /// says true, the Core call still runs and may still fail loud for its own reasons (caught as before).</para>
    /// </summary>
    public static bool WaveModelCanForm(GridModel model, PowerFlowResult r)
    {
        if (!r.Converged) return false;                         // ClassicalInit: "needs a converged power flow"
        if (model.Generators.Count == 0) return false;          // ClassicalInit: "No generators to initialise"
        foreach (Generator g in model.Generators)
            if (!(g.H > 0.0)) return false;                     // ClassicalInit: "needs H > 0 for every machine"
        return true;
    }

    /// <summary>Roll up the experimental models for the EXPERIMENTAL panel. Cheap models (reciprocity,
    /// the small wave eigenproblem) run every frame; the O(generators * solve) tracing/carbon are gated
    /// by size so a large network's per-frame cost stays bounded, and the wave model is only attempted when
    /// <see cref="WaveModelCanForm"/> says it can succeed (R018).</summary>
    private static ExperimentalDto BuildExperimental(GridModel model, PowerFlowResult r)
    {
        GridSim.Core.Physics.LawResult recip = GridSim.Core.Physics.Experimental.ReciprocityCheck.Evaluate(model);

        double waveHz = 0.0;
        string split = "needs per-machine inertia";
        bool waveDegraded = true;   // cleared only when the Core wave model actually formed (R099 degraded flag)
        bool waveAttempted = false;   // R018: whether the Core wave model was called below (its modes are reused)
        GridSim.Core.Physics.Experimental.ContinuumWaveModel.WaveModes? waveModes = null;
        if (WaveModelCanForm(model, r))
        {
            waveAttempted = true;
            try
            {
                GridSim.Core.Physics.Experimental.ContinuumWaveModel.WaveModes w =
                    GridSim.Core.Physics.Experimental.ContinuumWaveModel.FromSolvedCase(model, r);
                waveModes = w;
                waveHz = w.DominantInterAreaHz;
                waveDegraded = false;
                if (w.DominantModeShape.Count > 0)
                {
                    var plus = w.DominantModeShape.Where(s => s.Amplitude > PresentationLimits.ModeShapeSplitAmplitude).Select(s => $"bus {s.Machine}");
                    var minus = w.DominantModeShape.Where(s => s.Amplitude < -PresentationLimits.ModeShapeSplitAmplitude).Select(s => $"bus {s.Machine}");
                    split = $"({string.Join(", ", plus)}) vs ({string.Join(", ", minus)})";
                }
                else
                {
                    // The model formed but has no mode shape: there is no split to show, and inertia is not what
                    // is missing, so show "-" (the WPF twin's " - ") rather than the degraded text.
                    split = "-";
                }
            }
            catch (InvalidOperationException) { /* the Core model failed loud for a reason of its own: keep the degraded text */ }
        }

        var topFlows = new List<TraceFlowDto>();
        double carbon = 0.0;
        bool skipped = model.BusCount > PresentationLimits.HeavyModelBusLimit;
        bool flowGraphCyclic = false;   // only meaningful when tracing ran (R099 trace status)
        // D-DOC-087: tracing and carbon tracing refuse a non-converged state; such a frame says so instead.
        bool notConverged = !r.Converged;
        if (!skipped && !notConverged)
        {
            GridSim.Core.Physics.Experimental.PowerFlowTracing.TraceResult tr =
                GridSim.Core.Physics.Experimental.PowerFlowTracing.Trace(model, r);
            flowGraphCyclic = !tr.FlowGraphAcyclic;
            if (tr.FlowGraphAcyclic)
                topFlows = tr.Allocations.OrderByDescending(x => x.Mw).Take(6)
                    .Select(x => new TraceFlowDto(x.GenBus, x.LoadBus, x.Mw)).ToList();
            carbon = GridSim.Core.Physics.Experimental.CarbonTracing.Trace(model, r).SystemGramsPerKwh;
        }

        // Propagating-wave field for the map animation (a BFS + the small mode eigenproblem). R018 / N021 (October
        // 2026): when the wave model was called above, its modes (or its InvalidOperationException, as null) are
        // handed to the field, so the model is no longer built a second time per frame; WaveField.ComputeFromModes is
        // then bit-for-bit WaveField.Compute. When it was not called (the guard said it cannot form), Compute runs as
        // before; it now skips the wave model itself whenever Core's ContinuumWaveModel.CanForm says the call would
        // only throw, so a no-inertia case (gb-full) no longer pays the dense build there either.
        GridSim.Core.Physics.Experimental.WaveField.WaveFieldResult wf = waveAttempted
            ? GridSim.Core.Physics.Experimental.WaveField.ComputeFromModes(model, r, waveModes)
            : GridSim.Core.Physics.Experimental.WaveField.Compute(model, r);
        var waveField = new WaveFieldDto(wf.FrequencyHz, wf.SourceBus,
            wf.Nodes.Select(nw => new WaveNodeDto(nw.Bus, nw.Amplitude, nw.PhaseRad)).ToList());

        return new ExperimentalDto(recip.Status.ToString(), recip.Residual, waveHz, split, topFlows, carbon, skipped, waveField,
            WaveDegraded: waveDegraded, FlowGraphCyclic: flowGraphCyclic, TraceNotConverged: notConverged && !skipped);
    }

    /// <summary>Audit the frame's state against the physical laws for the CONFORMANCE panel - the same
    /// independent per-branch audit the CLI's physics-check verb runs, re-projected per frame.</summary>
    private static ConformanceDto BuildConformance(GridModel model, PowerFlowResult r)
    {
        GridSim.Core.Physics.PhysicsLawReport report = GridSim.Core.Physics.PhysicsLaws.Audit(model, r);
        var hard = new HashSet<int>(GridSim.Core.Physics.PhysicsCheckRegistry.HardGateLaws);
        var laws = report.Laws
            .Select(l => new LawPillDto(l.Number, l.Name, l.Status.ToString(), l.Residual, l.Unit, hard.Contains(l.Number)))
            .ToList();
        return new ConformanceDto(report.Gate(GridSim.Core.Physics.PhysicsCheckRegistry.HardGateLaws), laws);
    }

    /// <summary>Project the live authored-asset fleet rollups onto the INVERTER FLEET tab DTO (null when off).</summary>
    private static InverterFleetDto? BuildInverterFleet(SimFrame f)
    {
        if (f.AssetFleet is not { Count: > 0 } rollups) return null;
        var rows = new List<InverterFleetRowDto>(rollups.Count);
        int totalUnits = 0, totalTripped = 0;
        foreach (var r in rollups)
        {
            rows.Add(new InverterFleetRowDto(r.ClassName, r.Mode, r.Count, r.OnlineMw, r.TrippedCount, r.SocPercent));
            totalUnits += r.Count;
            totalTripped += r.TrippedCount;
        }
        return new InverterFleetDto(f.AssetFleetNetMw, f.AssetTrippedMw, rollups.Count, totalUnits, totalTripped, rows);
    }

    /// <summary>Fractional hour -> "HH:MM" on a 24-h clock. A non-finite hour reads "--:--".</summary>
    public static string FormatClock(double hour)
    {
        // GR-722: (int)Math.Floor(NaN) is unspecified in .NET (int.MinValue on x64, 0 on Arm64), and an infinite hour
        // becomes NaN through the modulo, so without this the clock text would depend on the CPU.
        if (!double.IsFinite(hour)) return "--:--";
        double h = ((hour % 24) + 24) % 24;
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { mm = 0; hh = (hh + 1) % 24; }
        return $"{hh:D2}:{mm:D2}";
    }
}
