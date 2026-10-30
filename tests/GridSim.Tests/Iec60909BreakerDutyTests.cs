using System;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The breaker check's three October 2026 additions. GR-606: the rated voltage is compared with the highest system
/// voltage Um (IEC 60038), not the nominal voltage. GR-124: a rating with an asymmetrical breaking current is checked
/// against the DC (asymmetrical) breaking duty sqrt(Ib^2 + idc^2). GR-617: a double-line-to-earth duty, whose Ik'' is the
/// earth current 3 I0, is checked at its largest line-conductor current, against the IEC 60909-0 closed forms.
/// </summary>
public class Iec60909BreakerDutyTests
{
    private const double Kv = 132.0, BaseMva = 100.0;

    // ---- GR-606: the highest system voltage ----
    [Theory]
    [InlineData(132.0, 145.0)]
    [InlineData(400.0, 420.0)]
    [InlineData(275.0, 300.0)]
    [InlineData(345.0, 362.0)]
    [InlineData(33.0, 36.0)]
    [InlineData(11.0, 12.0)]
    [InlineData(6.6, 7.2)]
    [InlineData(66.0, 72.5)]
    [InlineData(145.0, 145.0)]   // a nominal equal to a standard Um is its own Um
    [InlineData(1500.0, 1500.0)] // above the largest standard Um: the nominal itself
    public void Highest_system_voltage_is_the_lowest_standard_um_at_or_above_the_nominal(double nominalKv, double expectedKv)
    {
        Assert.Equal(expectedKv, Iec60909.HighestSystemVoltageKv(nominalKv));
    }

    [Fact]
    public void Highest_system_voltage_at_low_voltage_is_the_nominal_plus_ten_percent()
    {
        Assert.Equal(1.1 * 0.4, Iec60909.HighestSystemVoltageKv(0.4));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-132.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Highest_system_voltage_refuses_a_nominal_that_is_not_a_positive_number(double nominalKv)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.HighestSystemVoltageKv(nominalKv));
    }

    [Fact]
    public void A_breaker_rated_at_the_nominal_voltage_fails_the_voltage_check()
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, 400.0, theveninRPu: 0.002, theveninXPu: 0.02, BaseMva, f0Hz: 50.0);
        var atNominal = new Iec60909.BreakerRating(RatedVoltageKv: 400.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: 1e9);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, atNominal, 400.0);
        Assert.False(a.VoltageOk);
        Assert.False(a.Adequate);
        Assert.Equal(420.0, a.RequiredVoltageKv);

        Iec60909.BreakerAdequacy b = Iec60909.CheckBreaker(d, atNominal with { RatedVoltageKv = 420.0 }, 400.0);
        Assert.True(b.VoltageOk);
        Assert.True(b.Adequate);
    }

    // ---- GR-124: the asymmetrical (DC) breaking check ----
    [Fact]
    public void Without_an_asymmetrical_rating_the_dc_check_is_not_made()
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva, f0Hz: 50.0);
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: 1e9);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, rating, Kv);
        Assert.False(a.AsymmetricalChecked);
        Assert.True(a.AsymmetricalOk);
        Assert.True(double.IsNaN(a.AsymmetricalMarginKa));
        Assert.True(a.Adequate);
    }

    [Fact]
    public void The_asymmetrical_rating_is_checked_against_the_rms_of_ib_and_idc()
    {
        // A low R/X keeps a large DC component at tb = 0.05 s, so the asymmetrical duty is well above Ib.
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.005, theveninXPu: 0.20, BaseMva, f0Hz: 50.0,
            breakTimeSeconds: 0.05);
        double asymDuty = Math.Sqrt(d.BreakingIbKa * d.BreakingIbKa + d.DcComponentKa * d.DcComponentKa);
        Assert.True(asymDuty > d.BreakingIbKa * 1.05);

        var ok = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: d.BreakingIbKa + 1.0,
            ShortTimeWithstandKa: 1e9, ShortTimeSeconds: 1.0, BreakingAsymKa: asymDuty + 1.0);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, ok, Kv);
        Assert.True(a.AsymmetricalChecked);
        Assert.True(a.AsymmetricalOk);
        Assert.True(a.Adequate);
        Assert.Equal(1.0, a.AsymmetricalMarginKa, 12);

        // The symmetrical rating alone passes, but the DC duty is above the asymmetrical rating: not adequate.
        Iec60909.BreakerAdequacy b = Iec60909.CheckBreaker(d, ok with { BreakingAsymKa = asymDuty - 1.0 }, Kv);
        Assert.True(b.BreakingOk);
        Assert.False(b.AsymmetricalOk);
        Assert.False(b.Adequate);
        Assert.Equal(-1.0, b.AsymmetricalMarginKa, 12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.PositiveInfinity)]
    public void An_asymmetrical_rating_that_is_not_a_positive_number_is_refused(double asymKa)
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva, f0Hz: 50.0);
        var rating = new Iec60909.BreakerRating(145.0, 1e9, 1e9, 1e9, 1.0, asymKa);
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.CheckBreaker(d, rating, Kv));
    }

    // ---- GR-617: double-line-to-earth is checked at the line-conductor current ----
    [Fact]
    public void Double_line_to_earth_ratio_matches_the_iec_closed_form()
    {
        // IEC 60909-0: I''k2EL2 = c Un |Z0 - a Z2| / |D| and I''kE2E = sqrt(3) c Un |Z2| / |D| (D the common denominator),
        // so the largest line current over the earth current is max(|Z0 - a Z2|, |Z0 - a^2 Z2|) / (sqrt(3) |Z2|).
        var z1 = new Complex(0.01, 0.10);
        var z2 = new Complex(0.012, 0.11);
        var z0 = new Complex(0.03, 0.35);
        Iec60909.Duty d = Iec60909.FromSequenceImpedances(UnbalancedFaultType.DoubleLineToGround, 1.1, Kv, z1, z2, z0, BaseMva);

        var a = new Complex(-0.5, Math.Sqrt(3.0) / 2.0);
        double expected = Math.Max((z0 - a * z2).Magnitude, (z0 - a * a * z2).Magnitude) / (Math.Sqrt(3.0) * z2.Magnitude);
        Assert.Equal(expected, d.DlgLineToEarthRatio, 12);
        Assert.True(d.DlgLineToEarthRatio > 1.0);   // a high Z0: the line currents exceed the earth current
    }

    [Fact]
    public void Other_fault_types_carry_no_line_to_earth_ratio()
    {
        var z = new Complex(0.01, 0.10);
        Assert.True(double.IsNaN(Iec60909.FromSequenceImpedances(UnbalancedFaultType.SingleLineToGround, 1.1, Kv, z, z, z, BaseMva).DlgLineToEarthRatio));
        Assert.True(double.IsNaN(Iec60909.FromSequenceImpedances(UnbalancedFaultType.LineToLine, 1.1, Kv, z, z, Complex.Zero, BaseMva).DlgLineToEarthRatio));
        Assert.True(double.IsNaN(Iec60909.FromThevenin(1.1, Kv, 0.01, 0.10, BaseMva).DlgLineToEarthRatio));
    }

    [Fact]
    public void Double_line_to_earth_breaker_demands_are_scaled_to_the_line_current()
    {
        var z1 = new Complex(0.01, 0.10);
        var z0 = new Complex(0.03, 0.35);
        Iec60909.Duty d = Iec60909.FromSequenceImpedances(UnbalancedFaultType.DoubleLineToGround, 1.1, Kv, z1, z1, z0, BaseMva);
        double r = d.DlgLineToEarthRatio;
        Assert.True(r > 1.0);

        // A rating that covers the earth current but not the line current: before GR-617 this read adequate.
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9,
            BreakingSymKa: d.BreakingIbKa * (1.0 + r) / 2.0, ShortTimeWithstandKa: 1e9);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, rating, Kv);
        Assert.False(a.BreakingOk);
        Assert.False(a.Adequate);
        Assert.Equal(rating.BreakingSymKa - r * d.BreakingIbKa, a.BreakingMarginKa, 12);
        Assert.Equal(1e9 - r * d.PeakIpKa, a.MakingMarginKa, 6);
    }

    // ---- GR-603: a fault source with no rating is refused ----
    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    public void A_machine_with_no_rating_is_refused_naming_it(double mBase)
    {
        var m = new GridModel
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: mBase, Name: "unrated") },
            Branches = Array.Empty<Branch>(),
        };
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.ThreePhase(m, 1));
        Assert.Contains("generator unrated at bus 1", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Iec60909.LineToLine(m, 1));
    }
}
