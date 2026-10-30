using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The ZIP (voltage-dependent) load model: load draws P(V) = Pd*(Ap*V^2 + Bp*V + Cp) instead of a fixed
/// constant power. The default (0,0,1) is pure constant power, so every existing case is unchanged; a
/// constant-impedance or constant-current load correctly draws less power as its voltage sags, and the two
/// solvers agree on the ZIP solution just as they do on the constant-power one.
/// </summary>
public class ZipLoadTests
{
    // A small radial case with a heavy remote load, so its voltage sags well below 1.0 pu.
    private static GridModel Case(ZipLoad? zip) => new()
    {
        BaseMva = 100,
        Name = "zip-test",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 60, Qd: 20, BaseKv: 132, Zip: zip),
            new Bus(3, BusType.Pq, Pd: 90, Qd: 30, BaseKv: 132, Zip: zip),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.02, 0.08, 0.0),
            new Branch(2, 3, 0.03, 0.10, 0.0),
        },
    };

    [Fact]
    public void Constant_power_default_is_bit_for_bit_the_unchanged_solver()
    {
        // A model with no ZIP (null) and one with an explicit pure-constant-power ZIP must solve identically
        // to each other AND the explicit one must not perturb anything (HasZip stays false: (0,0,1) does not
        // vary with voltage).
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(Case(null));
        PowerFlowResult explicitCp = new NewtonRaphsonPowerFlow().Solve(Case(new ZipLoad(0, 0, 1, 0, 0, 1)));
        Assert.True(plain.Converged && explicitCp.Converged);
        for (int i = 0; i < plain.Vm.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(plain.Vm[i]), BitConverter.DoubleToInt64Bits(explicitCp.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(plain.VaDeg[i]), BitConverter.DoubleToInt64Bits(explicitCp.VaDeg[i]));
        }
    }

    [Fact]
    public void Constant_impedance_load_draws_less_power_when_its_voltage_sags()
    {
        // Constant power vs constant impedance (Ap=Aq=1). Under the same weak network the CP load holds its
        // MW as the voltage sags, whereas the constant-Z load's demand falls with V^2 - so the Z case sits at
        // a HIGHER voltage and draws LESS real power at the load buses.
        PowerFlowResult cp = new NewtonRaphsonPowerFlow().Solve(Case(null));
        PowerFlowResult z = new NewtonRaphsonPowerFlow().Solve(Case(new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 1, Bq: 0, Cq: 0)));
        Assert.True(cp.Converged && z.Converged);

        int b3 = Array.IndexOf(z.BusIds.ToArray(), 3);
        Assert.True(cp.Vm[b3] < 1.0, "the remote bus should sag under constant power");
        Assert.True(z.Vm[b3] > cp.Vm[b3], $"constant-Z load should hold voltage higher: Z {z.Vm[b3]:F4} vs CP {cp.Vm[b3]:F4}");

        // The slack picks up total load + losses; the constant-Z case draws less overall (load relief).
        int slack = Array.IndexOf(z.BusIds.ToArray(), 1);
        Assert.True(z.PInjMw[slack] < cp.PInjMw[slack],
            $"constant-Z total demand should be lower: Z {z.PInjMw[slack]:F2} vs CP {cp.PInjMw[slack]:F2} MW");
    }

    [Fact]
    public void The_solved_load_equals_the_zip_polynomial_at_the_solved_voltage()
    {
        // Physical check: at the solved point the actual bus load must equal Pd*mult(V) for the ZIP law.
        var zip = new ZipLoad(Ap: 0.5, Bp: 0.3, Cp: 0.2, Aq: 0.5, Bq: 0.3, Cq: 0.2); // sums to 1
        GridModel model = Case(zip);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(r.Converged);

        int b3 = Array.IndexOf(r.BusIds.ToArray(), 3);
        double v = r.Vm[b3];
        // Net injection at bus 3 (no generation) = -load. Expected load = 90 * mult(v).
        double expectedLoadMw = 90.0 * zip.PMultiplier(v);
        Assert.Equal(-expectedLoadMw, r.PInjMw[b3], 3);
    }

    [Fact]
    public void Dense_and_sparse_agree_on_a_zip_solution()
    {
        var zip = new ZipLoad(Ap: 0.4, Bp: 0.3, Cp: 0.3, Aq: 0.4, Bq: 0.3, Cq: 0.3);
        GridModel model = Case(zip);
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(model);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(model);
        Assert.True(dense.Converged && sparse.Converged);
        for (int i = 0; i < dense.Vm.Length; i++)
        {
            Assert.True(Math.Abs(dense.Vm[i] - sparse.Vm[i]) < 1e-9, $"Vm[{i}] dense {dense.Vm[i]} vs sparse {sparse.Vm[i]}");
            Assert.True(Math.Abs(dense.VaDeg[i] - sparse.VaDeg[i]) < 1e-7);
        }
    }

    [Fact]
    public void Inverter_fraction_blends_toward_constant_power_and_stays_a_valid_zip()
    {
        // A non-inverter remainder that is voltage-dependent (constant-impedance heavy).
        var nonInv = new ZipLoad(Ap: 0.5, Bp: 0.3, Cp: 0.2, Aq: 0.5, Bq: 0.3, Cq: 0.2);

        // f = 0 -> the pure non-inverter composition; f = 1 -> pure constant power (0,0,1).
        ZipLoad none = ZipLoad.FromInverterFraction(0.0, nonInv);
        Assert.Equal(nonInv, none);
        ZipLoad all = ZipLoad.FromInverterFraction(1.0, nonInv);
        Assert.False(all.VariesWithVoltage);
        Assert.Equal(1.0, all.Cp, 12);

        // f = 0.5 -> linear blend f*(0,0,1) + (1-f)*nonInv; each triple still sums to 1.
        ZipLoad half = ZipLoad.FromInverterFraction(0.5, nonInv);
        Assert.Equal(0.5 * 0.5, half.Ap, 12);            // (1-f)*Ap
        Assert.Equal(0.5 + 0.5 * 0.2, half.Cp, 12);      // f + (1-f)*Cp
        Assert.Equal(1.0, half.Ap + half.Bp + half.Cp, 12);
        Assert.Equal(1.0, half.Aq + half.Bq + half.Cq, 12);

        // Damping-loss direction: at a sagging 0.95 pu the more-inverter blend draws MORE power
        // (less voltage relief) than the less-inverter one.
        Assert.True(half.PMultiplier(0.95) > nonInv.PMultiplier(0.95));
        Assert.Equal(1.0, half.PMultiplier(1.0), 12);    // still exact at nominal
    }
}
