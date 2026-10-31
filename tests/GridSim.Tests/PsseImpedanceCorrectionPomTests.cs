using GridSim.Core.IO;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #5, PSS/E impedance correction tables, checked against the PSS/E-30.1 Program Operation Manual, Vol. I,
/// s.4.1.1.11: (1) the abscissa of a ratio table is the winding's off-nominal turns ratio in pu of its BUS base voltage,
/// also when the winding's nominal voltage NOMV differs from the bus base; (2) whether a table holds ratios or angles is
/// decided from the table (angle when the first T is below 0.5 or the last above 1.5), not from the winding's COD.
/// </summary>
public class PsseImpedanceCorrectionPomTests
{
    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static string WithTable(string text, string table) => text.Replace("\r\n", "\n")
        .Replace(", 1.03, 0.97, 21, 0, 0, 0, 0\n", ", 1.03, 0.97, 21, 1, 0, 0, 0\n")
        .Replace("0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n", $"0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n1, {table}\n");

    [Fact]
    public void A_ratio_table_reads_the_ratio_on_the_bus_base_when_NOMV_differs_from_it()
    {
        // CW 2 (winding voltages in kV): WINDV1 125.4 kV on a 132 kV bus whose winding is rated NOMV1 125.4 kV. On the bus
        // base the ratio is 125.4 / 132 = 0.95 (PSS/E's abscissa); on the winding's own nominal it would be 1.0. The table
        // gives F 1.1 at 0.95 and F 1.0 at 1.0, so the two readings differ and the bus-base one must win.
        string text = PsseTapControlTests.TwoBus(100, 50, cod: 0).Replace("\r\n", "\n")
            .Replace("\n1, 2, 0, '1', 1, 1, 1,", "\n1, 2, 0, '1', 2, 1, 1,")
            .Replace("\n1, 0, 0, 200, 200, 200, ", "\n125.4, 125.4, 0, 200, 200, 200, ")
            .Replace("\n1, 0\n", "\n33, 0\n");
        var r = PsseRawParser.ParseWithDiagnostics(WithTable(text, "0.9, 1.2, 0.95, 1.1, 1.0, 1.0, 1.1, 0.8"), "nomv", new PsseReadOptions { FrequencyHz = 50 });
        Branch t = r.Model.Branches[0];
        AssertSameBits(125.4 / 132 / (33.0 / 33.0), t.TapRatio, "tap ratio");
        double w = 125.4 / 132;
        double f = 1.2 + (w - 0.9) / (0.95 - 0.9) * (1.1 - 1.2);   // w is 0.95 to the last bit or a hair either side
        if (w > 0.95) f = 1.1 + (w - 0.95) / (1.0 - 0.95) * (1.0 - 1.1);
        Assert.True(Math.Abs(f - 1.1) < 1e-12, $"factor {f:R} should be the 0.95 point's 1.1");
        Assert.True(Math.Abs(t.X - 0.1 * 1.1) < 1e-12, $"X {t.X:R}: the factor must be read at the bus-base ratio 0.95 (F 1.1), not at 1.0 (F 1.0)");
    }

    [Fact]
    public void An_uncontrolled_phase_shifter_reads_an_angle_table_at_its_angle()
    {
        // COD 0 with ANG1 = 10 degrees and an angle table (-30 .. 30, so the POM calls it an angle table). Before, GridSim
        // read the table at the ratio (1.0, inside -30..30, F about 1.0 + ...); PSS/E reads it at the angle.
        string text = PsseTapControlTests.TwoBus(100, 50, cod: 0).Replace("\r\n", "\n")
            .Replace("\n1, 0, 0, 200, 200, 200, ", "\n1, 0, 10, 200, 200, 200, ");
        var r = PsseRawParser.ParseWithDiagnostics(WithTable(text, "-30, 1.3, 0, 1.0, 30, 1.3"), "pst", new PsseReadOptions { FrequencyHz = 50 });
        Branch t = r.Model.Branches[0];
        Assert.Equal(10.0, t.PhaseShiftDeg);
        double f = 1.0 + (10.0 - 0.0) / (30.0 - 0.0) * (1.3 - 1.0);
        AssertSameBits(0.1 * f, t.X, "X at 10 degrees");
    }

    [Fact]
    public void A_ratio_controlling_winding_with_an_angle_table_is_refused_with_tap_control_on()
    {
        string text = WithTable(PsseTapControlTests.TwoBus(100, 50, cod: 1), "-30, 1.3, 0, 1.0, 30, 1.3");
        var ex = Assert.ThrowsAny<Exception>(() => PsseRawParser.ParseWithDiagnostics(text, "bad", new PsseReadOptions { FrequencyHz = 50, ApplyTapControl = true }));
        Assert.Contains("phase shift angle table", ex.Message, StringComparison.Ordinal);
        Assert.Contains("POM s.4.1.1.11", ex.Message, StringComparison.Ordinal);
        // With tap control off the same file reads, the table applied at the fixed setting (angle 0: F 1.0).
        var r = PsseRawParser.ParseWithDiagnostics(text, "bad", new PsseReadOptions { FrequencyHz = 50 });
        AssertSameBits(0.1, r.Model.Branches[0].X, "X at angle 0");
    }

    [Fact]
    public void A_winding_with_its_control_switched_off_reads_a_mismatched_table_at_its_fixed_setting()
    {
        // COD -1 (ratio control switched off) builds no tap control, so an angle table on it is not refused even with
        // tap control on: it is read at the winding's fixed angle, 0 degrees here (F 1.0).
        string text = WithTable(PsseTapControlTests.TwoBus(100, 50, cod: -1), "-30, 1.3, 0, 1.0, 30, 1.3");
        var r = PsseRawParser.ParseWithDiagnostics(text, "off", new PsseReadOptions { FrequencyHz = 50, ApplyTapControl = true });
        AssertSameBits(0.1, r.Model.Branches[0].X, "X at angle 0");
    }
}
