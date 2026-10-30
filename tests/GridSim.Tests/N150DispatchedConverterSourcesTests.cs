using System;
using System.Linq;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N150 (1.95.243): the dispatched converter-source level reads each unit's SOLVED output when given the operating
/// point (SolvedGeneration.UnitOutput, the D-DOC-054 rule), not the schedule. Without an operating point it keeps the
/// scheduled Pg / Qg (bit for bit as before), and the rated level ignores the solve.
/// </summary>
public class N150DispatchedConverterSourcesTests
{
    /// <summary>case9 with its third machine (a PV unit, bus 3) turned into a wind farm, scheduled Qg = 0.</summary>
    private static GridModel WindOnPvBus()
    {
        GridModel m = IeeeCases.Case9();
        var gens = m.Generators.ToList();
        gens[2] = gens[2] with { Type = "wind", Name = "WF", Qg = 0.0 };
        return m.CopyWith(generators: gens);
    }

    [Fact]
    public void With_an_operating_point_the_dispatched_current_is_the_solved_output()
    {
        GridModel m = WindOnPvBus();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        (double p, double q) = SolvedGeneration.UnitOutput(m, r, 2);

        HarmonicSource solved = Assert.Single(ConverterHarmonicSources.FromModel(m, ConverterHarmonicLevel.Dispatched, operatingPoint: r).Sources);
        Assert.Equal(Math.Sqrt(p * p + q * q) / m.BaseMva, solved.FundamentalCurrentPu);

        // The schedule says Q = 0; the solve gives the PV unit's real Q, so the two levels differ.
        HarmonicSource scheduled = Assert.Single(ConverterHarmonicSources.FromModel(m, ConverterHarmonicLevel.Dispatched).Sources);
        Assert.Equal(Math.Sqrt(m.Generators[2].Pg * m.Generators[2].Pg) / m.BaseMva, scheduled.FundamentalCurrentPu);
        Assert.NotEqual(0.0, q);
        Assert.NotEqual(scheduled.FundamentalCurrentPu, solved.FundamentalCurrentPu);
    }

    [Fact]
    public void The_rated_level_ignores_the_operating_point()
    {
        GridModel m = WindOnPvBus();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        double with = Assert.Single(ConverterHarmonicSources.FromModel(m, ConverterHarmonicLevel.Rated, operatingPoint: r).Sources).FundamentalCurrentPu;
        double without = Assert.Single(ConverterHarmonicSources.FromModel(m, ConverterHarmonicLevel.Rated).Sources).FundamentalCurrentPu;
        Assert.Equal(BitConverter.DoubleToInt64Bits(without), BitConverter.DoubleToInt64Bits(with));
    }

    [Fact]
    public void A_non_converged_operating_point_is_refused()
    {
        GridModel m = WindOnPvBus();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        PowerFlowResult failed = new()
        {
            Converged = false, Iterations = r.Iterations, MaxMismatch = 1.0, Status = PowerFlowStatus.MaxIterations,
            BusIds = r.BusIds, Vm = r.Vm, VaDeg = r.VaDeg, PInjMw = r.PInjMw, QInjMvar = r.QInjMvar,
            TotalLossMw = r.TotalLossMw, TotalLossMvar = r.TotalLossMvar, LineFlows = r.LineFlows,
        };
        Assert.Throws<NonConvergedStateException>(() => ConverterHarmonicSources.FromModel(m, ConverterHarmonicLevel.Dispatched, operatingPoint: failed));
    }
}
