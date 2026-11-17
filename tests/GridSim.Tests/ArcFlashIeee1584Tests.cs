using System.Text.Json;
using GridSim.Core.Analysis.ArcFlash;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8 (industrial arc flash): IEEE 1584-2018 (<see cref="Ieee1584ArcFlash"/>).
/// <para>Published examples: IEEE 1584-2018 Annex D.1 (sample calculation for a medium-voltage system, 4.16 kV VCB) and
/// Annex D.2 (low-voltage system, 0.48 kV VCB). The expected numbers and their equation labels (D.9 ... D.106) are those
/// transcribed in the MIT-licensed <c>arcflash</c> library's test suite (github.com/LiaungYip/arcflash,
/// <c>ieee_1584/tests/test_example_calcs.py</c>), to the printed precision (3 decimals; boundaries to 1 mm). They were
/// not read from a copy of the standard on this machine; only the handful of numbers needed are used.</para>
/// <para>Independent re-derivation: two further cases (HOA at 13.8 kV, VCBB at 0.48 kV in a shallow enclosure) are
/// recomputed here from the equations with System.Math and the boundary from <c>D (5.0208/E)^(1/k12)</c>, a different
/// algebraic route from the implementation's equations 7-10. The coefficients are the same transcription, so this
/// checks the arithmetic and the equation wiring, not the coefficient values.</para>
/// </summary>
public sealed class ArcFlashIeee1584Tests
{
    private static ArcFlashRequest Req(double voc, ElectrodeConfiguration ec, double g, double d, ArcFlashEnclosure? enc, double ibf, double tFull, double tRed,
        double f = 60, int phases = 3) =>
        new("test", voc, ec, g, d, enc, new BoltedFaultInput(ibf, "declared for test"), new ClearingTimeInput(tFull, tRed, "declared for test"), f, phases);

    private static void Near(double expected, double? actual, double tol) =>
        Assert.True(actual is double a && Math.Abs(a - expected) <= tol, $"expected {expected} +- {tol}, got {actual}");

    [Fact]
    public void Annex_D1_medium_voltage_example()
    {
        ArcFlashResult r = Ieee1584ArcFlash.Calculate(Req(4.16, ElectrodeConfiguration.VCB, 104, 914.4, new ArcFlashEnclosure(1143, 762, 508), 15.0, 197, 223));
        Assert.Equal("EVALUATED", r.Status);
        const double kA = 5e-4, J = 5e-4, mm = 0.5;
        Near(11.117, r.Full!.IntermediateArcingCurrentKa[0], kA);    // D.9
        Near(12.816, r.Full.IntermediateArcingCurrentKa[1], kA);     // D.11
        Near(14.116, r.Full.IntermediateArcingCurrentKa[2], kA);     // D.13
        Near(12.979, r.Full.ArcingCurrentKa, kA);                    // D.17
        Near(27.632, r.Enclosure!.Width1In, 5e-4);                   // D.19
        Near(45.0, r.Enclosure.Height1In, 5e-4);                     // D.20
        Near(36.316, r.Enclosure.EesIn, 5e-4);                       // D.21
        Near(1.284, r.Enclosure.Cf, 5e-4);                           // D.22
        Near(8.652, r.Full.IntermediateIncidentEnergyJPerCm2[0], J); // D.24
        Near(11.977, r.Full.IntermediateIncidentEnergyJPerCm2[1], J);// D.26
        Near(13.367, r.Full.IntermediateIncidentEnergyJPerCm2[2], J);// D.28
        Near(12.152, r.Full.IncidentEnergyJPerCm2, J);               // D.32
        Near(1285, r.Full.IntermediateBoundaryMm[0], mm);            // D.34
        Near(1591, r.Full.IntermediateBoundaryMm[1], mm);            // D.36
        Near(1707, r.Full.IntermediateBoundaryMm[2], mm);            // D.38
        Near(1606, r.Full.ArcFlashBoundaryMm, mm);                   // D.42
        Near(0.047, r.VarCf, 5e-4);                                  // D.43
        Near(10.856, r.Reduced!.IntermediateArcingCurrentKa[0], kA); // D.45
        Near(12.515, r.Reduced.IntermediateArcingCurrentKa[1], kA);  // D.46
        Near(13.786, r.Reduced.IntermediateArcingCurrentKa[2], kA);  // D.47
        Near(12.675, r.Reduced.ArcingCurrentKa, kA);                 // D.51
        Near(8.980, r.Reduced.IntermediateIncidentEnergyJPerCm2[0], J);  // D.54
        Near(13.018, r.Reduced.IntermediateIncidentEnergyJPerCm2[1], J); // D.56
        Near(15.602, r.Reduced.IntermediateIncidentEnergyJPerCm2[2], J); // D.58
        Near(13.343, r.Reduced.IncidentEnergyJPerCm2, J);            // D.62
        Near(1316, r.Reduced.IntermediateBoundaryMm[0], mm);         // D.64
        Near(1678, r.Reduced.IntermediateBoundaryMm[1], mm);         // D.66
        Near(1884, r.Reduced.IntermediateBoundaryMm[2], mm);         // D.68
        Near(1704, r.Reduced.ArcFlashBoundaryMm, mm);                // D.72
        // The reduced current with its longer clearing time governs.
        Assert.Equal("reduced", r.Governing);
        Assert.Equal(r.Reduced.IncidentEnergyJPerCm2, r.IncidentEnergyJPerCm2);
        Assert.Equal(223, r.GoverningClearingTimeMs);
        Assert.Equal(r.IncidentEnergyJPerCm2!.Value / 4.184, r.IncidentEnergyCalPerCm2!.Value, 14);
    }

    [Fact]
    public void Annex_D2_low_voltage_example()
    {
        ArcFlashResult r = Ieee1584ArcFlash.Calculate(Req(0.48, ElectrodeConfiguration.VCB, 32, 609.6, new ArcFlashEnclosure(610, 610, 254), 45.0, 61.3, 319));
        Assert.Equal("EVALUATED", r.Status);
        Near(32.449, r.Full!.IntermediateArcingCurrentKa[0], 5e-4);  // D.82
        Near(28.793, r.Full.ArcingCurrentKa, 5e-4);                   // D.84
        Near(24.016, r.Enclosure!.Width1In, 5e-4);                    // D.86
        Near(24.016, r.Enclosure.Height1In, 5e-4);                    // D.87
        Near(24.016, r.Enclosure.EesIn, 5e-4);                        // D.88
        Near(1.085, r.Enclosure.Cf, 5e-4);                            // D.89
        Near(11.585, r.Full.IncidentEnergyJPerCm2, 5e-4);             // D.91
        Near(1029, r.Full.ArcFlashBoundaryMm, 0.5);                   // D.95
        Near(0.247, r.VarCf, 5e-4);                                   // D.96
        Near(25.244, r.Reduced!.ArcingCurrentKa, 5e-4);               // D.99
        Near(53.156, r.Reduced.IncidentEnergyJPerCm2, 5e-4);          // D.103
        Near(2669, r.Reduced.ArcFlashBoundaryMm, 0.5);                // D.106
        Assert.Equal("Typical", r.Enclosure.EnclosureType);
        Assert.Null(r.Full.IntermediateArcingCurrentKa[1]);
        Assert.Equal("reduced", r.Governing);
    }

    // ---- independent re-derivation (System.Math, boundary by D (5.0208/E)^(1/k12)) --------------------------------

    private static double Lg(double x) => Math.Log10(x);
    private static double P10(double x) => Math.Pow(10.0, x);

    // Equation 1 with a Table 1 row (k1..k10).
    private static double Iarc(double[] k, double ibf, double g) =>
        P10(k[0] + k[1] * Lg(ibf) + k[2] * Lg(g)) *
        (k[3] * Math.Pow(ibf, 6) + k[4] * Math.Pow(ibf, 5) + k[5] * Math.Pow(ibf, 4) + k[6] * Math.Pow(ibf, 3) + k[7] * ibf * ibf + k[8] * ibf + k[9]);

    // Equations 3-6 with a Table 3/4/5 row (k1..k13).
    private static double Energy(double[] k, double t, double g, double iNum, double ibf, double iArc, double d, double cf)
    {
        double den = k[3] * Math.Pow(ibf, 7) + k[4] * Math.Pow(ibf, 6) + k[5] * Math.Pow(ibf, 5) + k[6] * Math.Pow(ibf, 4) + k[7] * Math.Pow(ibf, 3) + k[8] * ibf * ibf + k[9] * ibf;
        return 12.552 / 50 * t * P10(k[0] + k[1] * Lg(g) + k[2] * iNum / den + k[10] * Lg(ibf) + k[11] * Lg(d) + k[12] * Lg(iArc) + Lg(1 / cf));
    }

    private static double Boundary(double e, double d, double k12) => d * Math.Pow(5.0208 / e, 1.0 / k12);

    [Fact]
    public void Open_air_13_8_kV_matches_an_independent_re_derivation()
    {
        double voc = 13.8, ibf = 20.0, g = 152.0, d = 914.4, t = 150.0;
        double[][] t1 =
        [
            [0.111147, 1.008, -0.24, 0, 0, -3.895E-09, 1.641E-06, -0.000197, 0.002615, 1.1],
            [0.000435, 1.006, -0.038, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981],
            [0.000904, 0.999, -0.02, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981],
        ];
        double[][] te =
        [
            [3.470417, 0.465, -0.261863, 0, 0, -3.895E-09, 0.000001641, -0.000197, 0.002615, 1.1, 0, -1.99, 1.04],
            [3.616266, 0.149, -0.761561, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981, 0, -1.639, 1.078],
            [2.04049, 0.177, 1.005092, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981, -0.05, -1.633, 1.151],
        ];
        double[] i = t1.Select(k => Iarc(k, ibf, g)).ToArray();
        double[] e = Enumerable.Range(0, 3).Select(j => Energy(te[j], t, g, i[j], ibf, i[j], d, 1.0)).ToArray();
        double[] b = Enumerable.Range(0, 3).Select(j => Boundary(e[j], d, te[j][11])).ToArray();
        // Equation 17 (Voc > 2.7 kV): straight-line extrapolation through the 2700 and 14300 V values.
        double Eq17(double[] x) => (x[2] - x[1]) / 11.6 * (voc - 14.3) + x[2];

        ArcFlashResult r = Ieee1584ArcFlash.Calculate(Req(voc, ElectrodeConfiguration.HOA, g, d, null, ibf, t, t));
        Assert.Equal("EVALUATED", r.Status);
        Assert.Equal("Open air", r.Enclosure!.EnclosureType);
        Assert.Equal(1.0, r.Enclosure.Cf);
        Assert.Equal(Eq17(i), r.Full!.ArcingCurrentKa, 9);
        Assert.Equal(Eq17(e), r.Full.IncidentEnergyJPerCm2, 9);
        Assert.Equal(Eq17(b), r.Full.ArcFlashBoundaryMm, 6);
        double varCf = 0 * Math.Pow(voc, 6) - 3.1555e-06 * Math.Pow(voc, 5) + 0.0001682 * Math.Pow(voc, 4) - 0.0034607 * Math.Pow(voc, 3)
                       + 0.034124 * voc * voc - 0.1599 * voc + 0.34629;
        Assert.Equal(varCf, r.VarCf!.Value, 12);
        double[] ir = i.Select(x => x * (1 - 0.5 * varCf)).ToArray();
        Assert.Equal(Eq17(ir), r.Reduced!.ArcingCurrentKa, 9);
        double[] er = Enumerable.Range(0, 3).Select(j => Energy(te[j], t, g, ir[j], ibf, ir[j], d, 1.0)).ToArray();
        Assert.Equal(Eq17(er), r.Reduced.IncidentEnergyJPerCm2, 9);
    }

    [Fact]
    public void Shallow_low_voltage_VCBB_matches_an_independent_re_derivation()
    {
        double voc = 0.48, ibf = 30.0, g = 25.0, d = 457.2, tf = 50.0, tr = 120.0;
        var enc = new ArcFlashEnclosure(355.6, 304.8, 100.0);
        double[] k1 = [-0.017432, 0.98, -0.05, 0, 0, -5.767E-09, 2.524E-06, -0.00034, 0.01187, 1.013];
        double[] k3 = [3.068459, 0.26, -0.098107, 0, 0, -5.767E-09, 0.000002524, -0.00034, 0.01187, 1.013, -0.06, -1.809, 1.19];
        double i600 = Iarc(k1, ibf, g);
        double iFull = 1 / Math.Sqrt((0.6 / voc) * (0.6 / voc) * (1 / (i600 * i600) - (0.36 - voc * voc) / (0.36 * ibf * ibf)));   // equation 25
        double varCf = 1.138e-06 * Math.Pow(voc, 6) - 6.0287e-05 * Math.Pow(voc, 5) + 0.0012758 * Math.Pow(voc, 4) - 0.013778 * Math.Pow(voc, 3)
                       + 0.080217 * voc * voc - 0.24066 * voc + 0.33524;
        // Shallow (Voc < 0.6 kV, height and width < 508 mm, depth <= 203.2 mm): dimensions times 0.03937, CF = 1/(b1 EES^2 + b2 EES + b3).
        double ees = (0.03937 * 355.6 + 0.03937 * 304.8) / 2;
        double cf = 1 / (-0.002778 * ees * ees + 0.1194 * ees - 0.2778);
        double eFull = Energy(k3, tf, g, i600, ibf, iFull, d, cf);
        double iRed = iFull * (1 - 0.5 * varCf);
        double eRed = Energy(k3, tr, g, i600, ibf, iRed, d, cf);

        ArcFlashResult r = Ieee1584ArcFlash.Calculate(Req(voc, ElectrodeConfiguration.VCBB, g, d, enc, ibf, tf, tr, f: 50));
        Assert.Equal("EVALUATED", r.Status);
        Assert.Equal("Shallow", r.Enclosure!.EnclosureType);
        Assert.Equal(ees, r.Enclosure.EesIn!.Value, 12);
        Assert.Equal(cf, r.Enclosure.Cf, 12);
        Assert.Equal(i600, r.Full!.IntermediateArcingCurrentKa[0]!.Value, 10);
        Assert.Equal(iFull, r.Full.ArcingCurrentKa, 10);
        Assert.Equal(iRed, r.Reduced!.ArcingCurrentKa, 10);
        Assert.Equal(eFull, r.Full.IncidentEnergyJPerCm2, 9);
        Assert.Equal(eRed, r.Reduced.IncidentEnergyJPerCm2, 9);
        Assert.Equal(Boundary(eFull, d, k3[11]), r.Full.ArcFlashBoundaryMm, 6);
        Assert.Equal(Boundary(eRed, d, k3[11]), r.Reduced.ArcFlashBoundaryMm, 6);
        Assert.Equal(eRed > eFull ? "reduced" : "full", r.Governing);
    }

    [Fact]
    public void Inputs_outside_the_envelope_are_out_of_scope_with_every_breach_listed()
    {
        ArcFlashResult r = Ieee1584ArcFlash.Calculate(Req(16.0, ElectrodeConfiguration.VCB, 10, 300, null, 70, 100, 0, f: 55, phases: 1));
        Assert.Equal("OUT OF SCOPE", r.Status);
        Assert.Null(r.IncidentEnergyJPerCm2);
        Assert.Null(r.Full);
        string all = string.Join("\n", r.OutOfScope);
        foreach (string s in new[] { "three-phase", "55 Hz", "16 kV", "70 kA", "gap 10 mm", "below 305 mm", "needs the enclosure", "reducedArcingCurrentMs" })
            Assert.Contains(s, all, StringComparison.Ordinal);

        Assert.Equal("OUT OF SCOPE", Ieee1584ArcFlash.Calculate(Req(0.2, ElectrodeConfiguration.HOA, 25, 457, null, 20, 50, 50)).Status);
        Assert.Contains(Ieee1584ArcFlash.Calculate(Req(0.48, ElectrodeConfiguration.VOA, 25, 457, new ArcFlashEnclosure(500, 500, 200), 20, 50, 50)).OutOfScope,
            s => s.Contains("takes no enclosure", StringComparison.Ordinal));
        Assert.Contains(Ieee1584ArcFlash.Calculate(Req(0.48, ElectrodeConfiguration.VCB, 76, 457, new ArcFlashEnclosure(500, 300, 200), 20, 50, 50)).OutOfScope,
            s => s.Contains("4 times the gap", StringComparison.Ordinal));
        // The limits themselves are inside.
        Assert.Equal("EVALUATED", Ieee1584ArcFlash.Calculate(Req(0.208, ElectrodeConfiguration.VCB, 6.35, 305, new ArcFlashEnclosure(508, 508, 508), 0.5, 10, 10)).Status);
        Assert.Equal("EVALUATED", Ieee1584ArcFlash.Calculate(Req(15.0, ElectrodeConfiguration.HCB, 254, 305, new ArcFlashEnclosure(1244.6, 1244.6, 1244.6), 65, 10, 10, f: 50)).Status);
    }

    [Fact]
    public void Bolted_fault_from_iec60909_is_read_only_and_identical_to_the_solver_output()
    {
        GridModel baseCase = IeeeCases.Case9();
        GridModel m = baseCase.CopyWith(buses: baseCase.Buses.Select(b => b with { BaseKv = 13.8 }).ToArray());
        string before = JsonSerializer.Serialize(m);
        Iec60909.Duty duty = Iec60909.ThreePhase(m, 5, ShortCircuitObjective.Maximum);
        BoltedFaultInput f = Ieee1584ArcFlash.FromIec60909(m, 5, ShortCircuitObjective.Maximum);
        Assert.Equal(BitConverter.DoubleToInt64Bits(duty.IkppKa), BitConverter.DoubleToInt64Bits(f.Ka));
        Assert.Equal(13.8, f.Iec60909!.BusBaseKv);
        Assert.Equal("Maximum", f.Iec60909.Objective);
        Assert.Equal(duty.RoverX, f.Iec60909.RoverX);
        Assert.Equal(before, JsonSerializer.Serialize(m));   // the case is not touched
        // A second solver call after the arc-flash read gives the same duty (no shared state).
        Assert.Equal(duty, Iec60909.ThreePhase(m, 5, ShortCircuitObjective.Maximum));
        Assert.Throws<ArgumentException>(() => Ieee1584ArcFlash.FromIec60909(m, 999, ShortCircuitObjective.Maximum));
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        ArcFlashRequest q = Req(4.16, ElectrodeConfiguration.VCBB, 104, 914.4, new ArcFlashEnclosure(1143, 762, 508), 15.0, 197, 223);
        Assert.Equal(JsonSerializer.Serialize(Ieee1584ArcFlash.Calculate(q)), JsonSerializer.Serialize(Ieee1584ArcFlash.Calculate(q)));
    }
}
