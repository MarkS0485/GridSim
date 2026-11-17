using System.Text.Json;
using GridSim.Core.Analysis.CableRating;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8 (cable thermal sizing): the IEC 60287 steady-state rating of one single-core cable laid direct
/// (<see cref="Iec60287CableRating"/>). The arithmetic is re-derived here from the standard's equations, written out
/// independently with System.Math; the nameplate rating is checked to stay separate evidence. A published ampacity
/// example with matching assumptions (for example CIGRE TB 880) is not available on this machine: that check is
/// deferred, and the result says so.
/// </summary>
public sealed class CableRatingIec60287Tests
{
    // 1 x 400 mm2 Cu, XLPE 18/30 kV, copper wire screen, PE oversheath: illustrative dimensions (not a datasheet).
    private static CableConstruction Cable(double r20 = 0.0470, ConductorMaterial m = ConductorMaterial.Copper) =>
        new(m, r20, 1.0, 23.3, 42.5, 44.5, 50.5, 3.5, 3.5, 2.5, 0.004, 90.0);

    private static CableRatingRequest Req(CableConstruction? c = null, double depth = 0.8, double rhoSoil = 1.0, double ambient = 20.0, double lambda1 = 0.0,
        NameplateRating? np = null, RatingSelection sel = RatingSelection.None, double f = 50.0) =>
        new("test", c ?? Cable(), new CableSystem(18.0, f), new BuriedInstallation(depth, rhoSoil, ambient),
            new ScreenLossInput(lambda1, "declared for test (single-point bonded, eddy loss neglected)"), np, sel);

    [Fact]
    public void Rating_matches_an_independent_re_derivation_of_IEC_60287()
    {
        CableRatingResult r = Iec60287CableRating.Calculate(Req(lambda1: 0.02));
        Assert.Equal("EVALUATED", r.Status);
        CableRatingWorking w = r.Working!;

        // IEC 60287-1-1 2.1: R' = R20 (1 + alpha20 (theta - 20)); xs^2 = 8 pi f / R' 1e-7 ks; ys = xs^4/(192 + 0.8 xs^4).
        double rDc = 0.0470e-3 * (1 + 3.93e-3 * (90 - 20));
        double xs2 = 8 * Math.PI * 50 / rDc * 1e-7 * 1.0;
        double ys = xs2 * xs2 / (192 + 0.8 * xs2 * xs2);
        double rAc = rDc * (1 + ys);
        // 2.2: C = eps / (18 ln(Di/dc)) 1e-9; Wd = omega C U0^2 tan delta.
        double c = 2.5 / (18 * Math.Log(42.5 / 23.3)) * 1e-9;
        double wd = 2 * Math.PI * 50 * c * 18e3 * 18e3 * 0.004;
        // IEC 60287-2-1: T1 = rho1/(2 pi) ln(1 + 2 t1/dc); T3 = rho3/(2 pi) ln(1 + 2 t3/Ds); T4 = rho/(2 pi) ln(u + sqrt(u^2 - 1)).
        double t1 = 3.5 / (2 * Math.PI) * Math.Log(1 + 2 * ((42.5 - 23.3) / 2) / 23.3);
        double t3 = 3.5 / (2 * Math.PI) * Math.Log(1 + 2 * ((50.5 - 44.5) / 2) / 44.5);
        double u = 2 * 0.8 / 0.0505;
        double t4 = 1.0 / (2 * Math.PI) * Math.Log(u + Math.Sqrt(u * u - 1));
        // 1.4.1.1, n = 1, T2 = 0, lambda2 = 0.
        double i = Math.Sqrt((70 - wd * (0.5 * t1 + t3 + t4)) / (rAc * t1 + rAc * (1 + 0.02) * (t3 + t4)));

        void Rel(double expected, double actual) => Assert.True(Math.Abs(actual - expected) <= 1e-12 * Math.Abs(expected), $"{expected:R} vs {actual:R}");
        Rel(rDc, w.DcResistanceAtMaxOhmPerM);
        Rel(xs2, w.SkinEffectXs2);
        Rel(ys, w.SkinEffectYs);
        Rel(rAc, w.AcResistanceOhmPerM);
        Rel(c, w.CapacitanceFPerM);
        Rel(wd, w.DielectricLossWPerM);
        Rel(t1, w.T1KmPerW);
        Rel(t3, w.T3KmPerW);
        Rel(u, w.U);
        Rel(t4, w.T4KmPerW);
        Rel(i, r.CalculatedRatingA!.Value);
        Assert.Equal(0.0, w.T2KmPerW);
        Assert.Equal(0.0, w.ProximityEffectYp);
        Assert.Equal(70.0, w.PermissibleRiseK);
        Rel(i * i * rAc, w.ConductorLossAtRatingWPerM);
        Rel(0.02 * i * i * rAc, w.ScreenLossAtRatingWPerM);
        // Energy balance: the conductor temperature rise rebuilt from the losses and thermal resistances is 70 K.
        double wc = i * i * rAc;
        double rise = (wc + 0.5 * wd) * t1 + (wc * 1.02 + wd) * (t3 + t4);
        Assert.Equal(70.0, rise, 9);
        Assert.InRange(r.CalculatedRatingA!.Value, 900, 1200);   // plausibility only: an isolated 400 mm2 Cu cable in 1 K.m/W soil
    }

    [Fact]
    public void Rating_responds_to_installation_inputs_in_the_physical_direction()
    {
        double baseA = Iec60287CableRating.Calculate(Req()).CalculatedRatingA!.Value;
        Assert.True(Iec60287CableRating.Calculate(Req(rhoSoil: 2.5)).CalculatedRatingA < baseA);
        Assert.True(Iec60287CableRating.Calculate(Req(depth: 1.5)).CalculatedRatingA < baseA);
        Assert.True(Iec60287CableRating.Calculate(Req(ambient: 30)).CalculatedRatingA < baseA);
        Assert.True(Iec60287CableRating.Calculate(Req(lambda1: 0.1)).CalculatedRatingA < baseA);
        Assert.True(Iec60287CableRating.Calculate(Req(c: Cable(0.0778, ConductorMaterial.Aluminium))).CalculatedRatingA < baseA);
        Assert.True(Iec60287CableRating.Calculate(Req(f: 60)).CalculatedRatingA < baseA);   // more skin effect and dielectric loss
    }

    [Fact]
    public void Nameplate_and_calculated_ratings_stay_separate_until_a_selection_is_made()
    {
        var np = new NameplateRating(950, "asset register (declared for test)");
        CableRatingResult none = Iec60287CableRating.Calculate(Req(np: np));
        Assert.Null(none.SelectedRatingA);
        Assert.Null(none.SelectedBasis);
        Assert.Equal(950, none.Nameplate!.Amperes);
        double calc = none.CalculatedRatingA!.Value;
        Assert.Equal(950 / calc, none.NameplateOverCalculated!.Value, 14);
        // The nameplate does not enter the calculation.
        Assert.Equal(calc, Iec60287CableRating.Calculate(Req()).CalculatedRatingA!.Value);

        Assert.Equal(calc, Iec60287CableRating.Calculate(Req(np: np, sel: RatingSelection.Calculated)).SelectedRatingA);
        Assert.Equal(950, Iec60287CableRating.Calculate(Req(np: np, sel: RatingSelection.Nameplate)).SelectedRatingA);
        CableRatingResult lower = Iec60287CableRating.Calculate(Req(np: np, sel: RatingSelection.Lower));
        Assert.Equal(Math.Min(950, calc), lower.SelectedRatingA);
        Assert.StartsWith("lower of the two", lower.SelectedBasis, StringComparison.Ordinal);
        Assert.Equal("OUT OF SCOPE", Iec60287CableRating.Calculate(Req(sel: RatingSelection.Nameplate)).Status);
    }

    [Fact]
    public void Inputs_outside_the_method_are_out_of_scope()
    {
        // A 2500 mm2 conductor: xs about 3.7, beyond the implemented skin-effect formula.
        CableRatingResult big = Iec60287CableRating.Calculate(Req(c: Cable(0.0072)));
        Assert.Equal("OUT OF SCOPE", big.Status);
        Assert.Contains(big.OutOfScope, s => s.Contains("exceeds 2.8", StringComparison.Ordinal));
        Assert.Null(big.CalculatedRatingA);
        Assert.Contains(Iec60287CableRating.Calculate(Req(depth: 0.02)).OutOfScope, s => s.Contains("fully buried", StringComparison.Ordinal));
        Assert.Contains(Iec60287CableRating.Calculate(Req(f: 55)).OutOfScope, s => s.Contains("50 or 60", StringComparison.Ordinal));
        Assert.Contains(Iec60287CableRating.Calculate(Req(ambient: 95)).OutOfScope, s => s.Contains("exceed the ambient", StringComparison.Ordinal));
        var disordered = Cable() with { ScreenDiameterMm = 40.0 };
        Assert.Contains(Iec60287CableRating.Calculate(Req(c: disordered)).OutOfScope, s => s.Contains("diameters", StringComparison.Ordinal));
        Assert.Contains(Iec60287CableRating.Calculate(Req(c: Cable() with { LossTangent = double.NaN })).OutOfScope, s => s.Contains("lossTangent", StringComparison.Ordinal));
        Assert.Contains(Iec60287CableRating.DeferredText, s => s.Contains("Published ampacity reference", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        CableRatingRequest q = Req(lambda1: 0.05, np: new NameplateRating(900, "x"), sel: RatingSelection.Lower);
        Assert.Equal(JsonSerializer.Serialize(Iec60287CableRating.Calculate(q)), JsonSerializer.Serialize(Iec60287CableRating.Calculate(q)));
    }
}
