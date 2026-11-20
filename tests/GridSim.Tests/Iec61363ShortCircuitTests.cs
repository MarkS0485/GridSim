using System;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.ShortCircuit.Iec61363;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-20b: the IEC 61363-1-style ship-system study. The standard is paid and was not read, so every oracle is derived here from the
/// elementary decomposition (decaying AC envelope plus decaying DC offset): the two-exponential machine envelope, the DC term
/// sqrt(2) I'' e^(-t/Ta), the stationary point of cos(wt) - e^(-t/Ta) solved by Newton iteration in the test, and by-hand sums for a machine,
/// a motor and a converter. No table value or worked example of the standard is used.
/// </summary>
public class Iec61363ShortCircuitTests
{
    private const double BaseMva = 100.0, Kv = 6.6, C = 1.10;
    private static double IBaseKa => BaseMva / (Math.Sqrt(3.0) * Kv);

    private static GridModel Case(params Generator[] gens) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
        Generators = gens,
        Branches = Array.Empty<Branch>(),
    };

    private static Generator Sync(double xd) => new(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xd, Name: "G1");

    private static string Gen(double tdSub, double tdTrans, double ta) =>
        $"{{\"bus\":1,\"xdTransPu\":0.3,\"xdSyncPu\":1.2,\"tdSubS\":{tdSub},\"tdTransS\":{tdTrans},\"taS\":{ta}}}";

    private static Iec61363Data Data(string gens, string motors = "") =>
        Iec61363Data.Parse("{\"schema\":\"gridsim-iec61363-data/1\",\"generators\":[" + gens + "],\"motors\":[" + motors + "]}");

    [Fact]
    public void Ac_envelope_and_dc_component_match_closed_forms()
    {
        GridModel m = Case(Sync(0.2));
        double[] ts = { 0.0, 0.02, 0.05, 0.1, 0.5, 2.0 };
        Iec61363BusResult r = Iec61363Method.Compute(m, 1, Data(Gen(0.05, 1.0, 0.04)), new Iec61363Options(ts));
        Assert.Equal(Iec61363Status.Ok, r.Status);
        double i0 = C / 0.2 * IBaseKa;
        Assert.Equal(i0, r.IkppKa, 9);
        double isub = C / 0.2, itr = C / 0.3, iss = C / 1.2;
        for (int k = 0; k < ts.Length; k++)
        {
            double t = ts[k];
            double ac = ((isub - itr) * Math.Exp(-t / 0.05) + (itr - iss) * Math.Exp(-t / 1.0) + iss) * IBaseKa;
            double dc = Math.Sqrt(2.0) * i0 * Math.Exp(-t / 0.04);
            Assert.Equal(ac, r.AcKa[k], 9);
            Assert.Equal(dc, r.DcKa[k], 9);
            Assert.Equal(Math.Sqrt(ac * ac + dc * dc / 2.0), r.AsymRmsKa[k], 9);
        }
        Assert.Equal(i0, r.AcKa[0], 12);                          // no decay at t = 0
        Assert.Equal(Math.Sqrt(2.0) * i0, r.DcKa[0], 12);         // the offset starts at the AC peak
    }

    [Fact]
    public void Peak_matches_stationary_point_of_cos_minus_exponential()
    {
        // Make the AC component constant (time constants far beyond the window) so i(t) = sqrt2 I'' (cos wt - e^(-t/Ta)).
        GridModel m = Case(Sync(0.2));
        const double ta = 0.045;
        Iec61363BusResult r = Iec61363Method.Compute(m, 1, Data(Gen(1e9, 2e9, ta)), new Iec61363Options(new[] { 0.0 }));
        double i0 = C / 0.2 * IBaseKa, w = 2.0 * Math.PI * m.FrequencyHz;
        // Newton on f(t) = -w sin(wt) + e^(-t/Ta)/Ta, starting at half a cycle.
        double t = Math.PI / w;
        for (int it = 0; it < 50; it++)
        {
            double f = -w * Math.Sin(w * t) + Math.Exp(-t / ta) / ta;
            double df = -w * w * Math.Cos(w * t) - Math.Exp(-t / ta) / (ta * ta);
            t -= f / df;
        }
        double expected = Math.Sqrt(2.0) * i0 * Math.Abs(Math.Cos(w * t) - Math.Exp(-t / ta));
        Assert.Equal(expected, r.PeakKa, 6);
        Assert.Equal(t, r.PeakTimeS, 6);
        // The elementary half-cycle factor 1 + e^(-pi/(w Ta)) is the value at T/2; the true maximum is slightly earlier (where e^(-t/Ta) is larger) and so slightly higher.
        double approx = Math.Sqrt(2.0) * i0 * (1.0 + Math.Exp(-Math.PI / (w * ta)));
        Assert.InRange(r.PeakKa / approx, 1.0 - 1e-9, 1.005);
    }

    [Fact]
    public void Machine_motor_and_converter_sum_by_hand()
    {
        var conv = new Generator(Bus: 1, Pg: 0.0, MBase: 50.0, Name: "PV", Type: "solar", ConverterInterfaced: true, ConverterFaultCurrentPu: 1.2);
        GridModel m = Case(Sync(0.2), conv);
        string motor = "{\"name\":\"M1\",\"bus\":1,\"ratedMva\":10,\"lockedRotorCurrentRatio\":6,\"acTimeConstantS\":0.04,\"taS\":0.03}";
        double tb = 0.1;
        Iec61363BusResult r = Iec61363Method.Compute(m, 1, Data(Gen(0.05, 1.0, 0.06), motor), new Iec61363Options(new[] { 0.0, tb }, tb));
        Assert.Equal(Iec61363Status.Ok, r.Status);
        double gen = C / 0.2, mot = C / ((1.0 / 6.0) * 100.0 / 10.0), cv = 1.2 * 50.0 / 100.0;
        Assert.Equal((gen + mot + cv) * IBaseKa, r.IkppKa, 9);
        double genT = ((C / 0.2 - C / 0.3) * Math.Exp(-tb / 0.05) + (C / 0.3 - C / 1.2) * Math.Exp(-tb / 1.0) + C / 1.2);
        double acExpected = (genT + mot * Math.Exp(-tb / 0.04) + cv) * IBaseKa;
        double dcExpected = Math.Sqrt(2.0) * (gen * Math.Exp(-tb / 0.06) + mot * Math.Exp(-tb / 0.03)) * IBaseKa;   // converter: no DC
        Assert.Equal(acExpected, r.AcAtBreakKa, 9);
        Assert.Equal(dcExpected, r.DcAtBreakKa, 9);
        Assert.Equal(3, r.Contributions.Count);
        Assert.Contains(r.Notes, n => n.Contains("converter"));
        Assert.True(r.PeakKa > 0.0 && r.PeakKa <= Math.Sqrt(2.0) * 2.0 * r.IkppKa * 1.0000001);
    }

    [Fact]
    public void Missing_data_is_incomplete_and_never_defaulted()
    {
        GridModel m = Case(Sync(0.2));
        Iec61363BusResult r = Iec61363Method.Compute(m, 1, Iec61363Data.Empty, new Iec61363Options(new[] { 0.1 }));
        Assert.Equal(Iec61363Status.Incomplete, r.Status);
        Assert.Contains(r.Reasons, x => x.Contains("none is defaulted"));
        Assert.True(double.IsNaN(r.PeakKa));
        Assert.NotEmpty(r.Awaiting);
        // A machine with the synthesised X''d is refused too.
        Iec61363BusResult s = Iec61363Method.Compute(Case(new Generator(1, 0.0, MBase: 100.0, Name: "G1")), 1, Data(Gen(0.05, 1.0, 0.04)), new Iec61363Options(new[] { 0.1 }));
        Assert.Equal(Iec61363Status.Incomplete, s.Status);
        Assert.Equal(Iec61363Status.Failed, Iec61363Method.Compute(m, 99, Data(Gen(0.05, 1.0, 0.04)), new Iec61363Options(new[] { 0.1 })).Status);
        Assert.Equal(Iec61363Status.Failed, Iec61363Method.Compute(m, 1, Data(Gen(0.05, 1.0, 0.04)), new Iec61363Options(new[] { -1.0 })).Status);
    }

    [Fact]
    public void Breaker_margins_are_signed_and_runs_repeat_bit_identically()
    {
        GridModel m = Case(Sync(0.2));
        var opt = new Iec61363Options(new[] { 0.1 }, 0.1, Breaker: new Iec61363Breaker(50.0, 20.0));
        Iec61363BusResult a = Iec61363Method.Compute(m, 1, Data(Gen(0.05, 1.0, 0.04)), opt);
        Iec61363BusResult b = Iec61363Method.Compute(m, 1, Data(Gen(0.05, 1.0, 0.04)), opt);
        Assert.NotNull(a.Breaker);
        Assert.Equal(50.0 - a.PeakKa, a.Breaker!.MakingMarginKa, 12);
        Assert.Equal(20.0 - a.AcAtBreakKa, a.Breaker.BreakingMarginKa, 12);
        Assert.Equal(a.Breaker.MakingMarginKa >= 0 && a.Breaker.BreakingMarginKa >= 0, a.Breaker.Adequate);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PeakKa), BitConverter.DoubleToInt64Bits(b.PeakKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.AsymRmsKa[0]), BitConverter.DoubleToInt64Bits(b.AsymRmsKa[0]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"gridsim-iec61363-data/1\",\"bogus\":1}")]
    [InlineData("{\"schema\":\"gridsim-iec61363-data/1\",\"generators\":[{\"bus\":1,\"xdTransPu\":0.3,\"xdSyncPu\":1.2,\"tdSubS\":0.05,\"tdTransS\":1.0}]}")]
    [InlineData("{\"schema\":\"gridsim-iec61363-data/1\",\"generators\":[{\"bus\":1,\"xdTransPu\":1.3,\"xdSyncPu\":1.2,\"tdSubS\":0.05,\"tdTransS\":1.0,\"taS\":0.04}]}")]
    [InlineData("{\"schema\":\"gridsim-iec61363-data/1\",\"motors\":[{\"name\":\"M\",\"bus\":1,\"ratedMva\":1,\"lockedRotorCurrentRatio\":6,\"acTimeConstantS\":0.04}]}")]
    public void Strict_parser_refuses_incomplete_files(string json) => Assert.Throws<FormatException>(() => Iec61363Data.Parse(json));
}
