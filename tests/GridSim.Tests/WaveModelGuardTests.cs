using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R018 / R020 (October 2026 review): the EXPERIMENTAL projection called ContinuumWaveModel.FromSolvedCase on every
/// frame with no guard; on a case without per-machine inertia it built a dense YBus and a dense (n + g)^2 augmented
/// admittance (about 1.45 GB on gb-full) only to throw on H = 0. <see cref="FrameProjector.WaveModelCanForm"/> now
/// screens the same conditions first. These tests prove the guard never changes an answer: whenever it says false the
/// Core call really throws, whenever the Core call succeeds the guard said true, and the projected EXPERIMENTAL DTO is
/// exactly what the unguarded code produced.
/// </summary>
public class WaveModelGuardTests
{
    private static (GridModel Model, PowerFlowResult Result) Solve(GridModel m, PowerFlowOptions? opt = null)
    {
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m, opt);
        return (solver.RegulatedModel, r);
    }

    private static SimFrame Frame(GridModel m, PowerFlowResult r) =>
        new(m, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: m.Buses.Sum(b => b.Pd));

    [Fact]
    public void Case_with_inertia_passes_the_guard_and_the_projection_matches_Core_exactly()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        Assert.True(m.Generators.All(g => g.H > 0));
        Assert.True(FrameProjector.WaveModelCanForm(m, r));

        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.FromSolvedCase(m, r);
        ExperimentalDto x = FrameProjector.Project(Frame(m, r)).Experimental!;

        Assert.Equal(w.DominantInterAreaHz, x.WaveDominantHz);   // exact, bit for bit
        Assert.NotEqual("needs per-machine inertia", x.WaveSplit);
    }

    [Fact]
    public void Case_without_inertia_fails_the_guard_and_Core_really_throws()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case14());   // MATPOWER: loader leaves H = 0
        Assert.True(r.Converged);
        Assert.False(FrameProjector.WaveModelCanForm(m, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(m, r));

        // The projection gives exactly what the old catch gave.
        ExperimentalDto x = FrameProjector.Project(Frame(m, r)).Experimental!;
        Assert.Equal(0.0, x.WaveDominantHz);
        Assert.Equal("needs per-machine inertia", x.WaveSplit);
    }

    [Fact]
    public void Non_converged_solve_fails_the_guard_and_Core_really_throws()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9(), new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(r.Converged);
        Assert.False(FrameProjector.WaveModelCanForm(m, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(m, r));
    }

    [Fact]
    public void One_generator_without_inertia_is_enough_to_fail_the_guard_and_Core_agrees()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        var gens = m.Generators.ToList();
        gens[^1] = gens[^1] with { H = 0.0 };
        GridModel noH = m.CopyWith(generators: gens.ToList());

        Assert.False(FrameProjector.WaveModelCanForm(noH, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(noH, r));

        gens[^1] = gens[^1] with { H = double.NaN };   // NaN is not > 0 either, in both places
        GridModel nanH = m.CopyWith(generators: gens.ToList());
        Assert.False(FrameProjector.WaveModelCanForm(nanH, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(nanH, r));
    }

    [Fact]
    public void No_generators_fails_the_guard_and_Core_agrees()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        GridModel none = m.CopyWith(generators: new List<Generator>());
        Assert.False(FrameProjector.WaveModelCanForm(none, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(none, r));
    }

    [Fact]
    public void Guard_skips_the_dense_build_allocation_on_a_no_inertia_case()
    {
        // The skipped call's allocation is what each frame saves. Measured on the 300-bus IEEE case (no H): the guard
        // itself allocates nothing like the dense build (it only walks the generator list).
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case300());
        Assert.False(FrameProjector.WaveModelCanForm(m, r));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool threw = false;
        try { ContinuumWaveModel.FromSolvedCase(m, r); } catch (InvalidOperationException) { threw = true; }
        long denseBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        bool can = FrameProjector.WaveModelCanForm(m, r);
        long guardBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(can);
        Assert.True(threw, "the Core call must still fail on a no-inertia case");
        // Moved 2026-10 (N153, Ruling R1, Mark delegated 2026-10-04): this pinned the defect, a call bound to fail that
        // first built the dense YBus and augmented admittance (n = 300: the dense YBus alone is 300^2 x 16 B = 1.44 MB).
        // ClassicalInit.FromSolvedCase now fails before the dense build, with the same exception and message, so the
        // call allocates no dense build at all. Before: Assert.True(denseBytes > 1_000_000) (measured over 1.44 MB);
        // after: under 1,000,000 B (measured 14,184 B in the S5 intermediate build). The guard still allocates almost
        // nothing, and the per-frame skip it buys is now also free for the direct callers.
        Assert.True(denseBytes < 1_000_000, $"the failing call allocated {denseBytes} B: the dense build is back");
        Assert.True(guardBytes < 1_000, $"guard allocated {guardBytes} B");
    }
}
