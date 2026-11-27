using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R5-02 (review of batch 4): the exit code of every B50 refusal, through the built CLI. Each case is the smallest one that
/// reaches the refusal; each must end with exit 1 (2 for the converter start, which follows R252), the message naming what
/// is refused, and no internal error.
/// </summary>
public sealed class BugHunt1125RefusalExitTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-b50");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private string Case(string name, GridModel m)
    {
        string d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        JsonCaseWriter.WriteDirectory(m, d);
        return d;
    }

    private string Raw(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static void Refused(CliProcess.Result r, string message, int exit = 1)
    {
        Assert.True(r.ExitCode == exit, r.ToString());
        Assert.Contains(message, r.StdErr + r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Ana05_fault_on_an_island_with_no_source_exits_1()
    {
        string c = Case("nogen", new GridModel
        {
            Name = "nogen", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 30, Qd: 10, BaseKv: 132) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        });
        Refused(JournalTestSupport.Run(_dir, "fault", c, "--bus", "2"), "needs at least one source (generator) to feed the fault");
    }

    [Fact]
    public void Ana06_acopf_full_with_a_facts_device_exits_1()
        => Refused(JournalTestSupport.Run(_dir, "acopf-full", Raw("facts.raw", PsseFactsTests.Case())), "1 FACTS device(s)");

    [Fact]
    public void Num03_fast_decoupled_with_an_x_zero_branch_exits_1()
    {
        string c = Case("r3", new GridModel
        {
            Name = "r3", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 1, BaseKv: 11), new Bus(3, BusType.Pq, Pd: 1, BaseKv: 11) },
            Generators = new[] { new Generator(1, 2) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.05), new Branch(2, 3, 0.02, 0.0, Name: "R23") },
        });
        Refused(JournalTestSupport.Run(_dir, "pf", c, "--engine", "fdxb"), "has X = 0 (purely resistive)");
    }

    [Fact]
    public void Num21_two_vsc_converters_holding_one_bus_exit_1()
    {
        string one = PsseVscTests.Case();
        int at = one.IndexOf("'VSC A'", StringComparison.Ordinal);
        int end = one.IndexOf("0 / END OF VSC DC DATA", StringComparison.Ordinal);
        string link = one[at..end];
        string two = one[..end] + link.Replace("'VSC A'", "'VSC B'", StringComparison.Ordinal) + one[end..];
        Refused(JournalTestSupport.Run(_dir, "pf", Raw("vsc2.raw", two)), "which VSC link VSC A's second converter also controls");
    }

    [Fact]
    public void Dyn04_stability_with_a_converter_started_above_imax_exits_2()
    {
        string c = Case("conv", new GridModel
        {
            Name = "conv", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 400, Qd: 50, BaseKv: 132) },
            Generators = new[] { new Generator(1, 0, H: 5, Name: "G1"), new Generator(2, 300, MBase: 100, Type: "battery", Name: "BESS") },
            Branches = new[] { new Branch(1, 3, 0.01, 0.05), new Branch(2, 3, 0.01, 0.05) },
        });
        // R252's pattern, and its exit code: an equilibrium the model cannot start from is exit 2 in stability (as B50 says).
        Refused(JournalTestSupport.Run(_dir, "stability", c, "--rms", "--fault-bus", "3"), "the solved operating point needs a current of", exit: 2);
    }

    // Re-pinned (ruling 14): the 2.1.161 refusal is replaced by the pi branch of the same line; the run completes (exit 0)
    // and names the line with a provenance note.
    [Fact]
    public void Dyn18_emt_with_a_line_shorter_than_the_step_runs_it_as_a_pi_branch()
    {
        string c = Case("short", new GridModel
        {
            Name = "short", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132) },
            Generators = new[] { new Generator(1, 10) },
            Branches = new[] { new Branch(1, 2, 0.0001, 0.001, B: 0.002) },
        });
        CliProcess.Result r = JournalTestSupport.Run(_dir, "emt", c, "--lines");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("it is modelled as a pi branch of the same line", r.StdErr, StringComparison.Ordinal);
    }
}
