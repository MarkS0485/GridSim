using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R020 remainder (October 2026 review, N022): the WPF EXPERIMENTAL tab's models (the physical-law audit, reciprocity,
/// the wave model, the tracing) ran on the UI thread. They now run on a thread-pool worker and only the formatting and
/// the collection updates run on the dispatcher. The test project does not reference GridSim.Wpf, so, like
/// <see cref="WpfPresentationSourceContractTests"/>, these read the source and fail if a later edit moves the models
/// back onto the UI thread or lets the worker touch WPF objects.
/// </summary>
public class WpfExperimentalOffUiThreadTests
{
    // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ExperimentalVm.cs moved to
    // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs, with the way back to the UI thread passed in as a poster (as
    // the CYCLE port did). Vm() reads the model, so every assertion on Compute, Refresh and Apply is unchanged except
    // one: the continuation's scheduler, TaskScheduler.FromCurrentSynchronizationContext(), now lives in the WPF poster,
    // and is asserted there (the WPF adapter), with the poster's use in the model's Refresh. Strengthened: the WPF
    // adapter must be that thin poster over the same SynchronizationContext (null without one, so the inline path is
    // the old one), and may not compute or apply a frame itself; and UiTabsThirdBatchParityTests now checks by
    // behaviour that the models run on a worker and the result is applied only where the poster runs it.
    private static string Vm() => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Experimental", "ExperimentalModel.cs");

    private static string WpfAdapter() => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "ExperimentalVm.cs");

    /// <summary>The text of the member whose declaration contains <paramref name="signature"/>, brace-matched.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"not found: {signature}");
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException("unbalanced braces");
    }

    [Fact]
    public void The_models_run_in_a_static_worker_function_that_touches_no_WPF_state()
    {
        string compute = Body(Vm(), "static Computed Compute(SimFrame frame)");
        Assert.Contains("PhysicsLaws.Audit(model, r)", compute);
        Assert.Contains("ReciprocityCheck.Evaluate(model)", compute);
        Assert.Contains("ContinuumWaveModel.FromSolvedCase(model, r)", compute);
        Assert.Contains("PowerFlowTracing.Trace(model, r)", compute);
        Assert.Contains("CarbonTracing.Trace(model, r)", compute);
        // No collections, brushes, bound properties or formatted text on the worker.
        foreach (string uiOnly in new[] { "Laws.Clear", "Laws.Add", "TopFlows.", "Brush", "Wave =", "Gate =", "Status =", "Carbon =", "$\"" })
            Assert.DoesNotContain(uiOnly, compute);
    }

    [Fact]
    public void Refresh_hands_the_frame_to_a_worker_and_applies_on_the_UI_context()
    {
        string refresh = Body(Vm(), "private void Refresh(SimFrame f)");
        Assert.Contains("Task.Run(() => Compute(next))", refresh);
        // The continuation goes back to the UI thread through the poster (taken on the UI thread for each chain) ...
        Assert.Contains("Action<Action>? ui = _uiPoster();", refresh);
        Assert.Contains(".ContinueWith(t => post(() =>", refresh);
        // ... which, in WPF, is the old scheduler on the calling thread's synchronisation context, and none without one.
        string wpf = WpfAdapter();
        Assert.Contains("public sealed class ExperimentalVm : ExperimentalModel", wpf);
        Assert.Contains("public ExperimentalVm() : base(CurrentUiPoster) { }", wpf);
        string poster = Body(wpf, "private static Action<Action>? CurrentUiPoster()");
        Assert.Contains("if (SynchronizationContext.Current is null) return null;", poster);
        Assert.Contains("TaskScheduler.FromCurrentSynchronizationContext()", poster);
        Assert.Contains("Task.Factory.StartNew(apply, CancellationToken.None, TaskCreationOptions.None, ui)", poster);
        foreach (string own in new[] { "Compute(", "Apply(", "Task.Run(", "Laws.", "TopFlows." })
            Assert.DoesNotContain(own, wpf);
        Assert.Contains("ExceptionDispatchInfo.Capture(", refresh);   // a model failure still surfaces (fail loud)
        Assert.Contains("if (ui is null) { Apply(Compute(f)); return; }", refresh);

        string apply = Body(Vm(), "private void Apply(Computed c)");
        Assert.Contains("Laws.Add(", apply);
        Assert.Contains("TopFlows.Add(", apply);
        Assert.DoesNotContain("PhysicsLaws.Audit", apply);
        Assert.DoesNotContain("FromSolvedCase", apply);
        Assert.DoesNotContain(".Trace(model", apply);
    }
}
