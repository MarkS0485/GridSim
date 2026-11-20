using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Protection.Coordination;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-10 Core: the renderer-neutral workbench layer. Model table against a direct LINQ oracle, grouping against the
/// closed-form group sums, plot geometry against analytic coordinates and the protection library, studies against the
/// engines they call, and the report generator (strictness, gaps, golden files).
/// </summary>
public sealed class R410WorkbenchCoreTests : IDisposable
{
    private readonly string _root = R401Cases.NewRoot();
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string RepoRoot() => CliProcess.Root();

    private static GridModel Case(string name) => MatpowerParser.ParseFile(Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m"));

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static byte[] B(string text) => new UTF8Encoding(false).GetBytes(text);

    private static BoundGroups Bind(string dir, string groups)
    {
        PlanningMaster master = R401Cases.Master(dir);
        string doc = "{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"" + master.BundleSha256 + "\" }, \"groups\": [ " + groups + " ] }";
        return BoundGroups.Bind(ModelGroupsDocument.Parse(B(doc)), master.Model);
    }

    private const string AreaA = "{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }";
    private const string AreaB = "{ \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [3] }";

    // ------------------------------------------------------------------ model table

    [Fact]
    public void Sort_and_filter_of_a_14_bus_case_equal_a_direct_linq_oracle()
    {
        GridModel m = Case("case14");
        ModelTableView v = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus,
            [new ModelFilter("pd", ModelFilterOp.Greater, "10")], "qd", true));
        // Oracle: LINQ over the model, stable, descending by Qd, buses with Pd > 10 only.
        int[] expected = m.Buses.Select((b, i) => (b, i)).Where(t => t.b.Pd > 10).OrderByDescending(t => t.b.Qd).Select(t => t.b.Id).ToArray();
        Assert.Equal(expected, v.AllRows.Select(r => (int)r.Cells[0].Number!.Value).ToArray());
        Assert.Equal(14, v.TotalRows);
        Assert.Equal(expected.Length, v.ShownRows);
        Assert.True(expected.Length > 1);

        // Ascending and a text filter agree with LINQ as well.
        ModelTableView asc = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, null, "pd", false));
        Assert.Equal(m.Buses.OrderBy(b => b.Pd).Select(b => b.Id), asc.AllRows.Select(r => (int)r.Cells[0].Number!.Value));
        ModelTableView pq = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, [new ModelFilter("type", ModelFilterOp.Equals, "pq")]));
        Assert.Equal(m.Buses.Where(b => b.Type == BusType.Pq).Select(b => b.Id), pq.AllRows.Select(r => (int)r.Cells[0].Number!.Value));
    }

    [Fact]
    public void An_absent_value_is_a_gap_that_sorts_last_and_matches_no_filter()
    {
        var m = new GridModel
        {
            Name = "gap",
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 5)],
            Branches = [new Branch(1, 2, 0.01, 0.1, RateAMva: 0), new Branch(1, 2, 0.01, 0.2, RateAMva: 50), new Branch(1, 2, 0.01, 0.3, RateAMva: 0)],
            Generators = [new Generator(1, 10)],
        };
        foreach (bool desc in new[] { false, true })
        {
            ModelTableView v = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Line, null, "rateA", desc));
            ModelRow[] rows = v.AllRows.ToArray();
            Assert.Equal(1, rows[0].Index);                       // the one rated line first, whichever direction
            Assert.All(rows.Skip(1), r => { Assert.True(r.Cells[9].Missing); Assert.Equal("-", r.Cells[9].Text); Assert.Null(r.Cells[9].Number); });
        }
        Assert.Equal(0, ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Line, [new ModelFilter("rateA", ModelFilterOp.LessOrEqual, "0")])).ShownRows);
    }

    [Fact]
    public void Grouping_by_an_R401_area_equals_the_closed_form_group_sums()
    {
        string dir = R401Cases.ThreeBus(_root);
        GridModel model = R401Cases.Master(dir).Model;
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB);

        ModelTableView buses = ModelTable.Build(model, new ModelTableQuery(ModelElementKind.Bus, GroupBy: GroupKind.Area), groups);
        Assert.Equal(["areaA", "areaB"], buses.Sections.Select(s => s.Title));
        int pd = buses.Columns.ToList().FindIndex(c => c.Key == "pd"), qd = buses.Columns.ToList().FindIndex(c => c.Key == "qd");
        // Closed form from the case data: area A holds bus 2 (100 MW, 20 MVAr); area B holds bus 3 (60 MW, 10 MVAr).
        Assert.Equal(100.0, buses.Sections[0].Totals[pd]);
        Assert.Equal(20.0, buses.Sections[0].Totals[qd]);
        Assert.Equal(60.0, buses.Sections[1].Totals[pd]);
        Assert.Equal(10.0, buses.Sections[1].Totals[qd]);

        ModelTableView gens = ModelTable.Build(model, new ModelTableQuery(ModelElementKind.Generator, GroupBy: GroupKind.Area), groups);
        int pg = gens.Columns.ToList().FindIndex(c => c.Key == "pg");
        Assert.Equal(120.0, gens.Sections[0].Totals[pg]);   // G1 at bus 1
        Assert.Equal(40.0, gens.Sections[1].Totals[pg]);    // G2 at bus 3

        // Lines: L1-2 is inside A; the two ties belong to neither area and form the ungrouped remainder.
        ModelTableView lines = ModelTable.Build(model, new ModelTableQuery(ModelElementKind.Line, GroupBy: GroupKind.Area), groups);
        Assert.Equal(["areaA", "areaB", "(ungrouped)"], lines.Sections.Select(s => s.Title));
        Assert.Empty(lines.Sections[1].Rows);   // a group with no line still has its (empty) section
        Assert.Equal([0], lines.Sections[0].Rows.Select(r => r.Index));
        Assert.Equal([1, 2], lines.Sections[2].Rows.Select(r => r.Index));
        // Every bus lands in exactly one section for an exclusive kind.
        Assert.Equal(model.Buses.Count, buses.Sections.Sum(s => s.Rows.Count));
    }

    [Fact]
    public void An_unknown_column_or_a_bad_operand_is_refused_not_ignored()
    {
        GridModel m = Case("case9");
        Assert.Throws<ArgumentException>(() => ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, null, "nope")));
        Assert.Throws<ArgumentException>(() => ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, [new ModelFilter("nope", ModelFilterOp.Equals, "1")])));
        Assert.Throws<ArgumentException>(() => ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, [new ModelFilter("pd", ModelFilterOp.Greater, "abc")])));
        Assert.Throws<ArgumentException>(() => ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Bus, [new ModelFilter("type", ModelFilterOp.Greater, "1")])));
    }

    [Fact]
    public void The_table_has_a_canonical_text_that_repeats()
    {
        GridModel m = Case("case9");
        string a = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Generator)).Canonical();
        string b = ModelTable.Build(m, new ModelTableQuery(ModelElementKind.Generator)).Canonical();
        Assert.Equal(a, b);
        Assert.StartsWith("gridsim-model-table/1\nkind\tGenerator\n", a, StringComparison.Ordinal);
        Assert.Equal(3, ModelTable.Count(m, ModelElementKind.Generator));
    }

    // ------------------------------------------------------------------ plots

    [Fact]
    public void A_tcc_curve_carries_the_protection_library_times_bit_for_bit_and_log_ticks_at_decades()
    {
        var si = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100, 0.2);
        var vi = new OvercurrentElement(OvercurrentCurve.IecVeryInverse, 150, 0.1);
        double[] currents = [110, 150, 200, 400, 800, 1600, 3200, 6400];
        PlotSpec p = PlotBuilders.Tcc("tcc", "TCC", [("SI", si), ("VI", vi)], currents);

        PlotPolyline[] lines = p.Polylines.ToArray();
        Assert.Equal(2, lines.Length);
        for (int k = 0; k < 2; k++)
        {
            OvercurrentElement el = k == 0 ? si : vi;
            for (int i = 0; i < currents.Length; i++)
            {
                double t = OvercurrentCoordination.OperateTimeSeconds(el, currents[i]);
                if (double.IsInfinity(t)) { Assert.True(lines[k].Points[i].Missing); continue; }
                Assert.Equal(BitConverter.DoubleToInt64Bits(t), BitConverter.DoubleToInt64Bits(lines[k].Points[i].DataY));
                Assert.Equal(currents[i], lines[k].Points[i].DataX);
            }
        }
        // The VI element does not operate at 110 A (below its 150 A pickup): a visible gap, not a zero.
        Assert.True(lines[1].Points[0].Missing);
        Assert.Contains(p.Gaps, g => g.Series == "VI" && g.DataX == 110);

        PlotPanel panel = p.Panels[0];
        Assert.True(panel.X.Log && panel.Y.Log);
        // Ticks are exactly the decades of the displayed range, evenly spaced.
        foreach (PlotAxis a in new[] { panel.X, panel.Y })
        {
            Assert.All(a.Ticks, t => Assert.Equal(1.0, Math.Round(t.Value / Math.Pow(10, Math.Round(Math.Log10(t.Value))), 12)));
            Assert.Equal(a.Min, a.Ticks[0].Value);
            Assert.Equal(a.Max, a.Ticks[^1].Value);
            double step = a.Ticks[1].Position - a.Ticks[0].Position;
            for (int i = 2; i < a.Ticks.Count; i++) Assert.Equal(step, a.Ticks[i].Position - a.Ticks[i - 1].Position, 9);
        }
        Assert.Equal(100, panel.X.Min);     // the smallest current is 110 A: the decade below is 100
        Assert.Equal(10000, panel.X.Max);   // the largest is 6400 A: the decade above is 10 000
        // A pickup rule sits at the mapped pickup current (100 A is the left edge of the axis).
        Assert.Contains(p.Panels[0].Items.OfType<PlotRule>(), r => r.Name == "pickup SI" && Math.Abs(r.X1 - panel.Left) < 1e-9);
    }

    [Fact]
    public void An_eigenvalue_plot_places_the_markers_at_the_analytic_coordinates()
    {
        // The 2x2 system [[-1, 2], [-2, -1]] has eigenvalues -1 +/- 2j.
        PlotSpec p = PlotBuilders.EigenvaluePlane("eig", "Eigenvalues", [-1, -1], [2, -2]);
        PlotPanel panel = p.Panels[0];
        PlotMarker[] ms = p.Markers.ToArray();
        Assert.Equal(2, ms.Length);
        // Independent mapping from the axis range the plot states.
        double X(double v) => panel.Left + (v - panel.X.Min) / (panel.X.Max - panel.X.Min) * panel.Width;
        double Y(double v) => panel.Top + panel.Height - (v - panel.Y.Min) / (panel.Y.Max - panel.Y.Min) * panel.Height;
        Assert.Equal(X(-1), ms[0].Px, 9);
        Assert.Equal(Y(2), ms[0].Py, 9);
        Assert.Equal(X(-1), ms[1].Px, 9);
        Assert.Equal(Y(-2), ms[1].Py, 9);
        // Both axes include zero, so the stability boundary (the imaginary axis) is inside the frame and right of the poles.
        Assert.True(panel.X.Min <= -1 && panel.X.Max >= 0);
        PlotRule boundary = panel.Items.OfType<PlotRule>().Single(r => r.Name == "stability boundary");
        Assert.Equal(X(0), boundary.X1, 9);
        Assert.True(boundary.X1 > ms[0].Px);
        Assert.Equal(ms[0].Px, ms[1].Px);                // a conjugate pair mirrors about the real axis
        Assert.Equal(Y(0) - ms[0].Py, ms[1].Py - Y(0), 9);
    }

    [Fact]
    public void A_spectrum_places_the_bar_at_the_exact_order_and_height()
    {
        // A synthetic signal with a single 5th harmonic of amplitude 0.2 beside the fundamental.
        double[] orders = [1, 2, 3, 4, 5, 6, 7];
        double[] amps = [1.0, 0, 0, 0, 0.2, 0, 0];
        PlotSpec p = PlotBuilders.Spectrum("spec", "Spectrum", orders, amps);
        PlotPanel panel = p.Panels[0];
        double X(double v) => panel.Left + (v - panel.X.Min) / (panel.X.Max - panel.X.Min) * panel.Width;
        double Y(double v) => panel.Top + panel.Height - (v - panel.Y.Min) / (panel.Y.Max - panel.Y.Min) * panel.Height;
        PlotBar[] bars = p.Bars.ToArray();
        Assert.Equal(7, bars.Length);
        PlotBar fifth = bars.Single(b => b.DataX == 5);
        Assert.Equal(X(5), fifth.Px, 9);
        Assert.Equal(Y(0.2), fifth.Py, 9);
        Assert.Equal(Y(0) - Y(0.2), fifth.Height, 9);
        Assert.Equal(0.2, fifth.DataY);
        // The bar is 0.6 of the order spacing wide; its neighbours are one order apart.
        Assert.Equal(0.6 * (X(1) - X(0)), fifth.Width, 9);
        // The y axis includes zero, so a zero harmonic is a zero-height bar at the baseline, not a gap.
        Assert.Equal(0, bars.Single(b => b.DataX == 3).Height, 9);
        Assert.Equal(0, panel.Y.Min);
    }

    [Fact]
    public void A_missing_value_breaks_the_line_and_is_marked_never_zeroed()
    {
        PlotSpec p = PlotBuilders.TimeSeries("ts", "Series", "t (s)", "v", [new PlotSeriesData("a", [0, 1, 2, 3], [1, double.NaN, 3, 4])]);
        PlotPolyline line = p.Polylines.Single();
        Assert.True(line.Points[1].Missing);
        Assert.True(double.IsNaN(line.Points[1].Py));
        PlotGap gap = Assert.Single(p.Gaps);
        Assert.Equal(1, gap.DataX);
        Assert.Equal(line.Points[1].Px, gap.Px);
        // The SVG draws two separate runs (a single point becomes a dot, the rest a polyline), not one line through the gap.
        string svg = PlotSvg.Render(p);
        Assert.Contains("<circle", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-dasharray=\"2 3\"", svg, StringComparison.Ordinal);
        // A missing x cannot be placed and is refused.
        Assert.Throws<ArgumentException>(() => PlotBuilders.TimeSeries("ts", "t", "x", "y", [new PlotSeriesData("a", [0, double.NaN], [1, 2])]));
        // A log axis refuses a non-positive x.
        Assert.Throws<ArgumentException>(() => PlotBuilders.Lines("l", "t", "x", "y", [new PlotSeriesData("a", [0, 1], [1, 2])], true, false));
    }

    [Fact]
    public void A_plot_has_a_canonical_text_and_sha256_that_repeat_and_follow_the_data()
    {
        PlotSpec a = PlotBuilders.Bode("bode", "Bode", [1, 10, 100, 1000], [0, -3, -20, -40], [0, -45, -80, -90]);
        PlotSpec b = PlotBuilders.Bode("bode", "Bode", [1, 10, 100, 1000], [0, -3, -20, -40], [0, -45, -80, -90]);
        PlotSpec c = PlotBuilders.Bode("bode", "Bode", [1, 10, 100, 1000], [0, -3, -20, -41], [0, -45, -80, -90]);
        Assert.Equal(a.Canonical(), b.Canonical());
        Assert.Equal(a.Sha256(), b.Sha256());
        Assert.NotEqual(a.Sha256(), c.Sha256());
        Assert.Equal(64, a.Sha256().Length);
        Assert.StartsWith("gridsim-plot/1\n", a.Canonical(), StringComparison.Ordinal);
        Assert.Equal(2, a.Panels.Count);                  // magnitude over phase
        Assert.True(a.Panels[0].X.Log && a.Panels[1].X.Log);
        Assert.Equal(a.Panels[0].X.Min, a.Panels[1].X.Min);
        Assert.Equal(PlotSvg.Render(a), PlotSvg.Render(b));
        Assert.StartsWith("<svg ", PlotSvg.Render(a), StringComparison.Ordinal);
    }

    [Fact]
    public void The_axis_rules_give_nice_ticks_that_cover_the_data()
    {
        PlotSpec p = PlotBuilders.TimeSeries("ts", "t", "x", "y", [new PlotSeriesData("a", [0, 1, 2], [0.93, 1.02, 1.07])]);
        PlotAxis y = p.Panels[0].Y;
        Assert.True(y.Min <= 0.93 && y.Max >= 1.07);
        double step = Math.Round(y.Ticks[1].Value - y.Ticks[0].Value, 10);
        Assert.Contains(step, new[] { 0.02, 0.05, 0.01 }.Select(s => Math.Round(s, 10)));
        Assert.All(y.Ticks, t => Assert.Equal(0.0, Math.Round(t.Value / step - Math.Round(t.Value / step), 6)));
        // A constant series is widened, never a zero-height axis.
        PlotAxis flat = PlotBuilders.TimeSeries("ts", "t", "x", "y", [new PlotSeriesData("a", [0, 1], [5, 5])]).Panels[0].Y;
        Assert.True(flat.Max > flat.Min);
    }

    [Fact]
    public void A_qv_plot_marks_the_operating_point_and_gaps_for_non_converged_points()
    {
        GridModel m = Case("case9");
        int bus = m.Buses.First(b => b.Type == BusType.Pq).Id;
        QvCurveResult c = QvCurve.Compute(m, bus);
        PlotSpec p = PlotBuilders.QvCurvePlot("qv", "QV", c);
        PlotPolyline line = p.Polylines.Single();
        Assert.Equal(c.Points.Count, line.Points.Count);
        for (int i = 0; i < c.Points.Count; i++)
        {
            Assert.Equal(c.Points[i].VPu, line.Points[i].DataX);
            if (c.Points[i].Converged) Assert.Equal(c.Points[i].QMvar, line.Points[i].DataY);
            else Assert.True(line.Points[i].Missing);
        }
        PlotMarker op = p.Markers.Single(x => x.Series == "operating point");
        Assert.Equal(c.BaseVPu, op.DataX);
        Assert.Equal(0, op.DataY);
    }

    // ------------------------------------------------------------------ studies (the engines they call)

    [Fact]
    public void The_power_flow_study_is_the_engine_solve_bit_for_bit()
    {
        GridModel m = Case("case9");
        WorkbenchResult r = WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("pf", WorkbenchStudies.PowerFlow));
        PowerFlowResult direct = SolverSelector.CreateNewton(m).Solve(m, ContingencyAnalysis.StudyOptions);
        Assert.Equal("CONVERGED", r.Status);
        WorkbenchTable buses = r.Table("buses")!;
        Assert.Equal(direct.BusIds.Count, buses.Rows.Count);
        for (int i = 0; i < direct.BusIds.Count; i++)
        {
            Assert.Equal(direct.BusIds[i], (int)buses.Rows[i][0].Number!.Value);
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct.Vm[i]), BitConverter.DoubleToInt64Bits(buses.Rows[i][1].Number!.Value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct.VaDeg[i]), BitConverter.DoubleToInt64Bits(buses.Rows[i][2].Number!.Value));
        }
        Assert.Equal(direct.LineFlows.Count, r.Table("branches")!.Rows.Count);
        PlotPolyline vp = r.Plots.Single().Spec.Polylines.Single();
        Assert.Equal(direct.Vm, vp.Points.Select(q => q.DataY).ToArray());
    }

    [Fact]
    public void An_unconverged_power_flow_gives_a_status_and_no_numbers()
    {
        GridModel m = Case("case9");
        GridModel heavy = m.CopyWith(buses: m.Buses.Select(b => b with { Pd = b.Pd * 60, Qd = b.Qd * 60 }).ToList());
        WorkbenchResult r = WorkbenchStudies.Run(heavy, null, WorkbenchStudyDefinition.Of("pf", WorkbenchStudies.PowerFlow));
        Assert.Equal("NOT CONVERGED", r.Status);
        Assert.Null(r.Table("buses"));
        Assert.Empty(r.Plots);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void The_security_study_is_the_registry_mode_over_the_p1_runner()
    {
        string dir = R401Cases.ThreeBus(_root);
        GridModel m = R401Cases.Master(dir).Model;
        WorkbenchResult r = WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("sec", WorkbenchStudies.TransmissionSecurity, ("outages", "0,1")));
        var request = new TransmissionSecurityRequest([new BranchOutage(0, 1, 2), new BranchOutage(1, 2, 3)], 0.95, 1.05, PostFaultRating.RateA, 50, null);
        TransmissionSecurityResult direct = TransmissionSecurityRunner.Run(m, request);
        Assert.Equal(direct.Status, r.Status);
        Assert.Equal(direct.Outcome, r.Outcome);
        WorkbenchTable states = r.Table("states")!;
        Assert.Equal(1 + direct.Outages.Count, states.Rows.Count);
        Assert.Equal(direct.BaseCase.Disposition, states.Rows[0][1].Text);
        for (int i = 0; i < direct.Outages.Count; i++)
        {
            Assert.Equal(direct.Outages[i].Disposition, states.Rows[i + 1][1].Text);
            Assert.Equal(direct.Outages[i].Limits.Count(l => l.Violated), (int)states.Rows[i + 1][5].Number!.Value);
        }
    }

    [Fact]
    public void The_group_summary_study_matches_the_registry_mode_and_needs_groups()
    {
        string dir = R401Cases.ThreeBus(_root);
        GridModel m = R401Cases.Master(dir).Model;
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB);
        WorkbenchResult r = WorkbenchStudies.Run(m, groups, WorkbenchStudyDefinition.Of("gs", WorkbenchStudies.GroupSummary));
        Assert.Equal("SUMMARISED", r.Status);
        WorkbenchTable t = r.Table("groups")!;
        Assert.Equal(["areaA", "areaB"], t.Rows.Select(x => x[0].Text));
        // Closed form: lossless case, B has a 40 MW unit and a 60 MW load, so A sends 20 MW into B.
        int tie = t.Columns.ToList().FindIndex(c => c.Key == "tieFlowMw");
        Assert.Equal(-20.0, t.Rows[1][tie].Number!.Value, 6);   // flow out of B is -20 MW (into B)
        Assert.Equal(20.0, t.Rows[0][tie].Number!.Value, 6);

        WorkbenchResult none = WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("gs", WorkbenchStudies.GroupSummary));
        Assert.Equal(WorkbenchStudies.InvalidStudy, none.Status);
        Assert.Contains("groups document", none.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_qv_study_is_the_r403_curve_and_a_bad_request_is_refused_with_its_reason()
    {
        GridModel m = Case("case9");
        int bus = m.Buses.First(b => b.Type == BusType.Pq).Id;
        WorkbenchResult r = WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("qv", WorkbenchStudies.QvCurveKind, ("bus", bus.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        QvCurveResult direct = QvCurve.Compute(m, bus);
        Assert.Equal(direct.Status, r.Status);
        Assert.Equal(direct.Points.Count, r.Table("points")!.Rows.Count);
        Assert.Equal(direct.Points[3].VPu, r.Table("points")!.Rows[3][0].Number!.Value);

        Assert.Equal(WorkbenchStudies.InvalidStudy, WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("qv", WorkbenchStudies.QvCurveKind, ("bus", "99"))).Status);
        Assert.Equal(WorkbenchStudies.InvalidStudy, WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("x", "no-such-kind")).Status);
        Assert.Equal(WorkbenchStudies.InvalidStudy, WorkbenchStudies.Run(m, null, WorkbenchStudyDefinition.Of("qv", WorkbenchStudies.QvCurveKind, ("vMin", "abc"))).Status);
    }

    [Fact]
    public void The_project_tree_lists_the_model_groups_studies_and_their_status()
    {
        string dir = R401Cases.ThreeBus(_root);
        GridModel m = R401Cases.Master(dir).Model;
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB);
        WorkbenchProject p = WorkbenchProject.WithDefaultStudies("three", m, groups);
        WorkbenchNode tree = p.Tree();
        Assert.Equal("three", tree.Title);
        WorkbenchNode model = tree.Children.Single(n => n.Id == "model");
        Assert.Equal(["3", "2", "3", "0"], model.Children.Select(n => n.Detail));
        Assert.Equal(["areaA", "areaB"], tree.Children.Single(n => n.Id == "groups").Children.Select(n => n.Title));
        WorkbenchNode studies = tree.Children.Single(n => n.Id == "studies");
        Assert.All(studies.Children, s => Assert.Equal("not run", s.Detail));
        p.Run("power-flow");
        Assert.Equal("CONVERGED", p.Tree().Children.Single(n => n.Id == "studies").Children.Single(n => n.Id == "study/power-flow").Detail);
        Assert.Throws<ArgumentException>(() => p.AddStudy(WorkbenchStudyDefinition.Of("power-flow", WorkbenchStudies.PowerFlow)));
        Assert.Throws<ArgumentException>(() => p.Run("nope"));
        Assert.Throws<InvalidOperationException>(() => p.ResultJson("n-1-security"));
    }

    [Fact]
    public void A_result_document_and_the_report_made_from_it_repeat_byte_for_byte()
    {
        GridModel m = Case("case9");
        string Make()
        {
            WorkbenchProject p = WorkbenchProject.WithDefaultStudies("case9", m);
            p.Run("power-flow");
            return p.ResultJson("power-flow");
        }
        string a = Make(), b = Make();
        Assert.Equal(a, b);
        using JsonDocument doc = JsonDocument.Parse(a);
        Assert.Equal("gridsim-workbench-result/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("tables").GetProperty("buses").GetProperty("rows").GetArrayLength());
        Assert.DoesNotContain('\r', a);

        byte[] result = B(a);
        byte[] template = B(ReportTemplate.DefaultJson(result));
        ReportDocument d1 = ReportRenderer.Build(template, result), d2 = ReportRenderer.Build(template, result);
        Assert.Equal(ReportRenderer.ToMarkdown(d1), ReportRenderer.ToMarkdown(d2));
        Assert.Equal(ReportRenderer.ToHtml(d1), ReportRenderer.ToHtml(d2));
        Assert.Equal(ReportRenderer.ToCsv(d1), ReportRenderer.ToCsv(d2));
        Assert.Contains("CONVERGED", ReportRenderer.ToMarkdown(d1), StringComparison.Ordinal);
        Assert.Contains("<svg ", ReportRenderer.ToHtml(d1), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ report generator

    private const string FixedResult = """
        {
          "schema": "gridsim-workbench-result/1",
          "study": { "id": "demo", "kind": "power-flow" },
          "case": { "name": "demo case", "buses": 3 },
          "status": "CONVERGED",
          "outcome": "Converged",
          "reason": null,
          "tables": {
            "buses": {
              "title": "Bus results",
              "rows": [
                { "bus": 1, "vm": 1.0, "note": "slack | ref", "rate": 100.5 },
                { "bus": 2, "vm": 0.9871234, "note": "load", "rate": null },
                { "bus": 3, "vm": 0.95, "note": "a,b \"c\"", "rate": 7 }
              ]
            }
          },
          "plots": {
            "profile": { "series": [ { "name": "v", "x": [0, 1, 2], "y": [1.0, null, 0.95] } ] }
          }
        }
        """;

    private const string FixedTemplate = """
        {
          "schema": "gridsim-report-template/1",
          "title": "Demo report",
          "sections": [
            { "heading": "Summary", "blocks": [
              { "type": "text", "text": "Fixed result for the golden files." },
              { "type": "value", "label": "Status", "path": "/status" },
              { "type": "value", "label": "Reason", "path": "/reason" },
              { "type": "value", "label": "Buses", "path": "/case/buses" },
              { "type": "value", "label": "Absent", "path": "/case/nothing" }
            ] },
            { "heading": "Buses", "blocks": [
              { "type": "table", "title": "Bus results", "path": "/tables/buses/rows", "columns": [
                { "key": "bus", "title": "Bus" },
                { "key": "vm", "title": "Voltage", "unit": "pu", "decimals": 4 },
                { "key": "note", "title": "Note" },
                { "key": "rate", "title": "Rate", "unit": "MVA", "decimals": 1 }
              ] },
              { "type": "table", "title": "Missing table", "path": "/tables/none/rows" }
            ] },
            { "heading": "Plots", "blocks": [
              { "type": "plot", "title": "Voltage profile", "x": "/plots/profile/series/0/x", "y": "/plots/profile/series/0/y", "xLabel": "Bus", "yLabel": "Voltage (pu)" },
              { "type": "plot", "title": "Missing plot", "x": "/plots/none/x", "y": "/plots/none/y" }
            ] }
          ]
        }
        """;

    private static string Golden(string name)
    {
        string path = Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "r410", name);
        Assert.True(File.Exists(path), "missing golden file " + path);
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void The_rendered_markdown_html_and_csv_equal_the_frozen_golden_files()
    {
        ReportDocument doc = ReportRenderer.Build(B(Lf(FixedTemplate)), B(Lf(FixedResult)));
        if (Environment.GetEnvironmentVariable("GRIDSIM_R410_WRITE_GOLDEN") is { Length: > 0 } outDir)
        {
            File.WriteAllText(Path.Combine(outDir, "report.md"), ReportRenderer.ToMarkdown(doc), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "report.html"), ReportRenderer.ToHtml(doc), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "report.csv"), ReportRenderer.ToCsv(doc), new UTF8Encoding(false));
        }
        Assert.Equal(Golden("report.md"), ReportRenderer.ToMarkdown(doc));
        Assert.Equal(Golden("report.html"), ReportRenderer.ToHtml(doc));
        Assert.Equal(Golden("report.csv"), ReportRenderer.ToCsv(doc));
    }

    [Fact]
    public void A_missing_binding_is_a_visible_gap_and_never_zero()
    {
        ReportDocument doc = ReportRenderer.Build(B(Lf(FixedTemplate)), B(Lf(FixedResult)));
        string md = ReportRenderer.ToMarkdown(doc);
        Assert.Contains("- **Reason:** n/a", md, StringComparison.Ordinal);       // JSON null
        Assert.Contains("- **Absent:** n/a", md, StringComparison.Ordinal);       // no such path
        Assert.Contains("*No table data at /tables/none/rows.*", md, StringComparison.Ordinal);
        Assert.Contains("not drawn: No plot data at /plots/none/x.", md, StringComparison.Ordinal);
        ReportOutTable t = doc.Sections[1].Blocks.OfType<ReportOutTable>().First();
        Assert.Equal("n/a", t.Rows[1][3]);                                         // the null rate is not "0.0"
        Assert.Equal("100.5", t.Rows[0][3]);
        // The plot with a null y still draws (a gap at x = 1), it is not dropped.
        ReportOutPlot plot = doc.Sections[2].Blocks.OfType<ReportOutPlot>().First();
        Assert.NotNull(plot.Plot);
        Assert.Single(plot.Plot!.Gaps);
        // CSV: a gap is an empty field, and quoting follows RFC 4180.
        string csv = ReportRenderer.ToCsv(doc);
        Assert.Contains("2,0.9871,load,\n", csv, StringComparison.Ordinal);
        Assert.Contains("3,0.9500,\"a,b \"\"c\"\"\",7.0\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provenance_records_the_template_and_result_hashes_and_the_generator()
    {
        ReportDocument doc = ReportRenderer.Build(B(Lf(FixedTemplate)), B(Lf(FixedResult)), "9.9.9");
        Assert.Equal(WorkbenchText.Sha256(B(Lf(FixedTemplate))), doc.Provenance.TemplateSha256);
        Assert.Equal(WorkbenchText.Sha256(B(Lf(FixedResult))), doc.Provenance.ResultSha256);
        Assert.Equal("gridsim-report/1", doc.Provenance.Generator);
        string md = ReportRenderer.ToMarkdown(doc);
        Assert.Contains("Template SHA-256: " + doc.Provenance.TemplateSha256, md, StringComparison.Ordinal);
        Assert.Contains("Result SHA-256: " + doc.Provenance.ResultSha256, md, StringComparison.Ordinal);
        Assert.Contains("(GridSim 9.9.9)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_is_checked_strictly_and_a_wrong_result_hash_is_refused_before_anything_renders()
    {
        string T(string extra) => "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"t\"" + extra + ", \"sections\": [] }";
        ReportException unknown = Assert.Throws<ReportException>(() => ReportTemplate.Parse(B(T(", \"surprise\": 1"))));
        Assert.Contains("unknown member 'surprise'", unknown.Message, StringComparison.Ordinal);
        Assert.Throws<ReportException>(() => ReportTemplate.Parse(B("{ \"schema\": \"gridsim-report-template/9\", \"title\": \"t\", \"sections\": [] }")));
        ReportException dup = Assert.Throws<ReportException>(() => ReportTemplate.Parse(B(T(", \"title\": \"again\""))));
        Assert.Contains("repeats the member 'title'", dup.Message, StringComparison.Ordinal);
        Assert.Throws<ReportException>(() => ReportTemplate.Parse(B("not json")));
        Assert.Throws<ReportException>(() => ReportTemplate.Parse(B("{ \"schema\": \"gridsim-report-template/1\", \"title\": \"t\", \"sections\": [ { \"heading\": \"h\", \"blocks\": [ { \"type\": \"pie\" } ] } ] }")));
        Assert.Throws<ReportException>(() => ReportTemplate.Parse(B("{ \"schema\": \"gridsim-report-template/1\", \"title\": \"t\", \"sections\": [ { \"heading\": \"h\", \"blocks\": [ { \"type\": \"value\", \"label\": \"l\", \"path\": \"no-slash\" } ] } ] }")));

        string good = WorkbenchText.Sha256(B(Lf(FixedResult)));
        byte[] hashed = B(T(", \"resultSha256\": \"" + good + "\""));
        Assert.Equal("t", ReportRenderer.Build(hashed, B(Lf(FixedResult))).Title);
        byte[] bad = B(T(", \"resultSha256\": \"" + new string('0', 64) + "\""));
        ReportException mismatch = Assert.Throws<ReportException>(() => ReportRenderer.Build(bad, B(Lf(FixedResult))));
        Assert.Contains("does not match the template", mismatch.Message, StringComparison.Ordinal);
        Assert.Throws<ReportException>(() => ReportRenderer.Build(B(T("")), B("{ \"a\": 1, \"a\": 2 }")));
        Assert.Throws<ReportException>(() => ReportRenderer.Build(B(T("")), B("[")));
    }

    [Fact]
    public void A_report_can_bind_to_any_result_json_with_pointers_including_escaped_names()
    {
        const string result = "{ \"a/b\": { \"c~d\": [ 10, 20 ] }, \"rows\": [ { \"x\": 1.23456 } ] }";
        const string template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"t\", \"sections\": [ { \"heading\": \"h\", \"blocks\": [ " +
            "{ \"type\": \"value\", \"label\": \"v\", \"path\": \"/a~1b/c~0d/1\" }, " +
            "{ \"type\": \"table\", \"title\": \"r\", \"path\": \"/rows\" } ] } ] }";
        string md = ReportRenderer.ToMarkdown(ReportRenderer.Build(B(template), B(result)));
        Assert.Contains("- **v:** 20", md, StringComparison.Ordinal);
        Assert.Contains("| 1.2346 |", md, StringComparison.Ordinal);   // columns taken from the first row, 4 decimals by default
    }

    [Fact]
    public void The_default_template_covers_the_summary_every_table_and_every_plot()
    {
        WorkbenchProject p = WorkbenchProject.WithDefaultStudies("case9", Case("case9"));
        p.Run("power-flow");
        byte[] result = B(p.ResultJson("power-flow"));
        string template = ReportTemplate.DefaultJson(result);
        ReportTemplate parsed = ReportTemplate.Parse(B(template));
        Assert.Equal("Summary", parsed.Sections[0].Heading);
        Assert.Equal(1 + 3 + 1, parsed.Sections.Count);   // summary + summary/buses/branches tables + the voltage profile
        Assert.Contains(parsed.Sections.SelectMany(s => s.Blocks), b => b is ReportPlotBlock);
    }
}
