using System.Net;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Protection.Coordination;
using GridSim.Web.Projection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-10 on the web client: the DTOs carry the Core workbench values and geometry unchanged (the parity half; the
/// Avalonia half is in the App tests), and the /workbench/{case} route serves the model browser, the studies and the report
/// without touching the dashboard, and is refused on a replay-only host.
/// </summary>
public sealed class WebWorkbenchTests
{
    private static GridModel Case(string name) =>
        MatpowerParser.ParseFile(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name + ".m"));

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = TestTemp.Under("gridsim-r410-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ------------------------------------------------------------------ DTO parity with the Core values

    [Fact]
    public void The_model_table_dto_has_the_same_cells_totals_and_order_as_the_core_table()
    {
        GridModel m = Case("case14");
        foreach (ModelElementKind kind in Enum.GetValues<ModelElementKind>())
        {
            ModelTableView core = ModelTable.Build(m, new ModelTableQuery(kind, null, kind == ModelElementKind.Bus ? "pd" : null, true));
            ModelTableDto dto = ModelTableDto.From(core);
            Assert.Equal(kind.ToString(), dto.Kind);
            Assert.Equal(core.TotalRows, dto.TotalRows);
            Assert.Equal(core.ShownRows, dto.ShownRows);
            Assert.Equal(core.Columns.Select(c => c.Key), dto.Columns.Select(c => c.Key));
            Assert.Equal(core.AllRows.Select(r => r.Cells.Select(c => c.Text).ToList()), dto.CellTexts());
            Assert.Equal(core.AllRows.SelectMany(r => r.Cells.Select(c => c.Number)), dto.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Cells.Select(c => c.Number)));
            Assert.Equal(core.AllRows.SelectMany(r => r.Cells.Select(c => c.Missing)), dto.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Cells.Select(c => c.Missing)));
            Assert.Equal(core.Sections.SelectMany(s => s.Totals), dto.Sections.SelectMany(s => s.Totals));
        }
    }

    [Fact]
    public void The_plot_dto_has_the_same_geometry_hash_and_svg_as_the_core_plot()
    {
        var si = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100, 0.2);
        PlotSpec[] plots =
        [
            PlotBuilders.Tcc("tcc", "TCC", [("SI", si)], [110, 200, 400, 800, 1600]),
            PlotBuilders.EigenvaluePlane("eig", "Eig", [-1, -1], [2, -2]),
            PlotBuilders.Spectrum("sp", "Spec", [1, 3, 5], [1, 0.1, 0.2]),
            PlotBuilders.Bode("bode", "Bode", [1, 10, 100], [0, -3, -20], [0, -45, -80]),
            PlotBuilders.TimeSeries("ts", "T", "x", "y", [new PlotSeriesData("a", [0, 1, 2], [1, double.NaN, 3])]),
        ];
        foreach (PlotSpec spec in plots)
        {
            PlotDto dto = PlotDto.From(spec);
            Assert.Equal(spec.Sha256(), dto.Sha256);
            Assert.Equal(PlotSvg.Render(spec), dto.Svg);
            Assert.Equal(spec.Panels.Count, dto.Panels.Count);
            for (int i = 0; i < spec.Panels.Count; i++)
            {
                PlotPanel p = spec.Panels[i];
                PlotPanelDto d = dto.Panels[i];
                Assert.Equal(p.X.Ticks.Select(t => (t.Value, t.Position, t.Label)), d.X.Ticks.Select(t => (t.Value, t.Position, t.Label)));
                Assert.Equal(p.Y.Ticks.Select(t => (t.Value, t.Position, t.Label)), d.Y.Ticks.Select(t => (t.Value, t.Position, t.Label)));
                Assert.Equal(p.Items.OfType<PlotPolyline>().SelectMany(l => l.Points.Select(q => (q.DataX, q.DataY, q.Px, q.Py))).ToList(),
                    d.Polylines.SelectMany(l => l.Points.Select(q => (q.DataX, q.DataY, q.Px, q.Py))).ToList());
                Assert.Equal(p.Items.OfType<PlotMarker>().Select(x => (x.DataX, x.DataY, x.Px, x.Py)).ToList(), d.Markers.Select(x => (x.DataX, x.DataY, x.Px, x.Py)).ToList());
                Assert.Equal(p.Items.OfType<PlotBar>().Select(x => (x.DataX, x.DataY, x.Px, x.Py, x.Width, x.Height)).ToList(),
                    d.Bars.Select(x => (x.DataX, x.DataY, x.Px, x.Py, x.Width, x.Height)).ToList());
                Assert.Equal(p.Items.OfType<PlotGap>().Select(x => (x.DataX, x.Px)).ToList(), d.Gaps.Select(x => (x.DataX, x.Px)).ToList());
            }
        }
    }

    [Fact]
    public void The_result_and_tree_dtos_carry_the_study_result_unchanged()
    {
        WorkbenchProject p = WorkbenchProject.WithDefaultStudies("case9", Case("case9"));
        WorkbenchResult r = p.Run("power-flow");
        WorkbenchResultDto dto = WorkbenchResultDto.From(r);
        Assert.Equal(r.Status, dto.Status);
        Assert.Equal(r.Tables.Select(t => t.Id), dto.Tables.Select(t => t.Id));
        Assert.Equal(r.Table("buses")!.Rows.Select(row => row.Select(c => c.Text).ToList()), dto.Tables.Single(t => t.Id == "buses").Rows.Select(row => row.Select(c => c.Text).ToList()));
        Assert.Equal(r.Plots.Select(x => x.Spec.Sha256()), dto.Plots.Select(x => x.Sha256));
        WorkbenchNodeDto tree = WorkbenchNodeDto.From(p.Tree());
        Assert.Equal("case9", tree.Title);
        Assert.Contains(tree.Children.Single(c => c.Id == "studies").Children, s => s.Id == "study/power-flow" && s.Detail == "CONVERGED");
    }

    // ------------------------------------------------------------------ the route

    [Fact]
    public async Task The_workbench_route_serves_the_model_browser_studies_and_report_for_a_model()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/workbench/case9", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("WORKBENCH", html, StringComparison.Ordinal);
        Assert.Contains("MODEL BROWSER", html, StringComparison.Ordinal);
        Assert.Contains("Demand P [MW]", html, StringComparison.Ordinal);
        Assert.Contains("STUDIES", html, StringComparison.Ordinal);
        Assert.Contains("RUN POWER-FLOW", html, StringComparison.Ordinal);
        Assert.Contains("REPORT", html, StringComparison.Ordinal);
        // case9 has 9 buses: the table carries one body row per bus in the prerendered page.
        GridModel m = Case("case9");
        Assert.Equal(m.Buses.Count, CountOf(html, "<tr><td"));
    }

    private static int CountOf(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task An_unknown_case_is_said_on_the_page_not_a_crash()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        string html = await c.GetStringAsync(new Uri("/workbench/no-such-model", UriKind.Relative));
        Assert.Contains("Unknown case", html, StringComparison.Ordinal);
        Assert.DoesNotContain("MODEL BROWSER", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/workbench/case9")]
    [InlineData("/workbench/gb-full")]
    [InlineData("/Workbench/case9")]
    public async Task A_replay_only_host_refuses_the_workbench_route(string path)
    {
        await using var f = Factory(("GridSim:ReplayOnly", "true"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("MODEL BROWSER", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dashboard_tab_strip_is_unchanged_by_the_workbench()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        string html = await c.GetStringAsync(new Uri("/dashboard/case9", UriKind.Relative));
        Assert.Contains("AUDIT LOG", html, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKBENCH", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_denied_private_model_is_a_card_message_not_an_unhandled_exception_on_the_workbench()
    {
        // The workbench page's load catch uses AccessControl.IsPageLoadRefusal; a private model (results/, gda-out/) opened
        // below the listing band throws AccessDeniedException from Require, which must be shown on the NOT AVAILABLE card.
        var access = new GridSim.Web.Site.AccessControl(GridSim.Core.Security.OperationRole.Viewer, false, GridSim.Web.Site.AccessDenialAudit.None);
        GridSim.Core.Security.AccessDeniedException ex = Assert.Throws<GridSim.Core.Security.AccessDeniedException>(() =>
            access.Require(access.IdentityFor(null, null), GridSim.Web.Model.CaseCatalog.PrivateListingRole, GridSim.Web.Site.AccessControl.DashboardPrivateOperation));
        Assert.True(GridSim.Web.Site.AccessControl.IsPageLoadRefusal(ex));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.True(GridSim.Web.Site.AccessControl.IsPageLoadRefusal(new InvalidOperationException("x")));
        Assert.False(GridSim.Web.Site.AccessControl.IsPageLoadRefusal(new FormatException()));
    }
}
