using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Ui.Hosting;
using GridSim.Ui.Presentation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme slice 1.8: the GDA bridge of the Avalonia app (GridSim.Ui.Hosting.BridgeChoices and BridgeSession)
/// against the WPF window's bridge code, its oracle. The WPF window keeps its own copy (it is pinned line by line by the
/// WPF source-contract tests), and this project cannot reference the Windows-only WPF assembly, so the WPF source is
/// read here as text, as UiHeaderTransportParityTests reads it:
/// <list type="bullet">
/// <item><description>the ZONE, FROM and SRC choices, the stream rate, the feasibility probe, the default look-back and
/// the three status-pill colours are the WPF values, entry for entry and in order;</description></item>
/// <item><description>every text the WPF bridge methods write (pill, event log, alarms, menu items, query titles and
/// parameters) appears word for word in the shared session, so a text cannot drift in one app alone;</description></item>
/// <item><description>the trip-by-name refusal gives the WPF answers;</description></item>
/// <item><description>the Avalonia transport bar's bridge pickers keep the WPF texts, widths, margins and tips, and show
/// only in bridge mode.</description></item>
/// </list>
/// </summary>
public sealed class UiBridgeParityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

    private static string WpfCode() => Read("src", "GridSim.Wpf", "MainWindow.xaml.cs");

    private static string SessionCode() => Read("src", "GridSim.Ui", "Hosting", "BridgeSession.cs");

    private static string ChoicesCode() => Read("src", "GridSim.Ui", "Hosting", "BridgeChoices.cs");

    // The text of the WPF member that starts at signature: a block body by brace count, or an expression body up to its
    // closing semicolon.
    private static string Member(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"WPF MainWindow.xaml.cs has no {signature}");
        int brace = code.IndexOf('{', at), arrow = code.IndexOf("=>", at, StringComparison.Ordinal);
        if (arrow >= 0 && (brace < 0 || arrow < brace))
            return code[at..(code.IndexOf(";\n", arrow, StringComparison.Ordinal) + 1)];
        int depth = 0;
        for (int i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[at..(i + 1)];
        }
        throw new InvalidOperationException($"unbalanced braces after {signature}");
    }

    // Every string literal of a piece of C#, comments removed first (a quoted word in a comment is not a text).
    private static List<string> Literals(string code)
    {
        string noComments = Regex.Replace(code, @"//[^\n]*", "");
        return Regex.Matches(noComments, @"\$?""(?:[^""\\\n]|\\.)*""").Select(m => m.Value).ToList();
    }

    [Fact]
    public void The_bridge_choices_and_constants_are_the_WPF_values_in_the_WPF_order()
    {
        string wpf = WpfCode();
        // ZONE: new("Label", "case") rows of the BridgeZones array.
        string zones = Member(wpf, "private static readonly BridgeZone[] BridgeZones =");
        var wpfZones = Regex.Matches(zones, @"new\(""([^""]*)"", ""([^""]*)""\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(8, wpfZones.Count);
        Assert.Equal(wpfZones, BridgeChoices.Zones.Select(z => (z.Label, z.Case)).ToList());
        // FROM: new("Label", TimeSpan.FromDays(n)).
        string starts = Member(wpf, "private static readonly BridgeStart[] BridgeStarts =");
        var wpfStarts = Regex.Matches(starts, @"new\(""([^""]*)"", TimeSpan\.FromDays\((\d+)\)\)")
            .Select(m => (m.Groups[1].Value, TimeSpan.FromDays(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))).ToList();
        Assert.Equal(6, wpfStarts.Count);
        Assert.Equal(wpfStarts, BridgeChoices.Starts.Select(s => (s.Label, s.Back)).ToList());
        // SRC.
        string sources = Member(wpf, "private static readonly BridgeSourceChoice[] BridgeSources =");
        var wpfSources = Regex.Matches(sources, @"new\(""([^""]*)"", ""([^""]*)""\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(3, wpfSources.Count);
        Assert.Equal(wpfSources, BridgeChoices.Sources.Select(s => (s.Label, s.Source)).ToList());

        // The constants.
        Assert.Contains("private const double BridgeStreamFps = 2;", wpf, StringComparison.Ordinal);
        Assert.Equal(2.0, BridgeChoices.StreamFps);
        Assert.Contains("private const double FeasibilityProbeMw = 500.0;", wpf, StringComparison.Ordinal);
        Assert.Equal(500.0, BridgeChoices.FeasibilityProbeMw);
        Assert.Contains("private static readonly TimeSpan DefaultBridgeBack = TimeSpan.FromDays(9);", wpf, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromDays(9), BridgeChoices.DefaultBack);
        Assert.Equal(BridgeChoices.Starts[0].Back, BridgeChoices.DefaultBack);

        // The pill colours: the same ramp at the same point (WPF Palette.X(v) is the frozen brush of ColourRamps.X(v)).
        var brushes = Regex.Matches(wpf, @"private static readonly SolidColorBrush (Bridge\w+Brush) = Palette\.Frozen\(Palette\.(\w+)\(([\d.]+)\)\);")
            .ToDictionary(m => m.Groups[1].Value, m => (m.Groups[2].Value, double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
        Assert.Equal(3, brushes.Count);
        UiColour Ramp((string Ramp, double At) b) => b.Ramp switch
        {
            "Voltage" => ColourRamps.Voltage(b.At),
            "Loading" => ColourRamps.Loading(b.At),
            _ => throw new InvalidOperationException("unexpected ramp " + b.Ramp),
        };
        Assert.Equal(Ramp(brushes["BridgeOkBrush"]), BridgeChoices.OkColour);
        Assert.Equal(Ramp(brushes["BridgeWarnBrush"]), BridgeChoices.WarnColour);
        Assert.Equal(Ramp(brushes["BridgeBadBrush"]), BridgeChoices.BadColour);
    }

    [Fact]
    public void Every_text_the_WPF_bridge_code_writes_is_written_word_for_word_by_the_shared_session()
    {
        // The transport texts (" PAUSE", " PLAY", the engine text "bridge") are the front end's, in the run view model;
        // every other text is the shared session's.
        string wpf = WpfCode(), choices = ChoicesCode();
        string session = SessionCode() + "\n" + Read("src", "GridSim.App", "Run", "RunViewModel.cs");
        string[] members =
        {
            "private async void LoadBridge(ModelSource s)",
            "private async void ApplyBridgeStream()",
            "private void SetBridgePaused(bool pause)",
            "private void ShowBridgeReconnectMenu()",
            "private void ShowBridgeContextMenu(System.Windows.Point screenPt)",
            "private void RunFeasibility(int busId, string busName)",
            "private void RunTripNearest(int busId, string busName)",
            "private void RunScenario(string trip, string label)",
            "private async void RunQuery(string op, object @params, string title)",
            "private SimFrame ToWhatIfFrame(SolveSnapshot snap)",
            "private void ReturnToLive()",
        };
        // The few places the shared code names a value differently (a parameter for a WPF field, the shared constant
        // for the WPF one, the dashboard feed for the WPF view model); the text around them is unchanged.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Test changed 2026-10-20 (bridge endpoint redaction, both apps): the connect line writes the endpoint through
            // BridgeProtocol.DisplayEndpoint in both apps; the pair still pins the two lines equal apart from the names.
            ["$\"Connected to bridge {BridgeProtocol.DisplayEndpoint(s.Bridge!)}  -  streaming {_bridgeCase} ({_bridgeSource}) from {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z\""] =
                "$\"Connected to bridge {BridgeProtocol.DisplayEndpoint(endpoint)}  -  streaming {caseName} ({source}) from {at:yyyy-MM-dd HH:mm}Z\"",
            ["$\"Feasibility: +{FeasibilityProbeMw:F0} MW here  ({bn})\""] = "$\"Feasibility: +{BridgeChoices.FeasibilityProbeMw:F0} MW here  ({busName})\"",
            ["$\"Feasibility  *  +{FeasibilityProbeMw:F0} MW at {busName}\""] = "$\"Feasibility  *  +{BridgeChoices.FeasibilityProbeMw:F0} MW at {busName}\"",
            ["$\"WHAT-IF  *  {_vm.Status.Trim()}\""] = "$\"WHAT-IF  *  {Feed.Status.Trim()}\"",
        };
        int checkedCount = 0;
        foreach (string m in members)
            foreach (string literal in Literals(Member(wpf, m)))
            {
                string expected = renamed.TryGetValue(literal, out string? r) ? r : literal;
                Assert.True(session.Contains(expected, StringComparison.Ordinal),
                    $"The WPF text {literal} (in {m}) is not in BridgeSession.cs or the app's RunViewModel.cs as {expected}");
                checkedCount++;
            }
        Assert.True(checkedCount > 40, $"only {checkedCount} texts were found: the extraction is broken");

        // The trip-by-name refusal: every WPF text is in the shared rule.
        foreach (string literal in Literals(Member(wpf, "internal static string? BridgeTripRefusal(GridModel m, string? name)")))
            Assert.True(choices.Contains(literal, StringComparison.Ordinal), $"The WPF refusal text {literal} is not in BridgeChoices.cs");
    }

    [Fact]
    public void The_shared_trip_refusal_answers_as_the_WPF_rule()
    {
        // The toy network of BridgeProtocolTests: SlackGen, UnitA; L1-2, T2-3.
        GridModel m = BridgeProtocol.ReadTopology("""
        { "type": "topology", "model": { "name": "toy3", "baseMva": 100,
          "buses": [ { "id": 1, "type": "Slack", "baseKv": 400, "name": "Slack400" }, { "id": 2, "type": "Pv", "baseKv": 400, "name": "Gen400" },
                     { "id": 3, "type": "Pq", "baseKv": 132, "pd": 80, "qd": 20, "name": "Load132" } ],
          "generators": [ { "bus": 1, "pg": 0, "name": "SlackGen" }, { "bus": 2, "pg": 120, "vg": 1.02, "mBase": 200, "h": 4.0, "name": "UnitA", "type": "CCGT" } ],
          "branches": [ { "from": 1, "to": 2, "r": 0.004, "x": 0.04, "b": 0.02, "rateAMva": 500, "name": "L1-2" },
                        { "from": 2, "to": 3, "r": 0.006, "x": 0.08, "tap": 1.0, "rateAMva": 250, "name": "T2-3", "isTransformer": true } ] } }
        """).ToModel();
        Assert.Null(BridgeChoices.TripRefusal(m, "UnitA"));
        Assert.Null(BridgeChoices.TripRefusal(m, "l1-2"));   // case-insensitive, as the server matches
        Assert.Equal("it has no name, and the bridge selects the element to trip by name", BridgeChoices.TripRefusal(m, null));
        Assert.Equal("it has no name, and the bridge selects the element to trip by name", BridgeChoices.TripRefusal(m, "  "));
        Assert.Equal("its name matches 2 elements in this model, and the bridge selects by name, so it could trip a different one",
            BridgeChoices.TripRefusal(m, "-"));
        Assert.Equal("its name matches 0 elements in this model, and the bridge selects by name, so it could trip a different one",
            BridgeChoices.TripRefusal(m, "nowhere"));
        Assert.Throws<ArgumentNullException>(() => BridgeChoices.TripRefusal(null!, "x"));
    }

    [Fact]
    public void The_bridge_pickers_keep_the_WPF_texts_sizes_and_tips_and_show_only_in_bridge_mode()
    {
        XElement wpf = XDocument.Load(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml")).Root!;
        XElement ava = XDocument.Load(Path.Combine(Root(), "src", "GridSim.App", "MainWindow.axaml")).Root!;
        XElement Named(XElement root, string name) =>
            Assert.Single(root.Descendants(), e => (string?)e.Attribute(X + "Name") == name);

        (string Name, string[] Attrs)[] controls =
        {
            ("BridgeZoneLabel", new[] { "Text", "Margin" }),
            ("BridgeZonePicker", new[] { "Width", "Margin", "ToolTip" }),
            ("BridgeStartLabel", new[] { "Text", "Margin" }),
            ("BridgeStartPicker", new[] { "Width", "Margin", "ToolTip" }),
            ("BridgeSourceLabel", new[] { "Text", "Margin" }),
            ("BridgeSourcePicker", new[] { "Width", "Margin", "ToolTip" }),
        };
        foreach ((string name, string[] attrs) in controls)
        {
            XElement we = Named(wpf, name), ae = Named(ava, name);
            Assert.Equal(we.Name.LocalName, ae.Name.LocalName);
            foreach (string attr in attrs)
            {
                string? wv = (string?)we.Attribute(attr);
                string? av = (string?)ae.Attribute(attr == "ToolTip" ? "ToolTip.Tip" : attr);
                Assert.True(wv is not null, $"WPF {name} has no {attr}");
                Assert.True(wv == av, $"{attr}: WPF {name} has '{wv}', the Avalonia one has '{av}'");
            }
            // WPF: Collapsed until LoadBridge shows them; here: shown exactly while the feed is a bridge source.
            Assert.Equal("Collapsed", (string?)we.Attribute("Visibility"));
            Assert.Equal("{Binding Run.IsBridge}", (string?)ae.Attribute("IsVisible"));
        }
        // The pickers list the shared choices and bind the session's selection.
        Assert.Equal("{x:Static hosting:BridgeChoices.Zones}", (string?)Named(ava, "BridgeZonePicker").Attribute("ItemsSource"));
        Assert.Equal("{Binding Run.Bridge.SelectedZone}", (string?)Named(ava, "BridgeZonePicker").Attribute("SelectedItem"));
        Assert.Equal("{x:Static hosting:BridgeChoices.Starts}", (string?)Named(ava, "BridgeStartPicker").Attribute("ItemsSource"));
        Assert.Equal("{Binding Run.Bridge.SelectedStart}", (string?)Named(ava, "BridgeStartPicker").Attribute("SelectedItem"));
        Assert.Equal("{x:Static hosting:BridgeChoices.Sources}", (string?)Named(ava, "BridgeSourcePicker").Attribute("ItemsSource"));
        Assert.Equal("{Binding Run.Bridge.SelectedSource}", (string?)Named(ava, "BridgeSourcePicker").Attribute("SelectedItem"));
        // SPEED is disabled on a bridge feed, as WPF disables SpeedBtn in LoadBridge.
        Assert.Contains("SpeedBtn.IsEnabled = false;", Member(WpfCode(), "private async void LoadBridge(ModelSource s)"), StringComparison.Ordinal);
        Assert.Equal("{Binding Run.CanChooseSpeed}", (string?)Named(ava, "SpeedButton").Attribute("IsEnabled"));
    }
}
