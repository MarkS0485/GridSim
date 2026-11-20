using System.Net;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench.Sld;
using GridSim.Web.Projection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-18 on the web client: the DTO carries the Core diagram's geometry, colours and SVG unchanged (the parity half; the Avalonia
/// half is in the App tests, which compare their own view model to the same Core calls), and the /workbench/{case} route shows the
/// diagram section without touching the dashboard or the web project's references (Web references Core only).
/// </summary>
public sealed class WebSldTests
{
    private static GridModel Case(string name) =>
        MatpowerParser.ParseFile(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name + ".m"));

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-r418-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case30")]
    public void The_diagram_dto_is_the_core_geometry_colours_and_svg_unchanged(string name)
    {
        SldModel m = SldModel.From(Case(name));
        SldDiagram d = SldDiagrams.Build(m, SldLayoutMode.Auto);
        SldColourSet colours = SldColouring.Colour(m, d.Geometry, SldColourMode.VoltageLevel);
        SldDiagramDto dto = SldDiagramDto.From(d, colours, name);

        Assert.Equal(d.Mode.ToString(), dto.Mode);
        Assert.Equal(d.Geometry.Sha256(), dto.Sha256);
        Assert.Equal(SldExport.ToSvg(d.Geometry, colours, name), dto.Svg);
        Assert.Equal((d.Geometry.Bounds.Left, d.Geometry.Bounds.Top, d.Geometry.Bounds.Right, d.Geometry.Bounds.Bottom), (dto.Left, dto.Top, dto.Right, dto.Bottom));
        Assert.Equal(d.Geometry.Symbols.Count, dto.Symbols.Count);
        for (int i = 0; i < dto.Symbols.Count; i++)
        {
            SldSymbol s = d.Geometry.Symbols[i];
            SldSymbolDto x = dto.Symbols[i];
            Assert.Equal((s.Id, s.Kind.ToString(), s.Label, s.InService, s.BaseKv, colours.Symbols[i]), (x.Id, x.Kind, x.Label, x.InService, x.BaseKv, x.Colour));
            Assert.Equal((s.Box.Left, s.Box.Top, s.Box.Right, s.Box.Bottom), (x.Left, x.Top, x.Right, x.Bottom));
            Assert.Equal(s.Ports.Select(p => (p.Id, p.X, p.Y)), x.Ports.Select(p => (p.Id, p.X, p.Y)));
        }
        Assert.Equal(d.Geometry.Connections.Count, dto.Connections.Count);
        for (int i = 0; i < dto.Connections.Count; i++)
        {
            SldConnection c = d.Geometry.Connections[i];
            SldConnectionDto x = dto.Connections[i];
            Assert.Equal((c.Id, c.ElementId, c.FromPort, c.ToPort, colours.Connections[i]), (x.Id, x.ElementId, x.FromPort, x.ToPort, x.Colour));
            Assert.Equal(c.Points.Select(p => (p.X, p.Y)), x.Points.Select(p => (p.X, p.Y)));
        }
        Assert.Equal(colours.Legend.Select(l => (l.Label, l.Colour)), dto.Legend.Select(l => (l.Label, l.Colour)));
        Assert.Equal(d.Geometry.Crossings.Count, dto.Crossings);
        // Every object the model has is in the DTO exactly once (the bijection, seen from the web side).
        Assert.Equal(m.ExpectedIds().OrderBy(x => x, StringComparer.Ordinal), dto.Symbols.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void The_dto_of_a_diagram_placed_from_the_case_is_also_core_unchanged_and_says_it_can_move_buses()
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "uk"), dispatch: false);
        SldModel m = SldModel.From(uk);
        SldDiagram d = SldDiagrams.Build(m, SldLayoutMode.Case);   // uk carries real layout coordinates
        SldDiagramDto dto = SldDiagramDto.From(d, SldColouring.Colour(m, d.Geometry, SldColourMode.Plain));
        Assert.Equal("Case", dto.Mode);
        Assert.True(dto.CanMove);
        Assert.Equal(d.Geometry.Sha256(), dto.Sha256);
    }

    [Fact]
    public void The_web_project_references_core_only_and_the_diagram_component_uses_the_core_calls()
    {
        string root = CliProcess.Root();
        string proj = File.ReadAllText(Path.Combine(root, "src", "GridSim.Web", "GridSim.Web.csproj"));
        Assert.DoesNotContain("GridSim.Ui", proj, StringComparison.Ordinal);
        Assert.DoesNotContain("GridSim.App", proj, StringComparison.Ordinal);
        string tab = File.ReadAllText(Path.Combine(root, "src", "GridSim.Web", "Components", "Tabs", "SldTab.razor"));
        Assert.Contains("SldDiagrams.Build", tab, StringComparison.Ordinal);
        Assert.Contains("SldDiagramDto.From", tab, StringComparison.Ordinal);
        Assert.Contains("SldCaseDocument.OpenWorkingCopy", tab, StringComparison.Ordinal);   // edits go to a private working copy
        Assert.DoesNotContain("SldCaseDocument.Open(", tab, StringComparison.Ordinal);       // never the shared case itself
        string page = File.ReadAllText(Path.Combine(root, "src", "GridSim.Web", "Components", "Pages", "Workbench.razor"));
        Assert.Contains("<SldTab", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_workbench_route_shows_the_diagram_of_the_model()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string html = await c.GetStringAsync(new Uri("/workbench/case9", UriKind.Relative));
        Assert.Contains("SINGLE-LINE DIAGRAM", html, StringComparison.Ordinal);
        Assert.Contains("<svg ", html, StringComparison.Ordinal);
        GridModel m = Case("case9");
        foreach (Bus b in m.Buses) Assert.Contains($"data-id=\"bus/{b.Id}\"", html, StringComparison.Ordinal);
        for (int i = 0; i < m.Branches.Count; i++) Assert.Contains($"data-id=\"branch/{i}\"", html, StringComparison.Ordinal);
        Assert.Contains("Read-only diagram", html, StringComparison.Ordinal);   // a MATPOWER case has no case files to edit

        // A JSON case folder is offered for editing (on a private working copy) unless the licence refuses the copy.
        string uk = await c.GetStringAsync(new Uri("/workbench/gb-reduced", UriKind.Relative));
        Assert.Contains("SINGLE-LINE DIAGRAM", uk, StringComparison.Ordinal);
        Assert.True(uk.Contains("Editable on a private working copy", StringComparison.Ordinal) || uk.Contains("Read-only: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dashboard_still_has_no_diagram_tab()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string html = await c.GetStringAsync(new Uri("/dashboard/case9", UriKind.Relative));
        Assert.DoesNotContain("SINGLE-LINE DIAGRAM", html, StringComparison.Ordinal);
    }
}
