using System;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N130 (CF2, October 2026; Ruling R1): gb-reduced-29 is NOT rebalanced. The file stays the unaltered source (N125), a
/// like-for-like cross-check with MATPOWER / pandapower, whose power flows also leave the imbalance on the slack; the
/// source has no balanced dispatch to take (PyPSA-GB's version carries no demand or generation). The decision and its
/// reasons are recorded in data/matpower/gb/README.md; a balanced study is a labelled run-time choice (acopf / dcopf, or
/// pf --distributed-slack pmax).
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    [Fact]
    public void N130_gb_reduced_29_is_shipped_unbalanced_and_the_readme_records_why()
    {
        GridModel m = MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(
            Path.Combine(S10TestSupport.DataDir, GbReferenceModels.Gb29RelativePath)));
        // The facts the README states, from the file itself.
        Assert.Equal(66, m.Generators.Count);
        Assert.Equal(82384.8, m.Generators.Sum(g => g.Pmax), 6);
        Assert.Equal(new[] { 1082.0, 216.0 }, m.Generators.Where(g => g.Bus == 27).Select(g => g.Pg).ToArray());
        Assert.Equal(BusType.Slack, m.Buses.Single(b => b.Id == 27).Type);

        // Corrected 2026-10-11 (end pass, first run): the test project copies only *.m, *.json and *.csv from data/ into
        // the build output, so data/matpower/gb/README.md is never beside the test binary; the README is a repository
        // document and is read from the repository root (the GridSim.slnx folder), as the other doc-reading tests do.
        // Before -> after: Path.Combine(S10TestSupport.DataDir, "matpower", "gb", "README.md") ->
        // Path.Combine(Doc17TextHygieneTests.RepoRoot(), "data", "matpower", "gb", "README.md").
        string readme = File.ReadAllText(Path.Combine(Doc17TextHygieneTests.RepoRoot(), "data", "matpower", "gb", "README.md"));
        Assert.Contains("**Decided (N130, Ruling R1, October 2026): gb-reduced-29 is NOT rebalanced.**", readme, StringComparison.Ordinal);
        Assert.Contains("pf gb-reduced-29 --distributed-slack pmax", readme, StringComparison.Ordinal);
    }
}
