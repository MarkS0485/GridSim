using GridSim.Core.Analysis;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>A study of a workbench project: an id, a kind and its parameters (text, so the definition is plain data).</summary>
public sealed record WorkbenchStudyDefinition(string Id, string Kind, IReadOnlyDictionary<string, string> Parameters)
{
    public static WorkbenchStudyDefinition Of(string id, string kind, params (string Key, string Value)[] parameters) =>
        new(id, kind, parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    public string Param(string key, string fallback) => Parameters.TryGetValue(key, out string? v) ? v : fallback;
}

/// <summary>A result table: columns and rows of cells (a missing value is a gap cell, never zero).</summary>
public sealed record WorkbenchTable(string Id, string Title, IReadOnlyList<ModelColumn> Columns, IReadOnlyList<IReadOnlyList<ModelCell>> Rows);

/// <summary>A result plot: its geometry and the data series it was built from (the report binds to the data).</summary>
public sealed record WorkbenchPlot(string Id, PlotSpec Spec, string XLabel, string YLabel, IReadOnlyList<PlotSeriesData> Series);

/// <summary>The outcome of running one study. <see cref="Status"/> and <see cref="Outcome"/> are the engine's own words (or
/// FAILED / INVALID STUDY with the reason); nothing is turned into a pass.</summary>
public sealed record WorkbenchResult(string StudyId, string Kind, string Status, string Outcome, string? Reason,
    IReadOnlyList<WorkbenchTable> Tables, IReadOnlyList<WorkbenchPlot> Plots)
{
    public WorkbenchTable? Table(string id) => Tables.FirstOrDefault(t => t.Id == id);
}

/// <summary>
/// Runs the workbench's studies by calling the existing Core entry points; there is no study logic here, only the request
/// parsing and the shaping of each engine's answer into tables and plots:
/// <list type="bullet">
/// <item><c>power-flow</c>: the Newton solve with the contingency-study options.</item>
/// <item><c>transmission-security</c>: the R4-01 study-case mode registry (the P1 preventive N-1 runner).</item>
/// <item><c>group-summary</c>: the R4-01 group-summary mode (needs the project's groups).</item>
/// <item><c>qv-curve</c>: the R4-03 QV curve.</item>
/// </list>
/// </summary>
public static class WorkbenchStudies
{
    public const string PowerFlow = "power-flow";
    public const string TransmissionSecurity = "transmission-security";
    public const string GroupSummary = "group-summary";
    public const string QvCurveKind = "qv-curve";

    public static IReadOnlyList<string> Kinds { get; } = [PowerFlow, TransmissionSecurity, GroupSummary, QvCurveKind];

    public const string InvalidStudy = "INVALID STUDY";
    public const string Failed = "FAILED";

    /// <summary>Run one study. A request that cannot be honoured is INVALID STUDY with its reason; an engine refusal is FAILED.</summary>
    public static WorkbenchResult Run(GridModel model, BoundGroups? groups, WorkbenchStudyDefinition def)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(def);
        try
        {
            return def.Kind switch
            {
                PowerFlow => RunPowerFlow(model, def),
                TransmissionSecurity => RunSecurity(model, def),
                GroupSummary => RunGroupSummary(model, groups, def),
                QvCurveKind => RunQv(model, def),
                _ => Refused(def, $"Unknown study kind '{def.Kind}'. Known kinds: {string.Join(", ", Kinds)}."),
            };
        }
        catch (FormatException ex) { return Refused(def, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException or StudyCaseException)
        {
            return new WorkbenchResult(def.Id, def.Kind, Failed, "EngineError", ex.Message, [], []);
        }
    }

    private static WorkbenchResult Refused(WorkbenchStudyDefinition def, string reason) =>
        new(def.Id, def.Kind, InvalidStudy, "Invalid", reason, [], []);

    // ---------------------------------------------------------------- column helpers

    private static ModelColumn Int(string key, string title) => new(key, title, "", ModelColumnType.Integer);
    private static ModelColumn Txt(string key, string title) => new(key, title, "", ModelColumnType.Text);
    private static ModelColumn Num(string key, string title, string unit, int decimals = 3) => new(key, title, unit, ModelColumnType.Number, false, decimals);
    private static ModelColumn Flg(string key, string title) => new(key, title, "", ModelColumnType.Flag);

    private static IReadOnlyList<ModelCell> Row(params ModelCell[] cells) => cells;
    private static ModelCell N(double? v, int decimals = 3) => v is double d ? ModelCell.Num(d, decimals) : ModelCell.Gap;
    private static ModelCell I(int? v) => v is int i ? ModelCell.Int(i) : ModelCell.Gap;

    private static double ParseNum(WorkbenchStudyDefinition def, string key, double fallback)
    {
        if (!def.Parameters.TryGetValue(key, out string? t)) return fallback;
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new FormatException($"Parameter '{key}' is not a number: '{t}'.");
        return v;
    }

    // ---------------------------------------------------------------- power flow

    private static WorkbenchResult RunPowerFlow(GridModel model, WorkbenchStudyDefinition def)
    {
        PowerFlowResult r = SolverSelector.CreateNewton(model).Solve(model, ContingencyAnalysis.StudyOptions);
        var summary = new WorkbenchTable("summary", "Solution summary",
            [Txt("item", "Item"), Num("value", "Value", "", 6)],
            [
                Row(ModelCell.Txt("status"), ModelCell.Txt(r.Status.ToString())),
                Row(ModelCell.Txt("iterations"), ModelCell.Int(r.Iterations)),
                Row(ModelCell.Txt("max mismatch (pu)"), N(r.MaxMismatch, 9)),
                Row(ModelCell.Txt("total loss (MW)"), r.Converged ? N(r.TotalLossMw, 4) : ModelCell.Gap),
                Row(ModelCell.Txt("total loss (MVAr)"), r.Converged ? N(r.TotalLossMvar, 4) : ModelCell.Gap),
            ]);
        if (!r.Converged)
            return new WorkbenchResult(def.Id, def.Kind, "NOT CONVERGED", r.Status.ToString(),
                "The power flow did not converge; no bus voltages or flows are given.", [summary], []);

        var buses = new List<IReadOnlyList<ModelCell>>();
        for (int i = 0; i < r.BusIds.Count; i++)
            buses.Add(Row(ModelCell.Int(r.BusIds[i]), ModelCell.Num(r.Vm[i], 5), ModelCell.Num(r.VaDeg[i], 4), ModelCell.Num(r.PInjMw[i], 4), ModelCell.Num(r.QInjMvar[i], 4)));
        var busTable = new WorkbenchTable("buses", "Bus results",
            [Int("bus", "Bus"), Num("vm", "Voltage", "pu", 5), Num("vaDeg", "Angle", "deg", 4), Num("pInjMw", "Net injection P", "MW", 4), Num("qInjMvar", "Net injection Q", "MVAr", 4)], buses);

        var flows = new List<IReadOnlyList<ModelCell>>();
        for (int i = 0; i < r.LineFlows.Count; i++)
        {
            LineFlow f = r.LineFlows[i];
            flows.Add(Row(ModelCell.Int(i), ModelCell.Int(f.From), ModelCell.Int(f.To), ModelCell.Num(f.PFromMw, 4), ModelCell.Num(f.QFromMvar, 4),
                ModelCell.Num(f.PToMw, 4), ModelCell.Num(f.QToMvar, 4), ModelCell.Num(f.PLossMw, 4),
                f.RatingMva > 0 ? ModelCell.Num(f.LoadingPercent, 2) : ModelCell.Gap));
        }
        var flowTable = new WorkbenchTable("branches", "Branch flows",
            [Int("branch", "Branch"), Int("from", "From"), Int("to", "To"), Num("pFromMw", "P from", "MW", 4), Num("qFromMvar", "Q from", "MVAr", 4),
             Num("pToMw", "P to", "MW", 4), Num("qToMvar", "Q to", "MVAr", 4), Num("pLossMw", "Loss", "MW", 4), Num("loadingPercent", "Loading", "%", 2)], flows);

        var xs = Enumerable.Range(0, r.BusIds.Count).Select(i => (double)i).ToList();
        var series = new[] { new PlotSeriesData("voltage", xs, r.Vm.ToList()) };
        PlotSpec spec = PlotBuilders.TimeSeries("voltage-profile", "Voltage profile", "Bus position", "Voltage (pu)", series);
        return new WorkbenchResult(def.Id, def.Kind, "CONVERGED", r.Status.ToString(), null, [summary, busTable, flowTable],
            [new WorkbenchPlot("voltage-profile", spec, "Bus position", "Voltage (pu)", series)]);
    }

    // ---------------------------------------------------------------- transmission security (R4-01 registry)

    private static WorkbenchResult RunSecurity(GridModel model, WorkbenchStudyDefinition def)
    {
        string outagesText = def.Param("outages", "all");
        var outages = new List<BranchOutage>();
        if (outagesText == "all")
        {
            for (int i = 0; i < model.Branches.Count; i++)
                if (model.Branches[i].InService) outages.Add(new BranchOutage(i, model.Branches[i].From, model.Branches[i].To));
        }
        else
        {
            foreach (string part in outagesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int idx) || idx < 0 || idx >= model.Branches.Count)
                    throw new FormatException($"Outage '{part}' is not a branch index of this model (0 to {model.Branches.Count - 1}).");
                outages.Add(new BranchOutage(idx, model.Branches[idx].From, model.Branches[idx].To));
            }
        }
        if (!Enum.TryParse(def.Param("rating", "RateA"), true, out PostFaultRating rating))
            throw new FormatException("Parameter 'rating' is not RateA, RateB or RateC.");
        var study = new TransmissionSecurityPlanningStudy(def.Id, outages, ParseNum(def, "minPu", 0.95), ParseNum(def, "maxPu", 1.05),
            rating, 50, null);
        PlanningJobResult job = StudyCaseModes.Available[TransmissionSecurityPlanningStudy.ModeName].Run(def.Id, model, study, null);

        var tables = new List<WorkbenchTable>();
        if (job.Evidence is TransmissionSecurityPlanningEvidence ev)
        {
            tables.Add(new WorkbenchTable("requested", "Requested outages",
                [Int("branch", "Branch"), Int("from", "From"), Int("to", "To"), Txt("applicability", "Applicability"), Txt("reason", "Reason")],
                ev.RequestedOutages.Select(o => Row(ModelCell.Int(o.Index), ModelCell.Int(o.From), ModelCell.Int(o.To), ModelCell.Txt(o.Applicability), ModelCell.Txt(o.Reason))).ToList()));
            if (ev.Result is TransmissionSecurityResult res)
            {
                var states = new List<SecurityState> { res.BaseCase };
                states.AddRange(res.Outages);
                tables.Add(new WorkbenchTable("states", "Security states",
                    [Int("outage", "Outage branch"), Txt("disposition", "Disposition"), Txt("solver", "Solver"), Num("lossMw", "Loss", "MW", 4),
                     Int("limits", "Limits checked"), Int("violations", "Violations"), Txt("reason", "Reason")],
                    states.Select(s => Row(I(s.OutageIndex), ModelCell.Txt(s.Disposition), ModelCell.Txt(s.SolverStatus?.ToString()), N(s.LossMw, 4),
                        ModelCell.Int(s.Limits.Count), ModelCell.Int(s.Limits.Count(l => l.Violated)), ModelCell.Txt(s.Reason))).ToList()));
                var viol = new List<IReadOnlyList<ModelCell>>();
                foreach (SecurityState s in states)
                    foreach (LimitEvidence l in s.Limits.Where(l => l.Violated))
                        viol.Add(Row(I(s.OutageIndex), ModelCell.Txt(l.Kind), ModelCell.Int(l.Index), ModelCell.Txt(l.Units), N(l.Observed, 4), N(l.Lower, 4), N(l.Upper, 4)));
                tables.Add(new WorkbenchTable("violations", "Limit violations",
                    [Int("outage", "Outage branch"), Txt("kind", "Quantity"), Int("index", "Element"), Txt("units", "Units"),
                     Num("observed", "Observed", "", 4), Num("lower", "Lower limit", "", 4), Num("upper", "Upper limit", "", 4)], viol));
            }
        }
        return new WorkbenchResult(def.Id, def.Kind, job.Status, job.Outcome, job.Reason, tables, []);
    }

    // ---------------------------------------------------------------- group summary (R4-01 registry)

    private static WorkbenchResult RunGroupSummary(GridModel model, BoundGroups? groups, WorkbenchStudyDefinition def)
    {
        if (groups is null) return Refused(def, "group-summary needs a groups document (gridsim-groups/1) in the project.");
        bool solve = !string.Equals(def.Param("solve", "true"), "false", StringComparison.OrdinalIgnoreCase);
        var study = new GroupSummaryStudy(def.Id, null, solve);
        PlanningJobResult job = StudyCaseModes.Available[GroupSummaryStudy.ModeName].Run(def.Id, model, study, groups);
        var tables = new List<WorkbenchTable>();
        if (job.Evidence is GroupSummaryEvidence ev)
        {
            tables.Add(new WorkbenchTable("groups", "Group summary",
                [Txt("group", "Group"), Txt("kind", "Kind"), Int("buses", "Buses"), Int("generators", "Units"), Int("branches", "Branches"),
                 Num("loadMw", "Load P", "MW"), Num("loadMvar", "Load Q", "MVAr"), Num("scheduledGenerationMw", "Scheduled generation", "MW"),
                 Num("scheduledBalanceMw", "Scheduled balance", "MW"), Num("netInjectionMw", "Solved net injection", "MW"),
                 Num("tieFlowMw", "Tie flow", "MW"), Num("internalLossMw", "Internal loss", "MW", 4), Num("shuntLossMw", "Shunt loss", "MW", 4)],
                ev.Groups.Select(g => Row(ModelCell.Txt(g.Group), ModelCell.Txt(g.Kind), ModelCell.Int(g.Buses), ModelCell.Int(g.Generators), ModelCell.Int(g.Branches),
                    ModelCell.Num(g.LoadMw, 3), ModelCell.Num(g.LoadMvar, 3), ModelCell.Num(g.ScheduledGenerationMw, 3), ModelCell.Num(g.ScheduledBalanceMw, 3),
                    N(g.SolvedNetInjectionMw), N(g.TieFlowMw), N(g.InternalLossMw, 4), N(g.ShuntLossMw, 4))).ToList()));
        }
        return new WorkbenchResult(def.Id, def.Kind, job.Status, job.Outcome, job.Reason, tables, []);
    }

    // ---------------------------------------------------------------- QV curve (R4-03)

    private static WorkbenchResult RunQv(GridModel model, WorkbenchStudyDefinition def)
    {
        int busId;
        if (def.Parameters.TryGetValue("bus", out string? bt))
        {
            if (!int.TryParse(bt, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out busId))
                throw new FormatException($"Parameter 'bus' is not a bus id: '{bt}'.");
            if (!model.Buses.Any(b => b.Id == busId)) throw new FormatException($"Bus {busId} is not in the model.");
        }
        else
        {
            Bus? first = model.Buses.FirstOrDefault(b => b.Type == BusType.Pq);
            if (first is null) return Refused(def, "The model has no load (PQ) bus to take a QV curve at; give the 'bus' parameter.");
            busId = first.Id;
        }
        QvCurveResult c = QvCurve.Compute(model, busId, ParseNum(def, "vMin", 0.4), ParseNum(def, "vMax", 1.2), ParseNum(def, "step", 0.01));
        var points = new WorkbenchTable("points", "QV points",
            [Num("vPu", "Voltage", "pu", 4), Num("qMvar", "Reactive power needed", "MVAr", 4), Flg("converged", "Converged")],
            c.Points.Select(p => Row(ModelCell.Num(p.VPu, 4), p.Converged ? ModelCell.Num(p.QMvar, 4) : ModelCell.Gap, ModelCell.Flag(p.Converged))).ToList());
        var summary = new WorkbenchTable("summary", "QV summary",
            [Txt("item", "Item"), Num("value", "Value", "", 4)],
            [
                Row(ModelCell.Txt("bus"), ModelCell.Int(c.BusId)),
                Row(ModelCell.Txt("base voltage (pu)"), N(c.BaseVPu, 4)),
                Row(ModelCell.Txt("nose voltage (pu)"), c.MinBracketed ? N(c.NoseVPu, 4) : ModelCell.Gap),
                Row(ModelCell.Txt("nose Q (MVAr)"), c.MinBracketed ? N(c.NoseQMvar, 4) : ModelCell.Gap),
                Row(ModelCell.Txt("margin (MVAr)"), c.MinBracketed ? N(c.MarginMvar, 4) : ModelCell.Gap),
            ]);
        PlotSpec spec = PlotBuilders.QvCurvePlot("qv-curve", "QV curve, bus " + WorkbenchText.Canon(c.BusId), c);
        var series = new[]
        {
            new PlotSeriesData("QV", c.Points.Select(p => p.VPu).ToList(), c.Points.Select(p => p.Converged ? p.QMvar : double.NaN).ToList()),
        };
        return new WorkbenchResult(def.Id, def.Kind, c.Status, c.Status == "OK" ? "Computed" : "Incomplete",
            c.Status == "OK" ? null : "The QV curve could not be computed: " + c.Status + ".", [summary, points],
            [new WorkbenchPlot("qv-curve", spec, "Bus voltage (pu)", "Reactive power needed (MVAr)", series)]);
    }
}
