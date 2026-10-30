using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S2 / P01 (October 2026): the Strathclyde reduced GB network (GBreducednetwork.m, 29 buses / 99 branches / 66
/// generators) as the bundled, named case <c>gb-reduced-29</c>. Source: PyPSA-GB @ 8e084af
/// data/network/reduced_network/GBreducednetwork.m (MIT), identical in every number to the Edinburgh test case archive
/// original (reducedGB/GBreducednetwork.m); provenance, licence and checksums in data/matpower/gb/README.md.
/// </summary>
/// <remarks>
/// Every pinned number below was produced by GridSim at 1.95.120 and is asserted EXACTLY (bit for bit), with the dense
/// and sparse Newton engines required to agree to the bit. Independent check, recorded here (it needs Python, so it is
/// not run by the suite): PYPOWER 5.1.21 runpf (the Python port of MATPOWER's Newton power flow, PF_TOL 1e-10) on the
/// same file with reactive limits off agrees with GridSim to at most 4.441e-16 pu in |V| and 3.553e-13 degrees in angle,
/// and its branch losses are 1724.353966062 MW (GridSim 1724.35396606217 MW).
/// </remarks>
public class GbReduced29Tests
{
    private static string Gb29Path => Path.Combine(S10TestSupport.DataDir, GbReferenceModels.Gb29RelativePath);

    /// <summary>The model exactly as the CLI loads it (parse, then the raw-unit repair, which leaves a per-unit file alone).</summary>
    private static GridModel Load() => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Gb29Path));

    private static readonly PowerFlowOptions PfOptions = new() { MaxIterations = 100, EnforceQLimits = true };   // the pf verb's set
    private static readonly PowerFlowOptions QOff = new() { MaxIterations = 100, EnforceQLimits = false };       // MATPOWER runpf's default

    [Fact]
    public void The_bundled_file_is_the_documented_source_byte_for_byte()
    {
        // SHA-256 of the file with LF line endings (git stores LF; a Windows checkout may hold CRLF), as README.md lists it.
        string text = File.ReadAllText(Gb29Path).Replace("\r\n", "\n");
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        Assert.Equal("6e46dc8c9cfd862971e9101b8b6486dad5bc4fb7917dfaa57b526d150f59d1f9", sha);
        // The header names the data's origin and the people to cite.
        Assert.Contains("Data provided by Manolis Belivanis from Strathclyde", text, StringComparison.Ordinal);
        Assert.Contains("Network consists of 29 buses, 99 transmission lines and 66 generators", text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_parses_to_the_published_counts_and_totals()
    {
        MatpowerParseResult p = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(Gb29Path), "GBreducednetwork");
        GridModel m = MatpowerUnitNormalizer.Normalize(p.Model);
        Assert.Equal(29, m.BusCount);
        Assert.Equal(99, m.Branches.Count);
        Assert.Equal(66, m.AllGenerators.Count);
        Assert.Equal(66, m.Generators.Count);                       // every unit in service
        Assert.Same(m.AllGenerators, m.Generators);
        Assert.Equal(100.0, m.BaseMva);
        Assert.Equal(27, m.Buses.Single(b => b.Type == BusType.Slack).Id);   // Sellindge
        Assert.Equal(5, m.Buses.Count(b => b.Type == BusType.Pq));
        // The four branches with a tap column are 2-degree phase shifters at ratio 1 (branches 2, 4, 36, 38 in file order).
        int[] shifters = Enumerable.Range(0, m.Branches.Count).Where(i => m.Branches[i].IsTransformer).ToArray();
        Assert.Equal(new[] { 1, 3, 35, 37 }, shifters);
        foreach (int i in shifters)
        {
            Assert.Equal(1.0, m.Branches[i].TapRatio);
            Assert.Equal(2.0, m.Branches[i].PhaseShiftDeg);
        }
        // Totals, in model order: the stated outputs are an OPF starting point, 18,686.28 MW above the demand.
        Assert.Equal(56325.86, m.Buses.Sum(b => b.Pd));
        Assert.Equal(16249.5, m.Buses.Sum(b => b.Qd));
        Assert.Equal(75012.14, m.Generators.Sum(g => g.Pg));
        Assert.All(m.AllGenerators, g => Assert.NotNull(g.Cost));   // mpc.gencost ("Made up" in the file) is carried
        // Exactly two parse notes: the assumed frequency and the carried gencost.
        Assert.Equal(2, p.Notes.Count);
        Assert.StartsWith("nominal frequency not given", p.Notes[0], StringComparison.Ordinal);
        Assert.StartsWith("read mpc.gencost (66 row(s))", p.Notes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_pf_solve_is_pinned_and_dense_equals_sparse()
    {
        GridModel m = Load();
        S10TestSupport.AssertSolvesIdentical("gb-reduced-29 pf", m, m, PfOptions);   // dense vs sparse, and run to run
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, PfOptions);
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, PfOptions);
        S10TestSupport.AssertBitIdentical("gb-reduced-29 dense vs sparse", s, d);

        Assert.True(s.Converged);
        Assert.Equal(16, s.Iterations);
        Assert.Equal(1.673328142715036E-12, s.MaxMismatch);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: loss MW 1905.8260814601151 -> 1905.826081460152; loss MVAr
        // 17914.637126061392 -> 17914.637126061352; slack -15939.453918539923 -> -15939.453918539908; Vmin 0.9000084138578155 ->
        // 0.9000084138578154; max angle 106.38418899424208 -> 106.38418899424201; max loading 191.31067949531095 ->
        // 191.31067949531086 (iterations and mismatch unchanged).
        Assert.Equal(1905.826081460152, s.TotalLossMw);
        Assert.Equal(17914.637126061352, s.TotalLossMvar);
        int slack = m.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        // The slack absorbs the stated surplus: its net injection is -15,939.45 MW (its units at -15,482.45 MW).
        Assert.Equal(-15939.453918539908, s.PInjMw[slack]);
        int lo = Array.IndexOf(s.Vm, s.Vm.Min());
        Assert.Equal(25, s.BusIds[lo]);
        Assert.Equal(0.9000084138578154, s.Vm[lo]);
        Assert.Equal(106.38418899424201, s.VaDeg.Max());
        Assert.Equal(191.31067949531086, s.LineFlows.Max(f => f.LoadingPercent));
        Assert.Equal(14, s.LineFlows.Count(f => f.LoadingPercent > 100));
    }

    [Fact]
    public void The_reactive_limits_off_solve_is_pinned()
    {
        // MATPOWER runpf's default (no reactive limits): the solve PYPOWER reproduces to 4.441e-16 pu (see the remarks).
        GridModel m = Load();
        S10TestSupport.AssertSolvesIdentical("gb-reduced-29 Q off", m, m, QOff);
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, QOff);
        Assert.True(s.Converged);
        Assert.Equal(7, s.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: mismatch 4.4431125445498765E-13 -> 7.153722059172196E-13; loss MW
        // 1724.35396606217 -> 1724.3539660621514; loss MVAr 15633.362986203494 -> 15633.362986203523; slack -16120.926033937878 ->
        // -16120.926033937883 (Vmin unchanged).
        Assert.Equal(7.153722059172196E-13, s.MaxMismatch);
        Assert.Equal(1724.3539660621514, s.TotalLossMw);
        Assert.Equal(15633.362986203523, s.TotalLossMvar);
        int slack = m.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        Assert.Equal(-16120.926033937883, s.PInjMw[slack]);
        int lo = Array.IndexOf(s.Vm, s.Vm.Min());
        Assert.Equal(24, s.BusIds[lo]);
        Assert.Equal(0.984503345696812, s.Vm[lo]);
    }

    [Fact]
    public void The_contingency_study_solver_gives_the_pf_bits()
    {
        // No OLTCs in a MATPOWER case, and StudyOptions are the pf verb's option values: the regulated steady state the
        // contingency verbs start from is the pf solve, bit for bit.
        GridModel m = Load();
        PowerFlowResult study = ContingencyAnalysis.CreateStudySolver(m).Solve(m, ContingencyAnalysis.StudyOptions);
        S10TestSupport.AssertBitIdentical("gb-reduced-29 study vs pf", new NewtonRaphsonPowerFlow().Solve(m, PfOptions), study);
    }

    [Fact]
    public void The_cli_knows_the_name_and_loads_the_bundled_file()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("\"gb-reduced-29\" => Gated.Matpower(Path.Combine(root, \"data\", GridSim.Core.IO.PyPsa.GbReferenceModels.Gb29RelativePath)),", src, StringComparison.Ordinal);
        string gated = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Gated.cs"));
        Assert.Contains("\"gb-reduced-29\"", gated, StringComparison.Ordinal);
        Assert.Equal("gb-reduced-29", GbReferenceModels.Gb29Id);
        // The file sits inside the bundled data/matpower tree, so it is never counted against the licence tier.
        Assert.True(GridSim.Core.Licensing.BundledNetworks.IsBundledPath(Gb29Path));
    }

    [Fact]
    public void The_pf_verb_on_the_name_prints_the_pinned_solve()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "gb-reduced-29", "--top", "1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Power flow - GBreducednetwork (29 buses)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("converged in 16 iteration(s), mismatch 1.67E-012 pu   losses 1905.826 MW / 17914.637 MVAr", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("bus 25     0.9000 pu", r.StdOut, StringComparison.Ordinal);
    }
}
