using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R018 / N021 (October 2026 review): <c>ClassicalInit.FromSolvedCase</c> finds a missing rotor (no machine, or a
/// machine with H not &gt; 0) only after building the dense YBus and the dense (n + g)^2 augmented admittance, about
/// 1.46 GB on gb-full, and <c>WaveField.Compute</c> reached it on every EXPERIMENTAL frame. Core now names the certain
/// failures without building anything (<c>ClassicalInit.CertainNoRotorFailure</c>, <see cref="ContinuumWaveModel.CanForm"/>),
/// so the wave field skips the call. (FromSolvedCase itself still builds first: WaveModelGuardTests pins that
/// allocation.) These tests prove nothing but the cost changed: the named failure is exactly the exception and message
/// the call throws (the message pinned against the literal text of the old code), the wave field is the same to the
/// bit, and no failure is named for an input that would fail some other way first.
/// </summary>
public class WaveModelEarlyFailureTests
{
    private static (GridModel M, PowerFlowResult R) Solved(GridModel m)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return (m, r);
    }

    private static void AssertSameField(WaveField.WaveFieldResult a, WaveField.WaveFieldResult b)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.FrequencyHz), BitConverter.DoubleToInt64Bits(b.FrequencyHz));
        Assert.Equal(a.SourceBus, b.SourceBus);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.WaveSpeedKmPerS), BitConverter.DoubleToInt64Bits(b.WaveSpeedKmPerS));
        Assert.Equal(a.Nodes.Count, b.Nodes.Count);
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            Assert.Equal(a.Nodes[i].Bus, b.Nodes[i].Bus);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Nodes[i].Amplitude), BitConverter.DoubleToInt64Bits(b.Nodes[i].Amplitude));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Nodes[i].PhaseRad), BitConverter.DoubleToInt64Bits(b.Nodes[i].PhaseRad));
        }
    }

    [Fact]
    public void No_inertia_message_is_the_old_text_exactly()
    {
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case14());   // MATPOWER: every H = 0
        var e = Assert.Throws<InvalidOperationException>(() => ClassicalInit.FromSolvedCase(m, r));
        // The literal the pre-R018 loop built for the first generator (bus 1, H = 0).
        Assert.Equal("Generator at bus 1 has no inertia constant (H = 0); the classical stability " +
            "model needs H > 0 for every machine. This case carries no per-machine inertia data " +
            "(e.g. a MATPOWER case whose loader defaults H = 0) and cannot be screened.", e.Message);
        Assert.False(ContinuumWaveModel.CanForm(m, r));
        Assert.Equal(e.Message, ClassicalInit.CertainNoRotorFailure(m, r, holdNonMachines: false));
    }

    [Fact]
    public void No_machine_message_is_the_old_text_exactly_when_holding_non_machines()
    {
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case14());
        var e = Assert.Throws<InvalidOperationException>(() => ClassicalInit.FromSolvedCase(m, r, holdNonMachines: true));
        Assert.Equal("No synchronous machines with inertia: the classical stability model has no rotor to integrate.", e.Message);
        Assert.Equal(e.Message, ClassicalInit.CertainNoRotorFailure(m, r, holdNonMachines: true));
    }

    [Fact]
    public void The_offending_machine_is_the_first_one_with_no_inertia_in_generator_order()
    {
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case9());
        var gens = m.Generators.ToList();
        gens[1] = gens[1] with { H = double.NaN };   // NaN is not > 0
        gens[2] = gens[2] with { H = 0.0 };
        GridModel bad = m.CopyWith(generators: gens);
        var e = Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(bad, r));
        Assert.Equal(ClassicalInit.NoInertiaMessage(gens[1]), e.Message);
        Assert.Equal(e.Message, ClassicalInit.CertainNoRotorFailure(bad, r, holdNonMachines: false));
        Assert.StartsWith($"Generator at bus {gens[1].Bus} has no inertia constant (H = NaN)", e.Message);
        Assert.False(ContinuumWaveModel.CanForm(bad, r));
    }

    [Fact]
    public void A_case_with_inertia_can_form_and_the_field_from_its_modes_is_bit_identical()
    {
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case9());
        Assert.True(ContinuumWaveModel.CanForm(m, r));
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.FromSolvedCase(m, r);
        Assert.True(w.DominantInterAreaHz > WaveField.MinModalHz);
        AssertSameField(WaveField.Compute(m, r), WaveField.ComputeFromModes(m, r, w));
        AssertSameField(WaveField.Compute(m, r, sourceBusId: 2), WaveField.ComputeFromModes(m, r, w, sourceBusId: 2));
        Assert.Equal(BitConverter.DoubleToInt64Bits(w.DominantInterAreaHz), BitConverter.DoubleToInt64Bits(WaveField.Compute(m, r).FrequencyHz));
    }

    [Fact]
    public void A_case_without_inertia_gives_the_nominal_field_from_null_modes()
    {
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case14());
        WaveField.WaveFieldResult f = WaveField.Compute(m, r);
        Assert.Equal(WaveField.NominalWaveHz, f.FrequencyHz);
        AssertSameField(f, WaveField.ComputeFromModes(m, r, null));
    }

    [Fact]
    public void Non_converged_and_generatorless_inputs_cannot_form_and_Core_really_throws()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(r.Converged);
        Assert.False(ContinuumWaveModel.CanForm(m, r));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(m, r));

        (GridModel m9, PowerFlowResult r9) = Solved(IeeeCases.Case9());
        GridModel none = m9.CopyWith(generators: new List<Generator>());
        Assert.False(ContinuumWaveModel.CanForm(none, r9));
        Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(none, r9));
    }

    [Fact]
    public void An_input_that_used_to_fail_another_way_first_still_fails_that_way()
    {
        // A dangling in-service branch makes YBus.Build throw KeyNotFoundException BEFORE the inertia check. The early
        // check must not pre-empt it with the inertia message, and CanForm must not claim certainty.
        (GridModel m, PowerFlowResult r) = Solved(IeeeCases.Case14());
        var branches = m.Branches.ToList();
        branches.Add(branches[0] with { From = 9999 });
        GridModel dangling = m.CopyWith(branches: branches);
        Assert.Null(ClassicalInit.CertainNoRotorFailure(dangling, r, holdNonMachines: false));
        Assert.True(ContinuumWaveModel.CanForm(dangling, r));
        Assert.Throws<KeyNotFoundException>(() => ClassicalInit.FromSolvedCase(dangling, r));

        // An out-of-service dangling branch is skipped by YBus.Build, so the inertia failure is certain again.
        branches[^1] = branches[^1] with { InService = false };
        GridModel open = m.CopyWith(branches: branches);
        Assert.NotNull(ClassicalInit.CertainNoRotorFailure(open, r, holdNonMachines: false));

        // A machine ahead of the offending one with an unknown terminal bus: the old loop's IndexOf threw first.
        (GridModel m9, PowerFlowResult r9) = Solved(IeeeCases.Case9());
        var gens = m9.Generators.ToList();
        gens[0] = gens[0] with { Bus = 4242 };
        gens[2] = gens[2] with { H = 0.0 };
        GridModel lost = m9.CopyWith(generators: gens);
        Assert.Null(ClassicalInit.CertainNoRotorFailure(lost, r9, holdNonMachines: false));
        Assert.Throws<KeyNotFoundException>(() => ClassicalInit.FromSolvedCase(lost, r9));
    }

    [Fact]
    public void Gb_full_wave_field_no_longer_allocates_the_dense_matrices()
    {
        // Before: 1,457,070,328 B allocated by every WaveField.Compute on gb-full (the wave model's dense build, only
        // to throw on H = 0; measured on 1.95.126, Release). After: CanForm reads the model only.
        GridModel gb = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-full"));
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(gb, new PowerFlowOptions { MaxIterations = 100 });
        Assert.True(r.Converged);

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        string? named = ClassicalInit.CertainNoRotorFailure(gb, r, holdNonMachines: false);
        bool can = ContinuumWaveModel.CanForm(gb, r);
        long check = GC.GetAllocatedBytesForCurrentThread() - a0;
        Assert.False(can);
        Assert.StartsWith("Generator at bus 194 has no inertia constant (H = 0)", named);   // the call's own message
        Assert.True(check < 1_000_000, $"the checks allocated {check:N0} B");

        a0 = GC.GetAllocatedBytesForCurrentThread();
        WaveField.WaveFieldResult f = WaveField.Compute(gb, r);
        long field = GC.GetAllocatedBytesForCurrentThread() - a0;
        Assert.Equal(WaveField.NominalWaveHz, f.FrequencyHz);
        Assert.True(field < 16_000_000, $"WaveField.Compute allocated {field:N0} B");
        AssertSameField(f, WaveField.ComputeFromModes(gb, r, null));
    }
}
