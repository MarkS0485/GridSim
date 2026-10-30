using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Shell;

namespace GridSim.UiSmoke;

/// <summary>
/// The capture routine (UI programme Phase 1 item 7). For each case it opens the real <see cref="MainWindow"/> at the
/// asked size, with everything that could reach outside the window replaced (<see cref="ShellOptionsFor"/>: an isolated
/// settings file, no start-up load, no bridge, no dialogs, no OS pickers, the free licence tier so the pictures do not
/// depend on the machine's licence), loads the case through the window's own run pipeline, pauses it and plays exactly
/// the asked number of ticks one STEP ONCE at a time, then captures every page to PNG:
/// <list type="bullet">
/// <item><description>every room tab in the window's order (the NETWORK dashboard with the map fitted, and once more
/// dived in on the busiest 33 kV substation as AssetSmoke does; then GENERATION &amp; INERTIA to CYCLE);</description></item>
/// <item><description>inside ASSETS every page kind: the landing page, the seven fleet pages and the unit pages AssetSmoke
/// opens (the same units, by the same rule), then every other class page and one unit of every other kind the tree has
/// (DEMANDS, BOUNDARIES, PROTECTIONS, ASSET CLASSES, OUT OF SERVICE, the OVERLAY when the case has one), the PROPERTIES
/// and CONNECTIONS sub-tabs on a substation, IEC VIEW on a substation and a transformer, and AUTHOR mode's home on a
/// temporary copy of the case (the case's own files are never opened for editing).</description></item>
/// </list>
/// Each page's PNG is checked before it counts: exactly the asked size, and the page's own area must hold some pixels
/// that are not its background. A page that throws or draws nothing is a failure (named, with the reason); the run goes
/// on to the next page, as AssetSmoke does. Must run on Avalonia's UI thread with the headless platform set up
/// (<see cref="Program"/> does that; the app's tests run it inside an [AvaloniaFact]).
/// </summary>
public static class UiSmokeRunner
{
    /// <summary>How long a page is left to settle (bindings, a background model, a second layout) before its capture.</summary>
    public const int SettleMs = 200;

    /// <summary>The longest wait for one thing (a load, a stepped frame, a background model) before it is a failure.</summary>
    public const int TimeoutMs = 180_000;

    /// <summary>The page name of the case itself in a failure (the case did not load, the run had an error).</summary>
    public const string CasePage = "(case)";

    // The AssetSmoke page names of the room tabs WPF also has under another name (AssetSmoke's labels with spaces made
    // dashes); every other tab is named from its header (TabFileName).
    private static readonly Dictionary<string, string> TabNames = new(StringComparer.Ordinal)
    {
        ["GENERATION & INERTIA"] = "generation-tab",
        [TabModels.MachineHeader] = "the-machine",
        [TabModels.ScadaHeader] = "scada",
    };

    /// <summary>The page name of the NETWORK tab (the AssetSmoke name of the fitted map).</summary>
    public const string NetworkFitted = "network-map-fitted";

    /// <summary>The page name of the NETWORK tab dived in (the AssetSmoke name).</summary>
    public const string NetworkDivedIn = "network-map-dived-in";

    /// <summary>The page name of AUTHOR mode's home on the ASSETS tab.</summary>
    public const string AuthorHome = "assets-author-home";

    /// <summary>
    /// Run every case of <paramref name="options"/>, writing the PNGs, manifest.json and index.html, and return what was
    /// captured. Progress lines go to <paramref name="log"/> in AssetSmoke's form ("  ok      page", "  CRASH   page: why").
    /// </summary>
    public static UiSmokeReport Run(UiSmokeOptions options, TextWriter log, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        Dispatcher.UIThread.VerifyAccess();
        var clock = Stopwatch.StartNew();
        string outDir = options.ResolveOutputFolder(utcNow ?? DateTime.UtcNow);
        Directory.CreateDirectory(outDir);
        var report = new UiSmokeReport { OutputFolder = outDir, Width = options.Width, Height = options.Height, Ticks = options.Ticks };
        log.WriteLine($"UiSmoke: {options.Cases.Count} case(s), {options.Ticks} ticks, {options.Width}x{options.Height}, into {outDir}");

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string arg in options.Cases)
        {
            // Two cases with the same short name get a running number, so neither overwrites the other's pictures.
            string slug = UiSmokeCases.Slug(arg);
            for (int n = 2; !slugs.Add(slug); n++) slug = UiSmokeCases.Slug(arg) + "-" + n.ToString(CultureInfo.InvariantCulture);
            RunCase(options, arg, slug, outDir, report, log);
        }

        // A --pages entry that matched nothing in any case is a failure: a misspelt name must not pass as "done".
        if (options.Pages is { } wanted)
            foreach (string entry in wanted)
                if (!report.Pages.Any(p => UiSmokeOptions.Matches(entry, p.Page))
                    && !report.Failures.Any(f => UiSmokeOptions.Matches(entry, f.Page))
                    && !report.Skipped.Any(s => UiSmokeOptions.Matches(entry, s.Page)))
                    report.Failures.Add(new UiSmokeFailure("(all)", entry, "--pages entry matched no page of any case"));

        report.Elapsed = clock.Elapsed;
        report.Write();
        log.WriteLine(report.Ok
            ? $"ALL PAGES OK ({report.Pages.Count} pages in {report.Elapsed.TotalSeconds:0.0} s): {report.IndexPath}"
            : $"{report.Failures.Count} FAILURE(S) ({report.Pages.Count} pages captured in {report.Elapsed.TotalSeconds:0.0} s): "
              + string.Join(", ", report.Failures.Select(f => $"{f.Case}/{f.Page}")));
        return report;
    }

    /// <summary>
    /// The window's options for a smoke run of <paramref name="source"/>: the settings file is
    /// <paramref name="settingsFile"/> (never the user's), the catalogue holds this one case, nothing loads at start-up,
    /// the bridge is off, the free licence tier is used, the map's geography is read as the app reads it (local files
    /// only), every dialog, picker and Author-mode question is a recorded no-op (<paramref name="calls"/>), and Author mode
    /// is pointed at <paramref name="scratch"/> (no bundled root, Save As suggests a folder there).
    /// </summary>
    /// <summary>
    /// The scratch folder of one case's run (the AUTHOR temporary copy, Save As suggestions), made inside that case's
    /// output folder (2026-10-24). It used to be under the user's temp folder, so the AUTHOR home picture printed a path
    /// with the Windows account name in it; under the output folder the picture shows only the path given to --out (put
    /// it inside the repository for pictures that go into the docs). Removed when the case's run ends.
    /// </summary>
    public static string ScratchFolderFor(string caseDir) => Path.Combine(caseDir, ".scratch-" + Guid.NewGuid().ToString("N"));

    public static ShellOptions ShellOptionsFor(ModelSource source, string settingsFile, string scratch, List<string> calls)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(calls);
        return new ShellOptions
        {
            SettingsStore = ShellSettingsStore.ForFile(Path.GetFullPath(settingsFile)),
            DiscoverCases = (_, _) => new List<ModelSource> { source },
            Licence = () => new LicenceGate(Entitlement.FreeDefault),
            LicenceDescription = () => "UiSmoke (free tier)",
            Actions = _ => new SmokeShellActions(calls),
            Pickers = _ => new SmokePickers(calls),
            AuthorPrompts = _ => new SmokeAuthorPrompts(calls),
            AuthorBundledRoots = new[] { Path.Combine(scratch, "no-bundled-root") },
            AuthorUserCasesFolder = Path.Combine(scratch, "user-cases"),
            UseNativeMenu = false,
            AutoLoadAtStart = false,
            LaunchArguments = () => Array.Empty<string>(),
            LaunchRefused = (_, text) => calls.Add("launch refused: " + text),
            BridgeEnabled = false,
        };
    }

    // ======================================== one case ========================================

    private static void RunCase(UiSmokeOptions options, string arg, string slug, string outDir, UiSmokeReport report, TextWriter log)
    {
        ModelSource source;
        try
        {
            source = UiSmokeCases.Resolve(arg);
        }
        catch (ArgumentException ex)
        {
            report.Failures.Add(new UiSmokeFailure(slug, CasePage, ex.Message));
            log.WriteLine($"  CRASH   {slug}: {ex.Message}");
            return;
        }

        string caseDir = Path.Combine(outDir, slug);
        Directory.CreateDirectory(caseDir);
        string settingsFile = Path.Combine(caseDir, "settings.json");
        string scratch = ScratchFolderFor(caseDir);
        Directory.CreateDirectory(scratch);
        log.WriteLine($"{slug}: {source.Label}  ({source.MFile ?? source.V2Case ?? source.Dir})");

        var w = new MainWindow(ShellOptionsFor(source, settingsFile, scratch, report.ShellCalls))
        {
            WindowState = WindowState.Normal,
            Width = options.Width,
            Height = options.Height,
        };
        var run = new CaseRun(options, slug, caseDir, w, report, log, scratch);
        int tick = 0;
        w.Show();
        try
        {
            tick = run.LoadAndPlay(source);
            run.CaptureTabs();
            run.CaptureAssets();
            run.CaptureAuthor(source);
        }
#pragma warning disable CA1031 // Not swallowed: the case is reported as failed with the reason, and the run goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            report.Failures.Add(new UiSmokeFailure(slug, CasePage, Describe(ex)));
            log.WriteLine($"  CRASH   {slug}: {Describe(ex)}");
        }
        finally
        {
            Close(w);
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a file still closing; the OS temp clean-up takes it */ }
            catch (UnauthorizedAccessException) { /* as above */ }
        }
        report.Cases.Add(new UiSmokeCase(slug, arg, source.Label, source.MFile ?? source.V2Case ?? source.Dir, tick, settingsFile));
    }

    /// <summary>An exception in one line: the innermost cause's type and message.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Exception b = ex.GetBaseException();
        return $"{b.GetType().Name}: {b.Message}";
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) PumpUntil(() => stop.IsCompleted, "the play loop to stop");
    }

    // ======================================== pumping the UI thread ========================================

    /// <summary>Run the UI thread's queued work until <paramref name="done"/> holds; a timeout is a failure.</summary>
    internal static void PumpUntil(Func<bool> done, string what, int timeoutMs = TimeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Run the UI thread's queued work for <paramref name="ms"/> milliseconds.</summary>
    internal static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    /// <summary>Wait (pumping) for <paramref name="task"/> and rethrow its failure.</summary>
    internal static void Await(Task task, string what)
    {
        PumpUntil(() => task.IsCompleted, what);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Wait (pumping) for <paramref name="task"/> and return its result.</summary>
    internal static T Await<T>(Task<T> task, string what)
    {
        PumpUntil(() => task.IsCompleted, what);
        return task.GetAwaiter().GetResult();
    }

    // ======================================== naming ========================================

    /// <summary>
    /// AssetSmoke's file name for a page label (its Shot: spaces and slashes become dashes), with any other character a
    /// file name cannot hold on some OS made an underscore, so the same label gives the same name as the WPF set.
    /// </summary>
    public static string PageName(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        string name = label.Replace(' ', '-').Replace("/", "-", StringComparison.Ordinal);
        char[] bad = { '\\', ':', '*', '?', '"', '<', '>', '|' };
        var chars = name.Select(c => c < 32 || bad.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>The page name of a room tab: AssetSmoke's name where WPF's smoke run has one, else its header made lower
    /// case with every run of other characters one dash (MARKET is market, NESO RP1 is neso-rp1).</summary>
    public static string TabFileName(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (TabNames.TryGetValue(header, out string? known)) return known;
        return Kebab(header);
    }

    /// <summary>Lower case, every run of characters that are not letters or digits one dash, no dash at either end; a
    /// camel-case name is split at its capitals (OutOfServiceUnit is out-of-service-unit).</summary>
    public static string Kebab(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (char.IsUpper(c) && i > 0 && char.IsLower(text[i - 1]) && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    // ======================================== pixels ========================================

    /// <summary>
    /// How many pixels of <paramref name="area"/> (in frame pixels, clipped to the frame) differ by more than a small
    /// tolerance in any channel from that area's most common colour: zero for an area that drew nothing but a fill.
    /// </summary>
    public static long Ink(WriteableBitmap frame, PixelRect area)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using ILockedFramebuffer fb = frame.Lock();
        int x0 = Math.Clamp(area.X, 0, fb.Size.Width), y0 = Math.Clamp(area.Y, 0, fb.Size.Height);
        int x1 = Math.Clamp(area.Right, 0, fb.Size.Width), y1 = Math.Clamp(area.Bottom, 0, fb.Size.Height);
        if (x1 <= x0 || y1 <= y0) return 0;
        int width = x1 - x0;
        var row = new byte[width * 4];
        // The most common colour of the area: its background.
        var counts = new Dictionary<uint, int>();
        for (int y = y0; y < y1; y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes + x0 * 4, row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                uint px = BitConverter.ToUInt32(row, x * 4);
                counts[px] = counts.GetValueOrDefault(px) + 1;
            }
        }
        uint bg = counts.MaxBy(kv => kv.Value).Key;
        const int Tolerance = 12;   // per channel: anti-aliasing at a fill's edge is not ink, a drawn glyph or line is
        long ink = 0;
        foreach ((uint px, int n) in counts)
        {
            int d = 0;
            for (int s = 0; s < 32; s += 8) d = Math.Max(d, Math.Abs((int)((px >> s) & 0xFF) - (int)((bg >> s) & 0xFF)));
            if (d > Tolerance) ink += n;
        }
        return ink;
    }

    // ======================================== the per-case work ========================================

    /// <summary>One case's window and the captures made in it.</summary>
    private sealed class CaseRun(UiSmokeOptions options, string slug, string caseDir, MainWindow w, UiSmokeReport report, TextWriter log, string scratch)
    {
        private MainWindowViewModel Vm => w.ViewModel;
        private RunViewModel Run => w.ViewModel.Run;
        private AssetNavigatorModel Nav => w.ViewModel.Tabs.Assets;

        // ---- the run: load, pause, exactly N ticks ----

        /// <summary>
        /// Load <paramref name="source"/> through the run pipeline (File > Open case's own path), pause it at once, then
        /// play exactly <see cref="UiSmokeOptions.Ticks"/> ticks one STEP ONCE at a time. Returns the tick shown. A
        /// refused load, a run error (a solve fault, a frame that is not a valid state) or a run that had already passed
        /// the asked tick before it could be paused fails the case: a picture of a broken run must not pass as a good one.
        /// </summary>
        public int LoadAndPlay(ModelSource source)
        {
            int frames = 0;
            Run.FrameArriving += (_, _) => frames++;
            Await(Vm.LoadCaseAsync(source), "the case to load");
            if (Run.HasError) throw new InvalidOperationException("the case did not load: " + Run.ErrorText);
            // The host plays from the load; pause it before the next interval so every tick from here is a counted step.
            if (!Run.IsPaused) Await(Run.TogglePlayAsync(), "the run to pause");
            PumpFor(300);   // a frame that was already being made lands now
            // On a large case (gb-full, 3,539 buses) that frame can take longer than the 300 ms: wait until the frame
            // shown is the host's tick, or the first step below is asked for while it is still being made and is lost.
            PumpUntil(() => Run.Host.Tick == 0 || Run.LastFrame?.Tick == Run.Host.Tick, "the frame being made at the pause to land");
            int tick = Run.Host.Tick;
            if (tick > options.Ticks)
                throw new InvalidOperationException($"the run had reached tick {tick} before it could be paused, past --ticks {options.Ticks}");
            while (tick < options.Ticks)
            {
                int before = frames;
                Await(Run.StepOnceAsync(), "a step to start");
                PumpUntil(() => frames > before && Run.Host.Tick > tick, $"tick {tick + 1}");
                tick = Run.Host.Tick;
            }
            PumpFor(SettleMs);
            if (Run.HasError) throw new InvalidOperationException("the run has an error after the steps: " + Run.ErrorText);
            if (Run.LastFrame is not { } last || last.Tick != options.Ticks)
                throw new InvalidOperationException($"the frame shown is tick {Run.LastFrame?.Tick.ToString(CultureInfo.InvariantCulture) ?? "none"}, not {options.Ticks}");
            log.WriteLine($"  loaded  {Run.LoadedText}; tick {last.Tick}");
            return last.Tick;
        }

        // ---- capturing one page ----

        /// <summary>
        /// Capture page <paramref name="name"/> (when --pages wants it): run <paramref name="prepare"/> to show it, let it
        /// render and settle, check the frame's size and that the page's own area drew something, and write the PNG. A
        /// failure is recorded with the reason and the run goes on.
        /// </summary>
        public void Capture(string name, Action prepare)
        {
            if (!options.Wants(name)) return;
            var clock = Stopwatch.StartNew();
            try
            {
                prepare();
                Render();
                PumpFor(SettleMs);
                Render();
                using WriteableBitmap frame = w.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("the headless renderer returned no frame");
                if (frame.PixelSize.Width != options.Width || frame.PixelSize.Height != options.Height)
                    throw new InvalidOperationException($"the frame is {frame.PixelSize.Width}x{frame.PixelSize.Height}, not the asked {options.Width}x{options.Height}");
                long ink = Ink(frame, ContentArea());
                if (ink == 0) throw new InvalidOperationException("the page drew nothing (its area is one flat colour)");
                string file = Path.Combine(caseDir, name + ".png");
                using (FileStream fs = File.Create(file)) frame.Save(fs);
                byte[] bytes = File.ReadAllBytes(file);
                string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                string rel = Path.GetRelativePath(report.OutputFolder, file).Replace('\\', '/');
                report.Pages.Add(new UiSmokePage(slug, name, rel, frame.PixelSize.Width, frame.PixelSize.Height, bytes.LongLength, sha, ink, clock.ElapsedMilliseconds));
                log.WriteLine($"  ok      {name}");
            }
#pragma warning disable CA1031 // Not swallowed: the page is reported as failed with the reason, as AssetSmoke's Check does.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                report.Failures.Add(new UiSmokeFailure(slug, name, Describe(ex)));
                log.WriteLine($"  CRASH   {name}: {Describe(ex)}");
            }
        }

        private void Skip(string name, string reason)
        {
            if (!options.Wants(name)) return;
            report.Skipped.Add(new UiSmokeSkip(slug, name, reason));
            log.WriteLine($"  skip    {name}: {reason}");
        }

        // Lay out and render once, so virtualised lists are realised and templates have instantiated.
        private void Render()
        {
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
        }

        // The selected tab's own content, in frame pixels: the page itself, without the window's header, the tab strip,
        // the transport bar and the status bar (they draw on every page, so they would make a blank page look drawn).
        private PixelRect ContentArea()
        {
            Control page = (w.TabStrip.SelectedItem as TabItem)?.Content as Control
                ?? throw new InvalidOperationException("no tab is selected, so there is no page to check");
            Point? origin = page.TranslatePoint(default, w);
            if (origin is not { } o || page.Bounds.Width < 1 || page.Bounds.Height < 1)
                throw new InvalidOperationException("the page is not laid out in the window");
            double scale = w.RenderScaling;
            return new PixelRect((int)Math.Floor(o.X * scale), (int)Math.Floor(o.Y * scale),
                (int)Math.Ceiling(page.Bounds.Width * scale), (int)Math.Ceiling(page.Bounds.Height * scale));
        }

        // ---- the room tabs ----

        /// <summary>Every room tab in the window's order (ASSETS separately, page by page).</summary>
        public void CaptureTabs()
        {
            foreach (string header in w.TabStrip.Items.OfType<TabItem>().Select(t => t.Header as string ?? ""))
            {
                switch (header)
                {
                    case "NETWORK":
                        // As AssetSmoke draws the WPF map (ShowFleet and ShowCoast on), so the two map pictures pair up;
                        // the window's own defaults (both off, the View menu's C and the FLEET button) are left alone
                        // in the settings file, since these are set on the control only.
                        w.MapControl.ShowCoast = true;
                        w.MapControl.ShowFleet = true;
                        Capture(NetworkFitted, () =>
                        {
                            w.SelectTab(header);
                            Render();
                            w.MapControl.FitToView();
                        });
                        Capture(NetworkDivedIn, () =>
                        {
                            w.SelectTab(header);
                            Render();
                            DiveIn();
                        });
                        // Leave the map fitted, as the next run of the NETWORK tab would expect.
                        w.MapControl.FitToView();
                        break;
                    case MainWindow.AssetsTabHeader:
                        break;   // CaptureAssets
                    case "":
                        report.Failures.Add(new UiSmokeFailure(slug, "(tab)", "a room tab has no text header, so it cannot be named"));
                        break;
                    case TabModels.ExperimentalHeader:
                        Capture(TabFileName(header), () =>
                        {
                            w.SelectTab(header);
                            // EXPERIMENTAL computes on a worker once its tab shows: wait for its verdict, so the picture
                            // shows the tab's result, not its placeholder.
                            PumpUntil(() => Vm.Tabs.Experimental.Gate != " - ", "the EXPERIMENTAL verdict");
                        });
                        break;
                    default:
                        Capture(TabFileName(header), () => w.SelectTab(header));
                        break;
                }
            }
        }

        // AssetSmoke's "network map dived-in": centre on the busiest 33 kV substation (falling back to the first bus) and
        // zoom in five times by two at the map's centre, so the level of detail shows every voltage level on the way down.
        private void DiveIn()
        {
            GridModel model = Run.LastFrame?.Model ?? throw new InvalidOperationException("no frame is shown");
            var degree = new Dictionary<int, int>();
            foreach (Branch b in model.Branches)
            {
                degree[b.From] = degree.GetValueOrDefault(b.From) + 1;
                degree[b.To] = degree.GetValueOrDefault(b.To) + 1;
            }
            Bus target = model.Buses
                .Where(b => b.BaseKv == 33)
                .OrderByDescending(b => degree.GetValueOrDefault(b.Id))
                .FirstOrDefault() ?? model.Buses[0];
            w.MapControl.CenterOn(target.Id);
            Render();
            var centre = new Point(w.MapControl.Bounds.Width / 2, w.MapControl.Bounds.Height / 2);
            for (int i = 0; i < 5; i++)
            {
                w.MapControl.ZoomAt(centre, 2.0);
                Render();
            }
        }

        // ---- ASSETS ----

        // The seven A2 fleet pages, with AssetSmoke's names.
        private static readonly (AssetPageKind Kind, string Name)[] Fleets =
        {
            (AssetPageKind.InterconnectorFleet, "interconnector fleet"),
            (AssetPageKind.GeneratorFleet, "generator fleet"),
            (AssetPageKind.PhasorFleet, "generator phasors"),
            (AssetPageKind.TransformerFleet, "transformer fleet"),
            (AssetPageKind.TapChangerFleet, "tap changer fleet"),
            (AssetPageKind.CircuitFleet, "circuit fleet"),
            (AssetPageKind.SubstationFleet, "substation fleet"),
        };

        // The A2 unit page kinds (captured by AssetSmoke's rule, not as "one of each kind").
        private static readonly HashSet<AssetPageKind> A2Units = new()
        {
            AssetPageKind.Interconnector, AssetPageKind.Generator, AssetPageKind.Transformer, AssetPageKind.Circuit, AssetPageKind.Substation,
        };

        /// <summary>Every ASSETS page kind (see the class remarks).</summary>
        public void CaptureAssets()
        {
            w.SelectTab(MainWindow.AssetsTabHeader);
            Render();
            GridModel model = Run.LastFrame?.Model ?? throw new InvalidOperationException("no frame is shown");

            // AssetSmoke's unit choice: the first transformer, the first line, the first generator that is not an
            // interconnector, the first interconnector and the first bus. "Interconnector" is the explorer tree's rule, the
            // one Core link rule (AssetIndex.IsLink, bug #28), and a generator is plant (AssetIndex.IsPlant): on gb-full the
            // first unit is the "grid" infeed at Melksham, which is neither and has no GENERATORS or INTERCONNECTORS node.
            static bool IsLink(Generator g) => AssetIndex.IsLink(g);
            int txIdx = Enumerable.Range(0, model.Branches.Count).FirstOrDefault(i => model.Branches[i].IsTransformer, -1);
            int lineIdx = Enumerable.Range(0, model.Branches.Count).FirstOrDefault(i => !model.Branches[i].IsTransformer, -1);
            int genIdx = Enumerable.Range(0, model.Generators.Count).FirstOrDefault(i => AssetIndex.IsPlant(model.Generators[i]), -1);
            int icIdx = Enumerable.Range(0, model.Generators.Count).FirstOrDefault(i => IsLink(model.Generators[i]), -1);
            int busId = model.Buses[0].Id;

            Capture("home", () => Select(Nav.Tree.Home));
            var done = new HashSet<AssetPageKind> { AssetPageKind.Home };
            string? substationPage = null, transformerPage = null;
            AssetTreeNode? substationNode = null, transformerNode = null;

            foreach ((AssetPageKind kind, string label) in Fleets)
            {
                done.Add(kind);
                AssetTreeNode? fleet = Nav.Tree.NodeFor(AssetPageKey.Fleet(kind));
                if (fleet is null) Skip(PageName(label), "the explorer tree has no node for this page");
                else Capture(PageName(label), () => Select(fleet));

                // The unit page AssetSmoke opens after this fleet page (same order as its run).
                switch (kind)
                {
                    case AssetPageKind.InterconnectorFleet:
                        if (icIdx < 0) Skip("interconnector-detail", "the case has no interconnector");
                        else
                        {
                            Generator ic = model.Generators[icIdx];
                            Unit($"interconnector detail ({ic.Name ?? ""})", AssetPageKey.Interconnector(icIdx, ic.Bus, ic.Name), AssetPageKind.Interconnector);
                        }
                        break;
                    case AssetPageKind.GeneratorFleet:
                        if (genIdx < 0) Skip("generator-detail", "the case has no generator that is not an interconnector");
                        else
                        {
                            Generator g = model.Generators[genIdx];
                            Unit($"generator detail ({g.Name ?? ""})", AssetPageKey.Generator(genIdx, g.Bus, g.Name), AssetPageKind.Generator);
                        }
                        break;
                    case AssetPageKind.TransformerFleet:
                        if (txIdx < 0) Skip("transformer-detail", "the case has no transformer");
                        else
                        {
                            Branch t = model.Branches[txIdx];
                            (transformerPage, transformerNode) = Unit($"transformer detail ({t.Name ?? ""})", AssetPageKey.Transformer(txIdx, t.From, t.To, t.Name), AssetPageKind.Transformer);
                        }
                        break;
                    case AssetPageKind.CircuitFleet:
                        if (lineIdx < 0) Skip("circuit-detail", "the case has no line or cable");
                        else
                        {
                            Branch l = model.Branches[lineIdx];
                            Unit($"circuit detail ({l.Name ?? ""})", AssetPageKey.Circuit(lineIdx, l.From, l.To, l.Name), AssetPageKind.Circuit);
                        }
                        break;
                    case AssetPageKind.SubstationFleet:
                        (substationPage, substationNode) = Unit($"substation detail (bus {busId.ToString(CultureInfo.InvariantCulture)})", AssetPageKey.Substation(busId), AssetPageKind.Substation);
                        break;
                }
            }
            done.UnionWith(A2Units);

            // Every other page kind the tree has (the B2 classes and their members, the overlay): its first node.
            foreach (AssetTreeNode node in Nav.Tree.PreOrder())
            {
                if (!done.Add(node.Page.Kind)) continue;
                AssetTreeNode n = node;
                Capture("assets-" + Kebab(n.Page.Kind.ToString()), () => Select(n));
            }
            foreach (AssetPageKind kind in new[] { AssetPageKind.OverlayFleet })
                if (!done.Contains(kind)) Skip("assets-" + Kebab(kind.ToString()), "the case has no asset overlay");

            // The PROPERTIES and CONNECTIONS sub-tabs, on the substation AssetSmoke opens.
            foreach (string sub in new[] { AssetNavigatorModel.PropertiesTab, AssetNavigatorModel.ConnectionsTab })
            {
                string name = "assets-" + Kebab(sub);
                if (substationNode is null) { Skip(name, "the explorer tree has no substation node"); continue; }
                AssetTreeNode at = substationNode;
                if (options.Wants(name))
                {
                    // A source without case records (a MATPOWER file, a replay) has no PROPERTIES sub-tab: by design,
                    // so it is passed over with the sub-tabs it does have, not failed.
                    Select(at);
                    if (!Nav.SubTabs.Contains(sub))
                    {
                        Skip(name, $"this source's pages have no {sub} sub-tab (they have {string.Join(", ", Nav.SubTabs)})");
                        continue;
                    }
                }
                Capture(name, () =>
                {
                    Select(at);
                    Nav.SelectedSubTab = sub;
                });
                Nav.SelectedSubTab = AssetNavigatorModel.OverviewTab;
            }

            // IEC VIEW on the substation and the transformer page.
            foreach ((string? page, AssetTreeNode? node) in new[] { (substationPage, substationNode), (transformerPage, transformerNode) })
            {
                if (page is null || node is null) continue;
                AssetTreeNode at = node;
                ToggleButton? toggle = null;
                Capture(page + "-iec-view", () =>
                {
                    Select(at);
                    Render();
                    toggle = w.AssetsView.GetVisualDescendants().OfType<ToggleButton>().SingleOrDefault(t => t.Name == "IecToggle" && t.IsEffectivelyVisible)
                        ?? throw new InvalidOperationException("the page shows no IEC VIEW toggle");
                    toggle.IsChecked = true;
                });
                if (toggle is not null) toggle.IsChecked = false;
            }
            Select(Nav.Tree.Home);
        }

        // Open the unit page by its key (AssetSmoke's choice) and capture it; returns its page name and node.
        private (string? Page, AssetTreeNode? Node) Unit(string label, AssetPageKey key, AssetPageKind kind)
        {
            string name = PageName(label);
            AssetTreeNode? node = Nav.Tree.NodeFor(key);
            if (node is null)
            {
                report.Failures.Add(new UiSmokeFailure(slug, name, $"the explorer tree has no node for {kind} {key}"));
                log.WriteLine($"  CRASH   {name}: the explorer tree has no node for it");
                return (null, null);
            }
            Capture(name, () => Select(node));
            return (name, node);
        }

        // Select a tree node as a click does; the page it opens must be the node's page.
        private void Select(AssetTreeNode node)
        {
            Nav.Select(node);
            if (Nav.Explorer.KeyOf(Nav.Current) is { } onScreen && onScreen.Kind != node.Page.Kind)
                throw new InvalidOperationException($"selecting {node} opened a {onScreen.Kind} page, not {node.Page.Kind}");
        }

        // ---- AUTHOR ----

        /// <summary>
        /// AUTHOR mode's home on a temporary copy of the case: a v1 case folder is copied file by file; any other case (a
        /// MATPOWER file, a v2 case) is written as a v2 case from the model the run loaded (V2CaseWriter, as the app's
        /// tests do). The copy is loaded through the run pipeline, the ASSETS toggle enters AUTHOR, and the page is
        /// captured; AUTHOR is then left without saving. The case's own files are never opened for editing.
        /// </summary>
        public void CaptureAuthor(ModelSource source)
        {
            // The copy is the user's own data, not a bundled network, so UiSmoke's free tier refuses a copy above its bus
            // limit (gb-full, 3,539 buses), as the app would for that user: a skip with the reason, not a failure.
            int buses = Run.LoadedModel?.Buses.Count ?? 0;
            GateResult tier = new LicenceGate(Entitlement.FreeDefault).Check(buses, bundled: false);
            if (!tier.Allowed)
            {
                Skip(AuthorHome, string.Create(CultureInfo.InvariantCulture, $"the temporary copy ({buses} buses) is above the free tier's limit"));
                return;
            }
            Capture(AuthorHome, () =>
            {
                string copyRoot = Path.Combine(scratch, "author-copy");
                Directory.CreateDirectory(copyRoot);
                ModelSource copy;
                if (source.MFile is null && source.V2Case is null && source.ReplayDir is null && source.Bridge is null)
                {
                    string dir = Path.Combine(copyRoot, Path.GetFileName(source.Dir));
                    Directory.CreateDirectory(dir);
                    foreach (string f in Directory.EnumerateFiles(source.Dir))
                        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                    copy = ModelCatalog.CaseFolderSource(dir);
                }
                else
                {
                    GridModel model = Run.LoadedModel ?? throw new InvalidOperationException("no model is loaded to copy");
                    string manifest = Path.Combine(copyRoot, UiSmokeCases.Slug(source.Label) + ".case.json");
                    V2CaseWriter.Write(model, manifest);
                    copy = ModelCatalog.V2CaseSource(manifest);
                }
                Await(Vm.LoadCaseAsync(copy), "the temporary copy to load");
                if (Run.HasError) throw new InvalidOperationException("the temporary copy did not load: " + Run.ErrorText);
                if (!Run.IsPaused) Await(Run.TogglePlayAsync(), "the run to pause");
                PumpUntil(() => Run.LastFrame is not null || Run.HasError, "the copy's first frame");
                if (Run.LastFrame is null)
                {
                    // Paused before its first frame: one step, so the ASSETS index has a frame to build on.
                    Await(Run.StepOnceAsync(), "a step to start");
                    PumpUntil(() => Run.LastFrame is not null || Run.HasError, "the copy's first frame");
                }
                w.SelectTab(MainWindow.AssetsTabHeader);
                Render();
                if (!Await(w.AuthorMode.ToggleAsync(), "AUTHOR to open"))
                    throw new InvalidOperationException("AUTHOR was refused: " + Nav.ModeNote);
            });
            if (Nav.IsAuthoring && !Await(w.AuthorMode.ToggleAsync(), "RUN to resume"))
                report.Failures.Add(new UiSmokeFailure(slug, AuthorHome, "AUTHOR could not be left: " + Nav.ModeNote));
        }
    }
}
