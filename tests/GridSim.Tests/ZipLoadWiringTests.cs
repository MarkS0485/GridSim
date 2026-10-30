using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N014 (R065 remainder): the last consumers of the bus load now take it from the one shared rule
/// <see cref="LoadAtVoltage"/> at the solved voltage, and the JSON case loader can at last set
/// <see cref="Bus.Zip"/>.
/// <list type="bullet">
/// <item><see cref="PhysicsFeasibility.Check"/> derives a generator bus's Qg as Qinj + Qload(Vm).</item>
/// <item><see cref="PowerFlowTracing"/> takes demand as Pload(Vm), so a ZIP load bus is no longer a phantom generator.</item>
/// <item>buses.json accepts an optional "zip" block, refused fail-loud when incomplete, non-finite or not summing to 1.</item>
/// </list>
/// A bus with no ZIP must give bit-for-bit the historical numbers; the bundled-case tests below pin that against
/// the old nominal-load formula evaluated independently in the test.
/// </summary>
public class ZipLoadWiringTests
{
    // ------------------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------------------

    private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

    // A hand-built "solved" state, so every number in the assertions can be checked on paper. Only the fields
    // the consumers under test read are meaningful; the rest are zero.
    private static PowerFlowResult State(int[] busIds, double[] vm, double[] pInj, double[] qInj, IReadOnlyList<LineFlow> flows) => new()
    {
        Converged = true,
        Iterations = 1,
        MaxMismatch = 0.0,
        BusIds = busIds,
        Vm = vm,
        VaDeg = new double[busIds.Length],
        PInjMw = pInj,
        QInjMvar = qInj,
        TotalLossMw = 0.0,
        TotalLossMvar = 0.0,
        LineFlows = flows,
    };

    private static string BundledCase(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "data", name);
        Assert.True(Directory.Exists(dir), $"missing shipped case {dir}");
        return dir;
    }

    public static IEnumerable<object[]> BundledCases() =>
        new[] { "case9", "case118", "gb-synthetic" }.Select(c => new object[] { c });

    private static GridModel Load(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case118" => IeeeCases.Case118(),
        "gb-synthetic" => JsonGridLoader.LoadDirectory(BundledCase("gb-synthetic")),
        _ => throw new ArgumentException($"unknown case {name}"),
    };

    // ------------------------------------------------------------------------------------------------------
    // PhysicsFeasibility.Check
    // ------------------------------------------------------------------------------------------------------

    // Two buses: the slack, and a PV generator bus that also carries a 20 MVAr load. The load's reactive part is
    // pure constant impedance (Aq = 1), its real part constant power; at Vm = 0.9 it draws 20 * 0.81 = 16.2 MVAr.
    private static readonly ZipLoad ConstantZQ = new(Ap: 0, Bp: 0, Cp: 1, Aq: 1, Bq: 0, Cq: 0);

    private static GridModel FeasibilityCase(ZipLoad? zip, double qmax) => new()
    {
        BaseMva = 100,
        Name = "zip-feasibility",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pv, Pd: 0, Qd: 20, Vm: 0.9, BaseKv: 132, Zip: zip),
        },
        Generators = new[]
        {
            new Generator(1, 0, Vg: 1.0),
            new Generator(2, 0, Vg: 0.9, Qmin: -100, Qmax: qmax),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.0, Name: "L12") },
    };

    // The solved state: bus 2 at 0.9 pu with a net reactive injection of -5 MVAr.
    private static PowerFlowResult FeasibilityState() =>
        State(new[] { 1, 2 }, new[] { 1.0, 0.9 }, new[] { 0.0, 0.0 }, new[] { 0.0, -5.0 }, Array.Empty<LineFlow>());

    [Fact]
    public void Feasibility_derives_Qg_from_the_zip_reactive_load_at_the_solved_voltage()
    {
        // Qmax = 10. Qg = Qinj + Qload(V) = -5 + 20 * QMultiplier(0.9) = -5 + 16.2 = 11.2 MVAr > 10: a violation,
        // and the reported value must be exactly what LoadAtVoltage says, bit for bit.
        GridModel model = FeasibilityCase(ConstantZQ, qmax: 10);
        double qLoad = LoadAtVoltage.Mw(model.Buses[1], 0.9).QMvar;
        Assert.Equal(Bits(20.0 * ConstantZQ.QMultiplier(0.9)), Bits(qLoad));   // the shared rule itself
        double expectedQg = -5.0 + qLoad;
        Assert.Equal(11.2, expectedQg, 12);                                    // the paper value, 11.2 MVAr

        var genQ = PhysicsFeasibility.Check(model, FeasibilityState(), new FeasibilityConstraints())
            .Where(x => x.Kind == "GenQ").ToList();
        PhysicsFeasibility.LimitViolation v = Assert.Single(genQ);
        Assert.Equal(2, v.A);
        Assert.Equal(Bits(expectedQg), Bits(v.Value));
        Assert.Equal(10.0, v.Limit);
    }

    [Fact]
    public void Feasibility_no_longer_flags_a_zip_bus_on_its_nominal_reactive_load()
    {
        // Qmax = 14. On the NOMINAL Qd the bus would read Qg = -5 + 20 = 15 > 14 (the old, wrong verdict); at the
        // solved voltage it draws only 16.2 MVAr of load, so Qg = 11.2 < 14 and there is no violation.
        var feas = new FeasibilityConstraints();
        Assert.DoesNotContain(
            PhysicsFeasibility.Check(FeasibilityCase(ConstantZQ, qmax: 14), FeasibilityState(), feas),
            v => v.Kind == "GenQ");

        // The same bus with no ZIP is constant power: Qg = -5 + 20 = 15 exactly, a violation of Qmax 14.
        PhysicsFeasibility.LimitViolation plain = Assert.Single(
            PhysicsFeasibility.Check(FeasibilityCase(null, qmax: 14), FeasibilityState(), feas),
            v => v.Kind == "GenQ");
        Assert.Equal(Bits(15.0), Bits(plain.Value));
    }

    [Theory]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void Feasibility_refuses_a_corrupt_reactive_limit_instead_of_reading_it_as_no_limit(double qmax)
    {
        // GR-495: a -Infinity Qmax used to be mapped to +Infinity ("no limit") and a NaN never compared, so the unit
        // was never checked. Both are corrupt data and are now refused by name.
        var ex = Assert.Throws<ArgumentException>(() =>
            PhysicsFeasibility.Check(FeasibilityCase(null, qmax: qmax), FeasibilityState(), new FeasibilityConstraints()));
        Assert.Contains("reactive limits", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void Feasibility_on_a_bundled_case_is_bit_identical_to_the_nominal_load_formula(string caseName)
    {
        // Every bundled case is constant power (no voltage-dependent ZIP anywhere). Force EVERY non-slack generator
        // bus to report its Qg (Qmax far below any real output), then check each reported Qg against the
        // historical formula Qinj + nominal Qd, evaluated independently here, bit for bit.
        GridModel solved = Load(caseName);
        Assert.DoesNotContain(solved.Buses, b => b.Zip is { VariesWithVoltage: true });
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(solved);
        Assert.True(r.Converged, $"{caseName} did not converge");

        GridModel tight = solved.CopyWith(generators: solved.Generators
            .Select(g => g with { Qmin = -2e9, Qmax = -1e9 }).ToList());
        var genQ = PhysicsFeasibility.Check(tight, r, new FeasibilityConstraints())
            .Where(x => x.Kind == "GenQ").ToList();

        var slack = solved.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).ToHashSet();
        var expectedBuses = solved.Generators.Select(g => g.Bus).Distinct()
            .Where(id => !slack.Contains(id) && r.BusIds.Contains(id)).ToHashSet();
        Assert.Equal(expectedBuses.Count, genQ.Count);
        Assert.NotEmpty(genQ);

        var busIndex = new Dictionary<int, int>();
        for (int i = 0; i < r.BusIds.Count; i++) busIndex[r.BusIds[i]] = i;
        var nominalQd = new Dictionary<int, double>();
        foreach (Bus b in solved.Buses) nominalQd[b.Id] = b.Qd;
        foreach (PhysicsFeasibility.LimitViolation v in genQ)
        {
            Assert.Contains(v.A, expectedBuses);
            double historical = r.QInjMvar[busIndex[v.A]] + nominalQd.GetValueOrDefault(v.A, 0.0);
            Assert.Equal(Bits(historical), Bits(v.Value));
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // PowerFlowTracing
    // ------------------------------------------------------------------------------------------------------

    // Bus 1 (slack, 85 MW of generation) feeds bus 2, a 100 MW constant-impedance load (Ap = 1) sitting at 0.9 pu,
    // which therefore draws 100 * 0.81 = 81 MW. The line delivers exactly that; the 4 MW difference is loss.
    private static readonly ZipLoad ConstantZP = new(Ap: 1, Bp: 0, Cp: 0, Aq: 0, Bq: 0, Cq: 1);

    private static GridModel TracingCase(ZipLoad? zip) => new()
    {
        BaseMva = 100,
        Name = "zip-tracing",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 100, Qd: 0, BaseKv: 132, Zip: zip),
        },
        Generators = new[] { new Generator(1, 85, Vg: 1.0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.0, Name: "L12") },
    };

    [Fact]
    public void Tracing_takes_demand_from_the_zip_load_at_the_solved_voltage()
    {
        GridModel model = TracingCase(ConstantZP);
        double load = LoadAtVoltage.Mw(model.Buses[1], 0.9).PMw;
        Assert.Equal(Bits(100.0 * ConstantZP.PMultiplier(0.9)), Bits(load));   // the shared rule itself
        Assert.Equal(81.0, load, 12);                                          // the paper value, 81 MW

        PowerFlowResult r = State(new[] { 1, 2 }, new[] { 1.0, 0.9 }, new[] { 85.0, -load }, new[] { 0.0, 0.0 },
            new[] { new LineFlow(1, 2, 85.0, 0.0, -load, 0.0, 85.0 - load, 0.0, Name: "L12") });

        PowerFlowTracing.TraceResult t = PowerFlowTracing.Trace(model, r);
        Assert.True(t.FlowGraphAcyclic);

        // Generation is recovered as Pinj + demand. With the ZIP demand the load bus nets to exactly zero, so the
        // only generator is bus 1 at 85 MW. (On the nominal 100 MW, bus 2 would have shown a phantom 19 MW
        // generator: -81 + 100.)
        KeyValuePair<int, double> gen = Assert.Single(t.GenerationMw);
        Assert.Equal(1, gen.Key);
        Assert.Equal(Bits(85.0), Bits(gen.Value));

        // All of bus 1's power that reaches bus 2's load is that load: 81 MW (to rounding in the 2x2 LU solve),
        // and the rest, 4 MW, is bus 1's share of the loss.
        PowerFlowTracing.GenLoadFlow alloc = Assert.Single(t.Allocations);
        Assert.Equal(1, alloc.GenBus);
        Assert.Equal(2, alloc.LoadBus);
        Assert.Equal(load, alloc.Mw, 12);
        Assert.Equal(Bits(Math.Max(0.0, 85.0 - alloc.Mw)), Bits(t.GenToLossMw[1]));
        Assert.Equal(4.0, t.GenToLossMw[1], 9);
    }

    [Fact]
    public void Tracing_a_constant_power_bus_still_uses_its_nominal_demand()
    {
        // Same network, no ZIP: the load bus draws its nominal 100 MW whatever the voltage, so with a net
        // injection of -100 MW it nets to zero generation exactly as before.
        GridModel model = TracingCase(null);
        PowerFlowResult r = State(new[] { 1, 2 }, new[] { 1.0, 0.9 }, new[] { 104.0, -100.0 }, new[] { 0.0, 0.0 },
            new[] { new LineFlow(1, 2, 104.0, 0.0, -100.0, 0.0, 4.0, 0.0, Name: "L12") });
        PowerFlowTracing.TraceResult t = PowerFlowTracing.Trace(model, r);
        KeyValuePair<int, double> gen = Assert.Single(t.GenerationMw);
        Assert.Equal(1, gen.Key);
        Assert.Equal(Bits(104.0), Bits(gen.Value));
        Assert.Equal(100.0, Assert.Single(t.Allocations).Mw, 12);
    }

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void Tracing_on_a_bundled_case_is_bit_identical_to_the_nominal_load_formula(string caseName)
    {
        // The traced generation per bus is max(0, Pinj + demand). With no ZIP anywhere, demand must be the nominal
        // Pd, so every traced generation must equal, bit for bit, max(0, Pinj + Pd) evaluated independently here.
        GridModel model = Load(caseName);
        Assert.DoesNotContain(model.Buses, b => b.Zip is { VariesWithVoltage: true });
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(r.Converged, $"{caseName} did not converge");

        var nominalPd = new Dictionary<int, double>();
        foreach (Bus b in model.Buses) nominalPd[b.Id] = b.Pd;
        var expected = new Dictionary<int, double>();
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            double g = Math.Max(0.0, r.PInjMw[i] + nominalPd.GetValueOrDefault(r.BusIds[i], 0.0));
            if (g > PowerFlowTracing.TraceZeroMw) expected[r.BusIds[i]] = g;
        }

        PowerFlowTracing.TraceResult t = PowerFlowTracing.Trace(model, r);
        Assert.Equal(expected.Count, t.GenerationMw.Count);
        foreach ((int bus, double g) in expected)
            Assert.Equal(Bits(g), Bits(t.GenerationMw[bus]));
    }

    // ------------------------------------------------------------------------------------------------------
    // JSON loader: buses.json "zip"
    // ------------------------------------------------------------------------------------------------------

    // A two-bus case whose load bus 2 carries the given JSON fragment (inserted after "baseKv"), e.g. a zip block.
    private static string WriteCase(string bus2Extra)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "zip", "baseMva": 100 }""");
        File.WriteAllText(Path.Combine(dir, "buses.json"),
            "[ { \"id\": 1, \"baseKv\": 132 }, { \"id\": 2, \"name\": \"Load2\", \"baseKv\": 132" + bus2Extra + " } ]");
        File.WriteAllText(Path.Combine(dir, "generation.json"), """
            [ { "name": "G", "bus": 1, "capacityMva": 500, "outputMw": 50, "role": "slack" } ]
            """);
        File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "name": "D", "bus": 2, "pMw": 50, "qMvar": 10 } ]""");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), """
            [ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1, "ratingMva": 900 } ]
            """);
        return dir;
    }

    private static InvalidDataException LoadFails(string bus2Extra)
    {
        string dir = WriteCase(bus2Extra);
        try { return Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private const string ValidZip =
        ", \"zip\": { \"ap\": 0.5, \"bp\": 0.25, \"cp\": 0.25, \"aq\": 0.1, \"bq\": 0.2, \"cq\": 0.7 }";

    [Fact]
    public void A_valid_zip_block_loads_onto_the_bus_exactly()
    {
        string dir = WriteCase(ValidZip);
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Null(m.Buses.Single(b => b.Id == 1).Zip);              // absent = constant power, as before
            Assert.Equal(new ZipLoad(Ap: 0.5, Bp: 0.25, Cp: 0.25, Aq: 0.1, Bq: 0.2, Cq: 0.7),
                m.Buses.Single(b => b.Id == 2).Zip);

            // And the model solves with it, the solved load following the polynomial (the Newton path already
            // honours ZIP; this proves the loaded coefficients reach it).
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
            Assert.True(r.Converged);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_explicit_null_zip_is_constant_power()
    {
        string dir = WriteCase(", \"zip\": null");
        try { Assert.Null(JsonGridLoader.LoadDirectory(dir).Buses.Single(b => b.Id == 2).Zip); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_zip_missing_a_coefficient_fails_loud_naming_the_bus_and_key()
    {
        InvalidDataException ex = LoadFails(
            ", \"zip\": { \"ap\": 0.5, \"bp\": 0.25, \"aq\": 0.1, \"bq\": 0.2, \"cq\": 0.7 }");
        Assert.Contains("buses.json", ex.Message);
        Assert.Contains("bus 2", ex.Message);
        Assert.Contains("'cp'", ex.Message);
    }

    [Fact]
    public void A_zip_real_triple_not_summing_to_one_fails_loud()
    {
        InvalidDataException ex = LoadFails(
            ", \"zip\": { \"ap\": 0.5, \"bp\": 0.25, \"cp\": 0.5, \"aq\": 0.1, \"bq\": 0.2, \"cq\": 0.7 }");
        Assert.Contains("bus 2", ex.Message);
        Assert.Contains("ap + bp + cp", ex.Message);
        Assert.Contains("1.25", ex.Message);
    }

    [Fact]
    public void A_zip_reactive_triple_not_summing_to_one_fails_loud()
    {
        InvalidDataException ex = LoadFails(
            ", \"zip\": { \"ap\": 0.5, \"bp\": 0.25, \"cp\": 0.25, \"aq\": 0, \"bq\": 0, \"cq\": 0 }");
        Assert.Contains("bus 2", ex.Message);
        Assert.Contains("aq + bq + cq", ex.Message);
    }

    [Fact]
    public void A_misspelt_zip_key_fails_loud()
    {
        // The strict-key rule (R268) applies inside the zip block too: "pz" is not a coefficient name.
        InvalidDataException ex = LoadFails(
            ", \"zip\": { \"pz\": 0.5, \"bp\": 0.25, \"cp\": 0.25, \"aq\": 0.1, \"bq\": 0.2, \"cq\": 0.7 }");
        Assert.Contains("buses.json", ex.Message);
        Assert.Contains("pz", ex.Message);
    }

    [Fact]
    public void A_non_numeric_zip_coefficient_fails_loud()
    {
        // JSON has no NaN literal; a string where a number belongs is refused by the reader, naming the file.
        InvalidDataException ex = LoadFails(
            ", \"zip\": { \"ap\": \"NaN\", \"bp\": 0.25, \"cp\": 0.25, \"aq\": 0.1, \"bq\": 0.2, \"cq\": 0.7 }");
        Assert.Contains("buses.json", ex.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_zip_coefficient_is_refused_naming_the_bus(double bad)
    {
        // Behind the JSON reader the coefficient check itself refuses a non-finite value (a backstop for any
        // future reader setting that admits named floating-point literals).
        var dto = new BusDto
        {
            Id = 7, Name = "Seven",
            Zip = new BusZipDto { Ap = 0, Bp = bad, Cp = 1, Aq = 0, Bq = 0, Cq = 1 },
        };
        var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.ParseZip(dto));
        Assert.Contains("bus 7", ex.Message);
        Assert.Contains("'Seven'", ex.Message);
        Assert.Contains("'bp'", ex.Message);
        Assert.Contains("not a finite number", ex.Message);
    }

    [Fact]
    public void A_decimal_triple_with_binary_rounding_is_accepted()
    {
        // 0.1 + 0.2 + 0.7 is 1.0000000000000002 in binary floating point, not 1.0: within the stated tolerance.
        var dto = new BusDto { Id = 3, Zip = new BusZipDto { Ap = 0.1, Bp = 0.2, Cp = 0.7, Aq = 0.1, Bq = 0.2, Cq = 0.7 } };
        Assert.Equal(new ZipLoad(0.1, 0.2, 0.7, 0.1, 0.2, 0.7), JsonGridLoader.ParseZip(dto));
        Assert.Null(JsonGridLoader.ParseZip(new BusDto { Id = 4 }));
    }

    [Fact]
    public void The_solve_export_zip_block_reads_back_through_the_loader_unchanged()
    {
        // Load a case with a zip, solve it, export it: the export's bus zip block (SolveExport, since 1.95.030)
        // must carry the same six coefficients, and feeding that block back through the loader's zip parser must
        // give back the identical ZipLoad.
        string dir = WriteCase(ValidZip);
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            var trace = new SolveTrace();
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
            Assert.True(r.Converged);
            var meta = new SolveExportMeta("zip", "cpu", "deadbeef", "2026-10-02T00:00:00Z", 1.0, "test");
            string json = SolveExport.ToJson(m, r, trace, meta);

            JsonElement buses = JsonDocument.Parse(json).RootElement.GetProperty("model").GetProperty("buses");
            JsonElement bus2 = buses.EnumerateArray().Single(b => b.GetProperty("id").GetInt32() == 2);
            BusZipDto? back = JsonSerializer.Deserialize<BusZipDto>(bus2.GetProperty("zip").GetRawText(), CaseInsensitive);
            Assert.NotNull(back);
            ZipLoad? reloaded = JsonGridLoader.ParseZip(new BusDto { Id = 2, Zip = back });
            Assert.Equal(m.Buses.Single(b => b.Id == 2).Zip, reloaded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void No_bundled_json_case_carries_a_zip_load()
    {
        // The bit-identity claim for the bundled cases rests on this: none of them sets a zip block.
        foreach (string c in new[] { "uk", "gb-synthetic", "gb-full" })
        {
            GridModel m = JsonGridLoader.LoadDirectory(BundledCase(c), dispatch: false);
            Assert.All(m.Buses, b => Assert.Null(b.Zip));
        }
    }
}
