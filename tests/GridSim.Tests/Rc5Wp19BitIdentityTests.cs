using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP19 bit-identity: the default DYR path, and the extended path for the models it already mapped, give exactly what
/// they gave before WP19. Method: this file was written and built against the 2.1.116 binaries (commit c3ccf218, before any
/// WP19 edit), run once to read each SHA-256 below, and the values were pinned; the same tests then ran unchanged against
/// the WP19 build. The hashed text is:
/// <list type="bullet">
/// <item>The default reader's refusal of a case14 DYR file that holds a record of every model WP19 names (EXDC2, IEEEX1,
/// ESAC8B, ESAC7B, ESST3A, PSS2C, IEEEG3, HYGOV4, GENQEC): the message lists them as unmapped. Through the Core API and
/// through the CLI (stderr with the temporary folder replaced by a fixed word, and the exit code).</item>
/// <item>The extended reader's document and notes for the WP3a case14 file (models mapped before WP19 only).</item>
/// <item>The CLI RMS machine rows for that file with <c>--dyr-extended</c> and a bus-4 fault, and for a default-path file
/// (GENROU, SEXS, IEEET1, TGOV1, HYGOV) with the same fault.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp19BitIdentityTests
{
    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string PsseFixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    /// <summary>case14 with a source reactance ZX of 0.25 on the bus-8 machine, as the WP3a tests use it.</summary>
    internal static string Case14Raw()
    {
        string raw = File.ReadAllText(PsseFixture("case14.raw"));
        const string bus8 = "8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 1, 0,";
        Assert.Contains(bus8, raw, StringComparison.Ordinal);
        return raw.Replace(bus8, "8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 0.25, 0,", StringComparison.Ordinal);
    }

    internal const string Genrou = " 7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0";

    /// <summary>The WP3a case14 file: models mapped by the extended library before WP19 only.</summary>
    internal const string Wp3aDyr =
          "     1 'GENROU' 1 " + Genrou + " /\n"
        + "     1 'ESST1A' 1  1  1  0.02  0.1 -0.1  1.0  10.0  0.0  0.0  210.0  0.02  6.43 -6.0  6.43 -6.0  0.038  0.0  1.0  4.54  6.0 /\n"
        + "     1 'PSS2A' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.1 -0.1 /\n"
        + "     2 'GENROU' 1 " + Genrou + " /\n"
        + "     2 'EXST1' 1  0.0  0.2 -0.2  1.0  10.0  200.0  0.02  7.0 -6.4  0.05  0.02  1.0 /\n"
        + "     2 'IEEEST' 1  3  0  0.05  0.0  0.0  0.0  0.0  0.0  0.2  0.05  0.3  0.1  1.5  2.0 -1.0  0.1 -0.1  1.2  0.0 /\n"
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     3 'SCRX' 1  0.1  10.0  200.0  0.05  0.0  5.0  0  10.0 /\n"
        + "     6 'GENROU' 1 " + Genrou + " /\n"
        + "     6 'ESDC1A' 1  0.0  46.0  0.06  0.0  0.0  5.0 -5.0  1.0  0.46  0.1  1.0  0  2.3  0.1  3.1  0.33 /\n"
        + "     6 'PSS2B' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.0  0.0  0.5 -0.5  2.0 -2.0  0.1 -0.1 /\n"
        + "     8 'GENCLS' 1  5.0  0.0 /\n";

    /// <summary>A default-path file (models of the default reader only).</summary>
    internal const string DefaultDyr =
          "     1 'GENROU' 1 " + Genrou + " /\n"
        + "     1 'SEXS' 1  0.1  10.0  100.0  0.05  0.0  5.0 /\n"
        + "     1 'TGOV1' 1  0.05  0.5  5.0  0.0  1.5  5.0  0.0 /\n"
        + "     2 'GENROU' 1 " + Genrou + " /\n"
        + "     2 'IEEET1' 1  0.0  50.0  0.05  5.0 -5.0  1.0  0.5  0.05  1.0  0  0.0  0.0  0.0  0.0 /\n"
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     3 'HYGOV' 1  0.05  0.3  5.0  0.0  0.5  0.0  1.0  0.0  1.0  1.0  0.0  0.0 /\n";

    /// <summary>One record of every model WP19 names, on case14 units with a GENROU each (values plausible, counts as the
    /// public data sheets give them where known).</summary>
    internal const string Wp19Dyr =
          "     1 'GENROU' 1 " + Genrou + " /\n"
        + "     1 'EXDC2' 1  0.0  300.0  0.01  0.0  0.0  4.95 -4.95  1.0  1.33  0.0675  0.6  0  2.29  0.117  3.05  0.279 /\n"
        + "     1 'PSS2C' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.0  0.0  0.0  0.0  0.5 -0.5  2.0 -2.0  0.1 -0.1 /\n"
        + "     2 'GENROU' 1 " + Genrou + " /\n"
        + "     2 'IEEEX1' 1  0.0  300.0  0.01  0.0  0.0  4.95 -4.95  1.0  1.33  0.0675  0.6  0  2.29  0.117  3.05  0.279 /\n"
        + "     2 'IEEEG3' 1  0.05  0.04  0.1 -0.1  1.0  0.0  0.05  0.3  5.0  1.0  0.5  1.0  1.5  1.0 /\n"
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     3 'ESAC8B' 1  0.0  80.0  5.0  10.0  0.2  1.0  0.0  35.0  0.0  0.8  1.0  6.5  0.3  9.0  3.0 /\n"
        + "     3 'HYGOV4' 1  100.0  0.05  0.04  0.1 -0.1  1.0  0.0  0.05  0.3  5.0  1.0  1.0  0.0  1.0  0.0 /\n"
        + "     6 'GENROU' 1 " + Genrou + " /\n"
        + "     6 'ESST3A' 1  0.0  0.2 -0.2  7.93  1.0  6.67  20.0  0.0  10.0 -10.0  1.0  6.15  0.0  6.9  0.2  0.081  5.8  20.0  0.4  1.0  0.0 /\n"
        + "     8 'GENROU' 1 " + Genrou + " /\n"
        + "     8 'ESAC7B' 1  0.0  4.24  4.24  0.0  0.0  5.79 -5.79  65.36  59.69  1.0 -0.95  4.96  0.0  1.1  6.9  0.0  1.0  0.18  0.02  0.212  0.0  0.0  1.0  3.0  0.03  4.0  0.1 /\n"
        + "     8 'GENQEC' 2  7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0 /\n";

    private static PsseParseResult Raw14() => PsseRawParser.ParseWithDiagnostics(Case14Raw(), "case14");

    [Fact]
    public void The_default_reader_refuses_the_wp19_models_as_before()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(Wp19Dyr, Raw14(), "case14", new PsseDyrOptions { SourceName = "wp19.dyr" }));
        Pin("5265c599252a434f938861bf903da85fc25cc47b59bb19524631ccca10cdfd05", Sha(Rc5LineEndings.ToCrLf(ex.Message)));
        var allowed = PsseDyrReader.Read(Wp19Dyr, Raw14(), "case14", new PsseDyrOptions { SourceName = "wp19.dyr", AllowUnmodelled = true });
        Pin("d5dc00ce5aa3a6f4ad31964fe7a7d0cf1c5612c5e81eb8ca995874a0b788a78d", Sha(allowed.Json + "\n" + string.Join("\n", allowed.Notes)));
    }

    [Fact]
    public void The_extended_reader_maps_the_wp3a_file_as_before()
    {
        PsseDyrResult r = PsseDyrReader.Read(Wp3aDyr, Raw14(), "case14", new PsseDyrOptions { SourceName = "wp3a.dyr", ExtendedLibrary = true });
        Pin("dfc68514d4b315c95256abe5c6f49e433a0fdcd963f95691ac2c086477cc9bd9", Sha(r.Json + "\n" + string.Join("\n", r.Notes)));
    }

    private static (string Rows, int Exit, string Err) RunCli(string dyr, params string[] extra)
    {
        string root = TestTemp.Under("gridsim-wp19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rawPath = Path.Combine(root, "case14.raw"), dyrPath = Path.Combine(root, "case14.dyr"), outDir = Path.Combine(root, "out");
            File.WriteAllText(rawPath, Case14Raw());
            File.WriteAllText(dyrPath, dyr);
            var args = new List<string> { "stability", rawPath, "--rms", "--dyn-data", dyrPath };
            args.AddRange(extra);
            args.AddRange(new[] { "--events", "fault:4@0.1,clear:4@0.15", "--events-out", outDir });
            CliProcess.Result r = CliProcess.RunBuilt(args.ToArray());
            string csv = Path.Combine(outDir, GridSim.Core.IO.RmsEventsExport.MachinesCsv);
            string rows = File.Exists(csv) ? string.Join("\n", File.ReadAllLines(csv).Where(l => !l.StartsWith('#'))) : "";
            return (rows, r.ExitCode, r.StdErr.Replace(root, "ROOT", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_cli_refuses_the_wp19_models_without_the_extended_library_as_before()
    {
        (string rows, int exit, string err) = RunCli(Wp19Dyr);
        Assert.Equal("", rows);
        Pin("1 20b2b5521f768453353e5ac43bb20c44abffdd693894697f147512e53806580f", exit.ToString(CultureInfo.InvariantCulture) + " " + Sha(err));
    }

    [Fact]
    public void The_cli_extended_run_of_the_wp3a_file_gives_the_same_rows()
    {
        (string rows, int exit, _) = RunCli(Wp3aDyr, "--dyr-extended");
        Assert.Equal(0, exit);
        Pin("76f58b0d87713efa3824a3bd96d521ae1c1ce263f551b869e1efe4c91b84fd1f", Sha(rows));
    }

    [Fact]
    public void The_cli_default_run_gives_the_same_rows()
    {
        (string rows, int exit, _) = RunCli(DefaultDyr);
        Assert.Equal(0, exit);
        Pin("8b36628af9081e99e7c81e198346ed7a30496365e465435ca61615c78a8341e0", Sha(rows));
    }
}
