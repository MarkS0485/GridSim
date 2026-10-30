using GridSim.Core.Abstractions;
using GridSim.Core.Diagnostics;
using GridSim.Core.Gda;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Simulation;

/// <summary>One reconstructed instant of the grid: the estimated state at a historical timestamp.</summary>
public sealed record GdaReplayStep(
    DateTime TimeUtc, double SystemLoadFactor,
    bool Converged, int Iterations, double MaxMismatch,
    double TotalLossMw, double VmMinPu, double VmMaxPu);

/// <summary>The full offline state-estimation pass over a historical demand series. <see cref="Provenance"/> is
/// <see cref="ProvenanceTag.DerivedFromRecorded"/> for a plain reconstruction and
/// <see cref="ProvenanceTag.Counterfactual"/> when hypothetical storage was added - a "what if a battery had been
/// there" run on real history is a conclusion, never an input to another replay.</summary>
public sealed record GdaReplayResult(
    string SeriesName, int Steps, int NonConverged, IReadOnlyList<GdaReplayStep> StepResults,
    ProvenanceTag Provenance = ProvenanceTag.DerivedFromRecorded,
    IReadOnlyList<IReadOnlyList<Storage.SiteStepRecord>>? StorageSteps = null,
    IReadOnlyList<Storage.SiteSummary>? StorageSummaries = null);

/// <summary>
/// <b>The historical grid state estimator.</b> Replays a <see cref="GdaTimeSeries"/> through a validated
/// steady-state power flow: for each historical timestamp it derives the operating case
/// (<see cref="GdaTimeStep.ApplyTo"/> -> demand scaled, slack re-balanced), warm-starts from the previous solve,
/// solves, and records the reconstructed state (voltages, losses, convergence). Because the input series is
/// horizon-checked and the loop calls <see cref="ForwardInferenceGuard"/> again at ingestion, it can only ever
/// reconstruct the PAST - offline state estimation, never nowcasting. An <c>onSolved</c> hook lets a caller dump
/// each solve's full math via <c>SolveExport</c>. With <c>passivePlant: true</c> the model's bound passive plant
/// (delayed OLTC taps, thermal derate; see <see cref="Assets.PassivePlantFleet"/>) runs step to step (R058).
/// </summary>
public static class GdaReplayLoop
{
    public static GdaReplayResult Replay(
        GridModel baseModel, GdaTimeSeries series, SolverKind kind = SolverKind.Auto,
        bool warmStart = true, DateTime? utcNow = null,
        Action<int, GdaTimeStep, GridModel, PowerFlowResult, SolveTrace?>? onSolved = null,
        Storage.StorageFleet? storage = null, bool passivePlant = false)
    {
        storage?.Validate(baseModel);

        // Passive plant (R058, opt-in, default off like storage): the bound transformer OLTC and line/transformer
        // thermal classes. An empty fleet (nothing bound) is kept as null so the loop takes exactly the same path
        // as with the flag off - the reduction gate, bit-identical by construction.
        Assets.PassivePlantFleet? plant = null;
        if (passivePlant)
        {
            Assets.PassivePlantFleet built = Assets.PassivePlantFleet.Build(baseModel);
            plant = built.IsEmpty ? null : built;
        }
        IReadOnlyList<Assets.BranchPlantState>? plantState = null;   // updates from the previous converged solve
        var storageSteps = storage is null ? null : new List<IReadOnlyList<Storage.SiteStepRecord>>(series.Steps.Count);
        // The load-bearing invariant, re-asserted at the ingestion boundary.
        ForwardInferenceGuard.AssertHistorical(series.Steps.Select(s => s.TimeUtc), utcNow, "GDA time-series");
        // Each step lasts until the next timestamp, so the series must be strictly increasing in time. Two rows swapped
        // (a negative gap) or a repeated time (a zero gap) used to be floored to the minimum step without a word, which
        // advanced storage by milliseconds for a half-hour and ran the passive-plant clock backwards.
        for (int i = 1; i < series.Steps.Count; i++)
            if (series.Steps[i].TimeUtc <= series.Steps[i - 1].TimeUtc)
                throw new InvalidDataException(
                    $"GDA time-series '{series.Name}': step {i} ({series.Steps[i].TimeUtc:O}) is not after step {i - 1} " +
                    $"({series.Steps[i - 1].TimeUtc:O}); the timestamps must be strictly increasing.");

        (IPowerFlowSolver pf, SolverKind resolved, _) = SolverSelector.Create(kind, baseModel, sparseThreshold: LiveSolveSettings.SparseThresholdBuses);
        var solver = new SteadyStateSolver(pf);
        int oltcPasses = resolved is SolverKind.SparseNewton or SolverKind.DistributionSweep ? 0 : LiveSolveSettings.OltcPasses;
        PowerFlowOptions? baseOpt = resolved == SolverKind.SparseNewton
            ? new PowerFlowOptions { MaxIterations = LiveSolveSettings.SparseMaxIterations, EnforceQLimits = false }
            : null;

        PowerFlowResult? last = null;
        var steps = new List<GdaReplayStep>(series.Steps.Count);
        int nonConv = 0;

        for (int i = 0; i < series.Steps.Count; i++)
        {
            GdaTimeStep step = series.Steps[i];
            GridModel m = step.ApplyTo(baseModel);
            if (storage is not null)
            {
                // Step length from the series itself (the gap to the next timestamp; the last reuses the previous gap).
                double stepHours = i + 1 < series.Steps.Count ? (series.Steps[i + 1].TimeUtc - step.TimeUtc).TotalHours
                    : i > 0 ? (step.TimeUtc - series.Steps[i - 1].TimeUtc).TotalHours : LiveSolveSettings.GdaSingleStepHours;
                double hour = step.TimeUtc.Hour + step.TimeUtc.Minute / 60.0;
                storageSteps!.Add(storage.Advance(i, hour, Math.Max(stepHours, LiveSolveSettings.GdaMinStepHours), m.Buses.Sum(b => b.Pd), double.NaN));
                m = storage.Apply(m);
            }
            if (plant is not null)
            {
                // Hand the governed transformers' tap regulation to the delayed passive OLTCs, then apply the taps and
                // derated ratings from the previous converged solve BEFORE this step's solve.
                m = plant.WithoutSupersededOltcs(m);
                if (plantState is not null) m = Assets.PassivePlantFleet.ApplyTo(m, plantState);
            }
            if (warmStart && last is not null) m = GridScenario.WithSolvedVoltages(m, last);

            SolveTrace? trace = onSolved is not null ? new SolveTrace() : null;
            PowerFlowOptions? opt = trace is null
                ? baseOpt
                : (baseOpt ?? new PowerFlowOptions()) with { Trace = trace };

            PowerFlowResult r = solver.Solve(m, opt, oltcPasses);
            GridModel solved = solver.RegulatedModel ?? m;
            if (r.Converged) last = r;   // R290: only a converged state seeds the next step's warm start
            if (!r.Converged) nonConv++;

            if (plant is not null && r.Converged)
            {
                // Advance the passive plant over the interval this solved state persists: the gap to the next
                // timestamp (the last step reuses the previous gap; a one-step series uses the single-step length),
                // floored at the minimum step - the same step length the storage branch above uses. Only a
                // CONVERGED solve is an observation (R039); otherwise the previous updates are held.
                double plantHours = i + 1 < series.Steps.Count ? (series.Steps[i + 1].TimeUtc - step.TimeUtc).TotalHours
                    : i > 0 ? (step.TimeUtc - series.Steps[i - 1].TimeUtc).TotalHours : LiveSolveSettings.GdaSingleStepHours;
                double plantDtS = Math.Max(plantHours, LiveSolveSettings.GdaMinStepHours) * 3600.0;
                double clockS = (step.TimeUtc - series.Steps[0].TimeUtc).TotalSeconds;
                plantState = plant.Advance(plantDtS, clockS, r, solved);
            }

            double vmin = r.Vm.Length > 0 ? r.Vm.Min() : double.NaN;
            double vmax = r.Vm.Length > 0 ? r.Vm.Max() : double.NaN;
            steps.Add(new GdaReplayStep(
                step.TimeUtc, step.SystemLoadFactor, r.Converged, r.Iterations, r.MaxMismatch,
                r.TotalLossMw, vmin, vmax));

            onSolved?.Invoke(i, step, solved, r, trace);
        }

        return storage is null
            ? new GdaReplayResult(series.Name, series.Steps.Count, nonConv, steps)
            : new GdaReplayResult(series.Name, series.Steps.Count, nonConv, steps, ProvenanceTag.Counterfactual,
                storageSteps, storage.Summaries());
    }
}
