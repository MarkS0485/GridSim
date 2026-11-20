using System.Numerics;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Shared builders for the R4-12 tests: a single machine against an infinite bus, a ring of machines, and the
/// independent circuit solution the oracles use (complex arithmetic only, no GridSim solver).</summary>
internal static class R412Fixtures
{
    public const double F0 = 50.0;
    public const double XInf = 1e-6;   // reactance behind the infinite bus in the assembler (the oracles include it)

    public static (GridModel Model, PowerFlowResult Pf) Smib(double pgMw, double vt, double xe, double eb = 1.0, double h = 3.5)
    {
        var buses = new List<Bus> { new(1, BusType.Slack, Vm: eb), new(2, BusType.Pv, Vm: vt) };
        var gens = new List<Generator>
        {
            new(1, 0.0, Vg: eb, MBase: 100.0, H: 0.0),
            new(2, pgMw, Vg: vt, MBase: 100.0, H: h),
        };
        var model = new GridModel
        {
            BaseMva = 100.0, Buses = buses, Generators = gens, Branches = new List<Branch> { new(1, 2, 0.0, xe) },
            FrequencyHz = F0, Name = "smib",
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    /// <summary>The independent circuit solution of the lossless SMIB: terminal voltage V2 = vt at angle theta (from
    /// sin(theta) = P Xl / (vt Eb)), generator current I2 = (V2 - V1)/(j Xl), V1 = Eb at angle 0.</summary>
    public static (Complex V2, Complex I2, Complex V1) SmibCircuit(double pPu, double vt, double xl, double eb)
    {
        double theta = Math.Asin(pPu * xl / (vt * eb));
        Complex v2 = Complex.FromPolarCoordinates(vt, theta);
        Complex v1 = new(eb, 0.0);
        Complex i2 = (v2 - v1) / new Complex(0.0, xl);
        return (v2, i2, v1);
    }

    /// <summary>A ring of <paramref name="ng"/> buses, each with a generator and a load, plus chords.</summary>
    public static (GridModel Model, PowerFlowResult Pf) Ring(int ng)
    {
        var buses = new List<Bus>();
        var gens = new List<Generator>();
        var branches = new List<Branch>();
        for (int i = 1; i <= ng; i++)
        {
            buses.Add(new Bus(i, i == 1 ? BusType.Slack : BusType.Pv, Pd: 40.0, Qd: 10.0, Vm: 1.02));
            gens.Add(new Generator(i, 40.0, Vg: 1.02, MBase: 100.0, H: 3.0 + 0.1 * (i % 7), Qmin: -200, Qmax: 200, Pmax: 500));
            branches.Add(new Branch(i, i % ng + 1, 0.005, 0.1, 0.02));
        }
        for (int i = 1; i <= ng / 2; i += 3) branches.Add(new Branch(i, i + ng / 2, 0.01, 0.4, 0.01));
        var model = new GridModel
        {
            BaseMva = 100.0, Buses = buses, Generators = gens, Branches = branches, FrequencyHz = F0, Name = "ring" + ng,
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged, "the ring power flow must converge");
        return (model, pf);
    }

    public static UserModel Sexs(double k, double te, double ta = 1.0, double tb = 1.0)
        => UserModelLibrary.Load("SEXS", new Dictionary<string, double> { ["K"] = k, ["Te"] = te, ["Ta"] = ta, ["Tb"] = tb });

    public static ModelDocument Doc(string json) => ModelDocument.Parse(json);
}
