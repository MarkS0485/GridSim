using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;

namespace GridSim.Cli;

/// <summary>
/// The <c>qsts</c> verb: a quasi-static time series of a case over a study file (<c>gridsim-qsts/1</c>, see
/// <see cref="QstsFile"/>), solved by <see cref="QstsEngine"/> on the same engine the case solve picks.
/// <code>
///   gridsim qsts &lt;case&gt; --study &lt;file.json&gt; [--top 10] [--allow-unsettled] [--json|--csv|--out &lt;file&gt;]
/// </code>
/// Exit 0 when every step converged and settled; 3 when a step did not converge (no result file: there is no complete
/// result) or, without <c>--allow-unsettled</c>, when any step's controls did not settle within their caps (the result
/// file IS written, with the exit code inside); 1 when the study file is refused; 2 for a usage error.
/// </summary>
internal static class QstsCli
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(GridModel m, IPowerFlowSolver engine, string engineLabel, string studyPath, int top, bool allowUnsettled)
    {
        QstsStudy study;
        try { study = QstsFile.Load(studyPath, m); study.Validate(m); }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"[qsts] {ex.Message}"); return CliExit.InputError; }

        QstsResult r;
        try { r = new QstsEngine(engine, new PowerFlowOptions()).Run(m, study); }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"[qsts] {ex.Message}"); return CliExit.InputError; }

        Console.WriteLine($"QSTS - {m.Name} ({m.BusCount} buses, {m.Branches.Count} branches), engine {engineLabel}, mode {study.Mode.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  {study.Steps} step(s) of {study.StepSeconds} s ({Hours(study.Steps * (long)study.StepSeconds)}), {r.Steps.Count} solved in {r.ElapsedSeconds.ToString("F2", Inv)} s wall"
            + $" ({(r.ElapsedSeconds > 0 ? (r.Steps.Count / r.ElapsedSeconds).ToString("F1", Inv) : "-")} steps/s)");
        if (!r.Completed)
        {
            Console.Error.WriteLine($"qsts: {r.FailureReason}; the run stopped there (no result written).");
            return CliExit.Verdict;
        }
        Console.WriteLine($"  power-flow solves {r.Steps.Sum(s => s.Solves)}, iterations {r.Steps.Sum(s => s.Iterations)}; unsettled steps {r.UnsettledSteps}");
        QstsEnergy e = r.Energy;
        Console.WriteLine($"  energy (MWh): load {F(e.LoadMwh)}, losses {F(e.LossMwh)}, source {F(e.SourceMwh)}, PV {F(e.PvMwh)} of {F(e.PvAvailableMwh)} available (curtailed {F(e.PvCurtailedMwh)}), storage charge {F(e.StorageChargeMwh)} / discharge {F(e.StorageDischargeMwh)}");
        double vmin = r.Steps.Min(s => s.VminPu), vmax = r.Steps.Max(s => s.VmaxPu);
        QstsStepRecord lo = r.Steps.First(s => s.VminPu == vmin), hi = r.Steps.First(s => s.VmaxPu == vmax);
        Console.WriteLine($"  voltage range {vmin.ToString("F4", Inv)} pu (bus {lo.VminBus}, t = {lo.TimeSeconds} s) to {vmax.ToString("F4", Inv)} pu (bus {hi.VmaxBus}, t = {hi.TimeSeconds} s); limits {study.Limits.VminPu.ToString("F3", Inv)}-{study.Limits.VmaxPu.ToString("F3", Inv)} pu");
        Console.WriteLine($"  tap operations {r.TotalTapOperations}, capacitor operations {r.TotalCapacitorOperations}");
        for (int i = 0; i < r.OltcNames.Count; i++) Console.WriteLine($"      oltc {r.OltcNames[i],-20} {r.TapOperations[i],6}");
        for (int i = 0; i < r.CapacitorNames.Count; i++) Console.WriteLine($"      bank {r.CapacitorNames[i],-20} {r.CapacitorOperations[i],6}");
        for (int i = 0; i < r.StorageNames.Count; i++) Console.WriteLine($"      storage {r.StorageNames[i],-17} final SOC {r.FinalSoc[i].ToString("F4", Inv)}");
        Console.WriteLine($"  buses outside their voltage limits: {r.BusViolations.Count}; branches above {study.Limits.LoadingPercent.ToString("F0", Inv)} %: {r.BranchViolations.Count}");
        foreach (QstsBusViolation v in r.BusViolations.OrderByDescending(v => v.PuSecondsAbove + v.PuSecondsBelow).ThenBy(v => v.Bus).Take(top))
            Console.WriteLine($"      bus {v.Bus,-8} above {Hours((long)v.SecondsAbove),10} (max {v.MaxPu.ToString("F4", Inv)}), below {Hours((long)v.SecondsBelow),10} (min {v.MinPu.ToString("F4", Inv)}), pu.h above {(v.PuSecondsAbove / 3600).ToString("F5", Inv)} below {(v.PuSecondsBelow / 3600).ToString("F5", Inv)}");
        foreach (QstsBranchViolation v in r.BranchViolations.OrderByDescending(v => v.PercentSecondsOver).ThenBy(v => v.Branch).Take(top))
            Console.WriteLine($"      branch {v.Branch,-5} {v.Name,-20} over {Hours((long)v.SecondsOver),10} (max {v.MaxPercent.ToString("F1", Inv)} %)");
        if (r.BusViolations.Count + r.BranchViolations.Count > 0 && (r.BusViolations.Count > top || r.BranchViolations.Count > top))
            Console.WriteLine($"      ... (--top N lists more; the result file has all)");

        int exit = r.UnsettledSteps > 0 && !allowUnsettled ? CliExit.Verdict : CliExit.Ok;
        if (exit != CliExit.Ok)
            Console.Error.WriteLine($"qsts: {r.UnsettledSteps} step(s) did not settle within the control iteration caps (see the steps table); --allow-unsettled accepts them.");

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("buses", m.BusCount).Set("engine", engineLabel).Set("mode", study.Mode.ToString().ToLowerInvariant())
                .Set("step_seconds", study.StepSeconds).Set("steps", study.Steps).Set("completed", r.Completed).Set("unsettled_steps", r.UnsettledSteps)
                .Set("tap_operations", r.TotalTapOperations).Set("capacitor_operations", r.TotalCapacitorOperations)
                .Set("vmin_pu", vmin).Set("vmax_pu", vmax)
                .Set("load_mwh", e.LoadMwh).Set("loss_mwh", e.LossMwh).Set("source_mwh", e.SourceMwh).Set("pv_available_mwh", e.PvAvailableMwh)
                .Set("pv_mwh", e.PvMwh).Set("pv_curtailed_mwh", e.PvCurtailedMwh).Set("storage_charge_mwh", e.StorageChargeMwh)
                .Set("storage_discharge_mwh", e.StorageDischargeMwh).Set("buses_violating", r.BusViolations.Count).Set("branches_violating", r.BranchViolations.Count)
                .Set("digest", r.Digest());
            CliResultTable steps = rep.Table("steps", "step", "time_s", "settled", "solves", "iterations", "vmin_pu", "vmin_bus", "vmax_pu", "vmax_bus",
                "max_loading_pct", "max_loading_branch", "loss_mw", "loss_mvar", "load_mw", "source_mw", "pv_available_mw", "pv_mw", "pv_mvar", "storage_mw");
            foreach (QstsStepRecord s in r.Steps)
                steps.Row(s.Step, s.TimeSeconds, s.Settled, s.Solves, s.Iterations, s.VminPu, s.VminBus, s.VmaxPu, s.VmaxBus, s.MaxLoadingPercent,
                    s.MaxLoadingBranch, s.LossMw, s.LossMvar, s.LoadMw, s.SourceMw, s.PvAvailableMw, s.PvMw, s.PvMvar, s.StorageMw);
            CliResultTable dev = rep.Table("devices", "kind", "name", "operations");
            for (int i = 0; i < r.OltcNames.Count; i++) dev.Row("oltc", r.OltcNames[i], r.TapOperations[i]);
            for (int i = 0; i < r.CapacitorNames.Count; i++) dev.Row("capacitor", r.CapacitorNames[i], r.CapacitorOperations[i]);
            CliResultTable ops = rep.Table("operations", "step", "time_s", "kind", "device", "from", "to", "sensed", "control_iteration");
            foreach (QstsOperation o in r.Operations) ops.Row(o.Step, o.TimeSeconds, o.Kind, o.Device, o.From, o.To, o.Sensed, o.ControlIteration);
            CliResultTable bv = rep.Table("bus_violations", "bus_id", "seconds_above", "seconds_below", "pu_seconds_above", "pu_seconds_below", "max_pu", "min_pu");
            foreach (QstsBusViolation v in r.BusViolations) bv.Row(v.Bus, v.SecondsAbove, v.SecondsBelow, v.PuSecondsAbove, v.PuSecondsBelow, v.MaxPu, v.MinPu);
            CliResultTable brv = rep.Table("branch_violations", "branch", "name", "seconds_over", "percent_seconds_over", "max_percent");
            foreach (QstsBranchViolation v in r.BranchViolations) brv.Row(v.Branch, v.Name, v.SecondsOver, v.PercentSecondsOver, v.MaxPercent);
            CliResultTable taps = rep.Table("taps", "step", "transformer", "tap");
            foreach (QstsStepRecord s in r.Steps)
                for (int i = 0; i < s.Taps.Length; i++) taps.Row(s.Step, r.OltcNames[i], s.Taps[i]);
            CliResultTable banks = rep.Table("banks", "step", "bank", "steps");
            foreach (QstsStepRecord s in r.Steps)
                for (int i = 0; i < s.CapacitorSteps.Length; i++) banks.Row(s.Step, r.CapacitorNames[i], s.CapacitorSteps[i]);
            CliResultTable pv = rep.Table("pv", "step", "unit", "p_mw", "q_mvar");
            foreach (QstsStepRecord s in r.Steps)
                for (int i = 0; i < s.PvP.Length; i++) pv.Row(s.Step, r.PvNames[i], s.PvP[i], s.PvQ[i]);
            CliResultTable st = rep.Table("storage", "step", "unit", "p_mw", "soc_start");
            foreach (QstsStepRecord s in r.Steps)
                for (int i = 0; i < s.StorageP.Length; i++) st.Row(s.Step, r.StorageNames[i], s.StorageP[i], s.Soc[i]);
            if (study.RecordStates)
            {
                CliResultTable vt = rep.Table("voltages", "step", "bus_id", "vm_pu");
                foreach (QstsStepRecord s in r.Steps)
                    for (int i = 0; i < s.Vm!.Length; i++) vt.Row(s.Step, r.BusIds[i], s.Vm[i]);
                CliResultTable lt = rep.Table("loadings", "step", "branch", "loading_pct");
                foreach (QstsStepRecord s in r.Steps)
                    for (int i = 0; i < s.LoadingPercent!.Length; i++)
                        if (s.LoadingPercent[i] != 0) lt.Row(s.Step, i, s.LoadingPercent[i]);
            }
            rep.Complete();
        }
        return exit;
    }

    private static string F(double v) => v.ToString("F3", Inv);

    /// <summary>A duration as h:mm:ss.</summary>
    internal static string Hours(long seconds) =>
        string.Create(Inv, $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}");
}
