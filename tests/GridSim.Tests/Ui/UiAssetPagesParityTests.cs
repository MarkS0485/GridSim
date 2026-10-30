using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Visuals;
using Xunit;
using Old = GridSim.Tests.Ui.OldAssets;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS A2: the page logic of the WPF ASSETS tab (the explorer, its landing page, the seven fleet pages and
/// the five unit pages) moved from src/GridSim.Wpf/Views/Assets into the platform-neutral GridSim.Ui.Assets.Pages, line
/// for line. That is an orchestration-only move, so every value each page shows must be exactly what the WPF page showed
/// before.
///
/// <para>How it is checked. A verbatim copy of the old WPF code (the OldAssets folder, exactly as at commit 6e729a66,
/// pinned by a SHA-256 of its text) and the new neutral explorer are driven side by side: the same SimHost frames (case9,
/// the GB reduced case and gb-synthetic, with the dashboard's own clock), the same navigation (every landing card, the
/// first and last row of every list, every band and kV chip, every cross-link a unit page offers, BACK, ASSETS), the
/// same model swaps (a page left open while the frames come from another case: the "not in the current model ... stale"
/// messages, the pruning, the kV chips after a swap), voltage and loading excursions, the Active gate, the GDA per-asset
/// payload and more than 96 frames on every unit page (the history cap). After every step the explorer, its current page
/// and its landing page are compared property by property: strings ordinal, doubles by their 64 bits, colour tokens
/// against the colour of the brush the old code chose, bools against the old Visibility, every row of every list the
/// same way (its internal diagram numbers too), and the type of every page and row. A property added to either side later
/// fails the coverage check until it is compared here. Run under the machine culture and under a decimal-comma culture.
/// A self-test shows the comparison is not vacuous (one bit of one double, one byte of one colour, one character).</para>
///
/// <para>Test changed 2026-10-20 (ASSETS fixes, Mark approved fixing the quirks as their own slices): the oracle stays
/// verbatim, and each fixed quirk is one named exception that checks its own exact fixed value and excuses nothing else
/// (S4: the substation fleet's kV chips after a model swap, FixedS4ChipsAfterASwap; S12: the transformer fleet's
/// "OLTC FITTED", FixedS12OltcFitted; S5: the home GENERATOR PHASORS card's angle spread, FixedS5HomeAngleSpread; S10:
/// the circuit voltage bands off the GB ladder, FixedS10CircuitBands; S3: the substation fleet's voltage table, first-wins
/// for a repeated bus id, FixedS3FirstWins; S7: the transformer PROTECTION panel fed from the case's
/// protections, FixedS7ProtectionPanel, with its new tag flag checked by FixedS7ProtectionTag).</para>
/// </summary>
public sealed class UiAssetPagesParityTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // The copies are what they claim to be
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The SHA-256 of each original file (commit 6e729a66) without its using, namespace and "// TEST" lines,
    /// lines joined by a line feed. Worked out from git when the oracle was copied.</summary>
    private static readonly Dictionary<string, string> OldHashes = new(StringComparer.Ordinal)
    {
        ["AssetPages"] = "6ef35132a774e2c302631e9620d5eec42451f6c6b1f3251ce0563e84ee622cdb",
        ["GeneratorPages"] = "9b7fa9b23a882ad06c40a28b364b18f6c3e80fc19508004a6f22fad0c4940f34",
        ["TransformerPages"] = "c6dda7ab9148bedd9d969453df0b18af662b80a1650c996ec404951274853f3a",
        ["CircuitPages"] = "1472bfc6cef72a04a0c574ac5f81592319051e876421e5e2db08b1960df8c430",
        ["SubstationPages"] = "c368ec1d65123fa340924aaf50050fc9a73453a7f7416be255d61418fdac1e86",
    };

    /// <summary>A source file's lines without its using and namespace lines and the copies' "// TEST" header.</summary>
    private static string Body(string path)
    {
        Assert.True(File.Exists(path), $"source file not found: {path}");
        IEnumerable<string> kept = File.ReadAllLines(path)
            .Where(l => !l.StartsWith("using ", StringComparison.Ordinal)
                     && !l.StartsWith("namespace ", StringComparison.Ordinal)
                     && !l.StartsWith("// TEST ", StringComparison.Ordinal));
        return string.Join("\n", kept);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void The_old_copies_are_the_original_WPF_files_verbatim()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        foreach ((string name, string hash) in OldHashes)
        {
            string copy = Path.Combine(root, "tests", "GridSim.Tests", "Ui", "OldAssets", name + ".cs");
            Assert.True(hash == Sha256(Body(copy)), $"{name}: the oracle copy is not the original file's text");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The feed
    // ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Cases => new()
    {
        { "case9", "" },            // "" = the machine culture the test host runs in
        { "case9", "de-DE" },       // a decimal comma and a full-stop thousands separator
        { "uk", "" },
        { "uk", "de-DE" },
        { "gb-synthetic", "" },     // 2,227 buses: past every row cap (200 rows, 64 phasor tiles)
    };

    private static ModelSource SourceOf(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "data", name);
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource(name, dir);
    }

    /// <summary>A live host over a bundled case, and the dashboard that stamps its frames with the clock.</summary>
    private sealed class Feed
    {
        private readonly SimHost _host = new();
        private readonly DashboardModel _dash = new();

        public Feed(string name)
        {
            LiveLoadResult loaded = _host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
            Assert.True(loaded.Loaded, loaded.Refusal);
        }

        /// <summary>The next live frame, and the clock the dashboard shows for it (the explorer is fed the dashboard's Clock).</summary>
        public (SimFrame Frame, string Clock) Next()
        {
            HostFrame hf = Assert.IsType<HostFrame>(_host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            _dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            return (hf.Frame!, _dash.Clock);
        }
    }

    /// <summary>A copy of a frame whose result has every voltage and every loading scaled (excursions both pages grade).</summary>
    private static SimFrame Scaled(SimFrame f, double vmScale, double loadingScale)
    {
        PowerFlowResult r = f.Result;
        var copy = new PowerFlowResult
        {
            Converged = r.Converged,
            Iterations = r.Iterations,
            MaxMismatch = r.MaxMismatch,
            BusIds = r.BusIds,
            Vm = r.Vm.Select(v => v * vmScale).ToArray(),
            VaDeg = r.VaDeg.Select(a => a * loadingScale).ToArray(),
            PInjMw = r.PInjMw,
            QInjMvar = r.QInjMvar.Select(q => -q * loadingScale).ToArray(),
            TotalLossMw = r.TotalLossMw,
            TotalLossMvar = r.TotalLossMvar,
            LineFlows = r.LineFlows.Select(l => l with
            {
                LoadingPercent = l.LoadingPercent * loadingScale,
                PFromMw = -l.PFromMw * loadingScale,
                PToMw = -l.PToMw * loadingScale,
                QLossMvar = -l.QLossMvar,
            }).ToList(),
        };
        return f with { Result = copy };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The twin: the old WPF explorer and the new neutral one, driven by the same calls
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class Twin
    {
        public readonly Old.AssetExplorerVm OldX = new();
        public readonly AssetExplorerModel NewX = new();
        public int Steps;

        /// <summary>The old page type names compared at least once (the walk's coverage).</summary>
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);

        /// <summary>The model of the last frame fed to both explorers (null before the first): the named ASSETS fix
        /// exceptions (S4, S12) work out the fixed values from it. Test changed 2026-10-20 (ASSETS fixes S4 and S12).</summary>
        public GridModel? LastModel;

        /// <summary>The last frame fed to both explorers (null before the first). Test changed 2026-10-20 (ASSETS fix
        /// S5).</summary>
        public SimFrame? LastFrame;

        /// <summary>
        /// The frame the landing page last refreshed on (null while it never has). Test changed 2026-10-20 (ASSETS fix
        /// S5, Mark approved fixing the quirks as their own slices): the S5 exception works out the home phasor card's
        /// old and fixed values from it. The explorer refreshes its landing page only while the landing page is the one
        /// on screen and the tab is active (a frame then, GoHome, a BACK onto it, or the tab shown again), each time with
        /// the explorer's last frame, which is always <see cref="LastFrame"/> (every frame goes through
        /// <see cref="Update"/>). So after every step: landing page on screen and active means it refreshed on
        /// LastFrame; otherwise it still shows the frame it had. If this tracking were ever wrong the exception would
        /// find the old card is not the old rule's value for that frame, excuse nothing, and the exact comparison would
        /// fail loud.
        /// </summary>
        public SimFrame? HomeFrame;

        public void Update(SimFrame f, string clock, string at)
        {
            OldX.Update(f, clock);
            NewX.Update(f, clock);
            LastModel = f.Model;
            LastFrame = f;
            Same(at);
        }

        public void Same(string at)
        {
            Steps++;
            Seen.Add(OldX.Current.GetType().Name);
            // Test changed 2026-10-20 (ASSETS fix S5): the frame the landing page now shows (see HomeFrame).
            if (ReferenceEquals(NewX.Current, NewX.Home) && NewX.Active && LastFrame is not null) HomeFrame = LastFrame;
            // The frame's model is handed to the comparison only for this one call (the S4 / S12 exceptions read it, and
            // the S5 exception reads the landing page's frame); every other caller of AssertSame compares with no
            // exception at all.
            FixModel = LastModel;
            FixHomeFrame = HomeFrame;
            FixFrame = LastFrame;   // Test changed 2026-10-20 (ASSETS fix S3): the S3 exception reads the last frame's result
            try { AssertSame(OldX, NewX, at); }
            finally { FixModel = null; FixHomeFrame = null; FixFrame = null; }
        }

        public void GoHome(string at) { OldX.GoHome(); NewX.GoHome(); Same(at); }
        public void Back(string at) { OldX.Back(); NewX.Back(); Same(at); }

        public void SetActive(bool on, string at) { OldX.Active = on; NewX.Active = on; Same(at); }

        /// <summary>Click a row or card on both sides: the old ICommand <paramref name="oldCommand"/>, the new action
        /// <paramref name="newAction"/>.</summary>
        public void Click(object oldRow, object newRow, string oldCommand, string newAction, string at)
        {
            var cmd = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(oldRow.GetType().GetProperty(oldCommand)!.GetValue(oldRow));
            var act = Assert.IsAssignableFrom<Action>(newRow.GetType().GetProperty(newAction)!.GetValue(newRow));
            cmd.Execute(null);
            act();
            Same(at);
        }

        /// <summary>A unit page's cross-link on both sides: the old parameter command with its bound id property, the
        /// new method with the same property.</summary>
        public void Link(string oldCommand, string newMethod, string idProperty, string at)
        {
            object oldPage = OldX.Current, newPage = NewX.Current;
            object oldId = oldPage.GetType().GetProperty(idProperty)!.GetValue(oldPage)!;
            object newId = newPage.GetType().GetProperty(idProperty)!.GetValue(newPage)!;
            var cmd = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(oldPage.GetType().GetProperty(oldCommand)!.GetValue(oldPage));
            MethodInfo method = newPage.GetType().GetMethod(newMethod)!;
            Assert.NotNull(method);
            cmd.Execute(oldId);
            method.Invoke(newPage, new[] { newId });
            Same(at);
        }

        public void Do(Action<Old.AssetExplorerVm> old, Action<AssetExplorerModel> neu, string at)
        {
            old(OldX);
            neu(NewX);
            Same(at);
        }
    }

    /// <summary>The cross-links each unit page offers: (old command, new method, id property).</summary>
    private static readonly Dictionary<string, (string Cmd, string Method, string Id)[]> Links = new(StringComparer.Ordinal)
    {
        ["InterconnectorDetailVm"] = new[] { ("OpenLanding", "OpenLandingSubstation", "LandingBusId") },
        ["GeneratorDetailVm"] = new[] { ("OpenBus", "OpenBusSubstation", "BusId") },
        ["TransformerDetailVm"] = new[] { ("OpenHv", "OpenHvSubstation", "HvBusId"), ("OpenLv", "OpenLvSubstation", "LvBusId"), ("OpenOltcBus", "OpenOltcBusSubstation", "OltcBusId") },
        ["CircuitDetailVm"] = new[] { ("OpenIn", "OpenInSubstation", "VInBusId"), ("OpenOut", "OpenOutSubstation", "VOutBusId") },
    };

    /// <summary>
    /// Walk the page on screen: every filter chip (each then a frame), the first and last row of every list that opens
    /// something (into it, deeper while <paramref name="depth"/> allows, then BACK), and every cross-link of a unit page.
    /// </summary>
    private static void Explore(Twin t, SimFrame f, string clock, int depth, string at)
    {
        t.Update(f, clock, $"{at}: on screen");
        if (depth <= 0) return;
        object oldPage = t.OldX.Current, newPage = t.NewX.Current;

        // Filter chips: the circuit fleet's band chips (Select) and the substation fleet's kV chips (Toggle).
        foreach ((string list, string cmd, string act) in new[] { ("Bands", "Select", "OnSelect"), ("Filters", "Toggle", "OnToggle") })
        {
            if (newPage.GetType().GetProperty(list) is not { } lp || lp.PropertyType == typeof(string)) continue;
            int n = ((IList)lp.GetValue(newPage)!).Count;
            for (int k = 0; k < n; k++)
            {
                var oldChips = (IList)oldPage.GetType().GetProperty(list)!.GetValue(oldPage)!;
                var newChips = (IList)lp.GetValue(newPage)!;
                if (k >= newChips.Count) break;   // a chip row rebuilt by a pick (both sides compared equal just before)
                t.Click(oldChips[k]!, newChips[k]!, cmd, act, $"{at}: {list} chip {k}");
                t.Update(f, clock, $"{at}: {list} chip {k} then a frame");
            }
            if (n > 0)
            {
                var oldChips = (IList)oldPage.GetType().GetProperty(list)!.GetValue(oldPage)!;
                var newChips = (IList)lp.GetValue(newPage)!;
                t.Click(oldChips[0]!, newChips[0]!, cmd, act, $"{at}: {list} back to ALL");
            }
        }

        // Rows and cards that open a page: the first and the last of each list.
        foreach (PropertyInfo lp in newPage.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(IList).IsAssignableFrom(lp.PropertyType)) continue;
            Type? rowType = lp.PropertyType.IsGenericType ? lp.PropertyType.GetGenericArguments()[0] : null;
            if (rowType?.GetProperty("OnOpen") is null) continue;
            int count = ((IList)lp.GetValue(newPage)!).Count;
            foreach (int k in new[] { 0, count - 1 }.Where(k => k >= 0 && k < count).Distinct())
            {
                var oldRows = (IList)oldPage.GetType().GetProperty(lp.Name)!.GetValue(oldPage)!;
                var newRows = (IList)lp.GetValue(newPage)!;
                t.Click(oldRows[k]!, newRows[k]!, "Open", "OnOpen", $"{at}: {lp.Name}[{k}] opened");
                Explore(t, f, clock, depth - 1, $"{at} > {lp.Name}[{k}]");
                t.Back($"{at}: back from {lp.Name}[{k}]");
            }
        }

        // A unit page's cross-links (each opens a substation page).
        if (Links.TryGetValue(oldPage.GetType().Name, out (string Cmd, string Method, string Id)[]? links))
        {
            foreach ((string cmd, string method, string id) in links)
            {
                t.Link(cmd, method, id, $"{at}: {cmd}");
                Explore(t, f, clock, depth - 1, $"{at} > {cmd}");
                t.Back($"{at}: back from {cmd}");
            }
        }
    }

    private static T WithCulture<T>(string culture, Func<T> body)
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            if (culture.Length > 0) CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return body();
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_page_equals_the_old_wpf_page_frame_for_frame_through_every_route(string name, string culture)
    {
        S12Hits = 0;   // Test changed 2026-10-20 (ASSETS fix S12): counted by FixedS12OltcFitted, asserted below
        S5Hits = 0;    // Test changed 2026-10-20 (ASSETS fix S5): counted by FixedS5HomeAngleSpread, asserted below
        S10ChipHits = S10RowHits = S10SubHits = 0;   // Test changed 2026-10-20 (ASSETS fix S10): FixedS10CircuitBands
        S3Hits = 0;    // Test changed 2026-10-20 (ASSETS fix S3): counted by FixedS3FirstWins, asserted below
        S7Hits = 0;    // Test changed 2026-10-20 (ASSETS fix S7): counted by FixedS7ProtectionPanel, asserted below
        Twin walked = WithCulture(culture, () =>
        {
            var feed = new Feed(name);
            var t = new Twin();
            t.Same($"{name} before the first frame");
            (SimFrame f, string clock) = feed.Next();
            t.Update(f, clock, $"{name} tick 1 (home)");

            // Every landing card, each page walked: rows, chips, unit pages and their links (two levels deep).
            var oldCards = ((Old.AssetHomeVm)t.OldX.Home).Classes;
            for (int c = 0; c < oldCards.Count; c++)
            {
                t.GoHome($"{name} home before card {c}");
                var newCards = (IList)t.NewX.Home.GetType().GetProperty("Classes")!.GetValue(t.NewX.Home)!;
                t.Click(oldCards[c], newCards[c]!, "Open", "OnOpen", $"{name} card {c} ({oldCards[c].Title})");
                (f, clock) = feed.Next();
                Explore(t, f, clock, 2, $"{name} {oldCards[c].Title}");

                // The same page through voltage and loading excursions (out of band, overloaded, reversed flows, OLTCs
                // HIGH / LOW, Q signs flipped), then a plain frame again.
                t.Update(Scaled(f, 1.08, 1.7), clock, $"{name} {oldCards[c].Title} high voltage, heavy loading");
                t.Update(Scaled(f, 0.9, 0.2), clock, $"{name} {oldCards[c].Title} low voltage, light loading");
                t.Update(f, clock, $"{name} {oldCards[c].Title} recovered");
            }
            t.GoHome($"{name} home at the end");
            t.Update(Scaled(f, 1.08, 1.7), clock, $"{name} home with excursions");
            t.Update(Scaled(f, 0.95, 1.0), clock, $"{name} home with low voltage");
            return t;
        });
        Assert.True(walked.Steps > 100, $"{name}: only {walked.Steps} comparisons ran");
        // Test changed 2026-10-20 (ASSETS fix S12): the transformer fleet's "OLTC FITTED" went through its named
        // exception, which checks both the old and the fixed value exactly.
        Assert.True(S12Hits > 0, $"{name}: the transformer fleet's OLTC FITTED was never compared");
        // Test changed 2026-10-20 (ASSETS fix S5): the home phasor card's spread went through its named exception, which
        // checks both the old (all buses) and the fixed (generator buses) value exactly.
        Assert.True(S5Hits > 0, $"{name}: the home phasor card's angle spread was never compared");
        // Test changed 2026-10-20 (ASSETS fix S10): the circuit bands. case9's circuits all sit on 345 kV buses, which the
        // old code snapped to "400 kV" and the fix bands at "345 kV", so there the band chips, the fleet rows' bands and
        // the circuit page's subtitle each went through the named exception (old and fixed values checked exactly). The
        // GB cases' circuits all sit on the GB ladder, so there the exception never applied: every band was compared
        // exactly against the old code, unchanged.
        if (name == "case9")
        {
            Assert.True(S10ChipHits > 0, $"{name}: the circuit band chips were never compared as fixed");
            Assert.True(S10RowHits > 0, $"{name}: the circuit rows' bands were never compared as fixed");
            Assert.True(S10SubHits > 0, $"{name}: the circuit page's subtitle was never compared as fixed");
        }
        else
        {
            Assert.True(S10ChipHits + S10RowHits + S10SubHits == 0,
                $"{name}: a GB case's circuit band changed ({S10ChipHits} chip rows, {S10RowHits} row values, {S10SubHits} subtitles)");
        }
        // Test changed 2026-10-20 (ASSETS fix S3): no bundled case has two buses sharing an id (the loaders refuse it), so
        // first-wins and last-wins give the same voltage table and the S3 exception never applied: every substation fleet
        // value was compared exactly against the old code, unchanged. The exception is shown to fire on a built case with a
        // repeated bus id (A_repeated_bus_id_reads_the_first_entry_on_the_substation_fleet).
        Assert.True(S3Hits == 0, $"{name}: the S3 exception applied {S3Hits} times on a bundled case (a repeated bus id?)");
        // Test changed 2026-10-20 (ASSETS fix S7): case9 and uk carry no protections, so there the PROTECTION panel never
        // went through its exception (every panel compared exactly, demo rows and all). gb-synthetic carries two devices,
        // both at bus 1: the walk may or may not open the one transformer at bus 1 (it opens the first and last rows of
        // each list); The_transformer_protection_panel_shows_the_cases_settings_where_it_has_them opens it on purpose.
        if (name is "case9" or "uk") Assert.True(S7Hits == 0, $"{name}: the S7 exception applied {S7Hits} times on a case with no protections");

        // The walk reached every page kind the case has: the landing page, the seven fleet pages, and each unit page
        // whose kind of unit the case carries.
        GridModel m = new Feed(name).Next().Frame.Model;
        var want = new List<string>
        {
            "AssetHomeVm", "GeneratorFleetVm", "GeneratorPhasorFleetVm", "InterconnectorFleetVm", "TransformerFleetVm",
            "CircuitFleetVm", "SubstationFleetVm", "TapChangerFleetVm", "SubstationDetailVm", "GeneratorDetailVm",
        };
        if (m.Generators.Any(g => UiFuels.Classify(g.Type).Category == GridSim.Core.Presentation.FuelCategory.Interconnector)) want.Add("InterconnectorDetailVm");
        if (m.Branches.Any(b => b.IsTransformer)) want.Add("TransformerDetailVm");
        if (m.Branches.Any(b => !b.IsTransformer)) want.Add("CircuitDetailVm");
        foreach (string kind in want) Assert.True(walked.Seen.Contains(kind), $"{name}: the walk never compared a {kind}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // History, the Active gate, the explorer's own routes, the GDA payload
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Open each unit page kind of the case (the first unit of each), on both sides.</summary>
    private static IEnumerable<(string What, Action<Old.AssetExplorerVm> Old, Action<AssetExplorerModel> New)> UnitPages(GridModel m)
    {
        int gen = Enumerable.Range(0, m.Generators.Count).FirstOrDefault(i => m.Generators[i].Type != "interconnector", -1);
        int ic = Enumerable.Range(0, m.Generators.Count).FirstOrDefault(i => m.Generators[i].Type == "interconnector", -1);
        int tx = Enumerable.Range(0, m.Branches.Count).FirstOrDefault(i => m.Branches[i].IsTransformer, -1);
        int line = Enumerable.Range(0, m.Branches.Count).FirstOrDefault(i => !m.Branches[i].IsTransformer, -1);
        int bus = m.Buses[0].Id;
        if (gen >= 0)
        {
            Generator g = m.Generators[gen];
            yield return ("generator", o => o.OpenGenerator(gen, g.Bus, g.Name), n => n.OpenGenerator(gen, g.Bus, g.Name));
        }
        if (ic >= 0)
        {
            Generator g = m.Generators[ic];
            yield return ("interconnector", o => o.OpenInterconnector(ic, g.Bus, g.Name), n => n.OpenInterconnector(ic, g.Bus, g.Name));
        }
        if (tx >= 0) yield return ("transformer", o => o.OpenTransformer(tx, m.Branches[tx]), n => n.OpenTransformer(tx, m.Branches[tx]));
        if (line >= 0) yield return ("circuit", o => o.OpenCircuit(line, m.Branches[line]), n => n.OpenCircuit(line, m.Branches[line]));
        yield return ("substation", o => o.OpenSubstation(bus), n => n.OpenSubstation(bus));
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("uk")]
    public void Every_unit_page_keeps_the_same_96_sample_history_and_the_active_gate_holds(string name)
    {
        var feed = new Feed(name);
        var frames = Enumerable.Range(0, 6).Select(_ => feed.Next()).ToList();
        GridModel m = frames[0].Frame.Model;
        foreach ((string what, Action<Old.AssetExplorerVm> openOld, Action<AssetExplorerModel> openNew) in UnitPages(m))
        {
            var t = new Twin();
            t.Update(frames[0].Frame, frames[0].Clock, $"{name} {what}: first frame");
            t.Do(openOld, openNew, $"{name} {what}: opened");
            // 110 frames: past the 96-sample cap, cycling the live frames so the trace moves.
            for (int k = 0; k < 110; k++)
            {
                (SimFrame f, string clock) = frames[k % frames.Count];
                t.Update(k % 7 == 3 ? Scaled(f, 1.02, 1.3) : f, clock, $"{name} {what}: frame {k}");
            }
            var trace = (IReadOnlyList<double>)t.NewX.Current.GetType().GetProperty("Trace")!.GetValue(t.NewX.Current)!;
            Assert.Equal(96, trace.Count);

            // The Active gate: off-screen frames do not refresh the page; showing it again catches up at once.
            t.SetActive(false, $"{name} {what}: hidden");
            t.Update(frames[1].Frame, frames[1].Clock, $"{name} {what}: a frame while hidden");
            t.Update(Scaled(frames[2].Frame, 1.1, 2.0), frames[2].Clock, $"{name} {what}: another frame while hidden");
            t.SetActive(true, $"{name} {what}: shown again");
            t.Back($"{name} {what}: back home");
            t.Update(frames[3].Frame, frames[3].Clock, $"{name} {what}: home again");
        }
    }

    [Theory]
    [InlineData("case9", "uk")]
    [InlineData("uk", "case9")]
    public void A_model_swap_under_every_page_says_stale_prunes_and_keeps_the_old_quirks(string first, string second)
    {
        var a = new Feed(first);
        var b = new Feed(second);
        (SimFrame fa, string ca) = a.Next();
        (SimFrame fb, string cb) = b.Next();
        GridModel m = fa.Model;

        // Every fleet page and every unit page, left open while the frames switch to the other case and back.
        var pages = new List<(string What, Action<Old.AssetExplorerVm> Old, Action<AssetExplorerModel> New)>();
        var oldCards = new Old.AssetExplorerVm().Home.Classes;
        for (int c = 0; c < oldCards.Count; c++)
        {
            int card = c;
            pages.Add(($"card {oldCards[c].Title}",
                o => o.Home.Classes[card].Open.Execute(null),
                n => ((AssetClassCardModel)((IList)n.Home.GetType().GetProperty("Classes")!.GetValue(n.Home)!)[card]!).OnOpen()));
        }
        pages.AddRange(UnitPages(m));

        // Test changed 2026-10-20 (ASSETS fix S4, Mark approved fixing the quirks as their own slices): the substation
        // fleet's kV chips are no longer the old page's stale first-model chips after the swap; the named exception
        // (FixedS4ChipsAfterASwap) checks the rebuilt row exactly, and this counts that it really ran.
        S4Hits = 0;

        foreach ((string what, Action<Old.AssetExplorerVm> openOld, Action<AssetExplorerModel> openNew) in pages)
        {
            var t = new Twin();
            t.Update(fa, ca, $"{first}->{second} {what}: first case");
            t.Do(openOld, openNew, $"{first}->{second} {what}: opened");
            t.Update(fa, ca, $"{first}->{second} {what}: again");
            t.Update(fb, cb, $"{first}->{second} {what}: the other case");
            t.Update(fb, cb, $"{first}->{second} {what}: the other case again");
            t.Update(fa, ca, $"{first}->{second} {what}: back to the first case");
            t.Back($"{first}->{second} {what}: back");
            t.Update(fb, cb, $"{first}->{second} {what}: home on the other case");
        }
        Assert.True(S4Hits > 0, $"{first}->{second}: the substation fleet's chips were never compared after a swap");

        // The unit pages say so in words when their unit has gone (the same words as the old pages: compared above).
        var x = new AssetExplorerModel();
        x.Update(fa, ca);
        foreach ((string what, _, Action<AssetExplorerModel> openNew) in UnitPages(m))
        {
            openNew(x);
            x.Update(fb, cb);
            if (x.Current.Sub.Contains("not in the current model", StringComparison.Ordinal))
                Assert.EndsWith("values below are stale", x.Current.Sub, StringComparison.Ordinal);
            x.GoHome();
        }
    }

    [Fact]
    public void The_explorer_routes_and_the_gda_payload_match_the_old_explorer()
    {
        var feed = new Feed("uk");
        (SimFrame f, string clock) = feed.Next();
        GridModel m = f.Model;
        var t = new Twin();
        t.Update(f, clock, "uk first frame");

        // OpenBranch by reference (in this frame, then a branch object not in it: index -1, resolved by ends and name).
        int tx = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer);
        int line = Enumerable.Range(0, m.Branches.Count).First(i => !m.Branches[i].IsTransformer);
        t.Do(o => o.OpenBranch(m.Branches[tx]), n => n.OpenBranch(m.Branches[tx]), "OpenBranch transformer");
        t.Do(o => o.OpenBranch(m.Branches[line]), n => n.OpenBranch(m.Branches[line]), "OpenBranch circuit");
        Branch foreign = m.Branches[line] with { };
        t.Do(o => o.OpenBranch(foreign), n => n.OpenBranch(foreign), "OpenBranch, a copy not in the frame");
        Branch gone = new(999_999, 999_998, 0.01, 0.1, Name: "no such circuit");
        t.Do(o => o.OpenBranch(gone), n => n.OpenBranch(gone), "OpenBranch, a branch in no model");

        // OpenTransformerNamed: a name the model carries, and one it does not ("not in the current model").
        string txName = m.Branches[tx].Name ?? "";
        t.Do(o => o.OpenTransformerNamed(txName), n => n.OpenTransformerNamed(txName), "OpenTransformerNamed (present)");
        t.Do(o => o.OpenTransformerNamed("nope"), n => n.OpenTransformerNamed("nope"), "OpenTransformerNamed (absent)");
        t.Update(f, clock, "after the named opens");
        for (int k = 0; k < 6; k++) t.Back($"back {k}");
        t.Back("back with nothing to go back to");

        // The GDA per-asset card: hidden until a payload arrives; a null payload shows the status alone; a payload shows
        // the shared formatter's rows; a second payload replaces the first.
        int bus = m.Buses[0].Id;
        t.Do(o => o.OpenSubstation(bus), n => n.OpenSubstation(bus), "substation page");
        var oldPage = (Old.SubstationDetailVm)t.OldX.Current;
        var newPage = (SubstationDetailModel<PlantRowModel, ConnectionRowModel>)t.NewX.Current;
        static AssetEntry E(params (string K, string V)[] fs) => new(fs.Select(x => new AssetField(x.K, x.V)).ToList());
        var asset = new AssetData("T_DRAXX-1", bus,
            Envelope: new[] { E(("bmUnit", "T_DRAXX-1"), ("p_max", "645"), ("p_min_stable_msg", "250"), ("msg_pct_of_pmax", "38.8"), ("psrType", "Biomass")) },
            BessSoc: new[] { E(("unit_id", "B1"), ("rated_power_mw", "50"), ("rated_energy_mwh", "100"), ("duration_hours", "2"), ("tech", "Li-ion"), ("source", "REPD")) },
            TapWear: Array.Empty<AssetEntry>(),
            Consumers: Enumerable.Range(1, 13).Select(i => E(("name", $"C{i}"), ("type", "t"))).ToArray());
        oldPage.ApplyAsset(null, "no GDA per-asset data for this bus"); newPage.ApplyAsset(null, "no GDA per-asset data for this bus");
        t.Same("GDA card, no payload");
        oldPage.ApplyAsset(asset, "GDA per-asset data"); newPage.ApplyAsset(asset, "GDA per-asset data");
        t.Same("GDA card, a payload");
        t.Update(f, clock, "GDA card, then a frame");
        Assert.True(newPage.AssetVisible);
        Assert.NotEmpty(newPage.AssetInsights);
    }

    [Fact]
    public void Two_namesake_links_open_two_different_units_and_two_rows()
    {
        // N258's behaviour, through the moved code: two interconnectors sharing a name are two rows, and each row opens
        // its own unit (by index), on both sides.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 400), new Bus(3, BusType.Pq, BaseKv: 400) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: 500), new Branch(1, 3, 0.01, 0.1, RateAMva: 500) },
            Generators = new[]
            {
                new Generator(1, 100),
                new Generator(2, 50, Name: "Link X", Type: "interconnector", Pmax: 1000),
                new Generator(3, -40, Name: "Link X", Type: "interconnector", Pmax: 1000),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        var f = new SimFrame(m, r, 12, 1, 50, 110, 10);
        var t = new Twin();
        t.Update(f, "12:00", "namesakes");
        t.Do(o => o.Open(new Old.InterconnectorFleetVm(o)), n => n.Open(n.CreateInterconnectorFleet()), "fleet");
        var fleet = (InterconnectorFleetModel<LinkFleetRowModel>)t.NewX.Current;
        Assert.Equal(2, fleet.Links.Count);
        var oldFleet = (Old.InterconnectorFleetVm)t.OldX.Current;
        for (int k = 0; k < 2; k++)
        {
            t.Click(oldFleet.Links[k], fleet.Links[k], "Open", "OnOpen", $"link row {k}");
            Assert.Equal(k == 0 ? "+50" : "-40", ((InterconnectorDetailModel)t.NewX.Current).Flow);
            t.Back($"back from link {k}");
        }
    }

    [Fact]
    public void An_unnamed_and_a_repeated_name_circuit_each_open_their_own_index()
    {
        // D-DOC-146's behaviour, through the moved code: an unnamed circuit resolves (by index), and two parallel circuits
        // with the same ends and the same name (different impedances, so different flows) each open their own page, from
        // the fleet row and from OpenBranch, on both sides.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 300, Qd: 50, BaseKv: 400), new Bus(3, BusType.Pq, Pd: 200, Qd: 40, BaseKv: 275) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1, RateAMva: 500),                       // unnamed
                new Branch(2, 3, 0.01, 0.08, RateAMva: 400, Name: "Dup"),
                new Branch(2, 3, 0.02, 0.20, RateAMva: 400, Name: "Dup"),          // same ends, same name
                new Branch(1, 3, 0.01, 0.12, RateAMva: 600, Name: "Tie"),
            },
            Generators = new[] { new Generator(1, 500) },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        var f = new SimFrame(m, r, 12, 1, 50, 500, 500);
        var t = new Twin();
        t.Update(f, "12:00", "circuits");
        t.Do(o => o.Open(new Old.CircuitFleetVm(o)), n => n.Open(n.CreateCircuitFleet()), "circuit fleet");
        var fleet = (CircuitFleetModel<BandChipModel, CircuitFleetRowModel>)t.NewX.Current;
        var oldFleet = (Old.CircuitFleetVm)t.OldX.Current;
        Assert.Equal(4, fleet.Circuits.Count);
        var opened = new HashSet<int>();
        for (int k = 0; k < fleet.Circuits.Count; k++)
        {
            CircuitFleetRowModel row = fleet.Circuits[k];
            t.Click(oldFleet.Circuits[k], row, "Open", "OnOpen", $"circuit row {k}");
            var page = (CircuitDetailModel<CircuitStatusRowModel>)t.NewX.Current;
            Assert.DoesNotContain("not in the current model", page.Sub, StringComparison.Ordinal);
            Assert.Equal(row.Flow, page.Flow + " MW");   // the page reads the row's own circuit
            opened.Add(row.BranchIndex);
            t.Back($"back from circuit row {k}");
        }
        Assert.Equal(new[] { 0, 1, 2, 3 }, opened.OrderBy(i => i));
        for (int i = 0; i < m.Branches.Count; i++)
        {
            int bi = i;
            t.Do(o => o.OpenBranch(m.Branches[bi]), n => n.OpenBranch(m.Branches[bi]), $"OpenBranch {bi}");
            var page = (CircuitDetailModel<CircuitStatusRowModel>)t.NewX.Current;
            Assert.Equal(m.Branches[bi].Name ?? $"branch {bi}", page.Title);
            Assert.Equal($"{r.LineFlows[bi].PFromMw:+#,0;-#,0;0}", page.Flow);
            t.Back($"back from OpenBranch {bi}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The old name of a neutral property: a colour token is the old brush, a bool the old Visibility, an action
    /// the old command; everything else kept its name.</summary>
    private static string OldName(object neu, string name)
    {
        if (neu is SubstationMapGenerator && name == nameof(SubstationMapGenerator.FuelColour)) return "FuelColor";
        if (neu is AssetPageModel && name == "MapInput") return "MapData";
        switch (name)
        {
            case "BackVisible": return "BackVis";
            case "AssetVisible": return "AssetVis";
            case "OnOpen": return "Open";
            case "OnSelect": return "Select";
            case "OnToggle": return "Toggle";
        }
        if (name.EndsWith("Colour", StringComparison.Ordinal))
        {
            string stem = name[..^"Colour".Length];
            return stem is "Bar" or "Chip" or "Dot" ? stem : stem + "Brush";
        }
        return name;
    }

    /// <summary>
    /// Old properties with no neutral twin by design: the WPF commands that became methods (the explorer's BACK and
    /// ASSETS, and each unit page's parameter cross-links). The tests drive them on the old side and the methods on the
    /// new side and compare what they lead to.
    /// </summary>
    private static readonly HashSet<string> OldOnly = new(StringComparer.Ordinal)
    {
        "BackCmd", "HomeCmd", "OpenLanding", "OpenBus", "OpenHv", "OpenLv", "OpenOltcBus", "OpenIn", "OpenOut",
    };

    /// <summary>The old type name of a neutral page or row type: generic arity dropped, "...Model" is "...Vm" for a
    /// page and the bare name for a row.</summary>
    private static string OldTypeName(Type neu)
    {
        string n = neu.Name;
        int tick = n.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) n = n[..tick];
        if (n == "SubstationMapInput") return "SubstationMapData";
        if (n == "SubstationMapConnection") return "MapConnection";
        if (n == "SubstationMapGenerator") return "MapGenerator";
        if (!n.EndsWith("Model", StringComparison.Ordinal)) return n;
        string stem = n[..^"Model".Length];
        return typeof(AssetPageModel).IsAssignableFrom(neu) || neu == typeof(AssetExplorerModel) ? stem + "Vm" : stem;
    }

    internal static void AssertSame(object old, object neu, string at)
    {
        Assert.True(OldTypeName(neu.GetType()) == old.GetType().Name, $"{at}: old {old.GetType().Name}, new {neu.GetType().Name}");
        PropertyInfo[] neuProps = neu.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).ToArray();
        PropertyInfo[] oldProps = old.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).ToArray();
        var oldByName = oldProps.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo np in neuProps)
        {
            // Test changed 2026-10-20 (ASSETS fix S7): the one property with no old twin (the PROTECTION tag flag), checked by
            // its own rule before the coverage lookup; it excuses no old property.
            if (FixedS7ProtectionTag(neu, np.Name, np.GetValue(neu), at)) continue;
            string oldName = OldName(neu, np.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? op), $"{at}: the old {old.GetType().Name} has no {oldName} for {np.Name}");
            covered.Add(oldName);
            // The named, deliberate differences from the old code (ASSETS fixes Mark approved); each is checked against
            // its own exact fixed value and is the only property it excuses. Everything else is compared exactly below.
            if (FixedS4ChipsAfterASwap(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            if (FixedS12OltcFitted(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            if (FixedS5HomeAngleSpread(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            if (FixedS10CircuitBands(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            if (FixedS3FirstWins(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            if (FixedS7ProtectionPanel(neu, np.Name, op!.GetValue(old), np.GetValue(neu), at)) continue;
            SameValue(op!.GetValue(old), np.GetValue(neu), $"{at}: {neu.GetType().Name}.{np.Name}");
        }
        Assert.Equal(oldProps.Select(p => p.Name).Where(n => !OldOnly.Contains(n)).OrderBy(s => s, StringComparer.Ordinal),
            covered.OrderBy(s => s, StringComparer.Ordinal));

        // The internal numbers a row carries for the single-line diagram (and the circuit row's sort key): same names,
        // same values.
        FieldInfo[] neuFields = neu.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Where(fi => fi.IsAssembly).ToArray();
        FieldInfo[] oldFields = old.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Where(fi => fi.IsAssembly).ToArray();
        Assert.Equal(oldFields.Select(fi => fi.Name).OrderBy(s => s, StringComparer.Ordinal), neuFields.Select(fi => fi.Name).OrderBy(s => s, StringComparer.Ordinal));
        foreach (FieldInfo nf in neuFields)
            SameValue(old.GetType().GetField(nf.Name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(old), nf.GetValue(neu), $"{at}: {neu.GetType().Name}.{nf.Name} (internal)");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The named exceptions: deliberate fixes of quirks the oracle still has (ASSETS plan 4.3, section 8 decision 4)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The model of the frame the twin last fed, for the one comparison running (set only by
    /// <see cref="Twin.Same"/>); null everywhere else, so no other comparison is ever excused.</summary>
    [ThreadStatic] private static GridModel? FixModel;

    /// <summary>How many times the S4 exception replaced the exact comparison (a swap test asserts it ran).</summary>
    [ThreadStatic] private static int S4Hits;

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S4, Mark approved fixing the quirks as their own slices): after a model swap
    /// the old SubstationFleetVm still shows the chips it built for the FIRST model (it built them once), while the fixed
    /// page rebuilds them for the model now running. So exactly one property may differ: the substation fleet page's
    /// Filters, and only while the old chips are not the running model's levels (that is, only after a swap). Then the
    /// new chips must be exactly the running model's row: "ALL" (NaN) then each distinct positive base kV, highest first,
    /// labelled "{kV:F0} kV", the selection kept when the running model still has the old page's selected level, else
    /// "ALL", lit on exactly one chip, each chip of the old chip type with a click action. While the old chips ARE the
    /// running model's levels (no swap, or swapped back) this returns false and the chips are compared exactly.
    /// </summary>
    private static bool FixedS4ChipsAfterASwap(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixModel is not { } m || name != "Filters") return false;
        Type t = neu.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(SubstationFleetModel<,>)) return false;

        var oldChips = Assert.IsAssignableFrom<IList>(ov);
        var newChips = Assert.IsAssignableFrom<IList>(nv);
        // The running model's levels, worked out here independently of the page (the old page's own rule).
        List<double> levels = m.Buses.Select(b => b.BaseKv).Where(kv => kv > 0).Distinct().OrderByDescending(kv => kv).ToList();
        List<double> oldLevels = oldChips.Cast<object>().Skip(1).Select(c => (double)c.GetType().GetProperty("Kv")!.GetValue(c)!).ToList();
        if (oldChips.Count == 0 || oldLevels.SequenceEqual(levels)) return false;   // not stale: compared exactly

        // The old page's selection (its lit chip; "ALL" is NaN), kept only if the running model still has that level.
        object? oldLit = oldChips.Cast<object>().SingleOrDefault(c => (bool)c.GetType().GetProperty("Active")!.GetValue(c)!);
        double oldSel = oldLit is null ? double.NaN : (double)oldLit.GetType().GetProperty("Kv")!.GetValue(oldLit)!;
        double sel = !double.IsNaN(oldSel) && levels.Contains(oldSel) ? oldSel : double.NaN;

        string what = $"{at}: {t.Name}.Filters (ASSETS fix S4: rebuilt for the running model)";
        Assert.True(newChips.Count == levels.Count + 1, $"{what}: want {levels.Count + 1} chips, got {newChips.Count}");
        string oldChipType = oldChips[0]!.GetType().Name;
        for (int k = 0; k < newChips.Count; k++)
        {
            var chip = Assert.IsAssignableFrom<KvFilterChipModel>(newChips[k]);
            Assert.True(OldTypeName(chip.GetType()) == oldChipType, $"{what}[{k}]: old {oldChipType}, new {chip.GetType().Name}");
            double kv = k == 0 ? double.NaN : levels[k - 1];
            string label = k == 0 ? "ALL" : $"{kv:F0} kV";
            bool lit = k == 0 ? double.IsNaN(sel) : kv == sel;
            SameValue(label, chip.Label, $"{what}[{k}].Label");
            SameValue(kv, chip.Kv, $"{what}[{k}].Kv");
            SameValue(lit, chip.Active, $"{what}[{k}].Active");
            Assert.NotNull(chip.OnToggle);
        }
        S4Hits++;
        return true;
    }

    /// <summary>How many times the S12 exception checked the tile (the walk asserts it ran).</summary>
    [ThreadStatic] private static int S12Hits;

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S12, Mark approved fixing the quirks as their own slices): the old
    /// TransformerFleetVm's "OLTC FITTED" reads "{OLTC records} of {transformers}", the fixed page reads
    /// "{transformers with at least one OLTC record mapped to them by SteadyStateSolver.MapOltcRecordsToBranches} of
    /// {transformers}". So exactly one property may differ: the transformer fleet page's OltcFitted, and both sides are
    /// checked against their own rule, worked out here from the running model: the old value must be exactly the
    /// record-count string and the new value exactly the mapped-transformer string (the two are equal on every bundled
    /// case, where each record maps to its own transformer). If the old value is not the record-count string of the
    /// running model (the page last refreshed on another model), this returns false and the values are compared
    /// exactly.
    /// </summary>
    private static bool FixedS12OltcFitted(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixModel is not { } m || name != "OltcFitted") return false;
        Type t = neu.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(TransformerFleetModel<>)) return false;

        int transformers = m.Branches.Count(b => b.IsTransformer);
        string oldRule = $"{m.Oltcs.Count} of {transformers}";
        if (ov is not string os || !string.Equals(os, oldRule, StringComparison.Ordinal)) return false;

        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(m);
        int fitted = map.Where(i => i >= 0 && m.Branches[i].IsTransformer).Distinct().Count();
        SameValue($"{fitted} of {transformers}", nv, $"{at}: {t.Name}.OltcFitted (ASSETS fix S12: transformers with a mapped OLTC)");
        S12Hits++;
        return true;
    }

    /// <summary>The frame the landing page last refreshed on, for the one comparison running (set only by
    /// <see cref="Twin.Same"/>); null everywhere else. Test changed 2026-10-20 (ASSETS fix S5).</summary>
    [ThreadStatic] private static SimFrame? FixHomeFrame;

    /// <summary>How many times the S5 exception checked a home phasor card value (the walk asserts it ran).</summary>
    [ThreadStatic] private static int S5Hits;

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S5, Mark approved fixing the quirks as their own slices): the old AssetHomeVm's
    /// GENERATOR PHASORS card takes its angle spread over ALL buses (max minus min of every r.VaDeg entry); the fixed card
    /// takes it over the generator buses by the phasor page's own rule (first-wins angle per bus id; the
    /// non-interconnector generators; a generator whose bus is not in the result skipped). So exactly four properties of
    /// exactly that one card may differ, the four the spread feeds: Metric ("{spread:F1}", or " - " at no spread),
    /// MetricUnit (" deg", or "" at no spread), MetricColour (Loading(0.9) above 30 deg, Loading(0.6) above 15 deg, else
    /// Voltage(1.0)) and BarWidth (min(1, spread / 60) * 220). Both sides are checked against their own rule, worked out
    /// here from the frame the landing page last refreshed on: the old value must be exactly the all-bus rule's value and
    /// the new value exactly the generator-bus rule's value. If the old value is not the all-bus rule's (no frame yet, or
    /// any doubt about which frame the page shows), this returns false and the values are compared exactly. Every other
    /// property of the card (Title, Tag, Detail, Count, MetricLabel "ANGLE SPREAD", the click) is still compared exactly.
    /// </summary>
    private static bool FixedS5HomeAngleSpread(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixHomeFrame is not { } f) return false;
        if (neu is not AssetClassCardModel card || !string.Equals(card.Title, "GENERATOR PHASORS", StringComparison.Ordinal)) return false;
        if (name is not ("Metric" or "MetricUnit" or "MetricColour" or "BarWidth")) return false;
        PowerFlowResult r = f.Result;

        // The old rule, the oracle's loop: every bus angle.
        double oMin = double.MaxValue, oMax = double.MinValue;
        for (int i = 0; i < r.VaDeg.Length; i++)
        {
            if (r.VaDeg[i] < oMin) oMin = r.VaDeg[i];
            if (r.VaDeg[i] > oMax) oMax = r.VaDeg[i];
        }
        double oldSpread = oMin < double.MaxValue ? oMax - oMin : 0;

        // The fixed rule, the phasor page's: generator buses only.
        double newSpread = GeneratorBusSpread(f.Model, r) ?? 0;

        string what = $"{at}: {neu.GetType().Name}.{name} (ASSETS fix S5: the spread over generator buses)";
        switch (name)
        {
            case "Metric":
            case "MetricUnit":
            {
                string Want(double s) => name == "Metric" ? (s > 0 ? $"{s:F1}" : " - ") : (s > 0 ? " deg" : "");
                if (ov is not string os || !string.Equals(os, Want(oldSpread), StringComparison.Ordinal)) return false;
                SameValue(Want(newSpread), nv, what);
                break;
            }
            case "MetricColour":
            {
                static UiColour Want(double s) => s > 30 ? ColourRamps.Loading(0.9) : s > 15 ? ColourRamps.Loading(0.6) : ColourRamps.Voltage(1.0);
                if (ov is not Old.SolidColorBrush brush || brush.Color.Token != Want(oldSpread)) return false;
                Assert.True(nv is UiColour c && c == Want(newSpread), $"{what}: want {ColourRamps.ToHex(Want(newSpread))}, got {nv}");
                break;
            }
            default:   // BarWidth
            {
                static double Want(double s) => Math.Min(1.0, s / 60.0) * AssetClassCardModel.BarTrack;
                if (ov is not double od || BitConverter.DoubleToInt64Bits(od) != BitConverter.DoubleToInt64Bits(Want(oldSpread))) return false;
                SameValue(Want(newSpread), nv, what);
                break;
            }
        }
        S5Hits++;
        return true;
    }

    /// <summary>
    /// The GENERATOR PHASORS page's spread rule, worked out here independently of either page (test changed 2026-10-20,
    /// ASSETS fix S5): the first-wins angle of each bus id, over the buses of the non-interconnector generators that are
    /// in the result; null when no such bus is found.
    /// </summary>
    internal static double? GeneratorBusSpread(GridModel m, PowerFlowResult r)
    {
        var vaById = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) vaById.TryAdd(r.BusIds[i], r.VaDeg[i]);
        double min = double.MaxValue, max = double.MinValue;
        foreach (Generator g in m.Generators)
        {
            if (UiFuels.Classify(g.Type).Category == GridSim.Core.Presentation.FuelCategory.Interconnector) continue;
            if (!vaById.TryGetValue(g.Bus, out double va)) continue;
            if (va < min) min = va;
            if (va > max) max = va;
        }
        return min < double.MaxValue ? max - min : null;
    }

    /// <summary>How many times the S10 exception checked a circuit band chip row, a fleet row's band value, and a
    /// circuit page's subtitle (the walk asserts each ran on case9 and none ran on a GB case).</summary>
    [ThreadStatic] private static int S10ChipHits, S10RowHits, S10SubHits;

    /// <summary>The GB ladder the circuit bands snap to (ASSETS fix S10 keeps it for voltages on it).</summary>
    private static readonly int[] GbLadder = { 400, 275, 132, 66, 33, 11 };

    /// <summary>The circuit band BEFORE the fix, worked out here (the old rule): the nearest GB ladder level, the first
    /// level in ladder order winning a tie.</summary>
    internal static int OldCircuitBand(double kv)
    {
        int best = GbLadder[0];
        double bestErr = double.MaxValue;
        foreach (int lvl in GbLadder)
            if (Math.Abs(kv - lvl) < bestErr) { bestErr = Math.Abs(kv - lvl); best = lvl; }
        return best;
    }

    /// <summary>The circuit band AFTER the fix, worked out here independently of the page (ASSETS fix S10, plan Q11):
    /// the nearest ladder level when within (strictly) 0.5 kV of it; otherwise the kV rounded half away from zero to a
    /// whole kV, unless that is below 1 kV (or NaN), which keeps the old snap.</summary>
    internal static int NewCircuitBand(double kv)
    {
        int ladder = OldCircuitBand(kv);
        if (Math.Abs(kv - ladder) < 0.5) return ladder;
        double exact = Math.Round(kv, MidpointRounding.AwayFromZero);
        return exact >= 1 && exact <= int.MaxValue ? (int)exact : ladder;
    }

    /// <summary>The kV a circuit is banded by: the higher of its two ends' base kV (first bus with the id; 0 when the id
    /// is missing), as the pages take it.</summary>
    internal static double CircuitKv(GridModel m, Branch b) =>
        Math.Max(m.Buses.FirstOrDefault(x => x.Id == b.From)?.BaseKv ?? 0, m.Buses.FirstOrDefault(x => x.Id == b.To)?.BaseKv ?? 0);

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S10, Mark approved fixing the quirks as their own slices): the old circuit pages
    /// band every circuit at the NEAREST GB ladder level (400 / 275 / 132 / 66 / 33 / 11 kV), so case9's 345 kV circuits
    /// read "400 kV"; the fixed pages band a circuit at the ladder level only when its kV lies within 0.5 kV of it, and
    /// otherwise at its own whole kV ("345 kV"). So exactly these properties may differ, and only where the two rules give
    /// different bands for the running model:
    ///   - the CABLES &amp; LINES page's Bands (the chip row): the old chips must be exactly the old rule's row for the
    ///     running model ("ALL" then each distinct old band, highest first) and the new chips exactly the new rule's
    ///     row, chip for chip in the same positions (the walked cases map old bands to new bands one to one, in order;
    ///     anything else fails loud), each chip lit exactly as the old chip in its position is, of the old chip type,
    ///     with a click action;
    ///   - a fleet row's Band and BandLabel ("{band} kV"): the old value must be the old rule's band of the row's
    ///     branch (by BranchIndex in the running model) and the new value the new rule's;
    ///   - a circuit page's Sub ("{from bus} -> {to bus}  {band} kV"): the old value must be exactly that text with the
    ///     old band for a circuit of the running model, and the new value the same text with the new band.
    /// Wherever the old value is not the old rule's for the running model (a page or row left from another model), or
    /// the two rules agree (every circuit on the GB ladder: every GB case), this returns false and the values are
    /// compared exactly.
    /// </summary>
    private static bool FixedS10CircuitBands(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixModel is not { } m) return false;
        Type t = neu.GetType();
        string what = $"{at}: {t.Name}.{name} (ASSETS fix S10: circuit bands off the GB ladder at their own kV)";

        // ---- the chip row of the CABLES & LINES page ----
        if (name == "Bands" && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(CircuitFleetModel<,>))
        {
            List<double> kvs = m.Branches.Where(b => !b.IsTransformer).Select(b => CircuitKv(m, b)).ToList();
            List<int> oldBands = kvs.Select(OldCircuitBand).Distinct().OrderByDescending(x => x).ToList();
            List<int> newBands = kvs.Select(NewCircuitBand).Distinct().OrderByDescending(x => x).ToList();
            if (oldBands.SequenceEqual(newBands)) return false;   // no band moves on this model: compared exactly

            var oldChips = Assert.IsAssignableFrom<IList>(ov);
            var newChips = Assert.IsAssignableFrom<IList>(nv);
            int Band(object c) => (int)c.GetType().GetProperty("Band")!.GetValue(c)!;
            string Label(object c) => (string)c.GetType().GetProperty("Label")!.GetValue(c)!;
            bool IsOn(object c) => (bool)c.GetType().GetProperty("IsOn")!.GetValue(c)!;
            List<object> olds = oldChips.Cast<object>().ToList();
            // The old chips must be the old rule's row for this model; otherwise (stale) excuse nothing.
            if (olds.Count != oldBands.Count + 1 || Band(olds[0]) != 0 || !olds.Skip(1).Select(Band).SequenceEqual(oldBands)) return false;

            Assert.True(newBands.Count == oldBands.Count, $"{what}: the old rule gives {oldBands.Count} bands, the new {newBands.Count} (not one to one)");
            Assert.True(newChips.Count == olds.Count, $"{what}: want {olds.Count} chips, got {newChips.Count}");
            for (int k = 0; k < olds.Count; k++)
            {
                var chip = Assert.IsAssignableFrom<BandChipModel>(newChips[k]);
                Assert.True(OldTypeName(chip.GetType()) == olds[k].GetType().Name, $"{what}[{k}]: old {olds[k].GetType().Name}, new {chip.GetType().Name}");
                int band = k == 0 ? 0 : newBands[k - 1];
                SameValue(k == 0 ? "ALL" : $"{band} kV", chip.Label, $"{what}[{k}].Label");
                SameValue(band, chip.Band, $"{what}[{k}].Band");
                SameValue(IsOn(olds[k]), chip.IsOn, $"{what}[{k}].IsOn");
                Assert.NotNull(chip.OnSelect);
                // The old chip in this position carries the old rule's label.
                SameValue(k == 0 ? "ALL" : $"{oldBands[k - 1]} kV", Label(olds[k]), $"{what}[{k}].Label (old)");
            }
            S10ChipHits++;
            return true;
        }

        // ---- a fleet row's band ----
        if ((name == "Band" || name == "BandLabel") && neu is CircuitFleetRowModel row)
        {
            if (row.BranchIndex < 0 || row.BranchIndex >= m.Branches.Count || m.Branches[row.BranchIndex].IsTransformer) return false;
            double kv = CircuitKv(m, m.Branches[row.BranchIndex]);
            int ob = OldCircuitBand(kv), nb = NewCircuitBand(kv);
            if (ob == nb) return false;   // this circuit's band does not move: compared exactly
            object oldWant = name == "Band" ? ob : $"{ob} kV";
            object newWant = name == "Band" ? nb : $"{nb} kV";
            if (!Equals(ov, oldWant)) return false;   // not the old rule's value for this model: compared exactly
            SameValue(newWant, nv, what);
            S10RowHits++;
            return true;
        }

        // ---- a circuit page's subtitle ----
        if (name == "Sub" && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(CircuitDetailModel<>) && ov is string os)
        {
            string BusName(int id) => m.Buses.FirstOrDefault(b => b.Id == id)?.Name ?? $"bus {id}";
            // Every circuit of the model whose old subtitle is exactly the old page's text; their new subtitles must agree
            // (they do: the same end names on the same kV give the same band), else fail loud.
            List<string> wants = m.Branches.Where(b => !b.IsTransformer)
                .Select(b => (Head: $"{BusName(b.From)} -> {BusName(b.To)}  ", Kv: CircuitKv(m, b)))
                .Where(c => string.Equals(c.Head + $"{OldCircuitBand(c.Kv)} kV", os, StringComparison.Ordinal))
                .Where(c => OldCircuitBand(c.Kv) != NewCircuitBand(c.Kv))
                .Select(c => c.Head + $"{NewCircuitBand(c.Kv)} kV")
                .Distinct(StringComparer.Ordinal).ToList();
            if (wants.Count == 0) return false;   // not a moved circuit of this model: compared exactly
            Assert.True(wants.Count == 1, $"{what}: the old subtitle \"{os}\" fits circuits with different new bands: {string.Join(" | ", wants)}");
            SameValue(wants[0], nv, what);
            S10SubHits++;
            return true;
        }
        return false;
    }

    // ---- ASSETS fix S3: one per-frame lookup, first-wins ----

    /// <summary>The frame the twin last fed, for the one comparison running (set only by <see cref="Twin.Same"/>); null
    /// everywhere else. Test changed 2026-10-20 (ASSETS fix S3).</summary>
    [ThreadStatic] private static SimFrame? FixFrame;

    /// <summary>How many times the S3 exception checked a substation fleet value (the bundled walk asserts none; the
    /// built repeated-id case asserts some).</summary>
    [ThreadStatic] private static int S3Hits;

    /// <summary>The substation fleet's voltage table BEFORE the fix, worked out here (the old rule): bus id -> solved
    /// Vm, LAST entry wins for a repeated id (an indexer assignment).</summary>
    internal static Dictionary<int, double> LastWinsVm(PowerFlowResult r)
    {
        var d = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) d[r.BusIds[i]] = r.Vm[i];
        return d;
    }

    /// <summary>The table AFTER the fix, worked out here independently of the page (the rule every other page already
    /// used): bus id -> solved Vm, FIRST entry wins for a repeated id.</summary>
    internal static Dictionary<int, double> FirstWinsVm(PowerFlowResult r)
    {
        var d = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) d.TryAdd(r.BusIds[i], r.Vm[i]);
        return d;
    }

    /// <summary>The ids that appear more than once, in id order (where the two rules can differ).</summary>
    internal static List<int> RepeatedIds(IEnumerable<int> ids) =>
        ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(i => i).ToList();

    /// <summary>The substation fleet's OUT OF BAND count by a voltage table: the model's buses whose voltage is in the
    /// table and outside the shared 0.94 / 1.06 pu band (the page's own loop, worked out here).</summary>
    internal static int OutOfBandBy(GridModel m, IReadOnlyDictionary<int, double> vmById) =>
        m.Buses.Count(b => vmById.TryGetValue(b.Id, out double vm) && !double.IsNaN(vm)
                           && (vm < GridSim.Core.Presentation.ScadaThresholds.VoltageBandLowPu
                               || vm > GridSim.Core.Presentation.ScadaThresholds.VoltageBandHighPu));

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S3, Mark approved fixing the quirks as their own slices): the old
    /// SubstationFleetVm builds its per-frame voltage table LAST-WINS (vmById[r.BusIds[i]] = r.Vm[i]); the fixed page uses
    /// the one shared first-wins lookup (FrameLookup.ByBusId, TryAdd), as every other page already did. The two differ
    /// only where two entries of the frame's r.BusIds share an id. So exactly these properties may differ, and only there:
    ///   - a fleet row's Vm ("{vm:F3}", or " - " when the id is not solved) and VmColour (ColourRamps.Voltage(vm), or
    ///     DashboardColours.Gray when not solved), for a row whose BusId is repeated in the result and whose two rules give
    ///     different voltages: the old value must be exactly the last-wins value's text / colour and the new value exactly
    ///     the first-wins value's;
    ///   - the fleet page's OutOfBand count, where the two tables give different counts: the old value must be exactly
    ///     the last-wins count and the new value exactly the first-wins count.
    /// When the frame has no repeated bus id (every bundled case: the loaders refuse one), this returns false at once and
    /// every value is compared exactly; likewise wherever the old value is not the last-wins rule's for the twin's last
    /// frame (a page showing another frame).
    /// </summary>
    private static bool FixedS3FirstWins(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixFrame is not { } f) return false;
        if (name is not ("Vm" or "VmColour" or "OutOfBand")) return false;
        PowerFlowResult r = f.Result;
        if (RepeatedIds(r.BusIds).Count == 0) return false;   // no repeated id: the two rules agree, compared exactly
        Dictionary<int, double> last = LastWinsVm(r), first = FirstWinsVm(r);
        string what = $"{at}: {neu.GetType().Name}.{name} (ASSETS fix S3: first-wins voltage table)";

        // ---- a fleet row's voltage text and colour ----
        if (name is "Vm" or "VmColour" && neu is SubstationFleetRowModel row)
        {
            double lv = last.GetValueOrDefault(row.BusId, double.NaN), fv = first.GetValueOrDefault(row.BusId, double.NaN);
            if (BitConverter.DoubleToInt64Bits(lv) == BitConverter.DoubleToInt64Bits(fv)) return false;   // same voltage
            if (name == "Vm")
            {
                static string Want(double v) => double.IsNaN(v) ? " - " : $"{v:F3}";
                if (ov is not string os || !string.Equals(os, Want(lv), StringComparison.Ordinal)) return false;
                SameValue(Want(fv), nv, what);
            }
            else
            {
                static UiColour Want(double v) => double.IsNaN(v) ? DashboardColours.Gray : ColourRamps.Voltage(v);
                if (ov is not Old.SolidColorBrush brush || brush.Color.Token != Want(lv)) return false;
                Assert.True(nv is UiColour c && c == Want(fv), $"{what}: want {ColourRamps.ToHex(Want(fv))}, got {nv}");
            }
            S3Hits++;
            return true;
        }

        // ---- the fleet page's OUT OF BAND count ----
        Type t = neu.GetType();
        if (name == "OutOfBand" && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(SubstationFleetModel<,>))
        {
            int oldCount = OutOfBandBy(f.Model, last), newCount = OutOfBandBy(f.Model, first);
            if (oldCount == newCount) return false;   // the counts agree: compared exactly
            if (ov is not string os || !string.Equals(os, oldCount.ToString(), StringComparison.Ordinal)) return false;
            SameValue(newCount.ToString(), nv, what);
            S3Hits++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// A built case with a repeated bus id (test changed 2026-10-20, ASSETS fix S3): four buses, two of them carrying id 2
    /// ("Centre 400 A" and "Centre 400 B"), and a hand-made solved result whose two id-2 entries carry different voltages
    /// (1.02 pu first, in band; 0.90 pu second, below the 0.94 pu band). The loaders refuse such a case (GR-147), so it is
    /// built here, never loaded.
    /// </summary>
    internal static SimFrame RepeatedBusIdFrame()
    {
        var m = new GridModel
        {
            Name = "repeated bus id (built)",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400, Name: "North 400"),
                new Bus(2, BusType.Pq, Pd: 120, BaseKv: 400, Name: "Centre 400 A"),
                new Bus(2, BusType.Pq, Pd: 80, BaseKv: 400, Name: "Centre 400 B"),
                new Bus(3, BusType.Pq, Pd: 50, BaseKv: 132, Name: "South 132"),
            },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.10, Name: "North - Centre"),
                new Branch(2, 3, 0.01, 0.10, Name: "Centre - South"),
            },
            Generators = new[] { new Generator(1, 250) },
        };
        var r = new PowerFlowResult
        {
            Converged = true,
            Iterations = 1,
            MaxMismatch = 0,
            BusIds = new[] { 1, 2, 2, 3 },
            Vm = new[] { 1.00, 1.02, 0.90, 0.97 },
            VaDeg = new[] { 0.0, -2.0, -3.0, -5.0 },
            PInjMw = new double[4],
            QInjMvar = new double[4],
            TotalLossMw = 0,
            TotalLossMvar = 0,
            LineFlows = Array.Empty<LineFlow>(),
        };
        return new SimFrame(m, r, Hour: 0, Tick: 1, FrequencyHz: 50, GenerationMw: 250, DemandMw: 250);
    }

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S3): on a case where two buses share an id the S3 exception really applies
    /// (on the bundled cases it never does), and the exact old and new values are pinned: the substation fleet's
    /// OUT OF BAND reads "2" before (both id-2 buses read the LAST entry, 0.90 pu) and "0" after (both read the FIRST,
    /// 1.02 pu); the "Centre 400 B" row (the row the page keeps for id 2) reads "0.900" before and "1.020" after (machine
    /// culture formatting, F3), its colour ColourRamps.Voltage(0.90) before and ColourRamps.Voltage(1.02) after. Every
    /// other value of the page, its rows and the explorer is compared exactly.
    /// </summary>
    [Fact]
    public void A_repeated_bus_id_reads_the_first_entry_on_the_substation_fleet()
    {
        SimFrame f = RepeatedBusIdFrame();
        S3Hits = 0;
        var t = new Twin();
        t.Update(f, "00:00", "repeated id: home");
        int card = new Old.AssetExplorerVm().Home.Classes.Select((c, i) => (c, i)).Single(x => x.c.Title == "SUBSTATIONS").i;
        t.Do(o => o.Home.Classes[card].Open.Execute(null),
             n => ((AssetClassCardModel)((IList)n.Home.GetType().GetProperty("Classes")!.GetValue(n.Home)!)[card]!).OnOpen(),
             "repeated id: substation fleet opened");
        t.Update(f, "00:00", "repeated id: substation fleet, a frame");
        Assert.True(S3Hits > 0, "the S3 exception never applied on the built repeated-id case");

        var oldPage = Assert.IsType<Old.SubstationFleetVm>(t.OldX.Current);
        var newPage = Assert.IsType<SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel>>(t.NewX.Current);
        Assert.Equal("2", oldPage.OutOfBand);
        Assert.Equal("0", newPage.OutOfBand);
        var oldRow = oldPage.Rows.Single(x => x.Name == "Centre 400 B");
        var newRow = newPage.Rows.Single(x => x.Name == "Centre 400 B");
        Assert.Equal($"{0.90:F3}", oldRow.Vm);
        Assert.Equal($"{1.02:F3}", newRow.Vm);
        Assert.Equal(ColourRamps.Voltage(0.90), ((Old.SolidColorBrush)oldRow.VmBrush).Color.Token);
        Assert.Equal(ColourRamps.Voltage(1.02), newRow.VmColour);
        // The row the page does not keep in its id table ("Centre 400 A": the second id-2 row overwrote it there, the same
        // in the old and the new page, a separate repeated-id behaviour S3 does not touch) still shows " - " on both sides.
        Assert.Equal(" - ", oldPage.Rows.Single(x => x.Name == "Centre 400 A").Vm);
        Assert.Equal(" - ", newPage.Rows.Single(x => x.Name == "Centre 400 A").Vm);
    }

    // ---- ASSETS fix S7: the transformer PROTECTION panel fed from the case's protections ----

    /// <summary>How many times the S7 exception compared a PROTECTION panel that shows real rows, and how many times the
    /// new-only ProtectionIsDemo tag flag was checked. Test changed 2026-10-20 (ASSETS fix S7).</summary>
    [ThreadStatic] private static int S7Hits, S7TagChecks;

    /// <summary>The five demo rows of the PROTECTION panel (label, value, status), exactly as the old page seeds them.</summary>
    internal static readonly (string Label, string Value, string Status)[] DemoProtectionRows =
    {
        ("Buchholz relay", "healthy", "OK"),
        ("Differential (87T)", "healthy", "OK"),
        ("Overcurrent (51)", "healthy", "OK"),
        ("Restricted earth fault (64)", "healthy", "OK"),
        ("Pressure relief (63)", "healthy", "OK"),
    };

    /// <summary>The unit of each protection-class parameter, written out here independently of the page (the unit each
    /// key's name carries; a loading fraction is a multiple of RateA, the RoCoF uplift a plain multiplier).</summary>
    internal static readonly Dictionary<string, string> ProtectionUnits = new(StringComparer.Ordinal)
    {
        ["rocofTripHzPerS"] = "Hz/s", ["rocofTripConfirmS"] = "s", ["dfDtTripHzPerS"] = "Hz/s", ["dfDtTripDwellS"] = "s",
        ["localRocofUpliftFactor"] = "x", ["ufTripHz"] = "Hz", ["ufTripDwellS"] = "s", ["ofTripHz"] = "Hz",
        ["ofTripDwellS"] = "s", ["uvTripPu"] = "pu", ["uvTripDwellS"] = "s", ["ovTripPu"] = "pu", ["ovTripDwellS"] = "s",
        ["lvrtVoltagePu"] = "pu", ["vectorShiftTripDeg"] = "deg", ["detectionLatencyS"] = "s",
        ["overloadTripFraction"] = "x RateA", ["overloadTripDwellS"] = "s", ["lfddStageHz"] = "Hz", ["lfddBlockMw"] = "MW",
        ["lfddDwellS"] = "s", ["autoRecloseDelayS"] = "s", ["settingsNominalHz"] = "Hz",
    };

    /// <summary>
    /// The PROTECTION panel's real rows for one transformer, worked out here independently of the page (ASSETS fix S7):
    /// for each device of the case's protections, in order, that monitors one of the transformer's terminal buses or
    /// targets the transformer by name: the device row (name, class, "OPENS THIS UNIT" / "HV BUS" / "LV BUS"), the
    /// monitored bus (name and id; "HV END" / "LV END" / "OTHER BUS"), the capacity in MW, the target branch when named
    /// ("THIS UNIT" / "OTHER BRANCH"), the class mode when it has one, then one row per class parameter (key, value and
    /// unit, provenance tag); a class missing from the library gives one "MISSING" row instead. Numbers are the shortest
    /// round-trip text, invariant culture. Empty when the case has no protections or none applies.
    /// </summary>
    internal static List<(string Label, string Value, string Status)> ExpectedProtectionRows(GridModel m, Branch b, int hv, int lv)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string BusName(int id) => m.Buses.FirstOrDefault(x => x.Id == id)?.Name ?? $"bus {id}";
        var rows = new List<(string Label, string Value, string Status)>();
        if (m.Protections is null) return rows;
        foreach (GridSim.Core.Assets.Protection.ProtectionInstance d in m.Protections.Instances)
        {
            bool opens = d.TargetBranch != null && b.Name != null && d.TargetBranch == b.Name;
            if (!opens && d.MonitorBus != hv && d.MonitorBus != lv) continue;
            rows.Add((d.Name, d.ClassName, opens ? "OPENS THIS UNIT" : d.MonitorBus == hv ? "HV BUS" : "LV BUS"));
            rows.Add(("Monitored bus", $"{BusName(d.MonitorBus)} (bus {d.MonitorBus})",
                d.MonitorBus == hv ? "HV END" : d.MonitorBus == lv ? "LV END" : "OTHER BUS"));
            rows.Add(("Capacity", N(d.CapacityMw) + " MW", "AUTHORED"));
            if (d.TargetBranch != null) rows.Add(("Target branch", d.TargetBranch, opens ? "THIS UNIT" : "OTHER BRANCH"));
            if (!m.Protections.Library.Contains(d.ClassName))
            {
                rows.Add(("Class", d.ClassName + " is not in the class library", "MISSING"));
                continue;
            }
            GridSim.Core.Assets.AssetClass c = m.Protections.Library.Get(d.ClassName);
            if (c.Mode != "") rows.Add(("Mode", c.Mode, "AUTHORED"));
            foreach (KeyValuePair<string, double> p in c.Params)
            {
                Assert.True(ProtectionUnits.ContainsKey(p.Key), $"protection parameter {p.Key} has no unit in the test's table");
                rows.Add((p.Key, N(p.Value) + " " + ProtectionUnits[p.Key], c.ProvenanceFor(p.Key).Tag.ToString()));
            }
        }
        return rows;
    }

    /// <summary>The transformer a transformer page shows, found in the model by the page's own public facts (its HV and
    /// LV bus ids and its title, which is the branch name or "TX {from}-{to}"); null when none matches.</summary>
    private static Branch? TransformerOfPage(GridModel m, object page)
    {
        Type t = page.GetType();
        int hv = (int)t.GetProperty("HvBusId")!.GetValue(page)!;
        int lv = (int)t.GetProperty("LvBusId")!.GetValue(page)!;
        string title = (string)t.GetProperty("Title")!.GetValue(page)!;
        List<Branch> hits = m.Branches.Where(b => b.IsTransformer
                && ((b.From == hv && b.To == lv) || (b.From == lv && b.To == hv))
                && (b.Name ?? $"TX {b.From}-{b.To}") == title).ToList();
        if (hits.Count == 0) return null;
        // Parallel namesakes on the same two buses link to exactly the same devices (same buses, same name): any will do,
        // and the rows they give are checked equal.
        foreach (Branch other in hits.Skip(1))
            Assert.Equal(ExpectedProtectionRows(m, hits[0], hv, lv), ExpectedProtectionRows(m, other, hv, lv));
        return hits[0];
    }

    /// <summary>True when a panel's rows are exactly the five demo rows, by text and chip colour.</summary>
    private static bool IsDemoPanel(IList rows) =>
        rows.Count == DemoProtectionRows.Length
        && Enumerable.Range(0, rows.Count).All(k => rows[k] is TransformerReadingModel r
            && r.Label == DemoProtectionRows[k].Label && r.Value == DemoProtectionRows[k].Value
            && r.Status == DemoProtectionRows[k].Status && r.ChipColour == ColourRamps.Loading(0.0));

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S7, Mark approved fixing the quirks as their own slices): the fixed transformer
    /// page has one property the old page has not, ProtectionIsDemo (the front ends show the PROTECTION panel's STATIC
    /// DEMO tag by it). It is checked here, every time a transformer page is compared, by its own rule: true exactly when
    /// the panel shows the five demo rows (or nothing yet, before the first refresh); and false whenever the case has
    /// protection settings for the page's transformer. It excuses nothing else.
    /// </summary>
    private static bool FixedS7ProtectionTag(object neu, string name, object? nv, string at)
    {
        if (name != "ProtectionIsDemo") return false;
        Type t = neu.GetType();
        Assert.True(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(TransformerDetailModel<,>),
            $"{at}: {t.Name}.ProtectionIsDemo on a page that is not the transformer page");
        var rows = (IList)t.GetProperty("Protection")!.GetValue(neu)!;
        // Before its first refresh the page has seeded nothing yet (an empty panel, the tag shown, as the old page showed
        // it): that counts as the demo panel too.
        bool demo = rows.Count == 0 || IsDemoPanel(rows);
        Assert.True(nv is bool shown && shown == demo, $"{at}: {t.Name}.ProtectionIsDemo is {nv}, but the panel shows {(demo ? "the demo rows" : "real rows")}");
        if (FixModel is { } m && TransformerOfPage(m, neu) is { } b
            && ExpectedProtectionRows(m, b, (int)t.GetProperty("HvBusId")!.GetValue(neu)!, (int)t.GetProperty("LvBusId")!.GetValue(neu)!).Count > 0)
            Assert.True(nv is false, $"{at}: {t.Name}.ProtectionIsDemo is true where the case has protection settings for this transformer");
        S7TagChecks++;
        return true;
    }

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S7, Mark approved fixing the quirks as their own slices; plan section 7 Q4,
    /// recommendation (b)): the old TransformerDetailVm's PROTECTION panel always holds the five demo rows; the fixed page
    /// lists the case's own protection settings for the transformer when the case has any (protections.json). So exactly
    /// one property may differ: the transformer page's Protection list, and only where the running model has protection
    /// settings for the page's transformer (ExpectedProtectionRows, worked out here, is not empty). There the old list
    /// must be exactly the five demo rows, and the new list exactly the expected rows, row for row (label, value, status,
    /// chip colour: grey, or the alarm colour for a MISSING class), each of the old row type. Everywhere else (no
    /// protections, none for this transformer, the transformer not in the running model) this returns false and the
    /// panel is compared exactly against the old demo rows.
    /// </summary>
    private static bool FixedS7ProtectionPanel(object neu, string name, object? ov, object? nv, string at)
    {
        if (FixModel is not { } m || name != "Protection") return false;
        Type t = neu.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(TransformerDetailModel<,>)) return false;
        if (TransformerOfPage(m, neu) is not { } b) return false;
        int hv = (int)t.GetProperty("HvBusId")!.GetValue(neu)!, lv = (int)t.GetProperty("LvBusId")!.GetValue(neu)!;
        List<(string Label, string Value, string Status)> want = ExpectedProtectionRows(m, b, hv, lv);
        if (want.Count == 0) return false;   // nothing applies: compared exactly (the demo rows)

        // The old panel must be the demo rows; otherwise excuse nothing.
        var olds = Assert.IsAssignableFrom<IList>(ov);
        if (olds.Count != DemoProtectionRows.Length) return false;
        for (int k = 0; k < olds.Count; k++)
        {
            object o = olds[k]!;
            if ((string)o.GetType().GetProperty("Label")!.GetValue(o)! != DemoProtectionRows[k].Label) return false;
        }

        string what = $"{at}: {t.Name}.Protection (ASSETS fix S7: the case's protection settings)";
        var news = Assert.IsAssignableFrom<IList>(nv);
        Assert.True(news.Count == want.Count, $"{what}: want {want.Count} rows, got {news.Count}");
        string oldRowType = olds[0]!.GetType().Name;
        for (int k = 0; k < want.Count; k++)
        {
            var row = Assert.IsAssignableFrom<TransformerReadingModel>(news[k]);
            Assert.True(OldTypeName(row.GetType()) == oldRowType, $"{what}[{k}]: old {oldRowType}, new {row.GetType().Name}");
            SameValue(want[k].Label, row.Label, $"{what}[{k}].Label");
            SameValue(want[k].Value, row.Value, $"{what}[{k}].Value");
            SameValue(want[k].Status, row.Status, $"{what}[{k}].Status");
            UiColour chip = want[k].Status == "MISSING" ? ColourRamps.Loading(1.2) : DashboardColours.Gray;
            Assert.True(row.ChipColour == chip, $"{what}[{k}].ChipColour: want {ColourRamps.ToHex(chip)}, got {ColourRamps.ToHex(row.ChipColour)}");
        }
        S7Hits++;
        return true;
    }

    /// <summary>
    /// Test changed 2026-10-20 (ASSETS fix S7): on gb-synthetic (the one bundled case with protections: two devices, both
    /// monitoring bus 1) the transformer page of "NSC GSP 2 SGT" (bus 1 to bus 96, the one transformer at bus 1) goes
    /// through the S7 exception, frame after frame, with the old page's demo rows and the fixed page's real rows each
    /// checked exactly, and its tag flag false; the page of a transformer with no protection (the first transformer of the
    /// case) is compared exactly, demo rows and all, with its tag flag true.
    /// </summary>
    [Fact]
    public void The_transformer_protection_panel_shows_the_cases_settings_where_it_has_them()
    {
        var feed = new Feed("gb-synthetic");
        (SimFrame f, string clock) = feed.Next();
        GridModel m = f.Model;
        Assert.NotNull(m.Protections);
        int withIdx = Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].IsTransformer && m.Branches[i].Name == "NSC GSP 2 SGT");
        int withoutIdx = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer
            && m.Protections!.Instances.All(d => d.MonitorBus != m.Branches[i].From && d.MonitorBus != m.Branches[i].To));

        S7Hits = 0;
        S7TagChecks = 0;
        var t = new Twin();
        t.Update(f, clock, "gb-synthetic: home");
        Branch with = m.Branches[withIdx];
        t.Do(o => o.OpenTransformer(withIdx, with), n => n.OpenTransformer(withIdx, with), "gb-synthetic: NSC GSP 2 SGT opened");
        for (int k = 0; k < 3; k++)
        {
            (f, clock) = feed.Next();
            t.Update(f, clock, $"gb-synthetic: NSC GSP 2 SGT frame {k}");
        }
        Assert.True(S7Hits >= 3, $"the S7 exception applied {S7Hits} times on the protected transformer");
        var page = Assert.IsAssignableFrom<TransformerDetailModel<TransformerReadingModel, TransformerEventModel>>(t.NewX.Current);
        Assert.False(page.ProtectionIsDemo);

        int hitsBefore = S7Hits, tagsBefore = S7TagChecks;
        t.Back("gb-synthetic: back");
        Branch without = m.Branches[withoutIdx];
        t.Do(o => o.OpenTransformer(withoutIdx, without), n => n.OpenTransformer(withoutIdx, without), "gb-synthetic: unprotected transformer opened");
        (f, clock) = feed.Next();
        t.Update(f, clock, "gb-synthetic: unprotected transformer, a frame");
        Assert.Equal(hitsBefore, S7Hits);                 // compared exactly: the demo rows, as before
        Assert.True(S7TagChecks > tagsBefore);
        var plain = Assert.IsAssignableFrom<TransformerDetailModel<TransformerReadingModel, TransformerEventModel>>(t.NewX.Current);
        Assert.True(plain.ProtectionIsDemo);
    }

    /// <summary>One value of the old code against the neutral value that replaced it.</summary>
    internal static void SameValue(object? ov, object? nv, string what)
    {
        switch (nv)
        {
            case null:
                Assert.True(ov is null, $"{what}: want {ov}, got null");
                return;
            case string s:
                Assert.True(ov is string os && string.Equals(os, s, StringComparison.Ordinal), $"{what}: want \"{ov}\", got \"{s}\"");
                return;
            case double d:
                Assert.True(ov is double od && BitConverter.DoubleToInt64Bits(od) == BitConverter.DoubleToInt64Bits(d), $"{what}: want {ov:R}, got {d:R}");
                return;
            case int i:
                Assert.True(ov is int oi && oi == i, $"{what}: want {ov}, got {i}");
                return;
            case UiColour c when ov is Old.Color oc:
                Assert.True(oc.Token == c, $"{what}: want {ColourRamps.ToHex(oc.Token)}, got {ColourRamps.ToHex(c)}");
                return;
            case UiColour c:
                // The token must be the colour of the brush the old code chose.
                var brush = Assert.IsType<Old.SolidColorBrush>(ov);
                Assert.True(brush.Color.Token == c, $"{what}: want {ColourRamps.ToHex(brush.Color.Token)}, got {ColourRamps.ToHex(c)}");
                return;
            case bool b when ov is Old.Visibility vis:
                Assert.True((b ? Old.Visibility.Visible : Old.Visibility.Collapsed) == vis, $"{what}: want {vis}, got {b}");
                return;
            case bool b:
                Assert.True(ov is bool ob && ob == b, $"{what}: want {ov}, got {b}");
                return;
            case Action:
                // A row's click: the old command (what it opens is compared by the navigation steps).
                Assert.IsAssignableFrom<System.Windows.Input.ICommand>(ov);
                return;
            case IList list:
                var olist = Assert.IsAssignableFrom<IList>(ov);
                Assert.True(olist.Count == list.Count, $"{what}: want {olist.Count} rows, got {list.Count}");
                for (int k = 0; k < list.Count; k++) SameRow(olist[k], list[k], $"{what}[{k}]");
                return;
            case AssetPageModel or SubstationMapInput or SubstationMapConnection or SubstationMapGenerator:
                Assert.NotNull(ov);
                AssertSame(ov!, nv, what);
                return;
            default:
                Assert.Fail($"{what}: no comparison for a {nv.GetType().Name}");
                return;
        }
    }

    /// <summary>One row of a list (a number, a string, or a row object, every public property compared).</summary>
    private static void SameRow(object? orow, object? nrow, string what)
    {
        if (nrow is string or double or null) { SameValue(orow, nrow, what); return; }
        Assert.NotNull(orow);
        AssertSame(orow!, nrow, what);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison is not vacuous
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_comparison_catches_one_bit_one_byte_and_one_character()
    {
        double x = 0.1 + 0.2;
        double y = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) + 1);
        Assert.ThrowsAny<Exception>(() => SameValue(x, y, "one bit"));
        SameValue(x, x, "same double");
        var grey = Old.Palette.Frozen(new Old.Color(0xFF, 0x80, 0x80, 0x80));
        Assert.ThrowsAny<Exception>(() => SameValue(grey, new UiColour(0xFF, 0x80, 0x80, 0x81), "one byte"));
        SameValue(grey, DashboardColours.Gray, "same colour");
        Assert.ThrowsAny<Exception>(() => SameValue("12.3 MW", "12,3 MW", "one character"));
        Assert.ThrowsAny<Exception>(() => SameValue(Old.Visibility.Collapsed, true, "visibility"));

        // A whole page: one changed property of a fresh page is caught.
        var oldPage = new Old.InterconnectorFleetVm(new Old.AssetExplorerVm());
        var newPage = new InterconnectorFleetModel<LinkFleetRowModel>(new AssetExplorerModel());
        AssertSame(oldPage, newPage, "fresh pages");
        newPage.Largest = "x";
        Assert.ThrowsAny<Exception>(() => AssertSame(oldPage, newPage, "changed page"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The WPF side is only an adapter, and the XAML still finds every name it binds
    // ---------------------------------------------------------------------------------------------------------------

    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { PresentationConstantsAntiDriftTests.RepoRoot(), "src" }.Concat(parts).ToArray()));

    private static readonly string[] PageFiles = { "AssetPages", "GeneratorPages", "TransformerPages", "CircuitPages", "SubstationPages" };

    [Fact]
    public void The_wpf_pages_derive_from_the_moved_models_and_keep_no_logic()
    {
        string all = string.Concat(PageFiles.Select(n => Src("GridSim.Wpf", "Views", "Assets", n + ".cs")));
        foreach (string adapter in new[]
        {
            "public sealed class AssetExplorerVm : AssetExplorerModel",
            "public sealed class AssetHomeVm(AssetExplorerVm explorer) : AssetHomeModel<AssetClassCard>(explorer);",
            "public sealed class InterconnectorFleetVm(AssetExplorerVm explorer) : InterconnectorFleetModel<LinkFleetRow>(explorer);",
            "public sealed class InterconnectorDetailVm(int index, int unitBus, string? unitName, AssetExplorerVm explorer)",
            "    : InterconnectorDetailModel(index, unitBus, unitName, explorer)",
            "public sealed class GeneratorFleetVm(AssetExplorerVm explorer) : GeneratorFleetModel<GenFleetRow>(explorer);",
            "    : GeneratorDetailModel(index, unitBus, unitName, explorer)",
            "public sealed class GeneratorPhasorFleetVm(AssetExplorerVm explorer) : GeneratorPhasorFleetModel<PhasorTile>(explorer);",
            "public sealed class TransformerFleetVm(AssetExplorerVm explorer) : TransformerFleetModel<TransformerFleetRow>(explorer);",
            "    : TransformerDetailModel<TransformerReading, TransformerEvent>(index, fromBus, toBus, branchName, explorer)",
            "public sealed class TapChangerFleetVm(AssetExplorerVm explorer) : TapChangerFleetModel<TapChangerRow>(explorer);",
            "public sealed class CircuitFleetVm(AssetExplorerVm explorer) : CircuitFleetModel<BandChip, CircuitFleetRow>(explorer);",
            "    : CircuitDetailModel<CircuitStatusRow>(index, fromBus, toBus, branchName, explorer)",
            "public sealed class SubstationFleetVm(AssetExplorerVm explorer) : SubstationFleetModel<KvFilterChip, SubstationFleetRow>(explorer);",
            "    : SubstationDetailModel<PlantRow, ConnectionRow>(busId, explorer)",
        })
            Assert.Contains(adapter, all, StringComparison.Ordinal);

        // No page logic left in WPF: no refresh, no lookups, no solver, no formatting, no colour arithmetic.
        foreach (string gone in new[] { "Refresh(", "AssetLookup", "SteadyStateSolver", "FirstOrDefault", "$\"", "Palette.Loading", "Palette.Voltage", "FromHex", "Fuels.Classify", "Brushes.Gray" })
            Assert.DoesNotContain(gone, all, StringComparison.Ordinal);

        // The explorer makes the WPF page for every factory, so every DataTemplate still matches.
        string explorer = Src("GridSim.Wpf", "Views", "Assets", "AssetPages.cs");
        foreach (MethodInfo factory in typeof(AssetExplorerModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(mi => mi.IsVirtual && mi.Name.StartsWith("Create", StringComparison.Ordinal)))
            Assert.Contains($"public override AssetPageModel {factory.Name}(", explorer, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_wpf_brush_follows_its_token_and_every_xaml_binding_still_has_its_property()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string wpf = string.Concat(PageFiles.Select(n => Src("GridSim.Wpf", "Views", "Assets", n + ".cs")));
        string ui = string.Concat(PageFiles.Select(n => Src("GridSim.Ui", "Assets", "Pages", n + ".cs")));

        // Each brush is the frozen brush of its token; a token that changes raises the brush too, unless the token is
        // fixed at creation (init only).
        var brushes = Regex.Matches(wpf, @"public Brush (\w+) => Palette\.Frozen\((\w+)\);");
        Assert.True(brushes.Count >= 20, $"only {brushes.Count} brushes found");
        foreach (Match b in brushes)
        {
            string brush = b.Groups[1].Value, token = b.Groups[2].Value;
            bool raised = wpf.Contains($"(name == nameof({token})) base.OnPropertyChanged(nameof({brush}));", StringComparison.Ordinal);
            bool fixedAtCreation = ui.Contains($"public UiColour {token} {{ get; init; }}", StringComparison.Ordinal);
            Assert.True(raised || fixedAtCreation, $"{brush} does not follow {token}");
        }

        // Every name the asset templates bind exists as a public property of a moved model or a WPF adapter.
        var props = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match p in Regex.Matches(ui + wpf, @"public (?:override )?[\w<>?,\[\] ]+? (\w+)\s*(?:\{|=>)")) props.Add(p.Groups[1].Value);
        foreach (string xaml in Directory.GetFiles(Path.Combine(root, "src", "GridSim.Wpf", "Views", "Assets"), "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{Binding (?:Path=)?([A-Za-z0-9_.]+)"))
                foreach (string part in m.Groups[1].Value.Split('.'))
                    if (part != "Assets")   // the tab's own DataContext path on the window's view-model
                        Assert.True(props.Contains(part), $"{Path.GetFileName(xaml)} binds {part}, which no asset page or row has");
        }
    }
}
