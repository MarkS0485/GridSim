using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Wpf;
using GridSim.Core.Simulation;   // D-DOC-201: LiveSim and SimFrame moved to Core (was GridSim.Wpf.Sim)
using GridSim.Wpf.Views;
using GridSim.Wpf.Views.Assets;
using GridSim.Wpf.Visuals;

// Headless-ish UI smoke: instantiate every asset page (and THE MACHINE, SCADA, the
// map, the generation tab, and the other room tabs) inside a real, off-screen window with a solved frame,
// forcing each DataTemplate to load and bind. Catches the template/resource crashes
// a unit test can't see and a quick manual click-through misses.
//
//   dotnet run --project tools/AssetSmoke [caseDir] [shotsDir]
//
// With shotsDir set, every page is also rendered to PNG via RenderTargetBitmap  - 
// entirely in-process and off-screen: no window grabbing, no synthesised input.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string caseDir = args.Length > 0 ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "uk"));
        string? shotsDir = args.Length > 1 ? args[1] : null;
        if (shotsDir is not null) Directory.CreateDirectory(shotsDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GridSim.Wpf;component/Theme.xaml"),
        });

        GridModel model = JsonGridLoader.LoadDirectory(caseDir);
        var sim = new LiveSim(model);
        SimFrame frame = sim.Step(1);
        // a few extra steps so live histories/traces have shape in the shots
        for (int i = 2; i <= 4; i++) frame = sim.Step(i);

        var vm = new DashboardViewModel();
        var window = new Window
        {
            Width = 1440, Height = 860, Left = -4000, Top = 0,   // off-screen, real layout
            ShowInTaskbar = false, ShowActivated = false,
            Background = (Brush)app.Resources["Bg"],
        };
        window.Show();

        var fails = new List<string>();
        void Shot(string label)
        {
            if (shotsDir is null || window.Content is not Visual v) return;
            var rtb = new RenderTargetBitmap(
                (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            }
            rtb.Render(dv);
            rtb.Render(v);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string file = Path.Combine(shotsDir, $"{label.Replace(' ', '-').Replace("/", "-")}.png");
            using FileStream fs = File.Create(file);
            enc.Save(fs);
        }

        void Check(string label, Action act)
        {
            try
            {
                act();
                Pump();
                Shot(label);
                Console.WriteLine($"  ok      {label}");
            }
            catch (Exception ex)
            {
                fails.Add(label);
                Console.WriteLine($"  CRASH   {label}: {ex.GetBaseException().GetType().Name}  -  {ex.GetBaseException().Message}");
            }
        }

        // ---- the network map (the NETWORK tab's heart), fitted and dived-in ----
        IReadOnlyList<BoundaryFlow> boundaries = BoundaryAnalysis.Evaluate(frame.Model, frame.Result);
        vm.Update(frame, boundaries);   // also feeds the generation tab + machine + scada
        var schematic = new FastSchematic { ShowFleet = true, ShowCoast = true };
        Check("network map fitted", () =>
        {
            window.Content = schematic;
            schematic.Load(frame.Model);
            schematic.SetState(frame.Result);
            Pump();
            schematic.FitToView();
            schematic.Animate(0.4);
        });
        Check("network map dived-in", () =>
        {
            // centre on the busiest 33 kV primary (falling back to any bus) and dive in
            // stages so LOD reveals each voltage level on the way down
            var degree = new Dictionary<int, int>();
            foreach (Branch b in frame.Model.Branches)
            {
                degree[b.From] = degree.GetValueOrDefault(b.From) + 1;
                degree[b.To] = degree.GetValueOrDefault(b.To) + 1;
            }
            Bus target = frame.Model.Buses
                .Where(b => b.BaseKv == 33)
                .OrderByDescending(b => degree.GetValueOrDefault(b.Id))
                .FirstOrDefault() ?? frame.Model.Buses[0];
            schematic.CenterOn(target.Id);
            Pump();
            for (int i = 0; i < 5; i++)
            {
                schematic.ZoomAt(new Point(window.ActualWidth / 2, window.ActualHeight / 2), 2.0);
                Pump();
            }
            schematic.Animate(0.4);
        });

        // ---- the generation & inertia tab ----
        Check("generation tab", () => window.Content = new GenerationView { DataContext = vm });

        // ---- the asset explorer, page by page ----
        var explorer = vm.Assets;
        var view = new AssetExplorerView { DataContext = vm };
        window.Content = view;
        Pump();

        // D-DOC-201: the detail pages are now opened by model INDEX plus their identifying fields (R055, D-DOC-146),
        // and every page takes its explorer; the calls below follow the current constructors.
        int txIdx = Enumerable.Range(0, model.Branches.Count).FirstOrDefault(i => model.Branches[i].IsTransformer, -1);
        int lineIdx = Enumerable.Range(0, model.Branches.Count).FirstOrDefault(i => !model.Branches[i].IsTransformer, -1);
        int genIdx = Enumerable.Range(0, model.Generators.Count).FirstOrDefault(i => model.Generators[i].Type != "interconnector", -1);
        // N258: the interconnector page is opened by index too (its bus and name confirm it).
        int icIdx = Enumerable.Range(0, model.Generators.Count).FirstOrDefault(i => model.Generators[i].Type == "interconnector", -1);
        string icName = icIdx >= 0 ? model.Generators[icIdx].Name ?? "" : "";
        int icBus = icIdx >= 0 ? model.Generators[icIdx].Bus : 0;
        int busId = model.Buses[0].Id;

        Check("home", () => { explorer.GoHome(); explorer.Update(frame, "12:00"); });
        Check("interconnector fleet", () => OpenAndPump(explorer, new InterconnectorFleetVm(explorer), frame));
        Check($"interconnector detail ({icName})", () => OpenAndPump(explorer, new InterconnectorDetailVm(icIdx, icBus, icName, explorer), frame));
        Check("generator fleet", () => OpenAndPump(explorer, new GeneratorFleetVm(explorer), frame));
        if (genIdx >= 0)
        {
            Generator g = model.Generators[genIdx];
            Check($"generator detail ({g.Name})", () => OpenAndPump(explorer, new GeneratorDetailVm(genIdx, g.Bus, g.Name, explorer), frame));
        }
        Check("generator phasors", () => OpenAndPump(explorer, new GeneratorPhasorFleetVm(explorer), frame));
        Check("transformer fleet", () => OpenAndPump(explorer, new TransformerFleetVm(explorer), frame));
        if (txIdx >= 0)
        {
            Branch t = model.Branches[txIdx];
            Check($"transformer detail ({t.Name})", () => OpenAndPump(explorer, new TransformerDetailVm(txIdx, t.From, t.To, t.Name, explorer), frame));
        }
        Check("tap changer fleet", () => OpenAndPump(explorer, new TapChangerFleetVm(explorer), frame));
        Check("circuit fleet", () => OpenAndPump(explorer, new CircuitFleetVm(explorer), frame));
        if (lineIdx >= 0)
        {
            Branch l = model.Branches[lineIdx];
            Check($"circuit detail ({l.Name})", () => OpenAndPump(explorer, new CircuitDetailVm(lineIdx, l.From, l.To, l.Name, explorer), frame));
        }
        Check("substation fleet", () => OpenAndPump(explorer, new SubstationFleetVm(explorer), frame));
        Check($"substation detail (bus {busId})", () => OpenAndPump(explorer, new SubstationDetailVm(busId, explorer), frame));

        // ---- the machine ----
        Check("the machine", () =>
        {
            window.Content = new MachineView { DataContext = vm };
            vm.Machine.Update(frame);
        });

        // ---- the SCADA console ----
        Check("scada", () =>
        {
            window.Content = new ScadaView { DataContext = vm };
            vm.Scada.Update(frame, "12:00");
            Pump();
            vm.Scada.Update(frame, "12:01");   // exercise the refresh-only path too
        });

        // ---- the remaining room tabs (parity checklist T6-T15) ----
        // Each WPF view binds its own DataContext to its tab model on the dashboard ({Binding Operator}, ...), so it is
        // hosted inside a Border that carries the dashboard, as the main window's tab item does. vm.Update above has fed
        // every tab once; a second update exercises each tab's refresh path, as the live loop does. HARMONICS has no view
        // in the WPF window (its panel is inline in MainWindow.xaml), so it has no picture here. SPEED DOMAINS,
        // INVERTER FLEET and EXPERIMENTAL compute only while their tab is showing (the shell sets Active), so each is
        // made active for its own picture, as selecting the tab does.
        void Tab(string label, Func<FrameworkElement> make, Action<bool>? show = null) => Check(label, () =>
        {
            show?.Invoke(true);
            window.Content = new System.Windows.Controls.Border { DataContext = vm, Child = make() };
            Pump();
            vm.Update(frame, boundaries);
            Pump();
        });
        Tab("operator", () => new OperatorView());
        Tab("market", () => new MarketView());
        Tab("neso rp1", () => new NesoRp1View());
        Tab("mechanical", () => new MachineEngineerView());
        Tab("human scale", () => new HumanScaleView());
        Tab("speed domains", () => new DomainsView(), on => vm.Domains.Active = on);
        Tab("inverter fleet", () => new InverterFleetView(), on => vm.InverterFleet.Active = on);
        Tab("experimental", () => new ExperimentalView(), on => vm.Experimental.Active = on);
        Tab("cycle", () => new CycleView());

        window.Close();
        app.Shutdown();

        Console.WriteLine(fails.Count == 0
            ? $"ALL PAGES OK ({model.Name}: {model.BusCount} buses)"
            : $"{fails.Count} PAGE(S) CRASHED: {string.Join(", ", fails)}");
        return fails.Count == 0 ? 0 : 1;
    }

    // UI programme ASSETS A2: the pages' common base is now the platform-neutral AssetPageModel (GridSim.Ui.Assets.Pages);
    // every WPF page VM derives from its neutral page model, so the calls above are unchanged.
    private static void OpenAndPump(AssetExplorerVm explorer, GridSim.Ui.Assets.Pages.AssetPageModel page, SimFrame frame)
    {
        explorer.Open(page);
        explorer.Update(frame, "12:00");   // two updates: first builds rows, second exercises the reuse path
        Pump();
        explorer.Update(frame, "12:01");
    }

    /// <summary>Run the dispatcher until layout/render has caught up, so templates actually instantiate.</summary>
    private static void Pump()
    {
        var f = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => f.Continue = false);
        Dispatcher.PushFrame(f);
    }
}
