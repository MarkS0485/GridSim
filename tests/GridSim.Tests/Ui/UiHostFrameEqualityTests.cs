using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Layout;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 0.2 (the strangler extraction): the simulation host moved out of the WPF window into the
/// platform-neutral <see cref="SimHost"/>. This is an orchestration-only move, so every frame must be bit for bit the
/// frame the window produced before. These tests drive a <see cref="LiveSim"/> (and a <see cref="PresolvedReplay"/>)
/// directly, the way the window's <c>SwitchTo</c> / <c>StepOnce</c> did before the move (the reference code below is a
/// copy of it), and compare every field of every frame from the host with exact equality: doubles by their bits, so a
/// sign of zero or a NaN payload counts as a difference.
/// </summary>
public sealed class UiHostFrameEqualityTests
{
    private const int Ticks = 12;

    private static LicenceGate FreeLicence() => new(Entitlement.FreeDefault);

    private static ModelSource Case9Source()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), $"bundled case not found: {file}");
        return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
    }

    private static ModelSource UkSource()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "data", "uk");
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource("GB reduced  10-zone", dir);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The reference: the WPF window's live path before the move, copied (MainWindow.SwitchTo and StepOnce at 1.95.478).
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record ReferenceFrame(SimFrame Frame, IReadOnlyList<BoundaryFlow> Boundaries, int Tick);

    /// <summary>The model read and the LiveSim built exactly as SwitchTo built them.</summary>
    private static LiveSim ReferenceSim(ModelSource s, SolverKind solver)
    {
        GridModel model = s.MFile is not null
            ? MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(s.MFile))   // fix raw ohm/kW distribution files
            : JsonGridLoader.LoadDirectory(s.Dir);
        model = GraphLayout.EnsureLayout(model);   // coordinate-less cases (IEEE/MATPOWER) get a tube-map layout
        var dynCfg = LiveSim.AuthoredBundleConfig(model);
        return new LiveSim(model, holdSteady: s.MFile is not null, solver: solver, dynamicsConfig: dynCfg);
    }

    /// <summary>One StepOnce body: the next tick at the given step length, then the boundary transfers.</summary>
    private static ReferenceFrame ReferenceStep(LiveSim sim, int tick, double step)
    {
        SimFrame f = sim.Step(tick, step);
        IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>();
        return new ReferenceFrame(f, boundaries, tick);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Live frames
    // ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Cases => new() { "case9", "uk" };

    private static ModelSource SourceOf(string name) => name == "case9" ? Case9Source() : UkSource();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Live_frames_from_the_host_equal_the_frames_from_driving_LiveSim_directly(string name)
    {
        ModelSource s = SourceOf(name);
        LiveSim reference = ReferenceSim(s, SolverKind.Auto);

        var host = new SimHost();
        LiveLoadResult loaded = host.LoadLive(s, SolverKind.Auto, FreeLicence());
        Assert.True(loaded.Loaded, loaded.Refusal);
        Assert.Equal(reference.ActiveSolver, loaded.ActiveSolver);
        Assert.Equal(reference.ActiveSolver, host.ActiveSolver);
        Assert.True(host.Playing);
        Assert.True(host.HasLiveSolver);
        Assert.Equal(0, host.Tick);
        Assert.Equal(SimHost.DefaultStepMinutes, host.StepMinutes);

        for (int tick = 1; tick <= Ticks; tick++)
        {
            ReferenceFrame want = ReferenceStep(reference, tick, SimHost.DefaultStepMinutes);
            HostFrame got = Assert.IsType<HostFrame>(host.ProduceNext());
            AssertSameLiveFrame(want, got, host.Epoch, $"{name} tick {tick}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_step_length_change_and_a_trip_reach_the_host_frames_exactly_as_they_reached_the_window(string name)
    {
        // The window changed the step length between ticks (the STEP picker) and injected a trip into the live
        // integrator (the T key); both take effect on the NEXT step. The host must take them at the same point.
        ModelSource s = SourceOf(name);
        LiveSim reference = ReferenceSim(s, SolverKind.Auto);
        var host = new SimHost();
        Assert.True(host.LoadLive(s, SolverKind.Auto, FreeLicence()).Loaded);

        double step = SimHost.DefaultStepMinutes;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            if (tick == 4) { step = 1.0 / 60; host.StepMinutes = step; }   // "1 s"
            if (tick == 6)
            {
                const double lostMw = 120.0;
                reference.TripInfeed(lostMw);
                Assert.True(host.TripInfeed(lostMw));
            }
            if (tick == 9) { step = 5; host.StepMinutes = step; }          // "5 min"
            ReferenceFrame want = ReferenceStep(reference, tick, step);
            HostFrame got = Assert.IsType<HostFrame>(host.ProduceNext());
            AssertSameLiveFrame(want, got, host.Epoch, $"{name} tick {tick}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_host_loop_delivers_the_same_frames_in_order_and_ends_on_cancellation(string name)
    {
        ModelSource s = SourceOf(name);
        LiveSim reference = ReferenceSim(s, SolverKind.Auto);
        var host = new SimHost { PlaybackRate = 60 };   // the fastest SPEED choice: about 13 ms between frames
        Assert.True(host.LoadLive(s, SolverKind.Auto, FreeLicence()).Loaded);

        var delivered = new List<HostFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));   // a hung loop fails, never hangs the run
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RequestStep();
        Task loop = host.RunAsync(f =>
        {
            delivered.Add(f);   // the loop awaits each delivery, so this list is only touched by one frame at a time
            if (delivered.Count == Ticks) { done.TrySetResult(); cts.Cancel(); }
            return Task.CompletedTask;
        }, cts.Token);
        await loop;   // cancellation is the normal way out: the task completes, it does not fault
        Assert.True(done.Task.IsCompleted, $"the loop ended after {delivered.Count} frames, before {Ticks}");
        Assert.Equal(Ticks, delivered.Count);
        for (int tick = 1; tick <= Ticks; tick++)
            AssertSameLiveFrame(ReferenceStep(reference, tick, SimHost.DefaultStepMinutes), delivered[tick - 1], host.Epoch, $"{name} loop tick {tick}");
    }

    [Fact]
    public void A_paused_host_makes_no_frame_and_a_reload_makes_an_in_flight_frame_stale()
    {
        var host = new SimHost();
        Assert.Null(host.ProduceNext());   // nothing loaded
        Assert.True(host.LoadLive(Case9Source(), SolverKind.Auto, FreeLicence()).Loaded);
        host.Playing = false;
        Assert.Null(host.ProduceNext());   // paused: no tick is taken
        Assert.Equal(0, host.Tick);
        host.Playing = true;
        HostFrame first = Assert.IsType<HostFrame>(host.ProduceNext());
        Assert.Equal(first.Epoch, host.Epoch);
        Assert.Equal(1, host.Tick);

        // A model switch while a frame is on its way: the frame's epoch is behind, so the window drops it, and the new
        // model starts again at tick 1 (the window's _tick = 0 on load).
        Assert.True(host.LoadLive(Case9Source(), SolverKind.Auto, FreeLicence()).Loaded);
        Assert.NotEqual(first.Epoch, host.Epoch);
        Assert.Equal(0, host.Tick);
        Assert.Equal(1, Assert.IsType<HostFrame>(host.ProduceNext()).Tick);

        // A bridge takes over: no local feed, tick 0, nothing plays, and the trip has nothing to reach.
        int before = host.Epoch;
        host.Detach();
        Assert.NotEqual(before, host.Epoch);
        Assert.False(host.HasLiveSolver);
        Assert.False(host.IsReplay);
        Assert.False(host.Playing);
        Assert.Equal(0, host.Tick);
        Assert.Null(host.ActiveSolver);
        Assert.False(host.TripInfeed(100));
        host.Playing = true;
        Assert.Null(host.ProduceNext());
    }

    [Fact]
    public void A_refused_load_says_why_in_the_old_words_and_changes_nothing()
    {
        var host = new SimHost();
        Assert.True(host.LoadLive(Case9Source(), SolverKind.Auto, FreeLicence()).Loaded);
        int epoch = host.Epoch;
        string solver = host.ActiveSolver!;

        string missing = Path.Combine(Path.GetTempPath(), $"gridsim-ui-missing-{Guid.NewGuid():N}");
        LiveLoadResult bad = host.LoadLive(new ModelSource("missing", missing), SolverKind.Auto, FreeLicence());
        Assert.False(bad.Loaded);
        Assert.Null(bad.Model);
        Assert.StartsWith("Load failed: ", bad.Refusal, StringComparison.Ordinal);
        string expected;
        try { JsonGridLoader.LoadDirectory(missing); expected = "(no exception)"; }
        catch (Exception ex) { expected = $"Load failed: {ex.Message}"; }
        Assert.Equal(expected, bad.Refusal);

        ReplayLoadResult badRun = host.LoadReplay(missing);
        Assert.False(badRun.Loaded);
        Assert.StartsWith("Replay load failed: ", badRun.Refusal, StringComparison.Ordinal);

        // Nothing changed: the live case9 run carries on where it was.
        Assert.Equal(epoch, host.Epoch);
        Assert.Equal(solver, host.ActiveSolver);
        Assert.True(host.HasLiveSolver);
        Assert.True(host.Playing);
    }

    [Fact]
    public void A_licensed_model_above_the_tier_cap_is_refused_with_the_gates_own_message()
    {
        // A tier of 5 buses and a model that is not a Bundled Network: case9 copied out of data/ (9 buses).
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-ui-licence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "case9.m");
            File.Copy(Case9Source().MFile!, file);
            var source = new ModelSource("user case9", dir, file);
            Assert.False(SimHost.IsBundledSource(source));
            var gate = new LicenceGate(Entitlement.FreeDefault with { BusLimit = 5 });
            GateResult want = gate.Check(9, bundled: false);
            Assert.False(want.Allowed);

            var host = new SimHost();
            LiveLoadResult r = host.LoadLive(source, SolverKind.Auto, gate);
            Assert.False(r.Loaded);
            Assert.Equal(want.Message, r.Refusal);
            Assert.False(host.HasLiveSolver);
            Assert.Equal(0, host.Epoch);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    [Fact]
    public void The_bundled_cases_are_exempt_as_before()
    {
        Assert.True(SimHost.IsBundledSource(Case9Source()));
        Assert.True(SimHost.IsBundledSource(UkSource()));
        Assert.Equal(BundledNetworks.IsBundledPath(Case9Source().MFile), SimHost.IsBundledSource(Case9Source()));
        Assert.Equal(BundledNetworks.IsBundledPath(UkSource().Dir), SimHost.IsBundledSource(UkSource()));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Replay frames
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Replay_frames_from_the_host_follow_the_old_cursor_rules_exactly()
    {
        string dir = MakeRun(4);
        try
        {
            // Reference: the window's replay path before the move (LoadReplay, then StepOnce while playing).
            PresolvedReplay reference = PresolvedReplay.Load(dir);

            var host = new SimHost();
            ReplayLoadResult loaded = host.LoadReplay(dir);
            Assert.True(loaded.Loaded, loaded.Refusal);
            Assert.Equal(reference.Title, loaded.Title);
            Assert.Equal(reference.Count, loaded.Cursor!.Count);
            Assert.Equal(0, loaded.Cursor.Index);
            Assert.Equal(reference.CurrentLabel, loaded.Cursor.CurrentLabel);
            Assert.Equal(reference.Label(reference.Count - 1), loaded.LastLabel);
            AssertSameFrame(reference.Current, loaded.First!, "replay frame 0");
            Assert.True(host.IsReplay);
            Assert.False(host.HasLiveSolver);
            Assert.False(host.Playing);          // a replay opens paused on frame 0
            Assert.Null(host.ProduceNext());

            host.Playing = true;                 // PLAY
            for (int n = 1; ; n++)
            {
                // StepOnce: advance unless at the end, show the frame at the cursor, stop once at the end.
                if (!reference.AtEnd) reference.Advance();
                bool stop = reference.AtEnd;
                HostFrame got = Assert.IsType<HostFrame>(host.ProduceNext());
                Assert.Equal(HostFrameKind.Replay, got.Kind);
                Assert.Equal(host.Epoch, got.Epoch);
                AssertSameFrame(reference.Current, got.Frame!, $"replay step {n}");
                Assert.Equal(reference.Index, host.ReplayCursor!.Index);
                Assert.Equal(stop, got.StoppedAtEnd);
                if (stop) break;
                Assert.True(n < 10, "the replay never reached its end");
            }
            Assert.False(host.Playing);          // a replay stops at the end
            Assert.Null(host.ProduceNext());

            // Hand-driven moves: the same cursor arithmetic, and each makes an earlier loop frame stale.
            int epoch = host.Epoch;
            ReplayPosition back = host.ReplayRewind();
            reference.Rewind();
            Assert.Equal(reference.Index, back.Cursor.Index);
            Assert.Equal(reference.Index + 1, back.Before);
            AssertSameFrame(reference.Current, back.Frame, "rewind");
            Assert.NotEqual(epoch, host.Epoch);

            ReplayPosition seek = host.ReplaySeek(99);   // clamped to the last frame, as the replay always did
            reference.Seek(99);
            Assert.Equal(reference.Index, seek.Cursor.Index);
            Assert.True(seek.Cursor.AtEnd);
            AssertSameFrame(reference.Current, seek.Frame, "seek");

            ReplayPosition fwd = host.ReplayAdvance();   // already at the end: stays there
            reference.Advance();
            Assert.Equal(reference.Index, fwd.Cursor.Index);

            ReplayPosition restart = host.ReplayRestart();
            reference.Restart();
            Assert.Equal(0, restart.Cursor.Index);
            AssertSameFrame(reference.Current, restart.Frame, "restart");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    [Fact]
    public void A_live_load_after_a_replay_drops_the_replay_and_the_replay_moves_refuse_without_one()
    {
        string dir = MakeRun(2);
        try
        {
            var host = new SimHost();
            Assert.True(host.LoadReplay(dir).Loaded);
            Assert.NotNull(host.ReplayCursor);
            Assert.True(host.LoadLive(Case9Source(), SolverKind.Auto, FreeLicence()).Loaded);
            Assert.False(host.IsReplay);
            Assert.Null(host.ReplayCursor);
            Assert.Throws<InvalidOperationException>(() => host.ReplayAdvance());
            Assert.Equal(HostFrameKind.LiveSolved, Assert.IsType<HostFrame>(host.ProduceNext()).Kind);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    [Fact]
    public void The_transport_choices_are_the_old_windows_values_in_the_old_order()
    {
        Assert.Equal(new[] { "Auto", "Newton (dense)", "Newton (sparse)", "Sweep (radial)" }, TransportChoices.Solvers.Select(c => c.Label));
        Assert.Equal(new[] { SolverKind.Auto, SolverKind.DenseNewton, SolverKind.SparseNewton, SolverKind.DistributionSweep },
            TransportChoices.Solvers.Select(c => c.Kind));
        Assert.Equal(new[] { "1 ms", "10 ms", "100 ms", "1 s", "10 s", "1 min", "5 min", "20 min" }, TransportChoices.Steps.Select(c => c.Label));
        double[] minutes = { 0.001 / 60, 0.01 / 60, 0.1 / 60, 1.0 / 60, 10.0 / 60, 1, 5, 20 };
        Assert.Equal(minutes.Select(BitConverter.DoubleToInt64Bits), TransportChoices.Steps.Select(c => BitConverter.DoubleToInt64Bits(c.Minutes)));
        Assert.Equal(new double[] { 1, 5, 15, 60 }, TransportChoices.Playbacks);
        // The default step is the last choice (20 min, the day sweep) and the x1 interval is the old timer's 800 ms.
        Assert.Equal(BitConverter.DoubleToInt64Bits(TransportChoices.Steps[^1].Minutes), BitConverter.DoubleToInt64Bits(SimHost.DefaultStepMinutes));
        Assert.Equal(800.0, SimHost.BasePlaybackMs);
        var host = new SimHost();
        Assert.Equal(TimeSpan.FromMilliseconds(800.0), host.Interval);
        host.PlaybackRate = 15;
        Assert.Equal(TimeSpan.FromMilliseconds(800.0 / 15), host.Interval);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.PlaybackRate = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.PlaybackRate = double.NaN);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A pre-solved run of <paramref name="count"/> case9 snapshots, as CultureAndOrderDeterminismTests writes one.</summary>
    private static string MakeRun(int count)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-ui-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        var csv = new System.Text.StringBuilder("index,time_utc\n");
        for (int i = 0; i < count; i++)
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", $"state-{i:D6}.json"), model, r, trace, meta);
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i},2024-06-01T{9 + i:D2}:00:00Z\n");
        }
        File.WriteAllText(Path.Combine(dir, "state-estimate.csv"), csv.ToString());
        return dir;
    }

    private static void AssertSameLiveFrame(ReferenceFrame want, HostFrame got, int currentEpoch, string ctx)
    {
        Assert.True(got.Kind == HostFrameKind.LiveSolved, $"{ctx}: kind {got.Kind} ({got.Fault?.Message})");
        Assert.Equal(currentEpoch, got.Epoch);
        Assert.Equal(want.Tick, got.Tick);
        Assert.False(got.StoppedAtEnd);
        Assert.Null(got.Fault);
        Assert.True(got.SolveMs >= 0, $"{ctx}: solve time {got.SolveMs}");   // wall clock: not compared, only sane
        AssertSameFrame(want.Frame, got.Frame!, ctx);
        Assert.Equal(want.Boundaries.Count, got.Boundaries.Count);
        for (int i = 0; i < want.Boundaries.Count; i++)
        {
            BoundaryFlow a = want.Boundaries[i], b = got.Boundaries[i];
            Assert.Equal(a.Name, b.Name);
            Bits(a.FlowMw, b.FlowMw, $"{ctx} boundary {a.Name} flow");
            Bits(a.LimitMw, b.LimitMw, $"{ctx} boundary {a.Name} limit");
            Bits(a.LoadingPercent, b.LoadingPercent, $"{ctx} boundary {a.Name} loading");
            Assert.Equal(a.Overloaded, b.Overloaded);
        }
    }

    /// <summary>Every field of two frames, doubles by their bits.</summary>
    internal static void AssertSameFrame(SimFrame a, SimFrame b, string ctx)
    {
        Bits(a.Hour, b.Hour, $"{ctx} hour");
        Assert.Equal(a.Tick, b.Tick);
        Bits(a.FrequencyHz, b.FrequencyHz, $"{ctx} frequency");
        Bits(a.GenerationMw, b.GenerationMw, $"{ctx} generation");
        Bits(a.DemandMw, b.DemandMw, $"{ctx} demand");
        Bits(a.RocofHzPerS, b.RocofHzPerS, $"{ctx} rocof");
        Bits(a.AssetFleetNetMw, b.AssetFleetNetMw, $"{ctx} asset fleet net");
        Bits(a.AssetTrippedMw, b.AssetTrippedMw, $"{ctx} asset tripped");
        Assert.Equal(a.AssetFleet, b.AssetFleet);
        Assert.Equal(a.Storage, b.Storage);
        Assert.Equal(a.TapsSettled, b.TapsSettled);
        Assert.Equal(a.TapsAtLimit, b.TapsAtLimit);
        Assert.Equal(a.TapsRegulated, b.TapsRegulated);

        PowerFlowResult ra = a.Result, rb = b.Result;
        Assert.Equal(ra.Converged, rb.Converged);
        Assert.Equal(ra.Iterations, rb.Iterations);
        Assert.Equal(ra.Status, rb.Status);
        Bits(ra.MaxMismatch, rb.MaxMismatch, $"{ctx} mismatch");
        Bits(ra.TotalLossMw, rb.TotalLossMw, $"{ctx} loss MW");
        Bits(ra.TotalLossMvar, rb.TotalLossMvar, $"{ctx} loss Mvar");
        Assert.Equal(ra.SweepVoltageStepPu.HasValue, rb.SweepVoltageStepPu.HasValue);
        if (ra.SweepVoltageStepPu is double sa) Bits(sa, rb.SweepVoltageStepPu!.Value, $"{ctx} sweep step");
        Assert.Equal(ra.SolvedBusTypes, rb.SolvedBusTypes);
        Assert.Equal(ra.BusIds, rb.BusIds);
        BitsAll(ra.Vm, rb.Vm, $"{ctx} Vm");
        BitsAll(ra.VaDeg, rb.VaDeg, $"{ctx} Va");
        BitsAll(ra.PInjMw, rb.PInjMw, $"{ctx} P");
        BitsAll(ra.QInjMvar, rb.QInjMvar, $"{ctx} Q");
        Assert.Equal(ra.LineFlows.Count, rb.LineFlows.Count);
        for (int i = 0; i < ra.LineFlows.Count; i++)
        {
            LineFlow x = ra.LineFlows[i], y = rb.LineFlows[i];
            Assert.Equal(x.From, y.From);
            Assert.Equal(x.To, y.To);
            Assert.Equal(x.Name, y.Name);
            Bits(x.PFromMw, y.PFromMw, $"{ctx} flow {i} Pfrom");
            Bits(x.QFromMvar, y.QFromMvar, $"{ctx} flow {i} Qfrom");
            Bits(x.PToMw, y.PToMw, $"{ctx} flow {i} Pto");
            Bits(x.QToMvar, y.QToMvar, $"{ctx} flow {i} Qto");
            Bits(x.PLossMw, y.PLossMw, $"{ctx} flow {i} Ploss");
            Bits(x.QLossMvar, y.QLossMvar, $"{ctx} flow {i} Qloss");
            Bits(x.RatingMva, y.RatingMva, $"{ctx} flow {i} rating");
            Bits(x.LoadingPercent, y.LoadingPercent, $"{ctx} flow {i} loading");
        }

        GridModel ma = a.Model, mb = b.Model;
        Bits(ma.BaseMva, mb.BaseMva, $"{ctx} base MVA");
        Bits(ma.FrequencyHz, mb.FrequencyHz, $"{ctx} nominal");
        Assert.Equal(ma.Name, mb.Name);
        Assert.Equal(ma.Buses.Count, mb.Buses.Count);
        for (int i = 0; i < ma.Buses.Count; i++)
        {
            Assert.Equal(ma.Buses[i], mb.Buses[i]);
            Bits(ma.Buses[i].Pd, mb.Buses[i].Pd, $"{ctx} bus {i} Pd");
            Bits(ma.Buses[i].Qd, mb.Buses[i].Qd, $"{ctx} bus {i} Qd");
            Bits(ma.Buses[i].X, mb.Buses[i].X, $"{ctx} bus {i} X");
            Bits(ma.Buses[i].Y, mb.Buses[i].Y, $"{ctx} bus {i} Y");
        }
        Assert.Equal(ma.Generators.Count, mb.Generators.Count);
        for (int i = 0; i < ma.Generators.Count; i++)
        {
            // Record equality compares the cost curves' coefficient lists by reference, so those are compared by value
            // (bits) on their own and left out of the record comparison of every other field.
            Assert.Equal(ma.Generators[i] with { Cost = null, ReactiveCost = null }, mb.Generators[i] with { Cost = null, ReactiveCost = null });
            SameCost(ma.Generators[i].Cost, mb.Generators[i].Cost, $"{ctx} gen {i} cost");
            SameCost(ma.Generators[i].ReactiveCost, mb.Generators[i].ReactiveCost, $"{ctx} gen {i} reactive cost");
            Bits(ma.Generators[i].Pg, mb.Generators[i].Pg, $"{ctx} gen {i} Pg");
            Bits(ma.Generators[i].Qg, mb.Generators[i].Qg, $"{ctx} gen {i} Qg");
        }
        Assert.Equal(ma.Branches.Count, mb.Branches.Count);
        for (int i = 0; i < ma.Branches.Count; i++)
        {
            Assert.Equal(ma.Branches[i], mb.Branches[i]);
            Bits(ma.Branches[i].TapRatio, mb.Branches[i].TapRatio, $"{ctx} branch {i} tap");
        }
        // A boundary's branch list is compared by value (record equality would compare the list by reference).
        Assert.Equal(ma.Boundaries.Count, mb.Boundaries.Count);
        for (int i = 0; i < ma.Boundaries.Count; i++)
        {
            Assert.Equal(ma.Boundaries[i].Name, mb.Boundaries[i].Name);
            Bits(ma.Boundaries[i].LimitMw, mb.Boundaries[i].LimitMw, $"{ctx} boundary {i} limit");
            Assert.Equal(ma.Boundaries[i].Branches, mb.Boundaries[i].Branches);
        }
    }

    private static void SameCost(GeneratorCost? a, GeneratorCost? b, string what)
    {
        Assert.Equal(a is null, b is null);
        if (a is null || b is null) return;
        Assert.Equal(a.Model, b.Model);
        Bits(a.StartupCost, b.StartupCost, $"{what} startup");
        Bits(a.ShutdownCost, b.ShutdownCost, $"{what} shutdown");
        BitsAll(a.Coefficients.ToArray(), b.Coefficients.ToArray(), $"{what} coefficients");
    }

    private static void Bits(double want, double got, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(want) == BitConverter.DoubleToInt64Bits(got),
            $"{what}: want {want:R} (0x{BitConverter.DoubleToInt64Bits(want):X16}), got {got:R} (0x{BitConverter.DoubleToInt64Bits(got):X16})");

    private static void BitsAll(double[] want, double[] got, string what)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++) Bits(want[i], got[i], $"{what}[{i}]");
    }
}
