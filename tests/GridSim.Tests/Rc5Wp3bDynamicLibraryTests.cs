using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP3b: governors GGOV1 (<see cref="Ggov1"/>) and WSIEG1 (<see cref="Wsieg1"/>; IEEEG1 was already in the library) and
/// the dynamic loads: the third-order induction motor (<see cref="InductionMotorLoadData"/>, PSS/E CIM5 and CIMW single
/// cage), the ZIP load with exponential recovery (<see cref="ZipRecoveryLoadData"/>) and the CMLD subset
/// (<see cref="CompositeLoadData"/>), each attached to the RMS study through <see cref="RmsLoadedMachineData"/>.
/// Verification:
/// <list type="number">
/// <item>Flat start: each model alone, and the governors and loads in the RMS kernel on case14, stay at their initial point
/// within a bound derived from the rounding of their blocks and the kernel's stated tolerances.</item>
/// <item>Step responses against tests/tooling/dynamic-library-reference.py (SciPy 1.18.1, solve_ivp Radau, rtol 1e-12):
/// each model's continuous equations written there independently. The zero-order-hold models converge at first order in the
/// step (pinned below 1 % of the response at 0.1 ms and shown to fall with the step); the motor's RK4 at fourth order; the
/// recovery load is exact against its closed form.</item>
/// <item>GGOV1's droop: the settled power equals the analytical Pm0 - dw / R (Rselect 1) and the valve stroke
/// fsr0 - dw / R (Rselect -1), within a bound read from the last step's own increments.</item>
/// <item>The motor: the initial slip and torque equal the analytical equivalent circuit's; below the analytical breakdown
/// voltage it stalls, above it it settles at the circuit's slip.</item>
/// <item>Determinism: dense and sparse power flows, repeated and parallel runs, and the three SIMD modes of the built CLI
/// give the same bits; with no dynamic load attached a study is the historical one bit for bit.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp3bDynamicLibraryTests
{
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string PsseFixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private const double Eps = 1.1102230246251565e-16;   // 2^-53, the unit roundoff

    /// <summary>
    /// The rest bound of a governor alone, derived rather than fitted: each of its at most 8 states (each at most 1 pu at
    /// rest on the turbine base) gains at most two roundings of eps |x| per step (x &lt;- a x + (1 - a) u with u = x, or a sum
    /// of terms that cancel exactly at rest), and the output reads them through gains of at most 1 + Kpgov = 11 (GGOV1) or
    /// 1 (WSIEG1), so after n steps the output has moved at most 2 eps n 8 11.
    /// </summary>
    private static double GovernorRestBound(int steps) => 2.0 * Eps * steps * 8 * 11;

    // ------------------------------------------------------------------------------------------------- model data

    /// <summary>The GGOV1 of the reference script (case 7): Rselect 1, Flag 0, R 0.04, Tpelec 1, Kpgov 10, Kigov 2, Tact 0.5,
    /// Kturb 1.5, Wfnl 0.2, Tb 0.1; the acceleration limiter off (Ka 0), wide rate limits.</summary>
    private static Ggov1 Ggov1Ref(int rselect = 1, int flag = 0, double dm = 0.0) => new(rselect, flag, 0.04, 1.0, 0.05, -0.05, 10.0, 2.0, 0.0, 1.0,
        1.0, 0.15, 0.5, 1.5, 0.2, 0.1, 0.0, 0.0, 3.0, 2.0, 0.67, 1.0, dm, 1.0, -1.0, 0.0, 0.1, 0.0, 0.1, 0.0, 0.0, 4.0, 5.0, 99.0, -99.0);

    /// <summary>The GGOV1 of the case14 DYR (bus 1): Flag 1, the acceleration limiter on (Ka 10, Ta 0.1), Trate 300 MW.</summary>
    private static Ggov1 Ggov1Full(double mbase = 100.0) => new(1, 1, 0.04, 1.0, 0.05, -0.05, 10.0, 2.0, 0.0, 1.0, 1.0, 0.15, 0.5, 1.5, 0.2, 0.1,
        0.0, 0.0, 3.0, 2.0, 0.67, 1.0, 0.0, 0.1, -0.1, 0.0, 0.1, 10.0, 0.1, 300.0, 0.0, 4.0, 5.0, 99.0, -99.0, mbase);

    private static readonly double[] TableGv = { 0.3, 0.6, 0.8, 1.0, 0.0 }, TablePgv = { 0.4, 0.75, 0.9, 1.0, 0.0 };

    /// <summary>The WSIEG1 of the reference script (case 8).</summary>
    private static Wsieg1 Wsieg1Ref() => new(20.0, 0.2, 0.1, 0.3, 1.0, -1.0, 1.0, 0.0, 0.3, 0.3, 0.0, 7.0, 0.4, 0.0, 0.5, 0.3, 0.0, 0.05, 0.0, 0.0,
        0.0, 0.0, 0.0, TableGv, TablePgv, 0);

    // Overbye's CIM5 example (Ra 0.01, Xa 0.06, Xm 4, R1 0.03, X1 0.04 on the motor base), H 1, at 50 Hz.
    private const double Ra = 0.01, Xa = 0.06, Xm = 4.0, R1 = 0.03, X1 = 0.04, Hm = 1.0, Hz = 50.0;

    private static InductionMotorParameters Motor(double torqueExponent) =>
        InductionMotorParameters.FromCircuit(Ra, Xa, Xm, R1, X1, Hm, InductionMotorTorque.PowerLaw(torqueExponent), Hz);

    /// <summary>The equivalent circuit, written here independently of the model's form: Ra + jXa in series with jXm in
    /// parallel with R1/s + jX1.</summary>
    private static Complex Circuit(double s)
    {
        var rotor = new Complex(R1 / s, X1);
        return new Complex(Ra, Xa) + new Complex(0.0, Xm) * rotor / (new Complex(0.0, Xm) + rotor);
    }

    /// <summary>The air-gap torque of the circuit at slip s and |V| = v: |Ir|^2 R1 / s with Ir the rotor branch current.</summary>
    private static double CircuitTorque(double s, double v)
    {
        Complex i = v / Circuit(s);
        Complex vm = v - new Complex(Ra, Xa) * i;
        Complex ir = vm / new Complex(R1 / s, X1);
        return (ir.Real * ir.Real + ir.Imaginary * ir.Imaginary) * R1 / s;
    }

    /// <summary>Bisection on the circuit: the slip below <paramref name="sHigh"/> where <paramref name="f"/> equals
    /// <paramref name="target"/> (f rising in s).</summary>
    private static double Solve(Func<double, double> f, double target, double sHigh)
    {
        double lo = 1e-12, hi = sHigh;
        for (int k = 0; k < 400 && hi - lo > 0; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid == lo || mid == hi) break;
            if (f(mid) < target) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // ----------------------------------------------------------------------------------------------- 1. flat start

    [Fact]
    public void Each_governor_alone_holds_its_initial_power_to_rounding()
    {
        foreach ((string name, IRmsElectricalPowerGovernor? eg, IRmsGovernor g, double pm0) in new (string, IRmsElectricalPowerGovernor?, IRmsGovernor, double)[]
        {
            ("GGOV1 Rselect 1", Ggov1Ref(), Ggov1Ref(), 0.6), ("GGOV1 Rselect -1", null, Ggov1Ref(-1, 1, 0.01), 0.6),
            ("GGOV1 full", Ggov1Full(), Ggov1Full(), 2.324), ("WSIEG1", null, Wsieg1Ref(), 0.7),
        })
        {
            IRmsGovernor gov = eg ?? g;
            gov.Initialize(pm0);
            double worst = 0, pm = pm0;
            for (int n = 0; n < 4000; n++)
            {
                pm = eg is { NeedsElectricalPower: true } ? eg.Step(0.0, pm0, 0.005) : gov.Step(0.0, 0.005);
                worst = Math.Max(worst, Math.Abs(pm - pm0));
            }
            // The full GGOV1 is on a 300 MW turbine base: its machine-base output carries the factor 3.
            Assert.True(worst <= GovernorRestBound(4000) * 3.0, $"{name}: moved {worst:R} at rest");
        }
        // WSIEG1's start inverts the gain table exactly where the table is linear.
        var w = Wsieg1Ref();
        Assert.Equal(0.3 + (0.7 - 0.4) * 0.3 / 0.35, w.GateFor(0.7), 15);
        Assert.Equal(0.7, w.Flow(w.GateFor(0.7)), 15);
    }

    [Fact]
    public void Each_dynamic_load_alone_draws_its_initial_power_and_stays_there()
    {
        var v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        foreach (DynamicLoadData d in new DynamicLoadData[] { MotorLoad(3), ZipLoad(9), CmldLoad(4, 47.8, -3.9) })
        {
            DynamicLoadPlan plan = d.Plan(v0, 100.0, Hz);
            IRmsConverterModel m = plan.CreateModel();
            Complex inj = m.Commit(v0);
            Complex s = v0 * Complex.Conjugate(-m.TerminalCurrent(inj, v0));
            // At t = 0 the load draws its power: the current is a handful of complex operations on terms of at most |Y| |V|
            // (|Y| below 10 pu), each with a relative rounding of eps, so S is within 32 eps |Y| |V|^2 of P0 + jQ0.
            double tol = 32 * Eps * 10.0 * 1.01 * 1.01;
            Assert.True(Math.Abs(s.Real - d.PMw / 100.0) <= tol && Math.Abs(s.Imaginary - d.QMvar / 100.0) <= tol, $"{d.Model}: draws {s} at t = 0");
            double worst = 0;
            for (int n = 0; n < 2000; n++)
            {
                m.Step(n * 0.005, v0, new[] { v0 }, 0.005);
                Complex i = m.Commit(v0);
                Complex sn = v0 * Complex.Conjugate(-m.TerminalCurrent(i, v0));
                worst = Math.Max(worst, (sn - s).Magnitude);
            }
            // At rest the states' rates cancel to rounding (each about 8 eps of their terms, terms at most 10 pu); over n steps
            // of dt the states move at most n dt 8 eps 10 / Tmin (Tmin = 2H or T'o, at least 0.4 s here), and the power reads
            // them through |Y| |V| below 11.
            double bound = 2000 * 0.005 * 8 * Eps * 10.0 / 0.4 * 11.0 + tol;
            Assert.True(worst <= bound, $"{d.Model}: moved {worst:R} at rest (bound {bound:R})");
        }
    }

    private static InductionMotorLoadData MotorLoad(int bus) => new(bus, 94.2, 19.0, Motor(2.0), 117.75);

    private static ZipRecoveryLoadData ZipLoad(int bus) => new(bus, 29.5, 16.6, StaticLoadCharacteristic.Zip(0.3, 0.3), 0.5, 0.0, 2.0, 2.0,
        StaticLoadCharacteristic.Zip(0.5, 0.2), 0.4, 1.0, 4.0, 3.0);

    private static CompositeLoadData CmldLoad(int bus, double p, double q) => new(bus, p, q, 100.0, 0.08, 1.0, 0.02, 0.02, 0.04, 0.04, 0.5,
        0.4, 0.8, Motor(2.0), 0.95, new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0), new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0));

    // -------------------------------------------------------------------------------------- case14 with the new models

    private static string Case14Raw() => File.ReadAllText(PsseFixture("case14.raw"));

    private const string Genrou = " 7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0";

    // The GGOV1, WSIEG1 and CIM5BL records in their PSS/E orders. GGOV1: the PSS/E model library data sheet 7.8 (public copy:
    // pdfcoffee.com/models-pdf-free.html), NREL PowerSystems.jl psse_dynamic_mapping.yaml (GeneralGovModel) and the PowerWorld
    // GGOV1 page. WSIEG1: the data sheet 7.35 and the PowerWorld WSIEG1 page. CIM5BL: the data sheet 9.2 and the PowerWorld CIM5
    // page.
    private const string Ggov1Record = "     1 'GGOV1' 1  1  1  0.04  1.0  0.05 -0.05  10.0  2.0  0.0  1.0  1.0  0.15  0.5  1.5  0.2  0.1  0.0  0.0  3.0  2.0  0.67  1.0  0.0  0.1 -0.1  0.0  0.1  10.0  0.1  300.0  0.0  4.0  5.0  99.0 -99.0 /\n";
    private const string Wsieg1Record = "     2 'WSIEG1' 1  0  0  20.0  0.2  0.1  0.3  1.0 -1.0  1.0  0.0  0.3  0.3  0.0  7.0  0.4  0.0  0.5  0.3  0.0  0.05  0.0  0.0  0.0  0.0  0.0  0.3  0.4  0.6  0.75  0.8  0.9  1.0  1.0  0.0  0.0  0 /\n";
    private const string Cim5Record = "     3 'CIM5BL' 1  1  0.01  0.06  4.0  0.03  0.04  0.0  0.0  0.0  0.0  0.0  0.0  0.0  1.25  1.0  0.0  0.0  0.0  2.0  0.0 /\n";

    private const string Dyr =
          "     1 'GENROU' 1 " + Genrou + " /\n" + Ggov1Record
        + "     2 'GENROU' 1 " + Genrou + " /\n" + Wsieg1Record
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     6 'GENROU' 1 " + Genrou + " /\n"
        + "     8 'GENROU' 1 " + Genrou + " /\n"
        + Cim5Record;

    private static PsseParseResult Raw14() => PsseRawParser.ParseWithDiagnostics(Case14Raw(), "case14");

    private static PsseDyrResult ReadExtended(string dyr, PsseParseResult raw, bool allow = false)
        => PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "wp3b.dyr", ExtendedLibrary = true, AllowUnmodelled = allow });

    private static MachineDynamicsSet LoadJson(PsseDyrResult r, GridModel model)
    {
        string dir = TestTemp.Under("gridsim-wp3b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "wp3b.dyr.dynamics.json");
            File.WriteAllText(path, r.Json);
            return MachineDynamicsLoader.Load(path, SchemasDir, model).WithDynamicLoads(r.DynamicLoads);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The case14 fleet from <see cref="Dyr"/> (governors on buses 1 and 2, held field), the power flow solved to
    /// 1e-13 so a flat start has no real mismatch to answer.</summary>
    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data, MachineDynamicsSet Set) Case14(PowerFlowResult? pf = null)
    {
        PsseParseResult raw = Raw14();
        MachineDynamicsSet set = LoadJson(ReadExtended(Dyr, raw), raw.Model);
        GridModel m = raw.Model;
        pf ??= new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        return (m, pf, m.Generators.Select((_, k) => set.For(k)!.Data).ToList(), set);
    }

    private static List<SubtransientInit.MachineData> WithoutGovernors(List<SubtransientInit.MachineData> d) => d.Select(x => x with { Governor = null }).ToList();

    [Fact]
    public void The_governed_case14_fleet_starts_at_rest_within_the_governor_gain_bound()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data, _) = Case14();
        Assert.Equal("GGOV1", data[0].Governor!.Model);
        Assert.Equal("WSIEG1", data[1].Governor!.Model);
        const double T = 5.0;
        RmsTransientStability.FleetStudy held = RmsTransientStability.StudyWithFleet(m, pf, WithoutGovernors(data), 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: T);
        MultiMachineSubtransient.Result gov = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: T);
        double omega = 0;
        foreach (MultiMachineSubtransient.Result r in new[] { held.Result, gov })
            foreach (MultiMachineSubtransient.MachineTrace t in r.Machines) omega = Math.Max(omega, t.SpeedPu.Max(Math.Abs));
        // A governor moves its power at most by its rest drift plus its gain times the largest speed it sees: GGOV1 through
        // Kturb (Kpgov + Kigov T) on the speed and, with Rselect 1, R times that again on each pu of electrical power, which
        // moves by at most Ks T per pu of speed (Ks = |E''| |V| / X'' below 1.5 / 0.18); its 300 MW base gives 3 on the
        // machine base. WSIEG1 through K times the steepest table slope (0.35 / 0.3). A power change dP moves a machine's
        // speed by at most dP T / (2H) (H = 3 s on the 100 MVA base).
        double ks = 1.5 / 0.18;
        double gGgov1 = 3.0 * (1.5 * (10.0 + 2.0 * T) * (1.0 + 0.04 * ks * T) * omega + GovernorRestBound(1000));
        double gWsieg1 = 20.0 * 0.35 / 0.3 * omega + GovernorRestBound(1000);
        double bound = (gGgov1 + gWsieg1) * T / (2.0 * 3.0);
        Assert.True(omega <= 1e-11, $"the runs are not at rest: {omega:R}");
        for (int i = 0; i < gov.Machines.Count; i++)
            for (int k = 0; k < gov.Machines[i].SpeedPu.Count; k++)
                Assert.True(Math.Abs(gov.Machines[i].SpeedPu[k] - held.Result.Machines[i].SpeedPu[k]) <= bound,
                    $"machine {i} speed {gov.Machines[i].SpeedPu[k]:R} against the held {held.Result.Machines[i].SpeedPu[k]:R} (bound {bound:R})");
        // And a fault moves the governed fleet differently from the held one, which rides through.
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(m, pf, data, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 3.0);
        MultiMachineSubtransient.Result heldFault = RmsTransientStability.Study(m, pf, WithoutGovernors(data), 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 3.0);
        Assert.True(fault.StayedInSynchronism);
        Assert.True(Math.Abs(fault.Machines[0].SpeedPu[^1] - heldFault.Machines[0].SpeedPu[^1]) > 1e-6);
    }

    [Fact]
    public void Dynamic_loads_in_the_rms_study_start_at_rest_within_the_kernel_tolerance_bound()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data, _) = Case14();
        List<SubtransientInit.MachineData> plain = WithoutGovernors(data);
        DynamicLoadData[] loads = { MotorLoad(3), ZipLoad(9), CmldLoad(4, 47.8, -3.9) };
        var loaded = new RmsLoadedMachineData(plain, loads);
        const double T = 5.0;
        RmsTransientStability.FleetStudy held = RmsTransientStability.StudyWithFleet(m, pf, plain, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: T);
        MultiMachineSubtransient.Result run = RmsTransientStability.Study(m, pf, loaded, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: T);
        Assert.True(run.ConverterIterationsConverged);
        Assert.Equal(3, run.Converters!.Count);
        // The loads change the network only at rounding, but the converter iteration stops once the voltages move less than
        // its tolerance (1e-10 pu): a load current is then off by at most 2 |Y| 1e-10 (|Y| below 10 pu for these loads, the
        // drawn current's slope at most twice the Norton admittance), which moves a machine's electrical power by at most
        // |E''| (below 1.5) times that per load, and its speed by that times T / (2 H) (H = 3 s).
        double bound = 3 * 1.5 * 2 * 10.0 * MultiMachineSubtransient.ConverterVoltageTolerancePu * T / (2 * 3.0);
        for (int i = 0; i < run.Machines.Count; i++)
            for (int k = 0; k < run.Machines[i].SpeedPu.Count; k++)
                Assert.True(Math.Abs(run.Machines[i].SpeedPu[k] - held.Result.Machines[i].SpeedPu[k]) <= bound,
                    $"machine {i} speed {run.Machines[i].SpeedPu[k]:R} against the static-load run {held.Result.Machines[i].SpeedPu[k]:R}");
        // Each load draws its power throughout (the traces are injections: the consumption with the sign reversed).
        for (int u = 0; u < 3; u++)
        {
            RmsConverterTrace t = run.Converters[u];
            Assert.Equal(loads[u].BusId, t.Bus);
            Assert.All(t.ActivePowerPu, p => Assert.True(Math.Abs(-p - loads[u].PMw / 100.0) <= 2 * 10.0 * 1e-10 + 1e-13, $"{t.Name}: P {p:R}"));
        }
        // A fault answers: the motor slows and the recovery load recovers.
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(m, pf, loaded, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(fault.StayedInSynchronism);
        Assert.True(fault.ConverterIterationsConverged);
        Assert.True(fault.Converters![0].ActivePowerPu.Max(p => Math.Abs(-p - 0.942)) > 1e-2);
        // The event study takes the loads too (a load step at another bus).
        MultiMachineSubtransient.Result step = RmsTransientStability.StudyEvents(m, pf, loaded, new RmsEvent[] { new RmsLoadStep(0.1, 5, 5.0, 1.0) },
            stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.Equal(3, step.Converters!.Count);
        Assert.True(step.Converters[0].TerminalVoltagePu[^1] < step.Converters[0].TerminalVoltagePu[0]);
    }

    [Fact]
    public void With_no_dynamic_load_attached_a_study_is_the_historical_one_bit_for_bit()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data, _) = Case14();
        List<SubtransientInit.MachineData> plain = WithoutGovernors(data);
        string want = Digest(RmsTransientStability.Study(m, pf, plain, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.0));
        Assert.Equal(want, Digest(RmsTransientStability.Study(m, pf, new RmsLoadedMachineData(plain, Array.Empty<DynamicLoadData>()), 4, 0.1, 0.05,
            stepSeconds: 0.005, durationSeconds: 1.0)));
        // A governor that does not read the electrical power takes the historical hook: TGOV1 data (on the two units that
        // carry active power; the condensers at 0 MW are below its valve floor) gives the same bits through both lists.
        var tg = plain.Select((x, k) => k < 2 ? x with { Governor = new Tgov1Data(0.05, 0.5, 0.0, 0.5) } : x).ToList();
        Assert.Equal(Digest(RmsTransientStability.Study(m, pf, tg, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.0)),
            Digest(RmsTransientStability.Study(m, pf, new RmsLoadedMachineData(tg, Array.Empty<DynamicLoadData>()), 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.0)));
    }

    // ------------------------------------------------------------------------------------- 2. step responses (SciPy)

    private static readonly double[] Times = { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };
    private static readonly double[] GovTimes = { 0.5, 1.0, 2.0, 5.0, 10.0, 20.0 };

    // Pinned from tests/tooling/dynamic-library-reference.py (SciPy 1.18.1), cases 7 to 11, in the order of their times.
    private static readonly double[] Ggov1Ref_ = { 0.6167141395874636, 0.6252156986791695, 0.6271954229714082, 0.6302048945177279, 0.6368482621350097, 0.6441732626172584 };
    private static readonly double[] Wsieg1Ref_ = { 0.7111318138373607, 0.7215184106648603, 0.7313050483909213, 0.7475766693766458, 0.7630333181479811, 0.7743045978951455 };
    private static readonly double[] MotorSpeedRef = { 0.9724535761226201, 0.9707499623083533, 0.9695415129821089, 0.9692050436382051, 0.969201408585958, 0.9692014066947084, 0.9692014066947078 };
    private static readonly double[] MotorPowerRef = { 0.704662195768268, 0.7466672654146894, 0.782078602396924, 0.7919304994132036, 0.7920368230003787, 0.7920368783179338, 0.7920368783179526 };
    private static readonly double[] ZipRef = { 0.8638455583573093, 0.8661332046724313, 0.8705404452865841, 0.8825139256082175, 0.8988795873272968, 0.9215514530887081, 0.9487019251307298 };
    private static readonly double[] CmldSpeedRef = { 0.9695860232997086, 0.9676467556138809, 0.9659653205275331, 0.9653524047595714, 0.9653396486807481, 0.9653396290322205, 0.9653396290321752 };

    private static double WorstError(Func<double, int, double[]> run, double[] times, double[] reference, double dt, double offset)
    {
        int steps = (int)Math.Round(times[^1] / dt);
        double[] y = run(dt, steps);
        double worst = 0;
        for (int i = 0; i < times.Length; i++)
        {
            int k = (int)Math.Round(times[i] / dt) - 1;
            worst = Math.Max(worst, Math.Abs(y[k] - offset - (reference[i] - offset)));
        }
        return worst;
    }

    /// <summary>First order (the zero-order-hold models): below 1 % of the largest change at 0.1 ms, and at most 0.6 of that
    /// at half the step (first order halves it; 0.6 leaves room for the second-order term). The 1 % is derived from the
    /// zero-order-hold step: a lag of time constant tau stepped with dt has a local error of (dt / tau)^2 / 2 of its change,
    /// which the lag damps at dt / tau per step, so the accumulated error is at most dt / (2 tau) of that block's change. The
    /// fastest time constant in these cases is 0.05 s (WSIEG1 T7), so at dt = 0.1 ms the bound is 1e-4 / 0.1 = 0.1 % of the
    /// block's change, and the 1 % tolerance allows a tenfold amplification between that block and the output. The motor
    /// cases have the rotor time constant (X1 + Xm) / (w0 R1), about 0.4 s, and the swing time constant 2 H, 2 s.</summary>
    private static void AgreesFirstOrder(string what, Func<double, int, double[]> run, double[] times, double[] reference, double offset)
    {
        double change = reference.Max(r => Math.Abs(r - offset));
        double e1 = WorstError(run, times, reference, 1e-4, offset), e2 = WorstError(run, times, reference, 5e-5, offset);
        Assert.True(e1 <= 1e-2 * change, $"{what}: worst error {e1:R} at 0.1 ms against a change of {change:R}");
        Assert.True(e2 <= 0.6 * e1, $"{what}: the error must fall with the step: {e1:R} at 0.1 ms, {e2:R} at 0.05 ms");
    }

    [Fact]
    public void Ggov1_closed_on_its_power_follows_the_scipy_integration()
    {
        AgreesFirstOrder("GGOV1", (dt, steps) =>
        {
            Ggov1 g = Ggov1Ref();
            g.Initialize(0.6);
            var y = new double[steps];
            double pm = 0.6;
            for (int k = 0; k < steps; k++) y[k] = pm = g.Step(-0.002, pm, dt);
            return y;
        }, GovTimes, Ggov1Ref_, 0.6);
    }

    [Fact]
    public void Wsieg1_through_its_gain_table_follows_the_scipy_integration()
    {
        AgreesFirstOrder("WSIEG1", (dt, steps) =>
        {
            Wsieg1 g = Wsieg1Ref();
            g.Initialize(0.7);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = g.Step(-0.004, dt) + g.LowPressureShaftPowerPu;
            return y;
        }, GovTimes, Wsieg1Ref_, 0.7);
        // The table is crossed: the gate starts below GV2 = 0.6 and ends above it.
        Wsieg1 w = Wsieg1Ref();
        w.Initialize(0.7);
        for (int k = 0; k < 4000; k++) w.Step(-0.004, 0.005);
        Assert.True(w.Gate > 0.6);
    }

    private static (InductionMotorState State, Complex E0, double W0) MotorAt(double torqueExponent, double power, Complex v0)
    {
        InductionMotorParameters p = Motor(torqueExponent);
        (Complex e0, double w0, double t0, _) = InductionMotorState.Equilibrium(p, power, v0, Hz, "test motor");
        return (new InductionMotorState(p, Hz, e0, w0, t0), e0, w0);
    }

    [Fact]
    public void The_motor_follows_the_scipy_integration_at_fourth_order()
    {
        // V held exactly, so RK4's global error is O(dt^4): at 5 ms below 1e-4 of the change, and halving the step divides it
        // by about 16 (at least 8 asserted, leaving room for the next-order term).
        foreach ((string what, double[] reference, bool power) in new[] { ("speed", MotorSpeedRef, false), ("power", MotorPowerRef, true) })
        {
            double offset = power ? 0.8 : 1.0 - Solve(s => (1.0 / Circuit(s)).Real, 0.8, 0.05);
            double Err(double dt)
            {
                (InductionMotorState m, _, _) = MotorAt(2.0, 0.8, Complex.One);
                double worst = 0;
                int steps = (int)Math.Round(5.0 / dt);
                var v = new Complex(0.9, 0.0);
                for (int k = 1, i = 0; k <= steps; k++)
                {
                    m.Advance(v, dt);
                    if (i < Times.Length && k == (int)Math.Round(Times[i] / dt))
                    {
                        double y = power ? (v * Complex.Conjugate(m.Current(v))).Real : m.Speed;
                        worst = Math.Max(worst, Math.Abs(y - reference[i]));
                        i++;
                    }
                }
                return worst;
            }
            double change = reference.Max(r => Math.Abs(r - offset));
            double e1 = Err(0.005), e2 = Err(0.0025);
            Assert.True(e1 <= 1e-4 * change, $"motor {what}: error {e1:R} at 5 ms against a change of {change:R}");
            Assert.True(e2 <= e1 / 8.0, $"motor {what}: the error must fall at fourth order: {e1:R} at 5 ms, {e2:R} at 2.5 ms");
        }
    }

    [Fact]
    public void The_recovery_load_is_its_closed_form_and_the_scipy_integration()
    {
        // P side of ZipLoad's characteristic at P0 = 1 pu: after a step to u = 0.9 the recovering half jumps by u^2 and xp
        // follows the exact lag: xp(t) = 0.5 (u^0 - u^2)(1 - e^(-t/2)); the static half is 0.3 u^2 + 0.3 u + 0.4.
        var d = new ZipRecoveryLoadData(1, 100.0, 0.0, StaticLoadCharacteristic.Zip(0.3, 0.3), 0.5, 0.0, 2.0, 2.0,
            StaticLoadCharacteristic.ConstantPower, 0.0, 0.0, 0.0, 0.0);
        IRmsConverterModel m = d.Plan(Complex.One, 100.0, Hz).CreateModel();
        var v = new Complex(0.9, 0.0);
        const double dt = 0.005;
        int steps = (int)Math.Round(5.0 / dt), i = 0;
        for (int k = 1; k <= steps; k++)
        {
            m.Step((k - 1) * dt, v, new[] { v }, dt);
            if (i < Times.Length && k == (int)Math.Round(Times[i] / dt))
            {
                Complex inj = m.Commit(v);
                double p = (v * Complex.Conjugate(-m.TerminalCurrent(inj, v))).Real;
                double t = k * dt, u = 0.9;
                double closed = 0.5 * (0.3 * u * u + 0.3 * u + 0.4) + 0.5 * (1.0 - u * u) * (1.0 - Math.Exp(-t / 2.0)) + 0.5 * u * u;
                // The exact ZOH reproduces the closed form at the step boundaries: k steps of x <- a x + (1 - a) b each add at
                // most two roundings of eps |b| (|b| below 0.1), and the current and power add a few roundings of |P| (below 1).
                Assert.True(Math.Abs(p - closed) <= 2 * Eps * k * 0.1 + 16 * Eps, $"t = {t}: {p:R} against the closed form {closed:R}");
                // SciPy's Radau at rtol 1e-12 is within 1e-12 of the closed form, so of GridSim too.
                Assert.True(Math.Abs(p - ZipRef[i]) <= 1e-11, $"t = {t}: {p:R} against SciPy {ZipRef[i]:R}");
                i++;
            }
        }
        Assert.Equal(Times.Length, i);
    }

    [Fact]
    public void The_cmld_subset_follows_the_scipy_integration()
    {
        // The reference script's case 11: bus at 1 pu draws 0.8 + j0.3 pu; the voltage steps to 0.9. GridSim holds the far-end
        // voltage across each step (solved at the start), so it converges at first order.
        CompositeLoadData d = new(1, 80.0, 30.0, 100.0, 0.08, 1.0, 0.02, 0.02, 0.04, 0.04, 0.5, 0.4, 0.8, Motor(2.0), 0.95,
            new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0), new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0));
        DynamicLoadPlan plan = d.Plan(Complex.One, 100.0, Hz);
        var probe = (CompositeLoadData.State)plan.CreateModel();
        Complex inj0 = probe.Commit(Complex.One);
        Complex s0 = Complex.Conjugate(-probe.TerminalCurrent(inj0, Complex.One));
        Assert.True(Math.Abs(s0.Real - 0.8) <= 1e-13 && Math.Abs(s0.Imaginary - 0.3) <= 1e-13, $"draws {s0} at t = 0");
        AgreesFirstOrder("CMLD motor A speed", (dt, steps) =>
        {
            var m = (CompositeLoadData.State)plan.CreateModel();
            var v = new Complex(0.9, 0.0);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) { m.Commit(v); m.Step(k * dt, v, new[] { v }, dt); y[k] = m.Motor.Speed; }
            return y;
        }, Times, CmldSpeedRef, probe.Motor.Speed);
    }

    // -------------------------------------------------------------------------------------------------- 3. GGOV1 droop

    /// <summary>Run the governor closed on its own power (Pelec = Pmech, the machine at steady state) at a held speed
    /// deviation until it settles; returns the power, the last step's increments of the PID integral and of the power, and the
    /// governor.</summary>
    private static (double Pm, double DPm) Settle(IRmsGovernor g, double dw, double pm0, double dt, int steps)
    {
        double pm = pm0, prev = pm0;
        var eg = g as IRmsElectricalPowerGovernor;
        for (int k = 0; k < steps; k++)
        {
            prev = pm;
            pm = eg is { NeedsElectricalPower: true } ? eg.Step(dw, pm, dt) : g.Step(dw, dt);
        }
        return (pm, Math.Abs(pm - prev));
    }

    /// <summary>Half an ulp of <paramref name="x"/>: an update smaller than this leaves x unchanged.</summary>
    private static double HalfUlp(double x) => 0.5 * (Math.BitIncrement(Math.Abs(x)) - Math.Abs(x));

    /// <summary>How far a settled integral x &lt;- x + k u dt can leave its input u from 0: it stops moving once |k u dt| is
    /// below half an ulp of x.</summary>
    private static double IntegralStall(double x, double k, double dt) => HalfUlp(x) / (k * dt);

    /// <summary>How far a settled lag x &lt;- a x + (1 - a) u (a = e^(-dt/T)) can leave its output from its input: it stops
    /// once (1 - a) |u - x| is below half an ulp of x.</summary>
    private static double LagStall(double x, double t, double dt) => HalfUlp(x) / (1.0 - Math.Exp(-dt / t));

    [Fact]
    public void Ggov1_droop_settles_at_the_analytical_power_and_valve_stroke()
    {
        // Each run is stepped until nothing moves (the last step changes the power by exactly 0). At such a fixed point every
        // integral and lag has stopped, which in floating point means only that its update fell below half an ulp: the PID
        // integral leaves |err| at most HalfUlp(xI) / (Kigov dt), and a lag leaves its output within HalfUlp / (1 - a) of
        // its input. The analytical steady state follows from err = 0 and each lag's output equal to its input, so the
        // settled state is within those stalls (divided by R where err is turned into power), plus a few roundings of each
        // term of err (8 eps / R).
        const double dt = 0.01, dw = -0.002, r = 0.04;
        Ggov1 g = Ggov1Ref();
        g.Initialize(0.6);
        (double pm, double dpm) = Settle(g, dw, 0.6, dt, 60000);
        Assert.Equal(0.0, dpm);
        // Rselect 1: err = Pref - R Pef - dw = 0 with Pef = Pelec = Pmech, so Pmech = Pm0 - dw / R.
        double want = 0.6 - dw / r;
        double bound = IntegralStall(g.GovernorOutput, 2.0, dt) / r + LagStall(pm, 1.0, dt) + 8 * Eps / r;
        Assert.True(Math.Abs(pm - want) <= bound, $"Rselect 1: {pm:R} against Pm0 - dw / R = {want:R} (bound {bound:R})");

        // Rselect -1 (fuel valve stroke feedback), Flag 1, Dm 0.01: R valve = Pref - dw with Pref = R fsr0, so the stroke settles
        // at fsr0 - dw / R and the power at Kturb (valve (1 + dw) - Wfnl) - Dm (1 + dw) (the turbine lag Tb 0.1 s settled).
        Ggov1 v = Ggov1Ref(-1, 1, 0.01);
        v.Initialize(0.6);
        double fsr0 = v.EquilibriumStroke(0.6);
        (double pv, double dpv) = Settle(v, dw, 0.6, dt, 60000);
        Assert.Equal(0.0, dpv);
        double stroke = fsr0 - dw / r;
        double strokeBound = IntegralStall(v.GovernorOutput, 2.0, dt) / r + 8 * Eps / r;
        Assert.True(Math.Abs(v.ValveStroke - stroke) <= strokeBound, $"Rselect -1: stroke {v.ValveStroke:R} against {stroke:R} (bound {strokeBound:R})");
        double wantV = 1.5 * (stroke * (1.0 + dw) - 0.2) - 0.01 * (1.0 + dw);
        double powerBound = 1.5 * strokeBound + LagStall(pv, 0.1, dt) + 8 * Eps;
        Assert.True(Math.Abs(pv - wantV) <= powerBound, $"Rselect -1: {pv:R} against {wantV:R} (bound {powerBound:R})");

        // The load limiter: a large speed drop asks for more fuel than Ldref allows. Settled, the limiter's integral leaves
        // e = Ldref / Kturb + Wfnl - TEXM within IntegralStall(xL, Kiload); TEXM is the fuel flow through the Tsa / Tsb lead-lag
        // (its lag, Tsb 5 s, settled) and the Tfload 3 s lag; the fuel flow is the valve (Flag 0), the valve the output fsr
        // through Tact 0.5 s. So fsr = Ldref / Kturb + Wfnl and the power Kturb (fsr - Wfnl) = Ldref, within those stalls.
        Ggov1 sat = Ggov1Ref();
        sat.Initialize(0.6);
        (double ps, double dps) = Settle(sat, -0.05, 0.6, dt, 60000);
        Assert.Equal(0.0, dps);
        double fsrWant = 1.0 / 1.5 + 0.2, f = sat.GovernorOutput;
        double fsrBound = IntegralStall(f, 0.67, dt) + LagStall(f, 5.0, dt) + LagStall(f, 3.0, dt) + LagStall(f, 0.5, dt) + 8 * Eps;
        Assert.True(Math.Abs(f - fsrWant) <= fsrBound, $"the load limit holds fsr at {f:R} against {fsrWant:R} (bound {fsrBound:R})");
        Assert.True(Math.Abs(ps - 1.0) <= 1.5 * fsrBound + LagStall(ps, 0.1, dt) + 8 * Eps, $"the load limit holds the power at Ldref: {ps:R}");
    }

    // ----------------------------------------------------------------------------------------- 4. motor, analytically

    [Fact]
    public void The_motor_starts_at_the_circuit_slip_and_torque()
    {
        InductionMotorParameters p = Motor(2.0);
        // The model's impedance is the circuit's at every slip (the third-order form is exact in steady state).
        foreach (double s in new[] { 0.001, 0.01, 0.05, 0.2, 1.0 })
            Assert.True((p.Impedance(s, Hz) - Circuit(s)).Magnitude <= 64 * Eps * Circuit(s).Magnitude, $"impedance at slip {s}");
        // The slip at which the circuit draws 0.8 pu at 1 pu, by bisection on the circuit here.
        double sc = Solve(s => (1.0 / Circuit(s)).Real, 0.8, 0.05);
        (InductionMotorState m, Complex e0, double w0) = MotorAt(2.0, 0.8, Complex.One);
        // Both bisections stop at adjacent doubles of their own power function, whose values agree to about 64 eps; the
        // power's slope in slip is about 1/(R1/s^2) pu per unit slip relative, so the slips agree to 64 eps / (s dP/ds / P),
        // below 1e-12 relative at this slip.
        Assert.True(Math.Abs((1.0 - w0) - sc) <= 1e-12 * sc, $"slip {1.0 - w0:R} against the circuit's {sc:R}");
        Complex i0 = m.Current(Complex.One);
        double te = e0.Real * i0.Real + e0.Imaginary * i0.Imaginary;
        Assert.True(Math.Abs(te - CircuitTorque(sc, 1.0)) <= 1e-11, $"torque {te:R} against the circuit's {CircuitTorque(sc, 1.0):R}");
        Assert.True(Math.Abs(te - (0.8 - Ra * (i0.Real * i0.Real + i0.Imaginary * i0.Imaginary))) <= 1e-14, "torque is the input power less the stator loss");
    }

    [Fact]
    public void The_motor_stalls_below_the_analytical_breakdown_voltage_and_settles_above_it()
    {
        // Constant load torque (exponent 0). The circuit's largest torque at |V| = 1, from its Thevenin form: Vth = jXm/(Ra +
        // j(Xa + Xm)), Zth = (Ra + jXa) || jXm, Tmax = |Vth|^2 / (2 (Rth + sqrt(Rth^2 + (Xth + X1)^2))). Torque scales with
        // V^2 at every slip, so the motor carries T0 only above Vb = sqrt(T0 / Tmax).
        Complex zs = new(Ra, Xa), jxm = new(0.0, Xm);
        Complex vth = jxm / (zs + jxm), zth = zs * jxm / (zs + jxm);
        double tmax = vth.Magnitude * vth.Magnitude / (2.0 * (zth.Real + Math.Sqrt(zth.Real * zth.Real + (zth.Imaginary + X1) * (zth.Imaginary + X1))));
        (InductionMotorState probe, Complex e0, _) = MotorAt(0.0, 0.8, Complex.One);
        Complex i0 = probe.Current(Complex.One);
        double t0 = e0.Real * i0.Real + e0.Imaginary * i0.Imaginary;
        double vb = Math.Sqrt(t0 / tmax);
        // The model's own breakdown search finds the same largest torque.
        InductionMotorParameters p = Motor(0.0);
        Assert.True(Math.Abs(p.ElectricalTorque(p.BreakdownSlip(Hz), 1.0, Hz) - tmax) <= 1e-12, "the breakdown torque");

        const double dt = 0.005;
        (InductionMotorState low, _, _) = MotorAt(0.0, 0.8, Complex.One);
        var vLow = new Complex(0.97 * vb, 0.0);
        int k = 0;
        for (; k < 12000 && low.Speed > 0.0; k++) low.Advance(vLow, dt);
        Assert.True(low.Speed == 0.0, $"at 0.97 Vb = {0.97 * vb:R} the motor must stall; speed {low.Speed:R} after {k * dt} s");

        (InductionMotorState high, _, _) = MotorAt(0.0, 0.8, Complex.One);
        var vHigh = new Complex(1.03 * vb, 0.0);
        double prev = high.Speed, step = 1.0;
        for (k = 0; k < 40000 && step > 1e-15; k++) { high.Advance(vHigh, dt); step = Math.Abs(high.Speed - prev); prev = high.Speed; }
        double sHigh = 1.0 - high.Speed;
        double sWant = Solve(s => CircuitTorque(s, 1.03 * vb), t0, BreakdownOfCircuit());
        // Settled: the speed stops once its RK4 update falls below half an ulp, so the torque error Te - TL left is at most
        // (step + HalfUlp(w)) 2H / dt; with dTe/ds at the operating point (from the circuit) the slip is within that over
        // dTe/ds of its equilibrium, plus the rounding of the two torque evaluations (the model's and the circuit's, 64 eps
        // T0 each) over dTe/ds. Near the breakdown slip dTe/ds is small, so the bound is wide there by construction.
        double slope = (CircuitTorque(sWant * (1 + 1e-6), 1.03 * vb) - CircuitTorque(sWant * (1 - 1e-6), 1.03 * vb)) / (2e-6 * sWant);
        double bound = ((step + HalfUlp(high.Speed)) * 2.0 * Hm / dt + 128 * Eps * t0) / slope;
        Assert.True(Math.Abs(sHigh - sWant) <= bound, $"at 1.03 Vb the motor settles at slip {sHigh:R} against the circuit's {sWant:R} (bound {bound:R})");
    }

    /// <summary>The slip of the circuit's largest torque, from the Thevenin form: R1 / s = sqrt(Rth^2 + (Xth + X1)^2).</summary>
    private static double BreakdownOfCircuit()
    {
        Complex zs = new(Ra, Xa), jxm = new(0.0, Xm);
        Complex zth = zs * jxm / (zs + jxm);
        return R1 / Math.Sqrt(zth.Real * zth.Real + (zth.Imaginary + X1) * (zth.Imaginary + X1));
    }

    // ------------------------------------------------------------------------------------------------------- DYR

    [Fact]
    public void The_dyr_records_map_value_by_value_in_their_psse_order()
    {
        PsseParseResult raw = Raw14();
        PsseDyrResult r = ReadExtended(Dyr, raw);
        MachineDynamicsSet set = LoadJson(r, raw.Model);
        var g = (Ggov1Data)set.For(0)!.Data.Governor!;
        Assert.Equal(new[] { 1.0, 1.0, 0.04, 1.0, 0.05, -0.05, 10.0, 2.0, 0.0, 1.0, 1.0, 0.15, 0.5, 1.5, 0.2, 0.1, 0.0, 0.0, 3.0, 2.0, 0.67, 1.0, 0.0, 0.1, -0.1,
                0.0, 0.1, 10.0, 0.1, 300.0, 0.0, 4.0, 5.0, 99.0, -99.0, 100.0 },
            new[] { g.Rselect, g.Flag, g.R, g.Tpelec, g.MaxErr, g.MinErr, g.Kpgov, g.Kigov, g.Kdgov, g.Tdgov, g.Vmax, g.Vmin, g.Tact, g.Kturb, g.Wfnl, g.Tb,
                g.Tc, g.Teng, g.Tfload, g.Kpload, g.Kiload, g.Ldref, g.Dm, g.Ropen, g.Rclose, g.Kimw, g.Aset, g.Ka, g.Ta, g.Trate, g.Db, g.Tsa, g.Tsb, g.Rup,
                g.Rdown, g.MachineBaseMva });
        var w = (Wsieg1Data)set.For(1)!.Data.Governor!;
        Assert.Equal(new[] { 20.0, 0.2, 0.1, 0.3, 1.0, -1.0, 1.0, 0.0, 0.3, 0.3, 0.0, 7.0, 0.4, 0.0, 0.5, 0.3, 0.0, 0.05, 0.0, 0.0, 0.0, 0.0, 0.0 },
            new[] { w.K, w.T1, w.T2, w.T3, w.Uo, w.Uc, w.Pmax, w.Pmin, w.T4, w.K1, w.K2, w.T5, w.K3, w.K4, w.T6, w.K5, w.K6, w.T7, w.K7, w.K8, w.Db1, w.Err, w.Db2 });
        Assert.Equal(new[] { 0.3, 0.6, 0.8, 1.0, 0.0 }, w.Gv);
        Assert.Equal(new[] { 0.4, 0.75, 0.9, 1.0, 0.0 }, w.Pgv);
        Assert.Equal(0, w.Iblock);
        var motor = (InductionMotorLoadData)Assert.Single(r.DynamicLoads);
        Assert.Equal((3, 94.2, 19.0, 117.75), (motor.BusId, motor.PMw, motor.QMvar, motor.MotorMva));
        Assert.Equal(InductionMotorParameters.FromCircuit(Ra, Xa, Xm, R1, X1, Hm, InductionMotorTorque.PowerLaw(2.0), raw.Model.FrequencyHz), motor.Motor);
        Assert.Contains(r.Notes, n => n.Contains("CIM5BL", StringComparison.Ordinal));
        Assert.Same(set.DynamicLoads[0], r.DynamicLoads[0]);
    }

    [Fact]
    public void The_default_reader_is_unchanged_and_the_extended_library_refuses_what_it_cannot_represent()
    {
        PsseParseResult raw = Raw14();
        var plain = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(Dyr, raw, "case14"));
        Assert.Contains("GGOV1 x1", plain.Message, StringComparison.Ordinal);
        Assert.Contains("WSIEG1 x1", plain.Message, StringComparison.Ordinal);
        Assert.Contains("CIM5BL x1", plain.Message, StringComparison.Ordinal);

        void Refused(string dyr, string what)
        {
            var ex = Assert.Throws<InvalidDataException>(() => ReadExtended(dyr, raw));
            Assert.Contains(what, ex.Message, StringComparison.Ordinal);
        }
        string gen1 = "     1 'GENROU' 1 " + Genrou + " /\n";
        Refused(gen1 + Ggov1Record.Replace("0.1  0.0  0.0  3.0", "0.1  0.0  0.2  3.0", StringComparison.Ordinal), "a transport delay Teng");
        Refused(gen1 + Ggov1Record.Replace("300.0  0.0  4.0", "300.0  0.01  4.0", StringComparison.Ordinal), "a speed deadband db");
        Refused(gen1 + Ggov1Record.Replace("1.0  0.0  0.1 -0.1", "1.0 -0.1  0.1 -0.1", StringComparison.Ordinal), "a negative Dm");
        Refused(gen1 + Ggov1Record.Replace("'GGOV1' 1  1  1", "'GGOV1' 1  2  1", StringComparison.Ordinal), "Rselect not 1, 0, -1 or -2");
        string gen2 = "     2 'GENROU' 1 " + Genrou + " /\n";
        Refused(gen2 + Wsieg1Record.Replace("'WSIEG1' 1  0  0", "'WSIEG1' 1  3  1", StringComparison.Ordinal), "a second (low-pressure) shaft");
        Refused(gen2 + Wsieg1Record.Replace("0.0  0.0  0.0  0.3  0.4", "0.001  0.0  0.0  0.3  0.4", StringComparison.Ordinal), "the intentional deadband db1");
        Refused(gen1 + Cim5Record.Replace("0.04  0.0  0.0", "0.04  0.01  0.08", StringComparison.Ordinal), "a double cage");
        Refused(gen1 + Cim5Record.Replace("1.25  1.0  0.0", "1.25  1.0  0.7", StringComparison.Ordinal), "the under-voltage trip relay");
        Refused(gen1 + Cim5Record.Replace("     3 'CIM5BL' 1", "     3 'CIM5BL' 7", StringComparison.Ordinal), "the RAW case holds no load '7' at bus 3");
        Refused(gen1 + "     3 'CIMWBL' 1  1  0.01  0.06  4.0  0.03  0.04  0.0  0.0  0.0  0.0  0.0  0.0  0.0  1.25  1.0  0.0  0.0  0.0  0.0  0.0  1.0  2.0 /\n",
            "given by one public source only");
        Refused(gen1 + "     3 'CMLDBLU2' 1  1  2  3 /\n", "GridSim's CMLD is a documented subset");
        Refused(gen1 + Cim5Record.Replace("'CIM5BL'", "'CIM5AR'", StringComparison.Ordinal), "the area-wide form is not read");
        var count = Assert.Throws<InvalidDataException>(() => ReadExtended(gen1 + "     3 'CIM5BL' 1  1  0.01 /\n", raw));
        Assert.Contains("CIM5BL needs exactly 20 values after the load ID", count.Message, StringComparison.Ordinal);
        // A DYR that gives only a motor still reads (no machine record needed).
        Assert.Single(ReadExtended(Cim5Record, raw).DynamicLoads);
    }

    [Fact]
    public void The_dynamics_document_checks_the_new_governor_data()
    {
        PsseParseResult raw = Raw14();
        PsseDyrResult r = ReadExtended(Dyr, raw);
        // A GGOV1 whose operating point the valve limit cannot hold is refused when the study starts (R252): Vmax 0.5 is below
        // the bus-1 unit's stroke.
        string tight = r.Json.Replace("\"valveMaxPu\": 1,", "\"valveMaxPu\": 0.5,", StringComparison.Ordinal);
        Assert.NotEqual(r.Json, tight);
        MachineDynamicsSet set = LoadJson(r with { Json = tight }, raw.Model);
        GridModel m = raw.Model;
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var ex = Assert.Throws<InvalidOperationException>(() => RmsTransientStability.Study(m, pf, m.Generators.Select((_, k) => set.For(k)!.Data).ToList(), 4, 0.1, 0.05,
            stepSeconds: 0.005, durationSeconds: 0.1));
        Assert.Contains("GGOV1", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- 5. determinism

    private static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { m.DeltaRad, m.SpeedPu, m.EqpPu, m.EdpPu, m.EqppPu, m.EddpPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
            foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
                foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    [Fact]
    public void Dense_and_sparse_power_flow_repeated_and_parallel_runs_give_the_same_bits()
    {
        PsseParseResult raw = Raw14();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(raw.Model), sparse = new SparseNewtonRaphsonPowerFlow().Solve(raw.Model);
        string Go(PowerFlowResult pf)
        {
            (GridModel m, _, List<SubtransientInit.MachineData> data, _) = Case14(pf);
            var loaded = new RmsLoadedMachineData(data, new DynamicLoadData[] { MotorLoad(3), ZipLoad(9), CmldLoad(4, 47.8, -3.9) });
            return Digest(RmsTransientStability.Study(m, pf, loaded, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.5));
        }
        string want = Go(dense);
        Assert.Equal(want, Go(sparse));
        Assert.Equal(want, Go(dense));
        var par = new string[4];
        Parallel.For(0, par.Length, i => par[i] = Go(i % 2 == 0 ? dense : sparse));
        Assert.All(par, d => Assert.Equal(want, d));
    }

    /// <summary>The built CLI in the three SIMD modes the release gate runs (default, DOTNET_EnableAVX2=0,
    /// DOTNET_EnableHWIntrinsic=0) gives the same machine rows for the case14 DYR with GGOV1, WSIEG1 and a CIM5BL motor.</summary>
    [Fact]
    public void The_cli_dyr_extended_run_is_the_same_bits_in_every_simd_mode()
    {
        string root = TestTemp.Under("gridsim-wp3b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rawPath = Path.Combine(root, "case14.raw"), dyrPath = Path.Combine(root, "case14.dyr");
            File.WriteAllText(rawPath, Case14Raw());
            File.WriteAllText(dyrPath, Dyr);
            var modes = new (string Name, Dictionary<string, string?> Env)[]
            {
                ("default", new Dictionary<string, string?>()),
                ("noavx2", new Dictionary<string, string?> { ["DOTNET_EnableAVX2"] = "0" }),
                ("nohw", new Dictionary<string, string?> { ["DOTNET_EnableHWIntrinsic"] = "0" }),
            };
            string? want = null;
            foreach ((string name, Dictionary<string, string?> env) in modes)
            {
                string outDir = Path.Combine(root, name);
                CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "stability", rawPath, "--rms", "--dyn-data", dyrPath, "--dyr-extended",
                    "--events", "fault:4@0.1,clear:4@0.15", "--events-out", outDir);
                Assert.True(r.ExitCode == 0, r.ToString());
                Assert.Contains("1 dynamic load(s) from the dynamics file", r.StdOut, StringComparison.Ordinal);
                string rows = string.Join("\n", File.ReadAllLines(Path.Combine(outDir, RmsEventsExport.MachinesCsv)).Where(l => !l.StartsWith('#')));
                want ??= rows;
                Assert.Equal(want, rows);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
