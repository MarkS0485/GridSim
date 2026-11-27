using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP14 review fixes (2.1.110): the reactive power optimisation of case30 is reported as infeasible with the
/// constraints that hold it, the AC-SCOPF screen of a general outage and its one-branch form are one routine, and further
/// bit-identity pins (taps, the Flipped status, acscopf with the loss objective, CSV results).
/// </summary>
public sealed class Rc5Wp14ReviewFixTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Sha(string s) => Sha(Encoding.UTF8.GetBytes(s));

    private static string ConsoleSha(string stdout) =>
        Sha(string.Join("\n", stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.Contains("result written to", StringComparison.Ordinal))));

    // ---- case30 rpo ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Case30_rpo_is_reported_infeasible_with_the_constraints_that_hold_it()
    {
        // Reactive power optimisation holds the five units off the slack bus at their case output, so only the voltages,
        // the reactive outputs and the slack unit can move the flows. With the case ratings no such point exists.
        // Independent check: tools/crossval/opf/case30_rpo_phase1.py (SciPy SLSQP, six starts, the MATPOWER branch model)
        // minimises a uniform rating factor s subject to the held dispatch and every other limit; the least s is
        // 1.0248577965 (the same from every start that converged), with 6-8 and 21-22 at their ratings, bus 7 at Vmin,
        // buses 25 and 29 at Vmax, the slack unit at Qmin and unit 5 at Qmax. So the case's ratings (s = 1) and 1.02 have no
        // feasible point and 1.026 has one; the 2.1.107 build ended both of the first two with a singular Newton system.
        GridModel m = Case("lib/case30.m");
        GridModel Scaled(double f) => m.CopyWith(branches: m.Branches.Select(b => b with { RateAMva = b.RateAMva * f }).ToArray());
        var opt = new AcOpfExtendedOptions { Objective = AcOpfObjective.ReactivePower };
        foreach (double f in new[] { 1.0, 1.02 })
        {
            AcOpfExtendedResult r = AcOpfExtended.Solve(Scaled(f), opt);
            Assert.Equal(AcOpfIpmStatus.Infeasible, r.Base.Status);
            Assert.False(r.Converged);
            Assert.StartsWith("likely infeasible (the signature of a problem with no feasible point): from iteration ", r.Base.Message, StringComparison.Ordinal);
            // The constraints with the largest final multipliers are the phase-1 binding set, less the slack unit's Qmin.
            foreach (string named in new[] { "branch 9 (6-8)", "Vmax bus 29", "Vmin bus 7", "Vmax bus 25", "branch 28 (21-22)", "Qmax generator 5 at bus 13" })
                Assert.Contains(named, r.Base.Message, StringComparison.Ordinal);
            Assert.EndsWith("reactive power optimisation holds every unit off the slack bus at its case output", r.Base.Message, StringComparison.Ordinal);
        }
        // Above the phase-1 factor the same problem converges; at 1.1 the flow limits no longer bind (as the review's own
        // model found: losses 2.0446 MW, no flow limit binding).
        AcOpfExtendedResult ok = AcOpfExtended.Solve(Scaled(1.026), opt);
        Assert.True(ok.Converged, ok.Base.Message);
        AcOpfExtendedResult wide = AcOpfExtended.Solve(Scaled(1.1), opt);
        Assert.True(wide.Converged, wide.Base.Message);
        Assert.Equal(2.0446, wide.LossesMw, 1e-4);
        Assert.DoesNotContain(wide.Base.Binding, b => b.Kind is "Sf" or "St");

        // The command line says it plainly on stderr and still fails (exit 3, no result file; stdout is pinned by T3).
        CliProcess.Result cli = CliProcess.RunBuilt("acopf-opt", "case30", "--objective", "rpo");
        Assert.Equal(3, cli.ExitCode);
        Assert.Contains("acopf-opt: no solution: likely infeasible ", cli.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_breakdown_that_is_not_infeasibility_stays_a_numerical_failure()
    {
        // case9 with the losses objective and an iteration cap of 3: the run stops at the cap, not by a breakdown, so no
        // infeasibility is claimed. And a converged run is untouched.
        GridModel m = Case("lib/case9.m");
        AcOpfExtendedResult capped = AcOpfExtended.Solve(m, new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses, Ipm = AcOpfExtended.DefaultIpm(AcOpfObjective.Losses) with { MaxIterations = 3 },
        });
        Assert.Equal(AcOpfIpmStatus.IterationCap, capped.Base.Status);
        Assert.True(AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses }).Converged);
    }

    // ---- The one screen ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_branch_screen_is_the_general_screen_of_a_one_branch_outage()
    {
        // Screen (the branch path) now runs the general screen; every outage of case9 x 0.75 at the base optimum gives the
        // same verdict and reason both ways.
        GridModel src = Case("lib/case9.m");
        GridModel m = src.CopyWith(branches: src.Branches.Select(b => b with { RateAMva = b.RateAMva * 0.75 }).ToArray());
        AcOpfIpmResult b0 = AcOpfExtended.Solve(m).Base;
        Assert.True(b0.Converged, b0.Message);
        for (int k = 0; k < m.Branches.Count; k++)
        {
            if (AcScopf.Islands(m, k)) continue;
            AcScopfScreen a = AcScopf.Screen(m, b0, k, PostFaultRating.RateA);
            AcScopfScreen g = AcScopf.ScreenCore(m, b0, b0.Generators, k, ScopfOutage.OfBranch(k), PostFaultRating.RateA);
            Assert.Equal(a, g);
        }
    }

    // ---- Pins ----------------------------------------------------------------------------------------------------------
    // Method: taken before any 2.1.110 edit with the 2.1.109 Release build (dev main 45d13509), options off. CLI rows as
    // Rc5Wp14 T3: the --out file hashed as written, the console (stdout, line ends LF, the "result written to" line
    // dropped) hashed as UTF-8. API rows: OptimisationReport.Json("acopf-opt", rows) and OptimisationReport.Csv(rows) of
    // OpfRounding.Solve, hashed as UTF-8 (the text the CLI writes to --out). The two CSV rows' console hashes equal T3's
    // for the same commands, and the API JSON of case30 losses without controls equals T3's CLI hash
    // (ef82a162...), which ties the two methods together. No binary hash was recorded.

    [Theory]
    [InlineData("acscopf case9 --objective losses --contingencies 1,2", "out.json", "c771abb14bc92b96b2530f2bd41e87c21f68b5796e1ae6811089cd9ff67f3928", "42ed4133551959ec947ff29b3d7a81ddae30b0e0c4ba97807b9f0bb1338f60ef")]
    [InlineData("acscopf case9 --contingencies 1,2", "out.csv", "9c2d71bb0c9bea3c3431f735a73840573477ad0403b03262524d1983feeb1a99", "f9ba0459b082f48dcb208c9bd6b640015906eb35f5cc22af58d04a01066c2a87")]
    [InlineData("acopf-opt case9 --objective cost", "out.csv", "c49d5a9dcc75d55882f72f5b111ede883325fd3e3357642b098814699f74242e", "3f38afc98b71e598edab08d4c7eefc911e8e207391c410df5bfd603fea52c619")]
    public void Cli_pins_for_acscopf_losses_and_csv_results(string command, string outName, string fileSha, string consoleSha)
    {
        string dir = TestTemp.Under("gridsim-wp14fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string outFile = Path.Combine(dir, outName);
            CliProcess.Result r = CliProcess.RunBuilt(command.Split(' ').Concat(new[] { "--out", outFile }).ToArray());
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(consoleSha, ConsoleSha(r.StdOut));
            Assert.Equal(fileSha, Sha(File.ReadAllBytes(outFile)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Api_pins_for_tap_controls_and_the_flipped_status()
    {
        // The Rc5Wp7 T3 two-bus transformer (OLTC 0.9 .. 1.1, step 0.0125; V2 <= 1.00; rpo): the rounded tap breaks V2 and
        // the flip to the other neighbour is feasible (status Flipped).
        var two = new GridModel
        {
            Name = "two-bus", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0), new Bus(2, BusType.Pq, Pd: 100, Qd: 50, VminPu: 0.9, VmaxPu: 1.00) },
            Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 1000, Name: "slack") },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "T1", IsTransformer: true) },
            Oltcs = new[] { new Oltc("T1", 2, 1.0, 0.9, 1.1, 0.0125) },
        };
        OpfControlSet oltc = OpfControlSet.Parse("{\"format\":\"gridsim-opf-controls/1\",\"taps\":\"oltcs\"}", two);
        OpfRoundingResult flipped = OpfRounding.Solve(two, new AcOpfExtendedOptions { Objective = AcOpfObjective.ReactivePower, Controls = oltc });
        Assert.Equal(OpfRoundingStatus.Flipped, flipped.Status);
        IReadOnlyList<OptimisationReport.Row> fr = OptimisationReport.Rows(flipped);
        Assert.Equal("ad340cda14907c9f042d410eab8d5e86965b060f4a2d60c0281a9b4a9ffa44c7", Sha(OptimisationReport.Json("acopf-opt", fr)));
        Assert.Equal("51b844879f898a6722df066cf8b7fead9910f652c3cc56a2cbab2c4f7a7ba2fb", Sha(OptimisationReport.Csv(fr)));

        // case14, losses, a tap control (0.9 .. 1.1, step 0.0125) on each of its three off-nominal transformers (branches
        // 7, 8 and 9: 4-7, 4-9, 5-6), flip and neighbours rounding.
        GridModel m14 = Case("lib/case14.m");
        int[] tapped = Enumerable.Range(0, m14.Branches.Count).Where(k => m14.Branches[k].TapRatio != 0 && m14.Branches[k].TapRatio != 1).ToArray();
        Assert.Equal(new[] { 7, 8, 9 }, tapped);
        var taps = new OpfControlSet(tapped.Select(k => new OpfTapControl(k, 0.9, 1.1, 0.0125)).ToArray(), Array.Empty<OpfShuntControl>());
        var opt = new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = taps };
        OpfRoundingResult flip = OpfRounding.Solve(m14, opt);
        Assert.Equal(OpfRoundingStatus.Rounded, flip.Status);
        IReadOnlyList<OptimisationReport.Row> rf = OptimisationReport.Rows(flip);
        Assert.Equal("5abf4f2f298b86d6159f5513655593227d8ed6c99559ef708735e2f7cc46c3c4", Sha(OptimisationReport.Json("acopf-opt", rf)));
        Assert.Equal("0f8c50b7c86ba62af6816f5bb6d469d6a8a4a78cdfb13906b040f929d28c9f63", Sha(OptimisationReport.Csv(rf)));
        OpfRoundingResult nb = OpfRounding.Solve(m14, opt, OpfRoundingMode.Neighbours);
        Assert.Equal(OpfRoundingStatus.BestNeighbour, nb.Status);
        IReadOnlyList<OptimisationReport.Row> rn = OptimisationReport.Rows(nb);
        Assert.Equal("8b910f35ab7dfc557de4147de40a299e7bdb9d61204674b7503cdcef5f8c2c56", Sha(OptimisationReport.Json("acopf-opt", rn)));
        Assert.Equal("6ea5284b971978f51866fd9830ce2abb33ce9873c2edc7bd1e5fe7502b65ca58", Sha(OptimisationReport.Csv(rn)));

        // The tie between the API and CLI methods: case30 losses without controls is T3's CLI file hash.
        OpfRoundingResult plain = OpfRounding.Solve(Case("lib/case30.m"), new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.Equal("ef82a162fb68c121f3e0d79d274c570eac95f6cf8c442c1c45af12a7895251c5", Sha(OptimisationReport.Json("acopf-opt", OptimisationReport.Rows(plain))));
    }
}
