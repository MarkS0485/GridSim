using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;

namespace GridSim.Cli;

/// <summary>
/// The <c>hosting</c> verb: hosting capacity for PV (or added load) at each candidate bus by bisection
/// (<see cref="HostingCapacity"/>), by the snapshot or the QSTS method. Program.cs parses the flags and hands over the
/// settings; this class runs the study and reports. Exit 0 when the study ran (a location limited within the range is a
/// result, not a failure); 1 a refused study; 2 usage.
/// </summary>
internal static class HostingCli
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(GridModel m, Func<IPowerFlowSolver> engines, string engineLabel, HostingStudy study, int top)
    {
        HostingResult r;
        try { r = HostingCapacity.Run(m, study, engines, new PowerFlowOptions()); }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"[hosting] {ex.Message}"); return CliExit.InputError; }

        string what = study.Resource == HostedResource.Pv ? "PV" : "added load";
        Console.WriteLine($"Hosting capacity ({what}, {study.Method.ToString().ToLowerInvariant()} method) - {m.Name} ({m.BusCount} buses), engine {engineLabel}");
        Console.WriteLine($"  {r.Locations.Count} candidate bus(es), search 0..{F(study.MaxMw)} MW to {F(study.ToleranceMw)} MW, {r.Locations.Sum(l => l.Evaluations)} size evaluations in {r.ElapsedSeconds.ToString("F2", Inv)} s ({study.Threads} thread(s))");
        if (study.Method == HostingMethod.Snapshot)
            Console.WriteLine($"  snapshots (demand x, resource x): {string.Join(", ", study.SnapshotsOrDefault.Select(s => $"({s.LoadMultiplier.ToString("0.###", Inv)}, {s.ResourceMultiplier.ToString("0.###", Inv)})"))}");
        else
            Console.WriteLine($"  time series: {study.Base.Steps} step(s) of {study.Base.StepSeconds} s, resource profile {study.ResourceProfile ?? "(constant 1)"}, allowed time outside limits {study.AllowedViolationSeconds.ToString("0.###", Inv)} s"
                + (study.TapIncreasePercent is double p ? $", tap operations up to +{p.ToString("0.###", Inv)} % over the base" : ""));
        Console.WriteLine($"  limits: {F(study.Base.Limits.VminPu)}-{F(study.Base.Limits.VmaxPu)} pu, loading {study.Base.Limits.LoadingPercent.ToString("0.#", Inv)} %"
            + (study.ReverseFlowBranch is null ? ", no reverse-power check" : $", reverse power through {study.ReverseFlowBranch} above {F(study.ReverseFlowAllowanceMw)} MW"));
        IReadOnlyList<HostingViolation> baseV = r.Locations.Count > 0 ? r.Locations[0].BaseViolations : Array.Empty<HostingViolation>();
        if (baseV.Count > 0)
            Console.WriteLine($"  the base case already breaks {baseV.Count} limit(s) (counted only where the resource makes them worse): {string.Join("; ", baseV.Take(3).Select(v => v.Detail))}{(baseV.Count > 3 ? " ..." : "")}");
        Console.WriteLine($"  {"bus",-8} {"hosting MW",12} {"fails at MW",12}  limiting constraint");
        foreach (HostingLocationResult l in r.Locations.OrderBy(l => l.HostingMw).ThenBy(l => l.Bus).Take(top))
            Console.WriteLine($"  {l.Bus,-8} {F(l.HostingMw),12} {(l.LimitedWithinRange ? F(l.FailingMw) : ">" + F(study.MaxMw)),12}  {(l.LimitedWithinRange ? $"{Name(l.Primary)}: {l.Limiting[0].Detail}" : "none within the search range")}");
        if (r.Locations.Count > top) Console.WriteLine($"  ... {r.Locations.Count - top} more (lowest first; --top N lists more)");

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("buses", m.BusCount).Set("engine", engineLabel).Set("resource", study.Resource == HostedResource.Pv ? "pv" : "load")
                .Set("method", study.Method.ToString().ToLowerInvariant()).Set("max_mw", study.MaxMw).Set("tolerance_mw", study.ToleranceMw)
                .Set("candidates", r.Locations.Count).Set("evaluations", r.Locations.Sum(l => l.Evaluations))
                .Set("min_hosting_mw", r.Locations.Count > 0 ? r.Locations.Min(l => l.HostingMw) : double.NaN)
                .Set("base_violations", baseV.Count);
            CliResultTable loc = rep.Table("locations", "bus_id", "hosting_mw", "failing_mw", "limited", "limiting", "detail", "evaluations");
            foreach (HostingLocationResult l in r.Locations)
                loc.Row(l.Bus, l.HostingMw, l.FailingMw, l.LimitedWithinRange, Name(l.Primary), l.Limiting.Count > 0 ? l.Limiting[0].Detail : "", l.Evaluations);
            CliResultTable lim = rep.Table("limiting", "bus_id", "constraint", "element", "value", "detail");
            foreach (HostingLocationResult l in r.Locations)
                foreach (HostingViolation v in l.Limiting) lim.Row(l.Bus, Name(v.Constraint), v.Element, v.Value, v.Detail);
            CliResultTable bv = rep.Table("base_violations", "constraint", "element", "value", "detail");
            foreach (HostingViolation v in baseV) bv.Row(Name(v.Constraint), v.Element, v.Value, v.Detail);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    internal static string Name(HostingConstraint c) => c switch
    {
        HostingConstraint.Overvoltage => "overvoltage",
        HostingConstraint.Undervoltage => "undervoltage",
        HostingConstraint.Thermal => "thermal",
        HostingConstraint.ReversePower => "reverse-power",
        HostingConstraint.TapOperations => "tap-operations",
        HostingConstraint.NonConvergence => "non-convergence",
        _ => "none",
    };

    private static string F(double v) => v.ToString("0.####", Inv);
}
