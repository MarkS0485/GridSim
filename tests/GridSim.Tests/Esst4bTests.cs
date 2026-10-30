using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// ESST4B (<see cref="Esst4b"/>), the static exciter with outer voltage and inner field PI regulators, checked against
/// hand values: the power source voltage VE from complex arithmetic done here with System.Numerics.Complex, the
/// stationary start from the first step's measurements, the steady states the two integrators force (Efd = VRMAX / KG on
/// the outer limit, VMMAX VB on the inner limit), the VBMAX ceiling, and in the RMS kernel an undisturbed case 9 that stays
/// at rest while a fault moves it. The PSS/E record (TR KPR KIR VRMAX VRMIN TA KPM KIM VMMAX VMMIN KG KP KI VBMAX KC XL
/// THETAP) maps value by value.
/// </summary>
public class Esst4bTests
{
    private static void Close(double expected, double actual, string what, double tol = 1e-13)
        => Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    private static Esst4b Make(double vrMax = 1.0, double vmMax = 1.0, double vbMax = 11.0, double kg = 0.0, double kir = 3.15, double kim = 0.0,
        double kc = 0.113, double ki = 0.0, double xl = 0.0, double thetap = 0.0)
        => new(3.15, kir, -0.87, vrMax, 0.01, 1.0, kim, -0.87, vmMax, kg, 6.5, ki, vbMax, kc, xl, thetap);

    [Fact]
    public void The_power_source_voltage_matches_complex_arithmetic_done_by_hand()
    {
        Esst4b x = Make(ki: 0.3, xl: 0.1, thetap: 30.0);
        Complex vt = new(1.02, 0.1), it = new(0.8, -0.3);
        Complex kpc = Complex.FromPolarCoordinates(6.5, 30.0 * Math.PI / 180.0);
        double want = (kpc * vt + Complex.ImaginaryOne * (0.3 + kpc * 0.1) * it).Magnitude;
        Close(want, x.SourceVoltage(vt.Real, vt.Imaginary, it.Real, it.Imaginary), "VE", 1e-14);
        // Potential source only: VE = KP |VT|.
        Close(6.5 * vt.Magnitude, Make().SourceVoltage(vt.Real, vt.Imaginary, it.Real, it.Imaginary), "VE = KP |VT|", 1e-15);
    }

    [Fact]
    public void Esst4b_starts_at_rest_from_its_first_measurements_and_stays_there()
    {
        foreach ((double kg, double kim) in new[] { (0.0, 0.0), (0.1, 0.0), (0.1, 2.0) })
        {
            Esst4b x = Make(kg: kg, kim: kim, vrMax: 5, vmMax: 5);
            double efd = 1.9;
            x.Initialize(efd);
            Assert.Equal(0.0, x.EquilibriumErrorPu(efd));
            for (int n = 0; n < 2000; n++) Close(efd, x.Step(0.0, 1.01, 0.05, 0.7, -0.2, efd, 0.005), $"KG {kg} KIM {kim}: at rest, step {n}", 1e-14);
        }
    }

    [Fact]
    public void The_integrators_force_efd_to_vrmax_over_kg_or_to_the_inner_limit()
    {
        // KG 0.25, KIM 2: a held positive error winds the outer integrator to VRMAX 0.6; the inner integrator then drives
        // VR - KG Efd to 0, so Efd settles at VRMAX / KG = 2.4 (VM = 2.4 / VB stays inside VMMAX 5).
        Esst4b x = Make(kg: 0.25, kim: 2.0, vrMax: 0.6, vmMax: 5.0);
        x.Initialize(1.9);
        double efd = 0;
        for (int n = 0; n < 40000; n++) efd = x.Step(0.01, 1.01, 0.05, 0.7, -0.2, 1.9, 0.005);
        Close(0.6 / 0.25, efd, "Efd = VRMAX / KG", 1e-10);

        // VMMAX 0.3 below the 2.4 / VB the outer loop asks for: Efd settles at VMMAX VB.
        Esst4b y = Make(kg: 0.25, kim: 2.0, vrMax: 0.6, vmMax: 0.3);
        y.Initialize(1.9);
        double e = 1.9;
        for (int n = 0; n < 40000; n++) e = y.Step(0.01, 1.01, 0.05, 0.7, -0.2, 1.9, 0.005);
        Close(0.3 * y.LastVb, e, "Efd = VMMAX VB", 1e-12);
    }

    [Fact]
    public void Vbmax_caps_the_power_source_and_bad_data_is_refused()
    {
        Esst4b x = Make(vbMax: 2.0, vrMax: 5, vmMax: 5);
        x.Initialize(1.5);
        x.Step(0.0, 1.01, 0.05, 0.7, -0.2, 1.5, 0.005);
        Assert.Equal(2.0, x.LastVb);
        Assert.Throws<ArgumentException>(() => new Esst4b(3.15, 0, -0.87, 1, 0.01, 1, 0, -0.87, 1, 0, 6.5, 0, 11, 0.1, 0, 0));
        Assert.Throws<ArgumentException>(() => new Esst4b(3.15, 3.15, -0.87, 1, 0.01, 1, 0, -0.87, 1, 0, 0, 0, 11, 0.1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make().Initialize(0.0));
        // VM = Efd / VB above VMMAX at the first step: refused there, not clamped.
        Esst4b z = Make(vmMax: 0.1);
        z.Initialize(1.9);
        Assert.Throws<ArgumentOutOfRangeException>(() => z.Step(0.0, 1.01, 0.05, 0.7, -0.2, 1.9, 0.005));
    }

    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    [Fact]
    public void An_esst4b_fleet_holds_case9_still_undisturbed_and_moves_under_a_fault()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Fixture, SchemasDir, model);
        var data = model.Generators.Select((g, k) => set.For(k)!.Data).ToList();
        List<RmsExciter?> Fleet() => model.Generators.Select(_ => (RmsExciter?)RmsExciter.FromEsst4b(Make(kg: 0.1, kim: 0.0, vrMax: 10, vmMax: 10, vbMax: 20))).ToList();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0, exciters: Fleet());
        Assert.True(still.StayedInSynchronism);
        foreach (MultiMachineSubtransient.MachineTrace m in still.Machines)
            Assert.True(Math.Abs(m.SpeedPu[^1]) < 1e-7, $"speed drifted to {m.SpeedPu[^1]:R}");
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: Fleet());
        Assert.True(fault.Machines.Any(m => Math.Abs(m.SpeedPu[^1]) > 1e-6), "a fault must move the fleet");
    }

    [Fact]
    public void An_esst4b_needs_its_machine_base_and_refuses_a_unit_transformer()
    {
        RmsExciter ex = RmsExciter.FromEsst4b(Make());
        ex.Initialize(1.5);
        var unbound = Assert.Throws<InvalidOperationException>(() => ex.Step(0.0, 0.0, 1.5, new Complex(1, 0), new Complex(0.5, 0), 0.005));
        Assert.Contains("machine base", unbound.Message, StringComparison.Ordinal);

        GridModel model = IeeeCases.Case9();
        GridModel withXt = model.CopyWith(generators: model.Generators.Select((g, k) => k == 1 ? g with { UnitTransformerXPu = 0.1 } : g).ToList());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(withXt);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Fixture, SchemasDir, withXt);
        var data = withXt.Generators.Select((g, k) => set.For(k)!.Data).ToList();
        var exs = withXt.Generators.Select(_ => (RmsExciter?)RmsExciter.FromEsst4b(Make(kg: 0.1, vrMax: 10, vmMax: 10, vbMax: 20))).ToList();
        var refused = Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(withXt, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 0.1, exciters: exs));
        Assert.Contains("unit transformer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dyr_esst4b_record_maps_value_by_value_and_loads()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        string dyr = "     6 'GENROU' 1   7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0 /\n"
                   + "     6 'ESST4B' 1   0.0  3.15  3.15  1.0  -0.87  0.01  1.0  0.0  1.0  -0.87  0.0  6.5  0.0  11.0  0.113  0.0  0.0 /";
        PsseDyrResult r = PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "st4b.dyr" });
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-esst4b-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "st4b.dynamics.json");
            File.WriteAllText(path, r.Json);
            MachineDynamicsEntry e = Assert.Single(MachineDynamicsLoader.Load(path, SchemasDir, raw.Model).Entries);
            Assert.Equal("ESST4B", e.Exciter!.Model);
            IReadOnlyDictionary<string, double> p = e.Exciter.Params;
            Assert.Equal((3.15, 3.15, 1.0, -0.87, 0.01), (p["st4bKpr"], p["st4bKir"], p["st4bVrMaxPu"], p["st4bVrMinPu"], p["st4bTaS"]));
            Assert.Equal((1.0, 0.0, 1.0, -0.87, 0.0), (p["st4bKpm"], p["st4bKim"], p["st4bVmMaxPu"], p["st4bVmMinPu"], p["st4bKg"]));
            Assert.Equal((6.5, 0.0, 11.0, 0.113, 0.0, 0.0), (p["st4bKp"], p["st4bKi"], p["st4bVbMaxPu"], p["st4bKc"], p["st4bXlPu"], p["st4bThetapDeg"]));
            Assert.True(e.Exciter.Create().NeedsTerminalQuantities);
        }
        finally { Directory.Delete(dir, true); }

        var trEx = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(dyr.Replace("'ESST4B' 1   0.0", "'ESST4B' 1   0.02", StringComparison.Ordinal), raw, "case14"));
        Assert.Contains("ESST4B for machine '1' at bus 6 (line 2): a voltage transducer lag TR", trEx.Message, StringComparison.Ordinal);
    }
}
