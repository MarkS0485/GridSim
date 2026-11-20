using System.Security.Cryptography;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 scripting, decision D1: <see cref="LiveSim.ReplaceNetwork"/> lets a caller change the network the loop re-solves between
/// steps. It is purely additive: a loop that never calls it, or calls it with the network it already has, gives the same bits
/// as before, and the existing LiveSim, solver and determinism classes are unchanged.
/// </summary>
public class LiveSimReplaceNetworkTests
{
    private static string Digest(SimFrame f)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(double d) => h.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(d)));
        Add(f.FrequencyHz); Add(f.RocofHzPerS); Add(f.GenerationMw); Add(f.DemandMw);
        foreach (double v in f.Result.Vm) Add(v);
        foreach (double v in f.Result.VaDeg) Add(v);
        foreach (double v in f.Result.PInjMw) Add(v);
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static List<string> Run(LiveSim sim, int steps, Action<int>? between = null)
    {
        var digests = new List<string>();
        for (int k = 0; k < steps; k++)
        {
            between?.Invoke(k);
            digests.Add(Digest(sim.Step(k, 20.0 / 60.0 / 1000.0)));
        }
        return digests;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Replacing_the_network_with_the_one_it_has_changes_no_bit(bool holdSteady)
    {
        GridModel m = IeeeCases.Case30();
        List<string> plain = Run(new LiveSim(m, holdSteady), 40);
        var sim = new LiveSim(m, holdSteady);
        List<string> replaced = Run(sim, 40, k => { if (k % 5 == 0) sim.ReplaceNetwork(m); });
        Assert.Equal(plain, replaced);
    }

    [Fact]
    public void A_branch_taken_out_between_steps_re_solves_to_the_same_state_as_a_loop_built_on_the_edited_case()
    {
        GridModel m = IeeeCases.Case30();
        Branch[] branches = m.Branches.ToArray();
        int victim = branches.Select((b, i) => (b, i)).First(x => x.b.InService && x.b.From == 2 && x.b.To == 5).i;
        branches[victim] = branches[victim] with { InService = false };
        GridModel edited = m.CopyWith(branches: branches);

        var sim = new LiveSim(m, holdSteady: true);
        SimFrame last = null!;
        for (int k = 0; k < 30; k++)
        {
            if (k == 10) sim.ReplaceNetwork(edited);
            last = sim.Step(k, 20.0 / 60.0 / 1000.0);
        }
        var fresh = new LiveSim(edited, holdSteady: true);
        SimFrame want = null!;
        for (int k = 0; k < 30; k++) want = fresh.Step(k, 20.0 / 60.0 / 1000.0);
        Assert.True(last.Result.Converged);
        for (int i = 0; i < want.Result.Vm.Length; i++) Assert.Equal(want.Result.Vm[i], last.Result.Vm[i], 6);
        Assert.Equal(0.0, last.Result.LineFlows[victim].PFromMw);
    }

    [Fact]
    public void A_network_that_is_not_the_loops_case_is_refused_and_the_loop_is_left_as_it_was()
    {
        GridModel m = IeeeCases.Case30();
        var sim = new LiveSim(m, holdSteady: true);
        string before = Digest(sim.Step(0, 20.0 / 60.0 / 1000.0));
        Assert.Throws<ArgumentNullException>(() => sim.ReplaceNetwork(null!));
        Assert.Throws<ArgumentException>(() => sim.ReplaceNetwork(IeeeCases.Case9()));
        Assert.Throws<ArgumentException>(() => sim.ReplaceNetwork(m.CopyWith(frequencyHz: 60.0)));
        GridModel reordered = m.CopyWith(buses: m.Buses.Reverse().ToArray());
        Assert.Throws<ArgumentException>(() => sim.ReplaceNetwork(reordered));
        var control = new LiveSim(m, holdSteady: true);
        control.Step(0, 20.0 / 60.0 / 1000.0);
        Assert.Equal(Digest(control.Step(1, 20.0 / 60.0 / 1000.0)), Digest(sim.Step(1, 20.0 / 60.0 / 1000.0)));
        Assert.NotEmpty(before);
    }

    [Fact]
    public void With_an_authored_asset_fleet_the_in_service_generator_count_cannot_change()
    {
        GridModel gb = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-synthetic"));
        var sim = new LiveSim(gb, holdSteady: true, dynamicsConfig: LiveSim.AuthoredBundleConfig(gb));
        Assert.NotNull(sim.AssetFleet);
        Generator[] fleet = gb.AllGenerators.ToArray();
        fleet[0] = fleet[0] with { InService = false };
        GridModel tripped = gb.CopyWith(generators: fleet, generatorsAreWholeFleet: true);
        Assert.Throws<InvalidOperationException>(() => sim.ReplaceNetwork(tripped));
        // A set-point change keeps the count and is accepted.
        fleet[0] = fleet[0] with { InService = true, Pg = fleet[0].Pg * 0.5 };
        sim.ReplaceNetwork(gb.CopyWith(generators: fleet, generatorsAreWholeFleet: true));
    }
}
