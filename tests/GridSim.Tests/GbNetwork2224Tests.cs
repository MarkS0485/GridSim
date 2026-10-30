using System.Security.Cryptography;
using System.Text;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S2 / P03 (October 2026): the full GB network of Bukhsh and McKinnon (GBnetwork.m: 2,224 buses, 3,207 branches, 394
/// generators) as the bundled, named case <c>gb-network-2224</c>. Source: the Edinburgh Power Systems Test Case Archive
/// original, https://www.maths.ed.ac.uk/optenergy/NetworkData/fullGB/GBnetwork.m (fetched 2026-10-04); provenance,
/// terms and checksum in data/matpower/gb/README.md. It is bundled as the original MATPOWER file, which GridSim reads
/// natively, rather than via pandapower's GBnetwork.json: that JSON (pandapower 3.5.5) is a DIFFERENT version of the
/// data (446 loads totalling 60,651.17 MW and 430 generating units, against this file's 60,077.56 MW and 394 units).
/// </summary>
/// <remarks>
/// Every pinned number was produced by GridSim at 1.95.120 and is asserted exactly; the dense and sparse engines agree
/// to the bit. Independent check, recorded here (not run by the suite: it needs Python): PYPOWER 5.1.21 runpf (MATPOWER's
/// Newton power flow in Python, PF_TOL 1e-10) on this file with reactive limits off agrees with GridSim to at most
/// 3.515e-13 pu in |V| and 9.833e-11 degrees in angle; its branch losses are 1220.290831817 MW (GridSim
/// 1220.2908318175455 MW). The case converges in 13 Newton iterations with reactive limits on, so no non-convergence
/// diagnosis was needed.
/// </remarks>
public class GbNetwork2224Tests
{
    private static string Path2224 => Path.Combine(S10TestSupport.DataDir, GbReferenceModels.Gb2224RelativePath);

    private static GridModel Load() => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Path2224));

    private static readonly PowerFlowOptions PfOptions = new() { MaxIterations = 100, EnforceQLimits = true };
    private static readonly PowerFlowOptions QOff = new() { MaxIterations = 100, EnforceQLimits = false };

    [Fact]
    public void The_bundled_file_is_the_edinburgh_original_byte_for_byte()
    {
        string text = File.ReadAllText(Path2224).Replace("\r\n", "\n");
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        Assert.Equal("d6c7d486d7c339fa2da926ad5ceeaa5b8a85286c079dc6bf12a09c8204c832c0", sha);
        Assert.Contains("%Data Stats: 2224 buses, 394 generators, 3207 branches", text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_parses_to_the_published_counts_and_totals()
    {
        MatpowerParseResult p = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(Path2224), "GBnetwork");
        GridModel m = MatpowerUnitNormalizer.Normalize(p.Model);
        Assert.Equal(2224, m.BusCount);
        Assert.Equal(3207, m.Branches.Count);
        Assert.Equal(394, m.AllGenerators.Count);
        Assert.Equal(394, m.Generators.Count);
        Assert.Equal(1314, m.Branches.Count(b => b.IsTransformer));
        Assert.Equal(0, m.Branches.Count(b => b.PhaseShiftDeg != 0));
        Assert.Equal(431, m.Buses.Single(b => b.Type == BusType.Slack).Id);
        Assert.Equal(377, m.Buses.Count(b => b.Type == BusType.Pv));
        Assert.Equal(60077.56000000001, m.Buses.Sum(b => b.Pd));   // the double sum in model order of the file's 60,077.56 MW
        Assert.Equal(61293.2301, m.Generators.Sum(g => g.Pg));
        Assert.Equal(2, p.Notes.Count);
        Assert.StartsWith("read mpc.gencost (394 row(s))", p.Notes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_pf_solve_converges_and_is_pinned_with_dense_equal_to_sparse()
    {
        GridModel m = Load();
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, PfOptions);
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, PfOptions);
        S10TestSupport.AssertBitIdentical("gb-network-2224 dense vs sparse", s, d);

        Assert.True(s.Converged);
        Assert.Equal(13, s.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the Newton
        // solve's sin / cos now come from GridSim's deterministic fdlibm, not the Windows C runtime. Before -> after: mismatch
        // 7.876687202799512E-11 -> 3.862107975815043E-11; loss MW 1224.9099110491472 -> 1224.9099110490408; loss MVAr
        // 3064.1303243000534 -> 3064.1303242937724; slack 315.23501104902647 -> 315.2350110493831; Vmin 0.9384735001948986 ->
        // 0.938473500194911; Vmax 1.0557620510024657 -> 1.055762051002459; max loading 23.382424967096668 -> 23.382424967096416.
        Assert.Equal(3.862107975815043E-11, s.MaxMismatch);
        Assert.Equal(1224.9099110490408, s.TotalLossMw);
        Assert.Equal(3064.1303242937724, s.TotalLossMvar);
        int slack = m.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        Assert.Equal(315.2350110493831, s.PInjMw[slack]);          // slack bus 431 carries no load: this is its unit's output
        int lo = Array.IndexOf(s.Vm, s.Vm.Min()), hi = Array.IndexOf(s.Vm, s.Vm.Max());
        Assert.Equal(1282, s.BusIds[lo]);
        Assert.Equal(0.938473500194911, s.Vm[lo]);
        Assert.Equal(808, s.BusIds[hi]);
        Assert.Equal(1.055762051002459, s.Vm[hi]);
        Assert.Equal(23.382424967096416, s.LineFlows.Max(f => f.LoadingPercent));
        Assert.Equal(0, s.LineFlows.Count(f => f.LoadingPercent > 100));
    }

    [Fact]
    public void The_reactive_limits_off_solve_is_pinned_and_the_files_stored_voltages_are_not_its_solution()
    {
        GridModel m = Load();
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, QOff);
        Assert.True(s.Converged);
        Assert.Equal(3, s.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: mismatch 5.774696916003845E-11 -> 3.044619654633547E-11; loss MW
        // 1220.2908318175455 -> 1220.2908318177113; Vmin 0.9435103547570256 -> 0.9435103547570594.
        Assert.Equal(3.044619654633547E-11, s.MaxMismatch);
        Assert.Equal(1220.2908318177113, s.TotalLossMw);
        int lo = Array.IndexOf(s.Vm, s.Vm.Min());
        Assert.Equal(1773, s.BusIds[lo]);
        Assert.Equal(0.9435103547570594, s.Vm[lo]);

        // The file stores a voltage per bus (its warm start, used as GridSim's). It is NOT a solution of the file's own
        // data: the generator set-points (mpc.gen VG) are given to two decimals while the stored bus voltages are not,
        // so every PV bus is held at its VG, not at the stored value. The largest gap is bus 65: stored 1.0351 pu, VG 1.04.
        double dvm = 0; int at = -1;
        for (int i = 0; i < m.BusCount; i++)
        {
            double gap = Math.Abs(s.Vm[i] - m.Buses[i].Vm);
            if (gap > dvm) { dvm = gap; at = i; }
        }
        Assert.Equal(65, m.Buses[at].Id);
        Assert.Equal(0.0049000000000001265, dvm);
        Assert.Equal(1.0351, m.Buses[at].Vm);
        Assert.Equal(1.04, m.Generators.Single(g => g.Bus == 65).Vg);
    }

    [Fact]
    public void The_cli_knows_the_name()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("\"gb-network-2224\" => Gated.Matpower(Path.Combine(root, \"data\", GridSim.Core.IO.PyPsa.GbReferenceModels.Gb2224RelativePath)),", src, StringComparison.Ordinal);
        Assert.Contains("\"gb-network-2224\"", File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Gated.cs")), StringComparison.Ordinal);
        Assert.True(GridSim.Core.Licensing.BundledNetworks.IsBundledPath(Path2224));
    }

    [Fact]
    public void The_pf_verb_on_the_name_prints_the_pinned_solve()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "gb-network-2224", "--top", "1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Power flow - GBnetwork (2224 buses)", r.StdOut, StringComparison.Ordinal);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the printed
        // mismatch follows the pinned solve above. Before -> after: 7.88E-011 -> 3.86E-011 (losses print unchanged).
        Assert.Contains("converged in 13 iteration(s), mismatch 3.86E-011 pu   losses 1224.910 MW / 3064.130 MVAr", r.StdOut, StringComparison.Ordinal);
    }
}
