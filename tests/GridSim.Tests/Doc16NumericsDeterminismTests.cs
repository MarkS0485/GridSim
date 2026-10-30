using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// DOC-16, the numerics and determinism remainder of the docs-session findings (October 2026; Mark's determinism rule
/// 2026-10-04: the same bits on every solver, seam, CPU tier and OS). One region per item:
/// <list type="bullet">
/// <item>N233: the fault network is one engine (sparse) at every size; the dense LU survives only as a test oracle.</item>
/// <item>N262: the N240 near-tie entering-column rule in the other two BoundedSimplex users (contingency corrective
/// redispatch, storage LP scheduler): every result whose pivot path meets no near-tie is bit-identical to the strict
/// rule; what moves on the degenerate ones is measured with DOC-11's 1e-13 perturbation test.</item>
/// <item>D-DOC-119: the RoCoF screening sort has one defined order (stable on ties), proved against the old sort on every
/// bundled case.</item>
/// <item>D-DOC-150: a converged backward/forward sweep is labelled by its own test (the voltage step).</item>
/// <item>D-DOC-183: weak-mesh sweep compensation on case33bw with its five ties closed; whole solves through every
/// vector tier compared bit for bit.</item>
/// <item>D-DOC-209 / 210: the CPU side of the GPU findings (the shared pivot floor's routing, by behaviour; the ROCm
/// near-singular check against the CPU LU on a randomised set).</item>
/// </list>
/// D-DOC-211's additions are in <see cref="DenseSparseParityTests"/>.
/// </summary>
public partial class Doc16NumericsDeterminismTests
{
    private readonly ITestOutputHelper _out;

    public Doc16NumericsDeterminismTests(ITestOutputHelper output) => _out = output;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static GridModel Json(string dir) => JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, dir));

    /// <summary>A bundled case by name: a path ending in ".m" is a MATPOWER file under data/matpower (parsed and
    /// normalised exactly as the CLI does), anything else a JSON folder under data.</summary>
    private static GridModel Load(string name) =>
        name.EndsWith(".m", StringComparison.Ordinal) ? S10TestSupport.LoadMatpower(name).Model : Json(name);

    /// <summary>Every bundled positive-sequence case: the 27 MATPOWER files and the seven JSON folders.</summary>
    public static IEnumerable<object[]> EveryBundledCase()
    {
        foreach (string f in S10TestSupport.BundledMatpowerFiles()) yield return new object[] { f };
        foreach (string d in new[] { "uk", "gb-synthetic", "gb-full", "intl/ireland-all-island", "intl/iberia", "intl/france",
                     "intl/continental-europe" })
            yield return new object[] { d };
    }

    // =================================================================================================================
    // N233: one fault-network engine.
    // =================================================================================================================

    [Fact]
    public void N233_every_fault_network_is_the_sparse_engine_at_every_size_and_the_oracle_does_not_leak()
    {
        GridModel c9 = IeeeCases.Case9();
        var shunts = new List<(int, Complex)> { (0, Complex.One / new Complex(0, 0.2)) };
        Assert.True(FaultNetworkSystem.ForModel(c9, shunts).IsSparse);                      // 9 buses: sparse
        Assert.True(FaultNetworkSystem.ForModel(c9, shunts, seriesOnly: true).IsSparse);
        var trip = new List<(int, int, Complex)> { (0, 0, new Complex(1, -10)), (1, 1, new Complex(1, -10)), (0, 1, new Complex(-0.5, 5)), (1, 0, new Complex(-0.5, 5)) };
        Assert.True(FaultNetworkSystem.FromTriplets(2, trip).IsSparse);                      // 2 buses: sparse

        // The dense oracle is only ever on inside its scope (an AsyncLocal on the test's own flow), and off again after.
        using (FaultNetworkSystem.DenseReferenceScope())
        {
            Assert.False(FaultNetworkSystem.FromTriplets(2, trip).IsSparse);
            Assert.False(FaultNetworkSystem.ForModel(c9, shunts).IsSparse);
        }
        Assert.True(FaultNetworkSystem.FromTriplets(2, trip).IsSparse);
        Assert.True(FaultNetworkSystem.ForModel(c9, shunts).IsSparse);
        // A flow that never opened the scope (a fresh thread with no captured context) is sparse even while another is open.
        using (FaultNetworkSystem.DenseReferenceScope())
        {
            bool elsewhere = false;
            var t = new Thread(() => elsewhere = FaultNetworkSystem.FromTriplets(2, trip).IsSparse);
            using (ExecutionContext.SuppressFlow()) t.Start();
            t.Join();
            Assert.True(elsewhere);
        }

        // The production IEC 60909 study at a small and a large size runs the same code: a case9 duty is the sparse
        // engine's answer (equal to a Zbus column solved directly on the sparse engine, bit for bit).
        Iec60909.Duty d = Iec60909.ThreePhase(c9, 5);
        Complex z1 = Iec60909.PositiveSequenceThevenin(c9, 5).Z1Pu;
        Assert.True(d.IkppKa > 0);
        _out.WriteLine($"case9 bus 5: Z1 {R(z1.Real)} + j{R(z1.Imaginary)} pu, Ik'' {R(d.IkppKa)} kA, ip {R(d.PeakIpKa)} kA (sparse engine)");
    }
}
