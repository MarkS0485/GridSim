using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Harmonic state estimation: the inverse of penetration. Given harmonic voltage measurements it recovers the
/// harmonic source injections that produced them - to machine precision on exact data, and for several
/// simultaneous sources. This is GridSim's estimator identity carried into the harmonic domain.
/// </summary>
public class HarmonicStateEstimatorTests
{
    private static List<HarmonicVoltageMeasurement> AllBusVoltages(GridModel model, Complex[] v, double sigma)
        => model.Buses.Select((b, i) => new HarmonicVoltageMeasurement(b.Id, v[i], sigma)).ToList();

    [Fact]
    // GR-406: renamed from Recovers_a_single_known_injection_to_machine_precision: the name said machine precision; the assertions are 1e-6 on the injection and 1e-3 on the residual. Tightening waits for a measured value.
    public void Recovers_a_single_known_injection_within_1e_6()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5 } };
        int srcBus = model.Buses[4].Id;
        var src = HarmonicSourceLibrary.Make("6-pulse", srcBus, fundamentalCurrentPu: 0.8);

        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, new[] { src }, opt);
        var meas = AllBusVoltages(model, pen.Voltage[0], 1e-4);

        HarmonicStateEstimate est = HarmonicStateEstimator.Estimate(model, 5, meas, new[] { srcBus }, opt);

        Complex trueInj = src.InjectionAt(5);
        Assert.True((est.Injection[0] - trueInj).Magnitude < 1e-6, $"{est.Injection[0]} vs {trueInj}");
        Assert.True(est.ResidualNorm < 1e-3, $"exact data -> tiny residual, was {est.ResidualNorm}");
    }

    [Fact]
    public void Recovers_two_simultaneous_sources()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5 } };
        int b1 = model.Buses[4].Id, b2 = model.Buses[7].Id;
        var s1 = HarmonicSourceLibrary.Make("6-pulse", b1, 0.8);
        var s2 = HarmonicSourceLibrary.Make("pv-inverter", b2, 0.6);

        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, new[] { s1, s2 }, opt);
        var meas = AllBusVoltages(model, pen.Voltage[0], 1e-4);

        HarmonicStateEstimate est = HarmonicStateEstimator.Estimate(model, 5, meas, new[] { b1, b2 }, opt);

        Assert.True((est.Injection[0] - s1.InjectionAt(5)).Magnitude < 1e-6);
        Assert.True((est.Injection[1] - s2.InjectionAt(5)).Magnitude < 1e-6);
    }

    [Fact]
    public void Rejects_an_under_determined_problem()
    {
        GridModel model = IeeeCases.Case9();
        var oneMeas = new[] { new HarmonicVoltageMeasurement(model.Buses[0].Id, Complex.One) };
        Assert.Throws<ArgumentException>(() =>
            HarmonicStateEstimator.Estimate(model, 5, oneMeas, new[] { model.Buses[0].Id, model.Buses[1].Id }));
    }
}
