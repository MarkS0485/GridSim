using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A NEW assertion alongside <c>DenseSparseParityTests</c> (October 2026 docs-programme finding): that suite's
/// comparison checks every per-branch flow and the loading, but not the per-branch losses
/// (<see cref="LineFlow.PLossMw"/>, <see cref="LineFlow.QLossMvar"/>). The losses feed the loss-allocation and
/// loss-contingency reports, so they are held to the same rule: the dense and sparse Newton engines must give the
/// SAME IEEE-754 bits for every branch's real and reactive loss. This file adds that check; it does not touch the
/// existing suite or its expectations.
/// </summary>
public class DenseSparseLossParityTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>The built-in IEEE systems, and the bundled MATPOWER files of the existing parity suite that the
    /// dense engine can hold (pegase excluded here only to keep this added check quick; the flows of those cases are
    /// already compared bit for bit by the existing suite).</summary>
    public static IEnumerable<object[]> Cases => new[]
    {
        "builtin:9", "builtin:14", "builtin:30", "builtin:57", "builtin:118", "builtin:300", "builtin:24", "builtin:145",
        "lib/case24_ieee_rts.m", "dist/case33bw.m", "dist/case69.m", "dist/case533mt_hi.m", "benchmarks/cigre_mv.m",
        "nesta/pglib_opf_case162_ieee_dtc.m", "nesta/pglib_opf_case240_pserc.m", "nesta/pglib_opf_case588_sdet.m",
        "rts/pglib_opf_case73_ieee_rts.m",
    }.Select(c => new object[] { c });

    private static GridModel Load(string id)
    {
        if (id.StartsWith("builtin:", StringComparison.Ordinal))
        {
            return id["builtin:".Length..] switch
            {
                "9" => IeeeCases.Case9(),
                "14" => IeeeCases.Case14(),
                "30" => IeeeCases.Case30(),
                "57" => IeeeCases.Case57(),
                "118" => IeeeCases.Case118(),
                "300" => IeeeCases.Case300(),
                "24" => IeeeCases.Case24Rts(),
                "145" => IeeeCases.Case145(),
                _ => throw new ArgumentException($"unknown built-in {id}", nameof(id)),
            };
        }
        string path = Path.Combine(DataDir, "matpower", id.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled case file missing: {path}");   // never silently skip
        return MatpowerParser.ParseFile(path);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Branch_losses_are_bit_identical_dense_vs_sparse(string id)
    {
        GridModel m = Load(id);
        foreach (PowerFlowOptions opt in new[]
                 {
                     new PowerFlowOptions { EnforceQLimits = true },
                     new PowerFlowOptions { EnforceQLimits = false },
                     new PowerFlowOptions { EnforceQLimits = true, FlatStart = true },
                 })
        {
            PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, opt);
            PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
            string label = $"{id} [qlim={opt.EnforceQLimits}, flat={opt.FlatStart}]";
            Assert.True(d.LineFlows.Count == s.LineFlows.Count, $"{label}: branch count dense {d.LineFlows.Count} vs sparse {s.LineFlows.Count}");

            var bad = new List<string>();
            for (int k = 0; k < d.LineFlows.Count; k++)
            {
                LineFlow a = d.LineFlows[k], b = s.LineFlows[k];
                if (BitConverter.DoubleToInt64Bits(a.PLossMw) != BitConverter.DoubleToInt64Bits(b.PLossMw))
                    bad.Add($"branch {k} PLossMw dense {a.PLossMw:R} vs sparse {b.PLossMw:R}");
                if (BitConverter.DoubleToInt64Bits(a.QLossMvar) != BitConverter.DoubleToInt64Bits(b.QLossMvar))
                    bad.Add($"branch {k} QLossMvar dense {a.QLossMvar:R} vs sparse {b.QLossMvar:R}");
            }
            Assert.True(bad.Count == 0, $"{label}: {bad.Count} loss value(s) differ:\n" + string.Join("\n", bad.Take(10)));
        }
    }
}
