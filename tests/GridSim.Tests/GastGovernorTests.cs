using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GAST, the simple gas-turbine governor (<see cref="Gast"/>), checked against closed forms of its block diagram rather
/// than against a second copy of its code:
/// <list type="bullet">
/// <item>with the temperature limit idle, a held speed step drives two cascaded exact zero-order-hold lags, whose
/// discrete solution is x1_n = c + d a1^n and x2_n = c + A a1^n + B a2^n with A = (1 - a2) d a1 / (a1 - a2) and
/// B = x2_0 - c - A (a_i = exp(-dt / T_i)), derived by hand from x_n = a x_(n-1) + (1 - a) u_n;</item>
/// <item>on the temperature limit the steady state solves x = AT + KT (AT - x), so the turbine settles at AT exactly
/// whatever KT;</item>
/// <item>on the valve limit it settles at VMAX; DTURB acts at once.</item>
/// </list>
/// The DYR mapping (R T1 T2 T3 AT KT VMAX VMIN DTURB) and the loader are checked value by value.
/// </summary>
public class GastGovernorTests
{
    private static void Close(double expected, double actual, string what, double tol = 1e-13)
        => Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void Gast_is_stationary_at_its_equilibrium_to_the_bit()
    {
        var g = new Gast(0.05, 0.4, 0.1, 3.0, 1.0, 2.0, 0.0, 1.1, 0.0);
        g.Initialize(0.8);
        for (int n = 0; n < 2000; n++) Assert.Equal(BitConverter.DoubleToInt64Bits(0.8), BitConverter.DoubleToInt64Bits(g.Step(0.0, 0.01)));
        Assert.Equal(0.8, g.ExhaustState);
        Assert.Equal(1.0 + 2.0 * (1.0 - 0.8), g.TemperatureLimit);
    }

    [Fact]
    public void Gast_speed_step_follows_the_closed_form_of_its_two_cascaded_lags()
    {
        // AT high, so the gate passes the droop path; R 0.05, dw -0.004: pd = 0.6 + 0.08.
        double r = 0.05, t1 = 0.4, t2 = 0.15, dt = 0.01, p0 = 0.6, dw = -0.004;
        var g = new Gast(r, t1, t2, 5.0, 10.0, 3.0, 0.0, 2.0, 0.0);
        g.Initialize(p0);
        double c = p0 - dw / r, d = p0 - c;
        double a1 = DetMath.Exp(-dt / t1), a2 = DetMath.Exp(-dt / t2);
        double A = (1 - a2) * d * a1 / (a1 - a2), B = p0 - c - A;
        for (int n = 1; n <= 400; n++)
        {
            double pm = g.Step(dw, dt);
            Close(c + d * Math.Pow(a1, n), g.ValvePosition, $"valve at step {n}");
            Close(c + A * Math.Pow(a1, n) + B * Math.Pow(a2, n), pm, $"turbine power at step {n}");
        }
        double last = 0;
        for (int n = 0; n < 3000; n++) last = g.Step(dw, dt);
        Close(0.68, last, "settles at Pref - dw/R", 1e-12);
    }

    [Fact]
    public void Gast_on_its_temperature_limit_settles_at_AT_whatever_KT()
    {
        foreach (double kt in new[] { 0.0, 0.5, 2.0, 5.0 })
        {
            // Pm0 0.85 under AT 0.9; a 1 % underspeed asks for 0.85 + 0.2 = 1.05, above AT, below VMAX.
            var g = new Gast(0.05, 0.4, 0.1, 3.0, 0.9, kt, 0.0, 1.5, 0.0);
            g.Initialize(0.85);
            double pm = 0;
            for (int n = 0; n < 20000; n++) pm = g.Step(-0.01, 0.01);
            Close(0.9, pm, $"KT {kt}: settles at AT", 1e-10);
            Close(0.9, g.TemperatureLimit, $"KT {kt}: the limit settles at AT", 1e-10);
        }
    }

    [Fact]
    public void Gast_valve_limit_caps_the_power_and_dturb_acts_at_once()
    {
        var g = new Gast(0.05, 0.4, 0.1, 3.0, 5.0, 2.0, 0.0, 1.0, 0.0);
        g.Initialize(0.9);
        double pm = 0;
        for (int n = 0; n < 5000; n++) pm = g.Step(-0.02, 0.01);
        Close(1.0, pm, "VMAX caps the turbine", 1e-12);
        Assert.Equal(1.0, g.ValvePosition);

        // DTURB: a speed deviation subtracts DTURB*dw on the same step, before any lag has moved much.
        var withD = new Gast(0.05, 0.4, 0.1, 3.0, 5.0, 2.0, 0.0, 1.5, 0.5);
        var noD = new Gast(0.05, 0.4, 0.1, 3.0, 5.0, 2.0, 0.0, 1.5, 0.0);
        withD.Initialize(0.7); noD.Initialize(0.7);
        Assert.Equal(noD.Step(0.01, 0.01) - 0.5 * 0.01, withD.Step(0.01, 0.01));
    }

    [Fact]
    public void Gast_refuses_a_start_above_AT_or_outside_its_valve_limits_and_bad_data()
    {
        var g = new Gast(0.05, 0.4, 0.1, 3.0, 0.9, 2.0, 0.0, 1.5, 0.0);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => g.Initialize(0.95));
        Assert.Contains("above the ambient temperature load limit AT = 0.9 pu", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Gast(0.05, 0.4, 0.1, 3.0, 2.0, 2.0, 0.0, 0.8, 0.0).Initialize(0.85));
        g.Initialize(0.9);   // exactly at AT: the gate's two inputs are equal, still an equilibrium
        Assert.Equal(0.9, g.Step(0.0, 0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Gast(0.0, 0.4, 0.1, 3.0, 1.0, 2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Gast(0.05, 0.4, 0.1, 3.0, 1.0, -1.0));
        Assert.Throws<ArgumentException>(() => new Gast(0.05, 0.4, 0.1, 3.0, 1.0, 2.0, 1.0, 0.5));
    }

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    [Fact]
    public void A_dyr_gast_record_maps_value_by_value_and_loads()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        string dyr = string.Join("\n",
            "     6 'GENROU' 1   7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0 /",
            "     6 'GAST'   1   0.05  0.4  0.1  3.0  1.2  2.0  1.1  0.0  0.02 /");
        PsseDyrResult r = PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "gast.dyr" });
        Assert.Equal(1, r.Machines);
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-gast-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        MachineDynamicsSet set;
        try
        {
            string path = Path.Combine(dir, "gast.dynamics.json");
            File.WriteAllText(path, r.Json);
            set = MachineDynamicsLoader.Load(path, Path.Combine(CliProcess.Root(), "schemas", "v2"), raw.Model);
        }
        finally { Directory.Delete(dir, true); }
        MachineDynamicsEntry e = Assert.Single(set.Entries);
        Assert.Equal(new GastData(0.05, 0.4, 0.1, 3.0, 1.2, 2.0, 0.0, 1.1, 0.02), e.Data.Governor);
    }
}
