using System.Text;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Comtrade;
using GridSim.Core.IO.Psse;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt rulings 14 to 19 (Mark, 26 November): one regression test per ruling that changes code. Ruling 14 (DYN-18 pi
/// fallback) and ruling 15 (DYN-02) are also covered where their old refusals were pinned (re-pinned there with proofs).
/// </summary>
public sealed class BugHunt1125RulingsTests
{
    private const string Cfg1991Ascii = "STN,DEV,1991\n1,1A,0D\n1,IA,A,,A,1,0,0,-32768,32767\n50\n1\n1000,2\n01/01/2020,00:00:00.000000\n01/01/2020,00:00:00.000000\nASCII\n";
    private const string Cfg1999Ascii = "STN,DEV,1999\n1,1A,0D\n1,IA,A,,A,1,0,0,-32768,32767,1,1,P\n50\n1\n1000,2\n01/01/2020,00:00:00.000000\n01/01/2020,00:00:00.000000\nASCII\n1\n";

    [Fact]
    public void Ruling19_99999_is_missing_from_revision_1999_on_and_data_in_1991()
    {
        byte[] dat = Encoding.ASCII.GetBytes("1,0,5\n2,1000,99999\n");
        ComtradeRecording old = ComtradeReader.Parse(Cfg1991Ascii, dat);
        Assert.Equal(99999.0, old.Analog[0][1]);
        Assert.Equal(0, old.MissingValues);
        ComtradeRecording newer = ComtradeReader.Parse(Cfg1999Ascii, dat);
        Assert.True(double.IsNaN(newer.Analog[0][1]));
        Assert.Equal(1, newer.MissingValues);
    }

    [Fact]
    public void Ruling19_nanosecond_stamps_follow_the_start_time_precision()
    {
        string cfg = Cfg1999Ascii.Replace("00:00:00.000000\n01/01/2020,00:00:00.000000", "00:00:00.000000000\n01/01/2020,00:00:00.000000000", StringComparison.Ordinal);
        ComtradeRecording r = ComtradeReader.Parse(cfg, Encoding.ASCII.GetBytes("1,0,5\n2,1000,6\n"));
        Assert.Equal(1e-6, r.TimesSeconds[1], 15);   // 1000 ns, not 1000 us
    }

    [Fact]
    public void Ruling18_a_non_empty_diagnostic_port_is_refused_and_an_empty_one_is_not()
    {
        string? refused = TelemetryGuard.CheckEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal) { ["DOTNET_DiagnosticPorts"] = "/tmp/port,suspend" });
        Assert.NotNull(refused);
        Assert.Contains("DOTNET_DiagnosticPorts", refused, StringComparison.Ordinal);
        Assert.Null(TelemetryGuard.CheckEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal) { ["DOTNET_DiagnosticPorts"] = "" }));
        Assert.NotNull(TelemetryGuard.CheckEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal) { ["dotnet_diagnosticports"] = "x" }));
    }

    [Fact]
    public void Ruling17_the_cgmes_expanded_total_is_512_mib()
        => Assert.Equal(512L * 1024 * 1024, GridSim.Core.IO.Cim.Zip.CgmesArchiveReader.MaxTotalBytes);

    [Fact]
    public void Ruling15_an_exciter_with_e1_and_se1_of_zero_fits_the_curve_through_the_other_point()
    {
        // E1 = 0, SE(E1) = 0 with a second point above 0, through the fit every exciter shares.
        (double a, double b) = AcExciter.SaturationFit("EXDC2", 0.0, 0.0, 4.5, 0.27);
        Assert.Equal(0.0, a);
        Assert.Equal(0.27 * 4.5, b * 4.5 * 4.5, 12);
    }
}
