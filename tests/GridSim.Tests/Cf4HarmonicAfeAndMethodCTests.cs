using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Distribution;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N348 + N149 rest + N134 rest (CF4, October 2026; Ruling R1):
///   - the harmonic export records the active-front-end impedance the study stamped (N149) and
///     tools/crossval/crossval_harmonics.py stamps it back, so an --afe-reactance study can be cross-validated;
///   - the three-phase harmonic study stamps active-front-end converters as balanced three-wire shunts;
///   - IEC 60909 Method C now gives the DC component idc (fc / f by f * tb) and applies to the unbalanced duties on each
///     fault's own sequence connection, consistently with the balanced Method C of N134.
/// </summary>
public sealed class Cf4HarmonicAfeAndMethodCTests
{
    private static void SameBits(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    // ------------------------------------------------------------------ N348: the export and the sidecar

    /// <summary>case9 plus an AFE solar plant at bus 5 (MBase 50) and a line-commutated link at bus 7 (as CF3's N149 model).</summary>
    private static GridModel AfeModel()
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        return m.CopyWith(generators: m.Generators
            .Append(new Generator(5, Pg: 10, MBase: 50, Name: "afe", Type: "solar"))
            .Append(new Generator(7, Pg: 20, MBase: 200, Name: "lcc", Type: "interconnector", ConverterFaultCurrentPu: 0.0))
            .ToList());
    }

    private static string Export(GridModel m, HarmonicModelOptions opt)
    {
        var sources = new HarmonicSourceSet
        {
            Case = "case9",
            Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", 5, 0.8) },
        };
        var o = opt with { Orders = new[] { 5, 7, 11 } };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, sources.Sources, o);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        var meta = new SolveExportMeta("case9", "harmonics", "abc123", "2026-10-11T00:00:00Z", 0.0, "host");
        return HarmonicExport.ToJson(m, sources, pen, idx, meta, modelOptions: o);
    }

    [Fact]
    public void N348_without_the_option_the_export_carries_no_afe_block()
    {
        string json = Export(AfeModel(), HarmonicModelOptions.Study);
        Assert.DoesNotContain("activeFrontEnd", json, StringComparison.Ordinal);
    }

    [Fact]
    public void N348_the_export_records_every_stamped_unit_exactly_as_stamped()
    {
        GridModel m = AfeModel();
        HarmonicModelOptions opt = HarmonicModelOptions.Study with { ActiveFrontEndReactancePu = 0.15, ActiveFrontEndResistancePu = 0.004 };
        using JsonDocument doc = JsonDocument.Parse(Export(m, opt));
        JsonElement afe = doc.RootElement.GetProperty("harmonicModel").GetProperty("activeFrontEnd");
        SameBits(0.15, afe.GetProperty("reactancePu").GetDouble());
        SameBits(0.004, afe.GetProperty("resistancePu").GetDouble());
        JsonElement[] units = afe.GetProperty("units").EnumerateArray().ToArray();
        Generator[] stamped = m.Generators.Where(FrequencyDependentYBus.IsActiveFrontEndPlant).ToArray();
        Assert.Equal(stamped.Length, units.Length);
        Assert.Single(units);   // the AFE solar plant only: not the line-commutated link, not the machines
        (double r, double x) = FrequencyDependentYBus.ActiveFrontEndImpedanceSystemPu(stamped[0], m.BaseMva, 0.004, 0.15);
        Assert.Equal(5, units[0].GetProperty("bus").GetInt32());
        SameBits(r, units[0].GetProperty("rSystemPu").GetDouble());
        SameBits(x, units[0].GetProperty("xSystemPu").GetDouble());
        SameBits(0.004 * 100.0 / 50.0, r);
        SameBits(0.15 * 100.0 / 50.0, x);
    }

    [Fact]
    public void N348_an_export_with_a_bad_afe_value_is_refused_by_the_stamp_rule()
    {
        HarmonicModelOptions bad = HarmonicModelOptions.Study with { ActiveFrontEndReactancePu = 0.15, ActiveFrontEndResistancePu = -1.0 };
        Assert.Throws<ArgumentException>(() => Export(AfeModel(), bad));
    }

    [Fact]
    public void N348_the_sidecar_stamps_the_recorded_afe_impedance()
    {
        string py = File.ReadAllText(Path.Combine(CliProcess.Root(), "tools", "crossval", "crossval_harmonics.py")).Replace("\r\n", "\n");
        Assert.Contains("afe = model.get(\"activeFrontEnd\")", py, StringComparison.Ordinal);
        Assert.Contains("r, x = _f(u.get(\"rSystemPu\")), _f(u.get(\"xSystemPu\"))", py, StringComparison.Ordinal);
        Assert.Contains("Y[i, i] += 1.0 / complex(r, h * x)", py, StringComparison.Ordinal);
        Assert.Contains("raise ValueError(f\"active-front-end unit at bus", py, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ N149 rest: the three-phase study

    private static MultiPhaseModel Feeder()
    {
        var z = PhaseImpedance.Diagonal(new Complex(0.01, 0.1));
        return new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            Array.Empty<PhaseLoad>());
    }

    [Fact]
    public void N149_rest_no_afe_list_is_the_historical_study_bit_for_bit()
    {
        MultiPhaseModel f = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(5, 100.0), new HarmonicComponent(7, 50.0) });
        ThreePhasePenetrationResult a = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 5, 7 });
        ThreePhasePenetrationResult b = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 5, 7 }, activeFrontEnds: null);
        ThreePhasePenetrationResult c = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 5, 7 },
            activeFrontEnds: Array.Empty<ThreePhaseActiveFrontEnd>());
        for (int oi = 0; oi < 2; oi++)
            for (int p = 0; p < 3; p++)
            {
                SameBits(a.Voltage[oi][0][p].Real, b.Voltage[oi][0][p].Real);
                SameBits(a.Voltage[oi][0][p].Imaginary, b.Voltage[oi][0][p].Imaginary);
                SameBits(a.Voltage[oi][0][p].Real, c.Voltage[oi][0][p].Real);
                SameBits(a.Voltage[oi][0][p].Imaginary, c.Voltage[oi][0][p].Imaginary);
            }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void N149_rest_a_balanced_injection_sees_the_afe_shunt_in_parallel(int h)
    {
        // Balanced non-triplen injection on a decoupled feeder: each phase is the line admittance plus the AFE's y in
        // parallel, V = I / (1 / (R + j h X)_line + y_afe), y_afe = 1 / (R + j h X)_afe on the feeder base.
        MultiPhaseModel f = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(h, 100.0) });
        var afe = new ThreePhaseActiveFrontEnd(1, RatingMva: 50.0, ReactancePu: 0.15, ResistancePu: 0.004);
        ThreePhasePenetrationResult r = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { h }, activeFrontEnds: new[] { afe });
        double scale = f.BaseMva / 50.0;
        Complex yAfe = Complex.One / new Complex(0.004 * scale, h * 0.15 * scale);
        Complex yLine = Complex.One / new Complex(0.01, h * 0.1);
        Complex[] inj = src.InjectionAt(h);
        for (int p = 0; p < 3; p++)
        {
            Complex expected = inj[p] / (yLine + yAfe);
            Complex got = r.Voltage[0][0][p];
            Assert.True((got - expected).Magnitude <= 1e-12 * expected.Magnitude, $"phase {p}: {got} vs {expected}");
        }
    }

    [Fact]
    public void N149_rest_a_three_wire_converter_takes_no_zero_sequence_current()
    {
        // A balanced triplen is pure zero sequence: the floating-star converter must not change the response (to rounding).
        MultiPhaseModel f = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(3, 100.0) });
        ThreePhasePenetrationResult without = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 3 });
        ThreePhasePenetrationResult with = ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 3 },
            activeFrontEnds: new[] { new ThreePhaseActiveFrontEnd(1, 50.0, 0.15) });
        for (int p = 0; p < 3; p++)
        {
            Complex a = without.Voltage[0][0][p], b = with.Voltage[0][0][p];
            Assert.True((a - b).Magnitude <= 1e-12 * a.Magnitude, $"phase {p}: {a} vs {b}");
        }
    }

    [Fact]
    public void N149_rest_bad_afe_entries_are_refused_by_name()
    {
        MultiPhaseModel f = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(5, 100.0) });
        void Run(ThreePhaseActiveFrontEnd a) => ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 5 }, activeFrontEnds: new[] { a });
        Assert.Contains("source bus", Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(0, 50, 0.15))).Message);
        Assert.Contains("not in the feeder", Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(9, 50, 0.15))).Message);
        Assert.Contains("rating", Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(1, 0, 0.15))).Message);
        Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(1, 50, 0.0)));
        Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(1, 50, double.NaN)));
        Assert.Throws<ArgumentException>(() => Run(new ThreePhaseActiveFrontEnd(1, 50, 0.15, -0.01)));
        Assert.Throws<ArgumentException>(() => ThreePhaseHarmonicPenetration.Solve(f, new[] { src }, new[] { 5 },
            activeFrontEnds: new ThreePhaseActiveFrontEnd[] { null! }));
    }

    // ------------------------------------------------------------------ N134 rest: Method C for idc and unbalanced

    [Theory]
    [InlineData(0.0, 0.27)]
    [InlineData(0.999, 0.27)]
    [InlineData(1.0, 0.15)]
    [InlineData(2.499, 0.15)]
    [InlineData(2.5, 0.092)]
    [InlineData(5.0, 0.055)]
    [InlineData(9.999, 0.055)]
    public void N134_rest_the_dc_equivalent_frequency_table(double ft, double ratio)
        => SameBits(ratio, Iec60909.MethodCDcFrequencyRatio(ft));

    [Theory]
    [InlineData(10.0)]
    [InlineData(25.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void N134_rest_outside_the_table_is_refused(double ft)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.MethodCDcFrequencyRatio(ft));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void N134_rest_three_phase_method_c_gives_the_dc_component_from_its_own_ratio(int bus)
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        Iec60909.Duty b = Iec60909.ThreePhase(m, bus, f0Hz: m.FrequencyHz);
        Iec60909.Duty c = Iec60909.ThreePhase(m, bus, f0Hz: m.FrequencyHz, peakFactorMethod: PeakFactorMethod.MethodC);
        double rxDc = Iec60909.MethodCDcRoverX(null, m, bus, Iec60909.DefaultSubtransientXdPu, Iec60909.DefaultLineZeroSeqRatio,
            m.FrequencyHz, 0.10);
        SameBits(rxDc, c.DcRoverX);
        SameBits(Math.Sqrt(2.0) * c.IkppKa * GridSim.Core.Numerics.DetMath.Exp(-2.0 * Math.PI * m.FrequencyHz * 0.10 * rxDc), c.DcComponentKa);
        Assert.True(double.IsNaN(b.DcRoverX));
        // Ik'', Ib, Ith and m stay Method B's, bit for bit.
        SameBits(b.IkppKa, c.IkppKa);
        SameBits(b.BreakingIbKa, c.BreakingIbKa);
        SameBits(b.ThermalIthKa, c.ThermalIthKa);
        SameBits(b.ThermalM, c.ThermalM);
    }

    [Fact]
    public void N134_rest_method_c_beyond_the_dc_table_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.ThreePhase(GridSim.Core.IO.IeeeCases.Case9(), 5, f0Hz: 50.0,
            breakTimeSeconds: 0.2, peakFactorMethod: PeakFactorMethod.MethodC));

    /// <summary>A single series path with an earth return: generator (X''d 0.2) at 20 kV bus 1, a delta / grounded-wye
    /// step-up to 132 kV bus 2, a 132 kV line to bus 3 with its own zero-sequence impedance.</summary>
    private static GridModel Radial()
        => new()
        {
            BaseMva = 100.0,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132),
            },
            Generators = new[] { new Generator(1, Pg: 50, MBase: 100, SubtransientXdPu: 0.2, Name: "g") },
            Branches = new[]
            {
                new Branch(1, 2, 0.002, 0.1, IsTransformer: true, FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded),
                new Branch(2, 3, 0.01, 0.05, R0: 0.03, X0: 0.15),
            },
        };

    private static Iec60909.Duty Unbalanced(string type, GridModel m, int bus, PeakFactorMethod? method)
        => (type, method) switch
        {
            ("ll", null) => Iec60909.LineToLine(m, bus),
            ("slg", null) => Iec60909.SingleLineToGround(m, bus),
            ("dlg", null) => Iec60909.DoubleLineToGround(m, bus),
            ("ll", { } k) => Iec60909.LineToLine(m, bus, peakFactorMethod: k),
            ("slg", { } k) => Iec60909.SingleLineToGround(m, bus, peakFactorMethod: k),
            ("dlg", { } k) => Iec60909.DoubleLineToGround(m, bus, peakFactorMethod: k),
            _ => throw new ArgumentException(type),
        };

    private static UnbalancedFaultType TypeOf(string type) => type switch
    {
        "ll" => UnbalancedFaultType.LineToLine,
        "slg" => UnbalancedFaultType.SingleLineToGround,
        _ => UnbalancedFaultType.DoubleLineToGround,
    };

    [Theory]
    [InlineData("ll")]
    [InlineData("slg")]
    [InlineData("dlg")]
    public void N134_rest_method_b_stays_the_default_for_unbalanced_duties(string type)
    {
        GridModel m = Radial();
        Assert.Equal(Unbalanced(type, m, 3, null), Unbalanced(type, m, 3, PeakFactorMethod.MethodB));   // record equality
        Assert.Equal(PeakFactorMethod.MethodB, Unbalanced(type, m, 3, null).PeakMethod);
    }

    [Theory]
    [InlineData("ll")]
    [InlineData("slg")]
    [InlineData("dlg")]
    public void N134_rest_unbalanced_method_c_moves_the_peak_and_dc_only(string type)
    {
        GridModel m = Radial();
        Iec60909.Duty b = Unbalanced(type, m, 3, PeakFactorMethod.MethodB);
        Iec60909.Duty c = Unbalanced(type, m, 3, PeakFactorMethod.MethodC);
        UnbalancedFaultType t = TypeOf(type);
        double rxPeak = Iec60909.MethodCRoverX(t, m, 3);
        double rxDc = Iec60909.MethodCDcRoverX(t, m, 3, Iec60909.DefaultSubtransientXdPu, Iec60909.DefaultLineZeroSeqRatio,
            global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, 0.10);
        Assert.Equal(PeakFactorMethod.MethodC, c.PeakMethod);
        SameBits(rxPeak, c.PeakRoverX);
        SameBits(rxDc, c.DcRoverX);
        SameBits(Iec60909.PeakFactor(rxPeak), c.Kappa);
        SameBits(c.Kappa * Math.Sqrt(2.0) * c.IkppKa, c.PeakIpKa);   // no converter: no converter share
        Assert.False(c.MeshedPeakFactor);
        SameBits(b.IkppKa, c.IkppKa);
        SameBits(b.BreakingIbKa, c.BreakingIbKa);
        SameBits(b.ThermalIthKa, c.ThermalIthKa);
        SameBits(b.ThermalM, c.ThermalM);
        SameBits(b.RoverX, c.RoverX);
        Assert.InRange(c.Kappa, 1.02, 2.0);
    }

    [Theory]
    [InlineData("ll")]
    [InlineData("slg")]
    public void N134_rest_on_a_single_series_path_method_c_gives_the_loops_own_ratio(string type)
    {
        // Every element of the loop is in series and scaled alike, so Zc = R + j r X and R/X_C = R/X of the loop, to a few
        // ulps, for the peak (r = 0.4) and for the DC component (r from f * tb); kappa and idc then match Method B's.
        GridModel m = Radial();
        Iec60909.Duty b = Unbalanced(type, m, 3, PeakFactorMethod.MethodB);
        Iec60909.Duty c = Unbalanced(type, m, 3, PeakFactorMethod.MethodC);
        Assert.False(b.MeshedPeakFactor);
        double ulp = Math.BitIncrement(b.RoverX) - b.RoverX;
        Assert.True(Math.Abs(c.PeakRoverX - b.RoverX) <= 8 * ulp, $"peak R/X_C {c.PeakRoverX:R} vs loop {b.RoverX:R}");
        // Corrected 2026-10-11 (end pass, first run): "8 ulps of R/X" assumed the rounding of the loop's R/X is relative
        // to R/X itself. It is not: the loop impedance comes out of a nodal solve, whose real part carries a rounding error
        // relative to |Z| (about X, since R/X is 0.034 here), so the error in R/X is a few machine epsilons ABSOLUTE, and
        // one epsilon is 32 ulps of 0.034. The "ll" DC ratio (fc / f = 0.055 at f * tb = 5) came out
        // 0.03428571428571428 against the loop's 0.03428571428571421, 6.94e-17 apart (10 ulps of R/X, 0.3125 epsilon
        // absolute). The bound is now 8 epsilon times |Z| / X of the loop, sqrt(1 + (R/X)^2), in R/X units.
        // Before -> after: Math.Abs(c.DcRoverX - b.RoverX) <= 8 * ulp
        //               -> Math.Abs(c.DcRoverX - b.RoverX) <= 8 * 2^-52 * sqrt(1 + (R/X)^2) (1.78e-15 here).
        // (The peak line above keeps its 8-ulp bound: it passes as written and the result is bit-identical on every CPU.)
        double machineEps = Math.BitIncrement(1.0) - 1.0;   // 2^-52
        double rxTol = 8.0 * machineEps * Math.Sqrt(1.0 + b.RoverX * b.RoverX);
        Assert.True(Math.Abs(c.DcRoverX - b.RoverX) <= rxTol, $"DC R/X_C {c.DcRoverX:R} vs loop {b.RoverX:R}");
        Assert.True(Math.Abs(c.Kappa - b.Kappa) < 1e-14);
        Assert.True(Math.Abs(c.DcComponentKa - b.DcComponentKa) <= 1e-12 * b.DcComponentKa);
    }

    [Fact]
    public void N134_rest_an_unknown_method_is_refused_for_unbalanced_duties()
    {
        GridModel m = Radial();
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.LineToLine(m, 3, peakFactorMethod: (PeakFactorMethod)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.SingleLineToGround(m, 3, peakFactorMethod: (PeakFactorMethod)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.DoubleLineToGround(m, 3, peakFactorMethod: (PeakFactorMethod)7));
    }
}
