using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The grounded harmonic analysis core: linear penetration (solve Y(h) V = I), the driving-point impedance /
/// frequency scan and its resonance detection, and the balanced-order sequence rotation. Penetration and the
/// scan are cross-checked against each other and against the network equations.
/// </summary>
public class HarmonicAnalysisTests
{
    private static Complex[] Multiply(ComplexCsr a, Complex[] x)
    {
        var y = new Complex[a.N];
        for (int i = 0; i < a.N; i++)
        {
            Complex s = Complex.Zero;
            for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                s += new Complex(a.G[k], a.B[k]) * x[a.ColIdx[k]];
            y[i] = s;
        }
        return y;
    }

    [Fact]
    public void Penetration_solves_the_network_equations_Yh_V_equals_I()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11 } };
        var sources = new[]
        {
            HarmonicSourceLibrary.Make("6-pulse", bus: model.Buses[4].Id, fundamentalCurrentPu: 0.8),
        };

        HarmonicPenetrationResult res = HarmonicPenetration.Solve(model, sources, opt);

        // At the 5th, Y(5) V(5) must equal the injected current I(5).
        int oi = res.Orders.ToList().IndexOf(5);
        Complex[] v = res.Voltage[oi];
        ComplexCsr y = FrequencyDependentYBus.Build(model, 5, opt);
        Complex[] yv = Multiply(y, v);

        var inj = new Complex[model.BusCount];
        inj[4] = sources[0].InjectionAt(5);   // bus index 4 is Buses[4]
        for (int i = 0; i < model.BusCount; i++)
            Assert.True((yv[i] - inj[i]).Magnitude < 1e-8, $"KCL residual at bus {i}: {yv[i]} vs {inj[i]}");
    }

    [Fact]
    public void Impedance_scan_equals_the_penetration_response_to_a_unit_injection()
    {
        GridModel model = IeeeCases.Case9();
        int busId = model.Buses[6].Id;
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11, 13 } };

        // A single source injecting 1.0 pu ONLY at the 7th, at that bus.
        var src = new HarmonicSource(busId, "unit", 1.0, new[] { new HarmonicComponent(7, 100.0) });
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, new[] { src }, opt);
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, busId, opt);

        int oi = opt.Orders.ToList().IndexOf(7);
        int bi = model.Buses.ToList().FindIndex(b => b.Id == busId);
        // V_k = Z_kk * I_k with I_k = 1.0 -> the penetration voltage at the bus equals the scan impedance.
        Assert.True((pen.Voltage[oi][bi] - scan.Z[oi]).Magnitude < 1e-7,
            $"V_k {pen.Voltage[oi][bi]} vs Z_kk {scan.Z[oi]}");
    }

    [Fact]
    public void The_scan_finds_a_parallel_resonance_above_the_fundamental()
    {
        // Source reactance to ground at bus 0, a shunt capacitor at bus 1, an inductive line between them:
        // a classic parallel resonance somewhere in the ordered-harmonic range.
        var model = new GridModel
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Buses = new[]
            {
                new Bus(0, BusType.Slack, Bs: -500),  // inductive source (strong grid) to ground
                new Bus(1, BusType.Pq, Bs: 8),         // shunt capacitor
            },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(0, 1, 0.002, 0.1) },
        };
        var opt = new HarmonicModelOptions { Orders = Enumerable.Range(2, 39).ToArray() }; // 2..40

        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, busId: 1, opt);

        Assert.True(scan.ResonanceOrder > 2, $"resonance at order {scan.ResonanceOrder}");
        double zAtTwo = scan.Z[0].Magnitude;                          // order 2 is the first scanned
        Assert.True(scan.PeakMagnitude > 3.0 * zAtTwo,
            $"resonance peak |Z|={scan.PeakMagnitude:F3} should tower over the low-order |Z|={zAtTwo:F3}");
    }

    [Fact]
    public void Balanced_orders_rotate_positive_negative_zero()
    {
        Assert.Equal(HarmonicSequenceKind.Positive, HarmonicSequence.Of(1));
        Assert.Equal(HarmonicSequenceKind.Negative, HarmonicSequence.Of(5));
        Assert.Equal(HarmonicSequenceKind.Zero, HarmonicSequence.Of(3));
        Assert.Equal(HarmonicSequenceKind.Positive, HarmonicSequence.Of(7));
        Assert.True(HarmonicSequence.IsTriplen(9));
        Assert.False(HarmonicSequence.IsTriplen(11));
    }
}
