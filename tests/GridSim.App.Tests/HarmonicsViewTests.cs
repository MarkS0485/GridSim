using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Harmonics;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.7: the HARMONICS tab in the window. It sits at its WPF place (after EXPERIMENTAL, before
/// CYCLE), shows the shared <see cref="HarmonicsModel"/>'s four texts (the WPF words: see
/// tests/GridSim.Tests/Ui/UiHarmonicsParityTests.cs for the comparison with the WPF XAML), and, as in WPF, is fed
/// nothing: after N frames of a running case its texts are still exactly the model's, and the dashboard's tab hooks
/// never reached it.
/// </summary>
public sealed class HarmonicsViewTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case9()
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource("case9", Path.GetDirectoryName(file)!, file);
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
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

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static string Text(Control view, string name) =>
        (view.FindControl<TextBlock>(name) ?? throw new Xunit.Sdk.XunitException($"no text block {name}")).Text ?? "";

    [AvaloniaFact]
    public void The_HARMONICS_tab_sits_at_its_WPF_place_and_shows_the_model_texts_after_N_frames_unfed()
    {
        const int N = 6;
        using var temp = new TempFolder();
        ModelSource source = Case9();
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;

            // At its WPF place: straight after EXPERIMENTAL and straight before CYCLE.
            string[] headers = w.TabStrip.Items.OfType<TabItem>().Select(t => (string)t.Header!).ToArray();
            int at = Array.IndexOf(headers, HarmonicsModel.Header);
            Assert.True(at > 0, "no HARMONICS tab");
            Assert.Equal("EXPERIMENTAL", headers[at - 1]);
            Assert.Equal("CYCLE", headers[at + 1]);
            Assert.Equal(TabModels.HarmonicsHeader, HarmonicsModel.Header);
            Assert.Contains("RoomTabItem", ((TabItem)w.TabStrip.Items[at]!).Classes);

            // Fed nothing: the dashboard's tab hooks never name HARMONICS, frame after frame.
            Assert.DoesNotContain(HarmonicsModel.Header, TabModels.UpdateOrder);
            var fed = new List<string>();
            vm.Tabs.TabUpdated += fed.Add;
            int frames = 0;
            vm.Run.FrameArriving += (_, e) => { if (e.Kind == RunFrameKind.LiveSolved) frames++; };
            Await(vm.LoadCaseAsync(source));
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Await(vm.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
            PumpUntil(() => frames >= N, $"{N} frames");
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            Assert.NotEmpty(fed);
            Assert.DoesNotContain(HarmonicsModel.Header, fed);

            // Shown after N frames: each text block shows the model's text, the WPF words, unchanged.
            w.TabStrip.SelectedIndex = at;
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
            HarmonicsView view = w.FindControl<HarmonicsView>("HarmonicsTab")!;
            Assert.Same(vm.Tabs.Harmonics, view.DataContext);
            Assert.True(view.IsEffectivelyVisible);
            HarmonicsModel m = vm.Tabs.Harmonics;
            Assert.Equal(m.Title, Text(view, "TitleText"));
            Assert.Equal(m.Summary, Text(view, "SummaryText"));
            Assert.Equal(m.Command, Text(view, "CommandText"));
            Assert.Equal(m.Note, Text(view, "NoteText"));
            Assert.Equal(HarmonicsModel.Texts,
                new[] { Text(view, "TitleText"), Text(view, "SummaryText"), Text(view, "CommandText"), Text(view, "NoteText") });
        }
        finally
        {
            w.Close();
            if (w.StopLoop is { } stop) Await(stop);
        }
    }
}
