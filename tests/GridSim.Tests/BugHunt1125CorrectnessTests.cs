using System.Numerics;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Probabilistic;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 4 (correctness, rulings 8, 9, 10 and 12): one regression test per finding. Each test
/// states the case that was wrong and the number or refusal that is right now.
/// </summary>
public sealed class BugHunt1125CorrectnessTests
{
    private const double Kv = 132.0;

    // ---- ANA-01: the 87T star-point check compares bus IDs, not list positions -------------------------------------------

    private static GridModel ThreeWindingPlusTwoWinding() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 33), new Bus(3, BusType.Pq, Pd: 5, BaseKv: 11),
            new Bus(4, BusType.Pq, BaseKv: 132, Name: "T1 star point"), new Bus(5, BusType.Pq, Pd: 8, BaseKv: 33),
        },
        Generators = new[] { new Generator(1, 23) },
        Branches = new[]
        {
            new Branch(1, 4, 0.0, 0.05, IsTransformer: true, Name: "T1-H"), new Branch(4, 2, 0.0, 0.05, IsTransformer: true, Name: "T1-M"),
            new Branch(4, 3, 0.0, 0.05, IsTransformer: true, Name: "T1-L"), new Branch(1, 5, 0.0, 0.1, IsTransformer: true, Name: "T2"),
        },
    };

    private static PlacedDevice Td(int branch, int from, int to) =>
        new PlacedDevice("T87", DeviceType.Relay, "87", new DeviceLocation(branch, from, to, "from"), 1, 1, 0, 0, [], null, [], null, null)
        { TransformerDifferential = new TransformerDifferentialSetting("YNyn0", 30, 0.2, 0.3, 2.0, 1.5) };

    [Fact]
    public void Ana01_the_87T_star_check_uses_the_star_bus_id()
    {
        GridModel m = ThreeWindingPlusTwoWinding();
        Assert.Equal(new[] { 4 }, Iec60909.ThreeWindingStarPoints(m));
        // The two-winding T2 (1-5): the old check read model.Buses[4] (bus 5) as the star and refused it as a leg.
        TransformerDifferential.Check("Device 'T87'", Td(3, 1, 5), Td(3, 1, 5).TransformerDifferential!, m);
        // A real leg of the three-winding unit is refused, naming the star bus by its ID.
        var ex = Assert.Throws<DeviceSetException>(() => TransformerDifferential.Check("Device 'T87'", Td(0, 1, 4), Td(0, 1, 4).TransformerDifferential!, m));
        Assert.Contains("is a leg of a three-winding unit (star bus 4)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ana01_a_star_bus_whose_id_equals_the_bus_count_is_not_an_index_error()
    {
        // Star bus id 4 in a four-bus model (CIM numbers star buses after the nodes): Buses[4] was out of range.
        GridModel full = ThreeWindingPlusTwoWinding();
        GridModel m = full.CopyWith(buses: full.Buses.Take(4).ToList(), branches: full.Branches.Take(3).ToList());
        var ex = Assert.Throws<DeviceSetException>(() => TransformerDifferential.Check("Device 'T87'", Td(1, 4, 2), Td(1, 4, 2).TransformerDifferential!, m));
        Assert.Contains("star bus 4", ex.Message, StringComparison.Ordinal);
    }

    // ---- ANA-02: distance elements on a collapsed loop voltage ----------------------------------------------------------

    private static GridModel TwoSourceLine() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, Pd: 20, BaseKv: Kv), new Bus(3, BusType.Pv, BaseKv: Kv) },
        Generators = new[] { new Generator(1, 0, MBase: 100, SubtransientXdPu: 0.1), new Generator(3, 10, MBase: 100, SubtransientXdPu: 0.1) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "L12"), new Branch(2, 3, 0.01, 0.1, Name: "L23") },
    };

    private const string MhoAt2Toward3 = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "R23", "type": "relay", "function": "21", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "vt": { "primaryV": 132000, "secondaryV": 110 },
        "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 15, "angleDeg": 84, "timeS": 0 } ] } } ] }
    """;

    [Fact]
    public void Ana02_a_bolted_fault_behind_the_relay_is_reverse_by_the_memory_voltage_and_one_in_front_is_forward()
    {
        GridModel m = TwoSourceLine();
        BoundDeviceSet bound = DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(MhoAt2Toward3)), m, new CurveLibrary());
        // Bolted fault on the relay's own bus: behind it (the line feeds the bus). The loop voltage is rounding noise.
        PlacedRow behind = Assert.Single(PlacedElementEvaluator.Evaluate(bound, FaultPhasors.Build(m, FaultSpec.AtBus(2), FaultType.ThreePhase)));
        Assert.False(behind.Operates);
        Assert.Contains("direction reverse from the prefault (memory) voltage", behind.Reason, StringComparison.Ordinal);
        // A bolted fault just inside the line (1e-5 of it, loop voltage about 1e-5 pu, above the collapse threshold): measured
        // as before, forward, zone 1 at once.
        PlacedRow front = Assert.Single(PlacedElementEvaluator.Evaluate(bound, FaultPhasors.Build(m, FaultSpec.AlongBranch(1, 1e-5), FaultType.ThreePhase)));
        Assert.True(front.Operates, front.ToString());
        Assert.Equal(1, front.Zone);
        Assert.True(front.Reason is null || !front.Reason.Contains("collapsed", StringComparison.Ordinal), front.Reason);
        // A fault with a real impedance between it and the relay is measured as before (no collapse note).
        PlacedRow mid = Assert.Single(PlacedElementEvaluator.Evaluate(bound, FaultPhasors.Build(m, FaultSpec.AlongBranch(1, 0.3), FaultType.ThreePhase)));
        Assert.True(mid.Operates, mid.ToString());
        Assert.True(mid.Reason is null || !mid.Reason.Contains("collapsed", StringComparison.Ordinal));
        Assert.True(mid.MeasuredValue > 0.0);
    }

    [Fact]
    public void Ana02_the_generic_21_reads_a_collapsed_end_as_not_measured()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "L12") },
        };
        // A crafted faulted state: bus 1 at 1e-8 pu with a tiny flow at the line angle into the line, so V^2 / conj(S) is
        // 1e-9 pu at 84 degrees, inside zone 1 by the old reading; the far end carries no flow.
        double ang = Math.Atan2(0.1, 0.01);
        double sMw = 1e-7 * 100.0;
        var state = new PowerFlowResult
        {
            Converged = true, Iterations = 1, MaxMismatch = 0.0, BusIds = new[] { 1, 2 }, Vm = new[] { 1e-8, 1.0 }, VaDeg = new[] { 0.0, 0.0 },
            PInjMw = new[] { 0.0, 0.0 }, QInjMvar = new[] { 0.0, 0.0 }, TotalLossMw = 0, TotalLossMvar = 0,
            LineFlows = new[] { new LineFlow(1, 2, sMw * Math.Cos(ang), sMw * Math.Sin(ang), 0, 0, 0, 0, "L12") },
        };
        var ctx = new ProtectionContext(m, state, new ProtectionSettings(), null);
        RelayVerdict v = Assert.Single(new DistanceRelay().Evaluate(ctx));
        Assert.False(v.Operate);
        Assert.Contains("not measured: the relay voltage collapsed", v.Basis, StringComparison.Ordinal);
    }

    // ---- ANA-03: K_T takes c_max on the transformer's low-voltage side ---------------------------------------------------

    [Fact]
    public void Ana03_kt_of_an_11_to_0_4_kv_transformer_uses_c_max_1_05()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 0.5, BaseKv: 0.4) },
            Generators = new[] { new Generator(1, 0.5, MBase: 10) },
            Branches = new[] { new Branch(1, 2, 0.0, 5.0, IsTransformer: true, RateAMva: 1.0, Name: "TX") },   // 5 % on 1 MVA
        };
        Iec60909.CorrectionReport r = Iec60909.ApplyCorrectionFactors(m, 0.85);
        Iec60909.CorrectionApplied kt = Assert.Single(r.Applied, a => a.Factor == "K_T");
        double expected = 0.95 * 1.05 / (1.0 + 0.6 * 0.05);
        Assert.Equal(expected, kt.Value, 15);
        Assert.Equal(Iec60909.TransformerCorrectionKt(0.05, 1.05), kt.Value);
        // The old factor (c_max 1.10, the HV side's) was 4.8 % higher.
        Assert.Equal(1.10 / 1.05, Iec60909.TransformerCorrectionKt(0.05, 1.10) / kt.Value, 12);
        // The same unit the other way round (LV at the from end) gives the same factor.
        GridModel flipped = m.CopyWith(branches: new[] { new Branch(2, 1, 0.0, 5.0, IsTransformer: true, RateAMva: 1.0, Name: "TX") });
        Assert.Equal(kt.Value, Assert.Single(Iec60909.ApplyCorrectionFactors(flipped, 0.85).Applied, a => a.Factor == "K_T").Value);
    }

    // ---- ANA-05: a fault with no source in its island is refused ---------------------------------------------------------

    [Fact]
    public void Ana05_a_fault_fed_only_by_loads_is_refused_like_iec60909()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, Pd: 30, Qd: 10, BaseKv: Kv) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        var ex = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(m, pf, 2));
        Assert.Contains("needs at least one source (generator) to feed the fault", ex.Message, StringComparison.Ordinal);
        var ex2 = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.Unbalanced(m, pf, 2, FaultType.LineToLine));
        Assert.Contains("needs at least one source", ex2.Message, StringComparison.Ordinal);
        // With a generator the fault runs as before.
        GridModel fed = m.CopyWith(generators: new[] { new Generator(1, 30) });
        PowerFlowResult pf2 = new NewtonRaphsonPowerFlow().Solve(fed);
        Assert.True(FaultAnalysis.ThreePhase(fed, pf2, 2).FaultCurrentPhasorPu.Magnitude > 1.0);
    }

    // ---- ANA-06: the AC-OPF refuses devices it does not model ------------------------------------------------------------

    [Fact]
    public void Ana06_acopf_refuses_unmodelled_devices_by_kind_and_count()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, Pd: 30, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 30) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            InductionMachines = new[] { new InductionMachine(2, "M1", InductionPowerCode.Mechanical, 5, 10, 132, 0.01, 0.1, 3.0, 0.02, 0.1) },
        };
        var ex = Assert.Throws<InvalidDataException>(() => AcOpfInteriorPoint.Solve(m));
        Assert.Contains("1 induction machine(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not model them", ex.Message, StringComparison.Ordinal);
    }

    // ---- ANA-07: harmonic scaling by the sign of X and B ------------------------------------------------------------------

    private static Complex Cell(ComplexCsr y, int r, int c)
    {
        for (int k = y.RowPtr[r]; k < y.RowPtr[r + 1]; k++) if (y.ColIdx[k] == c) return new Complex(y.G[k], y.B[k]);
        return Complex.Zero;
    }

    [Fact]
    public void Ana07_a_series_capacitor_scales_by_one_over_h_and_an_inductor_by_h()
    {
        Assert.Equal(0.5, AdmittanceStamp.SeriesReactanceAtOrder(0.1, 5, false), 15);
        Assert.Equal(-0.02, AdmittanceStamp.SeriesReactanceAtOrder(-0.1, 5, false), 15);
        Assert.Equal(-0.5, AdmittanceStamp.SeriesReactanceAtOrder(-0.1, 5, true), 15);   // a star-equivalent leg stays inductive
        Assert.Equal(0.25, AdmittanceStamp.ShuntSusceptanceAtOrder(0.05, 5), 15);
        Assert.Equal(-0.01, AdmittanceStamp.ShuntSusceptanceAtOrder(-0.05, 5), 15);
        foreach (double x in new[] { 0.1, -0.1 })
            Assert.Equal(x, AdmittanceStamp.SeriesReactanceAtOrder(x, 1.0, false));   // exact at the fundamental
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0.001, -0.05, Name: "SC") },
        };
        Complex y12 = Cell(FrequencyDependentYBus.Build(m, 5.0), 0, 1);
        Complex expected = -Complex.One / new Complex(0.001, -0.05 / 5.0);
        Assert.True((y12 - expected).Magnitude < 1e-9 * expected.Magnitude, $"Y12(5) = {y12}, expected {expected}");
        // The fundamental is unchanged.
        Complex y1 = Cell(FrequencyDependentYBus.Build(m, 1.0), 0, 1);
        Assert.Equal(-Complex.One / new Complex(0.001, -0.05), y1);
    }

    // ---- ANA-20: PLF probabilities over converged samples ---------------------------------------------------------------

    [Fact]
    public void Ana20_failed_samples_make_the_result_incomplete_and_leave_the_probabilities()
    {
        // Slack behind X = 0.1 pu to a load: every sample above 500 MW fails (the nose). Rating 1 MVA: every converged
        // sample exceeds it, so the exceedance probability over the converged samples is exactly 1 (it used to be
        // Converged / Samples, counting each failed sample as "no overload").
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 400, Qd: 0, BaseKv: 132) },
            Generators = new[] { new Generator(1, Pg: 400) },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1, RateAMva: 1) },
        };
        var inputs = new[] { new UncertainInput("l2", UncertainTarget.BusLoad, 2, 0, new NormalDistribution(480, 40)) };
        var req = new PlfRequest(inputs, null, PlfEvaluator.Ac, ProbabilisticMethod.MonteCarlo, 200, null, 3);
        PlfResult r = ProbabilisticLoadFlow.Run(model, req);
        Assert.True(r.NonConverged > 0 && r.Converged > 0);
        Assert.Equal(ProbabilisticStatus.Incomplete, r.Status);
        Assert.Contains($"{r.NonConverged} of 200 samples did not converge", r.Reason, StringComparison.Ordinal);
        Assert.Equal(1.0, r.Branches[0].ExceedProbability);
        // Every sample converges: COMPLETE, no reason.
        PlfResult ok = ProbabilisticLoadFlow.Run(model, req with { Inputs = new[] { new UncertainInput("l2", UncertainTarget.BusLoad, 2, 0, new NormalDistribution(100, 5)) } });
        Assert.Equal(ProbabilisticStatus.Complete, ok.Status);
        Assert.Null(ok.Reason);
    }

    // ---- NUM-03: FDLF refuses an X = 0 branch by name ------------------------------------------------------------------

    [Theory]
    [InlineData(FdlfVariant.XB)]
    [InlineData(FdlfVariant.BX)]
    public void Num03_fast_decoupled_refuses_a_purely_resistive_branch_by_name(FdlfVariant v)
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 1, BaseKv: 11), new Bus(3, BusType.Pq, Pd: 1, BaseKv: 11) },
            Generators = new[] { new Generator(1, 2) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.05), new Branch(2, 3, 0.02, 0.0, Name: "R23") },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => new FastDecoupledPowerFlow(v).Solve(m));
        Assert.Contains("branch 2-3 ('R23') has X = 0", ex.Message, StringComparison.Ordinal);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    // ---- NUM-04: QSTS devices stay constant power on a ZIP bus ---------------------------------------------------------

    [Fact]
    public void Num04_device_injections_keep_their_power_on_a_zip_bus()
    {
        var zip = new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 1, Bq: 0, Cq: 0);   // constant impedance
        var bus = new Bus(2, BusType.Pq, Pd: 3.0, Qd: 1.0, Zip: zip);
        // 1 MW of PV, a 0.5 MW load addition, 0.2 MW of storage discharge.
        Bus b = QstsDeviceDemand.WithConstantPowerDevices(bus, addP: 0.5, pvP: 1.0, stP: 0.2, addQ: 0.1, pvQ: 0.0);
        foreach (double v in new[] { 0.95, 1.0, 1.05 })
        {
            Assert.Equal(3.0 * v * v + (0.5 - 1.0 - 0.2), b.Pd * b.Zip!.PMultiplier(v), 12);
            Assert.Equal(1.0 * v * v + 0.1, b.Qd * b.Zip!.QMultiplier(v), 12);
        }
        // A bus with no varying ZIP: the old sum, bit for bit.
        Bus plain = QstsDeviceDemand.WithConstantPowerDevices(new Bus(2, BusType.Pq, Pd: 3.0, Qd: 1.0), 0.5, 1.0, 0.2, 0.1, 0.0);
        Assert.Equal(3.0 + 0.5 - 1.0 - 0.2, plain.Pd);
        Assert.Null(plain.Zip);
        // Devices that cancel the load exactly while its ZIP part varies cannot be one ZIP: refused by name.
        var ex = Assert.Throws<InvalidOperationException>(() => QstsDeviceDemand.WithConstantPowerDevices(bus, 0.0, 3.0, 0.0, 0.0, 0.0));
        Assert.Contains("at bus 2 the device injections cancel the real load exactly", ex.Message, StringComparison.Ordinal);
    }

    // ---- NUM-05: an out-of-service OLTC transformer is not stepped -----------------------------------------------------

    [Fact]
    public void Num05_the_tap_loop_leaves_an_open_transformer_alone()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, Pd: 60, Qd: 30, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 60) },
            Branches = new[]
            {
                new Branch(1, 2, 0.02, 0.2, Name: "L12"),
                new Branch(1, 2, 0.0, 0.1, IsTransformer: true, InService: false, Name: "T12"),
            },
            Oltcs = new[] { new Oltc("T12", 2, TargetVpu: 1.0, DeadbandPu: 0.01) },
        };
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m);
        Assert.True(r.Converged);
        Assert.True(r.Vm[1] < 0.99, "the controlled bus is out of band");
        Assert.Equal(1.0, solver.RegulatedModel.Branches[1].TapRatio);
        Assert.True(solver.TapsSettled);
    }

    // ---- NUM-21: two voltage-controlling VSCs on one bus are refused ---------------------------------------------------

    [Fact]
    public void Num21_two_vsc_converters_holding_one_bus_are_refused()
    {
        VscDcLink Link(string name) => new(name, 1.0,
            new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.Voltage, 300.0, 1.0, 0, 0, 0, 0, 0, 1.0, 50, -50),
            new VscConverter(3, VscDcControl.Power, VscAcControl.PowerFactor, 20.0, 1.0, 0, 0, 0, 0, 0, 1.0, 50, -50));
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pq, Pd: 30, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 40) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(1, 3, 0.01, 0.1) },
            VscLinks = new[] { Link("V1"), Link("V2") },
        };
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(m));
        Assert.Contains("VSC link V2's first converter controls the voltage of bus 2, which VSC link V1's first converter also controls", ex.Message, StringComparison.Ordinal);
    }

    // ---- DYN-01 / DYN-02: the saturation fit -----------------------------------------------------------------------------

    [Fact]
    public void Dyn01_the_saturation_points_fit_the_same_curve_in_either_order()
    {
        // IEEE 421.5 DC1A sample order: E1 at the ceiling (3.1, 0.33), E2 at 75 % (2.3, 0.10).
        (double A, double B) desc = AcExciter.SaturationFit("ESDC1A", 3.1, 0.33, 2.3, 0.10);
        (double A, double B) asc = AcExciter.SaturationFit("ESDC1A", 2.3, 0.10, 3.1, 0.33);
        Assert.Equal(asc, desc);
        // The curve passes through both points: SE(E) E = B (E - A)^2.
        Assert.Equal(0.33 * 3.1, asc.B * (3.1 - asc.A) * (3.1 - asc.A), 12);
        Assert.Equal(0.10 * 2.3, asc.B * (2.3 - asc.A) * (2.3 - asc.A), 12);
        Assert.Throws<ArgumentException>(() => AcExciter.SaturationFit("X", 2.0, 0.1, 2.0, 0.2));   // the same E twice
    }

    // Re-pinned (ruling 15): the 2.1.161 test asserted the refusal of one SE of 0. Mark's ruling takes it as the curve that
    // starts at that point: A = E1, B = SE2 E2 / (E2 - E1)^2, which passes through (E2, SE2) by construction (proved below
    // from SE(E) E = B (E - A)^2) and is 0 at and below E1. Either order gives the same curve.
    [Fact]
    public void Dyn02_one_se_of_zero_is_the_curve_that_starts_at_that_point()
    {
        (double a, double b) = AcExciter.SaturationFit("EXDC2", 0.0, 0.0, 1.0, 1.0);
        Assert.Equal(0.0, a);
        Assert.Equal(1.0, b);
        (double a2, double b2) = AcExciter.SaturationFit("EXDC2", 3.1, 0.33, 2.3, 0.0);
        Assert.Equal(2.3, a2);
        Assert.Equal(0.33 * 3.1, b2 * (3.1 - a2) * (3.1 - a2), 12);
        Assert.Equal((a2, b2), AcExciter.SaturationFit("EXDC2", 2.3, 0.0, 3.1, 0.33));
        Assert.Throws<ArgumentException>(() => AcExciter.SaturationFit("EXDC2", 3.1, 0.0, 2.3, 0.33));   // the SE 0 point above the other
        Assert.Equal((0.0, 0.0), AcExciter.SaturationFit("EXDC2", 0.0, 0.0, 1.0, 0.0));   // both 0: no saturation, as before
    }

    // ---- DYN-03: motor RK4 sub-steps beyond the stability bound ----------------------------------------------------------

    [Fact]
    public void Dyn03_a_stalled_motor_at_a_10_ms_step_stays_bounded()
    {
        var p = new InductionMotorParameters(Ra: 0.01, X: 3.0, Xp: 0.2, TpoSeconds: 0.5, H: 0.5, InductionMotorTorque.PowerLaw(0.0));
        var v = new Complex(0.3, 0.0);
        var s = new InductionMotorState(p, 50.0, v, 0.0, 0.0);
        Assert.Equal(1, s.SubSteps(0.005));    // the default step is inside the bound: one RK4 step, as before
        Assert.Equal(2, s.SubSteps(0.010));    // |lambda| dt = 3.44 > 2.5
        for (int k = 0; k < 400; k++) s.Advance(v, 0.010);
        Assert.True(double.IsFinite(s.Emf.Magnitude) && s.Emf.Magnitude < 1.0, $"E' = {s.Emf}");
        // A step inside the bound is the plain RK4 step, bit for bit with a single sub-step.
        var a = new InductionMotorState(p, 50.0, v, 0.0, 0.0);
        var b = new InductionMotorState(p, 50.0, v, 0.0, 0.0);
        a.Advance(v, 0.005);
        b.Advance(v, 0.0025); b.Advance(v, 0.0025);
        Assert.NotEqual(a.Emf, b.Emf);   // the 5 ms step is not split
    }

    // ---- DYN-04: a converter started above its current limit is refused ------------------------------------------------

    [Fact]
    public void Dyn04_a_converter_whose_solved_current_is_above_imax_is_refused_at_start()
    {
        var par = new RmsConverterParameters();
        var over = new RmsConverterUnit(4, "PV4", 1.0, 1.0, 0.7, new Complex(0.98, 0.0), par);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RmsConverterState(over));
        Assert.Contains("converter PV4 at bus 4: the solved operating point needs a current of", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R252", ex.Message, StringComparison.Ordinal);
        _ = new RmsConverterState(over with { Q0 = 0.3 });   // inside the limit: starts as before
    }

    // ---- DYN-05 / DYN-06: the notes ------------------------------------------------------------------------------------

    [Fact]
    public void Dyn05_a_mapped_hygov_record_is_noted_as_approximated()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        string dyr = "     3 'GENROU' 1 " + Rc5Wp19BitIdentityTests.Genrou + " /\n"
            + "     3 'HYGOV' 1  0.05  0.3  5.0  0.0  0.5  0.0  1.0  0.0  1.0  1.0  0.0  0.0 /\n";
        PsseDyrResult r = PsseDyrReader.Read(dyr, raw, "case14");
        Assert.Contains(r.Notes, n => n.Contains("HYGOV for", StringComparison.Ordinal) && n.Contains("approximated", StringComparison.Ordinal)
            && n.Contains("(1 - g0 TW s) / (1 + 0.5 g0 TW s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Dyn06_an_avr_behind_a_folded_unit_transformer_is_noted_as_regulating_the_hv_bus()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pv, BaseKv: Kv) },
            Generators = new[] { new Generator(1, 0, H: 4), new Generator(2, 50, H: 4, UnitTransformerXPu: 0.1, Name: "G2") },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        };
        var ex = new List<RmsExciter?> { RmsExciter.FromSexs(1.0, 10.0, 100.0, 0.05), RmsExciter.FromSexs(1.0, 10.0, 100.0, 0.05) };
        string note = Assert.Single(RmsTransientStability.HvBusRegulationNotes(m, ex));
        Assert.Contains("generator 1 at bus 2 (G2): its SEXS exciter regulates the HV bus voltage", note, StringComparison.Ordinal);
        Assert.Empty(RmsTransientStability.HvBusRegulationNotes(m, null));
    }

    // ---- DYN-18: an EMT line shorter than the step is a pi branch of the same line (ruling 14) ------------------------

    // Re-pinned (ruling 14): this test asserted the 2.1.161 refusal. Mark's ruling replaces the refusal with the pi branch
    // of the same line (series L = Zc tau, charging C = tau / Zc, half at each end), never stretched. The proof that the
    // conversion is that pi branch and nothing else: the run equals, bit for bit, the same network written with an explicit
    // SeriesBranch(R 0, L) and two shunt capacitors in the same element order.
    [Fact]
    public void Dyn18_a_travelling_wave_line_shorter_than_the_step_is_its_pi_branch()
    {
        const double zc = 100.0, tau = 45e-6, dt = 100e-6;
        var withLine = new List<EmtElement>
        {
            new VoltageSource(1, 0, Rs: 100.0, Emf: t => Math.Sin(2 * Math.PI * 50 * t)),
            new BergeronLine(2, 0, 1, zc, tau),
            new ShuntElement(3, 1, G: 0.01),
        };
        var explicitPi = new List<EmtElement>
        {
            new VoltageSource(1, 0, Rs: 100.0, Emf: t => Math.Sin(2 * Math.PI * 50 * t)),
            new SeriesBranch(2, 0, 1, 0.0, zc * tau),
            new ShuntElement(4, 0, C: tau / zc / 2.0),
            new ShuntElement(5, 1, C: tau / zc / 2.0),
            new ShuntElement(3, 1, G: 0.01),
        };
        var a = new EmtEngine(new EmtNetwork(2, withLine), dt);
        Assert.Single(a.PiConvertedLines);
        Assert.Equal(2, a.PiConvertedLines[0].Id);
        var probes = new[] { new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1), new EmtProbe("i", EmtProbeKind.BranchCurrent, 2) };
        EmtResult ra = a.Simulate(new EmtCase(new EmtNetwork(2, withLine), dt, 0.02, probes, Array.Empty<EmtEvent>()));
        EmtResult rb = new EmtEngine(new EmtNetwork(2, explicitPi), dt).Simulate(new EmtCase(new EmtNetwork(2, explicitPi), dt, 0.02, probes, Array.Empty<EmtEvent>()));
        Assert.Equal(rb.Points.Count, ra.Points.Count);
        for (int k = 0; k < ra.Points.Count; k++)
            for (int j = 0; j < probes.Length; j++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(rb.Points[k].Probe[j]), BitConverter.DoubleToInt64Bits(ra.Points[k].Probe[j]));
        Assert.Empty(new EmtEngine(new EmtNetwork(2, withLine), 10e-6).PiConvertedLines);   // a step within the travel time: a Bergeron line as before
    }
}
