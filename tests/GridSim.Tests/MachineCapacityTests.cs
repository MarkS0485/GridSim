using GridSim.Core.IO.Json;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// The RAM gate (<see cref="MachineCapacity"/>, Mark 2026-10-12): the tier arithmetic, the override, and the bus counts
/// it gates on, checked against the bundled data so a gated row can never be filed under the wrong size.
/// </summary>
public class MachineCapacityTests
{
    private const long Gib = 1L << 30;

    private readonly ITestOutputHelper _out;

    public MachineCapacityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void This_machine_reports_its_tier()
    {
        _out.WriteLine(MachineCapacity.Describe());
        Assert.True(MachineCapacity.InstalledGb > 0);
        Assert.True(MachineCapacity.Cap(Engine.Sparse) > 0);
        Assert.True(MachineCapacity.Cap(Engine.Dense) > 0);
    }

    [Fact]
    public void Installed_memory_rounds_up_to_whole_gigabytes()
    {
        Assert.Equal(8, MachineCapacity.NominalGb((long)(7.7 * Gib)));   // an "8 GB" laptop after the firmware's share
        Assert.Equal(8, MachineCapacity.NominalGb(8 * Gib));
        Assert.Equal(16, MachineCapacity.NominalGb((long)(15.7 * Gib)));
        Assert.Equal(32, MachineCapacity.NominalGb((long)(31.8 * Gib)));
    }

    [Fact]
    public void Eight_gigabytes_runs_up_to_gb_full_and_no_further()
    {
        foreach (Engine e in new[] { Engine.Sparse, Engine.Dense })
        {
            int cap = MachineCapacity.MaxBuses(8, e);
            Assert.Equal(MachineCapacity.AnchorBuses, cap);
            Assert.True(CaseSizes.GbFull <= cap, $"{e}: gb-full must run on 8 GB");
            Assert.True(CaseSizes.ContinentalEurope > cap, $"{e}: continental-europe must not run on 8 GB");
            Assert.True(CaseSizes.Pegase9241 > cap, $"{e}: case9241pegase must not run on 8 GB");
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Sixteen_gigabytes_and_up_runs_everything(int gb)
    {
        Assert.Equal(MachineCapacity.Unlimited, MachineCapacity.MaxBuses(gb, Engine.Sparse));
        Assert.Equal(MachineCapacity.Unlimited, MachineCapacity.MaxBuses(gb, Engine.Dense));
    }

    [Fact]
    public void Between_the_anchors_sparse_rises_linearly_and_dense_along_n_squared()
    {
        // 12 GB, half way: sparse 3,600 + (9,241 - 3,600) / 2; dense sqrt(3,600^2 + (9,241^2 - 3,600^2) / 2).
        Assert.Equal(6420, MachineCapacity.MaxBuses(12, Engine.Sparse));
        Assert.Equal(7012, MachineCapacity.MaxBuses(12, Engine.Dense));
        Assert.True(CaseSizes.ContinentalEurope <= MachineCapacity.MaxBuses(12, Engine.Sparse));
        Assert.True(CaseSizes.Pegase9241 > MachineCapacity.MaxBuses(12, Engine.Dense));
        // Never falls as memory grows.
        for (int gb = 1; gb < 16; gb++)
            foreach (Engine e in new[] { Engine.Sparse, Engine.Dense })
                Assert.True(MachineCapacity.MaxBuses(gb, e) <= MachineCapacity.MaxBuses(gb + 1, e), $"{e} cap falls from {gb} to {gb + 1} GB");
    }

    [Fact]
    public void Below_eight_gigabytes_large_cases_do_not_run()
    {
        Assert.Equal(2700, MachineCapacity.MaxBuses(6, Engine.Sparse));
        Assert.True(MachineCapacity.MaxBuses(6, Engine.Dense) < CaseSizes.GbFull);
    }

    [Fact]
    public void The_override_is_all_a_positive_number_or_unset_and_anything_else_fails_loud()
    {
        Assert.Null(MachineCapacity.ParseOverride(null));
        Assert.Null(MachineCapacity.ParseOverride(""));
        Assert.Null(MachineCapacity.ParseOverride("  "));
        Assert.Equal(MachineCapacity.Unlimited, MachineCapacity.ParseOverride("all"));
        Assert.Equal(MachineCapacity.Unlimited, MachineCapacity.ParseOverride(" ALL "));
        Assert.Equal(5000, MachineCapacity.ParseOverride("5000"));
        foreach (string bad in new[] { "0", "-1", "lots", "3.5", "1e4", "5,000" })
            Assert.Throws<InvalidOperationException>(() => MachineCapacity.ParseOverride(bad));
    }

    [Fact]
    public void A_case_within_the_cap_is_not_skipped_and_one_above_it_names_the_override()
    {
        Assert.Null(MachineCapacity.SkipReason(9, Engine.Dense));
        int cap = MachineCapacity.Cap(Engine.Sparse);
        if (cap == MachineCapacity.Unlimited) return;   // a machine (or override) with no cap has nothing above it
        string? reason = MachineCapacity.SkipReason(cap + 1, Engine.Sparse);
        Assert.NotNull(reason);
        Assert.Contains(MachineCapacity.Variable + "=all", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gated_case_sizes_are_the_bundled_cases_own()
    {
        Assert.Equal(CaseSizes.GbFull, JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, "gb-full"), dispatch: false).BusCount);
        Assert.Equal(CaseSizes.ContinentalEurope,
            JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, "intl", "continental-europe"), dispatch: false).BusCount);
        foreach (KeyValuePair<string, int> f in CaseSizes.LargeMatpowerFiles)
            Assert.Equal(f.Value, S10TestSupport.LoadMatpower(f.Key).Model.BusCount);
    }

    [Fact]
    public void The_release_workflow_runs_every_gated_test()
    {
        string rel = File.ReadAllText(Path.Combine(CliProcess.Root(), ".github", "workflows", "release.yml")).ReplaceLineEndings("\n");
        Assert.Contains("      - name: Test\n", rel, StringComparison.Ordinal);
        Assert.Contains("          " + MachineCapacity.Variable + ": all\n        run: dotnet test GridSim.slnx -c Release --no-build --nologo",
            rel, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_bundled_matpower_file_is_within_the_eight_gigabyte_cap()
    {
        // A new large file must be added to CaseSizes.LargeMatpowerFiles (and so gated), never slip in ungated.
        foreach (string rel in S10TestSupport.BundledMatpowerFiles())
        {
            if (CaseSizes.LargeMatpowerFiles.ContainsKey(rel)) continue;
            int n = S10TestSupport.LoadMatpower(rel).Model.BusCount;
            Assert.True(n <= MachineCapacity.AnchorBuses, $"{rel}: {n} buses is above {MachineCapacity.AnchorBuses}; add it to CaseSizes.LargeMatpowerFiles");
        }
    }
}
