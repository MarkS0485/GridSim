using System.Numerics;
using System.Reflection;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// N271 (October 2026; ruled by main under Mark's determinism rule 2026-10-04, Ruling R1: Mark delegated EE calls
/// 2026-10-04): the RMS subtransient initialisation had two network engines, a dense Kron-reduced admittance for cases up
/// to 1,500 buses and the sparse full network above, which are the same linear model but not the same bits. It now has
/// one, the sparse full network, at every size (as the fault network since N233); the dense Kron path survives only as a
/// test oracle under <see cref="SubtransientInit.DenseKronReferenceScope"/>, which production cannot open. These tests
/// pin that:
/// <list type="bullet">
/// <item>production prepares the sparse network at every size, and nothing public can select the dense path;</item>
/// <item>the oracle scope is per async flow (a test's scope never leaks into another thread's study);</item>
/// <item>the sparse network and the dense oracle are the same linear map, to a stated tolerance, on WSCC-9 and on IEEE
/// 14, 30 and 118 (machines only, and with converter plant); the measured worst difference is written to the output so
/// the batch run records it;</item>
/// <item>the sparse engine is deterministic: repeated runs agree bit for bit.</item>
/// </list>
/// </summary>
public sealed class N271RmsOneNetworkEngineTests
{
    private readonly ITestOutputHelper _out;
    public N271RmsOneNetworkEngineTests(ITestOutputHelper output) => _out = output;

    /// <summary>A built-in case solved, with synthetic GENROU-like data (H at least 3 s so every unit integrates;
    /// X''d = X''q), and optionally every third unit (indices 2, 5, 8, ...) converter plant, so WSCC-9 has one.</summary>
    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case(GridModel b, bool converters)
    {
        GridModel m = b.CopyWith(generators: b.Generators
            .Select((g, k) => converters && k % 3 == 2 ? g with { Type = "battery" } : g).ToList());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        var data = m.Generators.Select(g =>
        {
            const double x = 0.2;
            return new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 3 * x, x, 0.8 * x, 2.5 * x, 1.5 * x, 0.8 * x,
                8.0, 0.03, 0.4, 0.05);
        }).ToList();
        return (m, pf, data);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in new[] { "case9", "case14", "case30", "case118" })
            foreach (bool conv in new[] { false, true })
                yield return new object[] { name, conv };
    }

    private static GridModel Builtin(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        "case118" => IeeeCases.Case118(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact]
    public void Production_prepares_the_sparse_network_at_every_size()
    {
        foreach (string name in new[] { "case9", "case14", "case118" })
        {
            (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case(Builtin(name), converters: false);
            int fault = m.Buses[m.Buses.Count / 2].Id;
            SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, data, fault);
            Assert.True(p.IsSparse, $"{name}: production must prepare the sparse network");
            Assert.NotNull(p.Network);
            Assert.NotNull(p.NetworkFaulted);
            Assert.True(p.YReduced.Length == 0, $"{name}: no dense Kron matrix may be formed");
            Assert.Null(p.YReducedFaulted);
        }
    }

    [Fact]
    public void No_public_api_can_select_the_dense_path()
    {
        // The old public selector (bool? sparseNetwork) is gone from every entry point, and the oracle scope is internal.
        foreach (MethodInfo mi in typeof(SubtransientInit).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(RmsTransientStability).GetMethods(BindingFlags.Public | BindingFlags.Static)))
            Assert.DoesNotContain(mi.GetParameters(), pa => pa.Name == "sparseNetwork");
        MethodInfo? scope = typeof(SubtransientInit).GetMethod("DenseKronReferenceScope", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(scope);
        Assert.True(scope!.IsAssembly, "the dense oracle scope must be internal (tests only)");
        Assert.Null(typeof(SubtransientInit).GetMethod("DenseKronReferenceScope", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(SubtransientInit).GetField("DenseKronBusLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void The_oracle_scope_is_per_flow_and_restores_on_dispose()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case(IeeeCases.Case9(), converters: false);
        Assert.True(SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);
        using (SubtransientInit.DenseKronReferenceScope())
        {
            Assert.False(SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);
            using (SubtransientInit.DenseKronReferenceScope())
                Assert.False(SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);   // nested scopes
            Assert.False(SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);       // the outer scope still holds
            // A flow that never opened the scope (a fresh thread with no captured context) is production: sparse, even
            // while this flow holds the scope (the same check N233 makes for the fault network's oracle).
            bool elsewhere = false;
            var t = new Thread(() => elsewhere = SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);
            using (ExecutionContext.SuppressFlow()) t.Start();
            t.Join();
            Assert.True(elsewhere, "the oracle scope leaked into another thread");
        }
        Assert.True(SubtransientInit.FromSolvedCase(m, pf, data, 7).IsSparse);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_sparse_network_and_the_dense_oracle_are_the_same_linear_map(string name, bool converters)
    {
        // Same E and converter currents into both networks (base and faulted stage): the machine currents and the
        // converter-bus voltages must agree to 1e-9 of their scale (the same relative tolerance N233 / D-DOC-231 kept
        // for the fault network's dense oracle). The measured worst relative difference is printed for the record.
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case(Builtin(name), converters);
        int fault = m.Buses[m.Buses.Count / 2].Id;
        SubtransientInit.Prepared sp = SubtransientInit.FromSolvedCase(m, pf, data, fault);
        SubtransientInit.Prepared dp;
        using (SubtransientInit.DenseKronReferenceScope()) dp = SubtransientInit.FromSolvedCase(m, pf, data, fault);

        // The initial state does not depend on the network engine at all: bit for bit.
        Assert.Equal(dp.Delta0, sp.Delta0);
        Assert.Equal(dp.Eqpp0, sp.Eqpp0);
        Assert.Equal(dp.Edpp0, sp.Edpp0);
        Assert.Equal(dp.Efd, sp.Efd);
        Assert.Equal(dp.Pm, sp.Pm);
        Assert.Equal(dp.Machines.Count, sp.Machines.Count);
        Assert.Equal(dp.ConverterUnits.Count, sp.ConverterUnits.Count);

        int n = sp.Machines.Count, c = sp.ConverterUnits.Count;
        Complex[] e = Enumerable.Range(0, n).Select(i => Complex.FromPolarCoordinates(1.0 + 0.003 * i, 0.05 * i - 0.2)).ToArray();
        Complex[] ic = Enumerable.Range(0, c).Select(i => new Complex(0.4 - 0.05 * i, -0.1)).ToArray();
        double worst = 0.0;
        foreach (bool faulted in new[] { false, true })
        {
            IRmsNetwork sparse = faulted ? sp.NetworkFaulted! : sp.Network!;
            Complex[,] y = faulted ? dp.YReducedFaulted! : dp.YReduced;
            IRmsNetwork dense = c == 0 ? new DenseMachineNetwork(y) : new ConverterNetwork(y, n);
            (Complex[] imS, Complex[] vcS) = sparse.Solve(e, ic);
            (Complex[] imD, Complex[] vcD) = dense.Solve(e, ic);
            double scaleI = imD.Max(z => z.Magnitude), scaleV = vcD.Length == 0 ? 1.0 : vcD.Max(z => z.Magnitude);
            for (int i = 0; i < n; i++) worst = Math.Max(worst, (imS[i] - imD[i]).Magnitude / scaleI);
            for (int k = 0; k < c; k++) worst = Math.Max(worst, (vcS[k] - vcD[k]).Magnitude / scaleV);
        }
        _out.WriteLine($"N271 {name} converters={converters}: worst relative |sparse - dense oracle| = {worst:R}");
        Assert.True(worst <= 1e-9, $"{name}: sparse vs dense oracle differ by {worst:R} relative");
    }

    [Fact]
    public void Case9_fault_run_on_the_sparse_engine_matches_the_oracle_and_is_deterministic()
    {
        // A whole fault-and-clear study: the production (sparse) run against the dense oracle, rotor angles to 1e-9 rad
        // over 2 s, and two production runs bit for bit. The measured worst angle difference is printed for the record.
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case(IeeeCases.Case9(), converters: true);
        MultiMachineSubtransient.Result Run() => RmsTransientStability.Study(m, pf, data, 7, 0.1, 0.08,
            stepSeconds: 0.002, durationSeconds: 2.0);
        MultiMachineSubtransient.Result a = Run(), b = Run();
        MultiMachineSubtransient.Result oracle;
        using (SubtransientInit.DenseKronReferenceScope()) oracle = Run();

        double worst = 0.0;
        for (int i = 0; i < a.Machines.Count; i++)
        {
            Assert.Equal(a.Machines[i].DeltaRad, b.Machines[i].DeltaRad);   // deterministic: exact
            Assert.Equal(a.Machines[i].SpeedPu, b.Machines[i].SpeedPu);
            for (int k = 0; k < a.Machines[i].DeltaRad.Count; k++)
                worst = Math.Max(worst, Math.Abs(a.Machines[i].DeltaRad[k] - oracle.Machines[i].DeltaRad[k]));
        }
        _out.WriteLine($"N271 case9 2 s fault run: worst |delta sparse - delta dense oracle| = {worst:R} rad");
        Assert.True(worst <= 1e-9, $"sparse vs dense oracle rotor angles differ by {worst:R} rad");
        Assert.Equal(oracle.StayedInSynchronism, a.StayedInSynchronism);
    }
}
