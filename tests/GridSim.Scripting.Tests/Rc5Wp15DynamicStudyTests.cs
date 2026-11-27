using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>
/// RC5 2.1.110 (WP15 follow-up): STUDY RMS and STUDY CCT in a <c>GRAMMAR 1 WITH STUDIES</c> script. They build the request
/// the <c>dynamics-study</c> verb builds and run it through the same Core runner, so the oracle is the verb itself: the
/// built command line runs a study file with the script's fixed settings on the same RAW and DYR files, and every number the
/// script keeps (final frequency, least bus voltage, the CCT bracket) is compared bit for bit with the verb's result file.
/// </summary>
public sealed class Rc5Wp15DynamicStudyTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Fixture(string folder, string name) => Path.Combine(Root(), "tests", "GridSim.Tests", "fixtures", folder, name);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Run the built CLI (same configuration as this test assembly) in <paramref name="dir"/>.</summary>
    private static (int Exit, string Err) Cli(string dir, params string[] args)
    {
        // AppContext.BaseDirectory = .../tests/GridSim.Scripting.Tests/bin/<Configuration>/<tfm>/
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        string dll = Path.Combine(Root(), "src", "GridSim.Cli", "bin", configuration, "net10.0", "gridsim.dll");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(dir, "licence.gridsim");
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException("dynamics-study did not finish"); }
        return (p.ExitCode, err.Result);
    }

    /// <summary>The study file the script's request equals: its fixed settings, the reference machine (bus 1, id 1, the
    /// first in-service machine) and the channels (its frequency, then every bus voltage in bus order).</summary>
    private static string StudyFile(string dir, string name, object row)
    {
        var channels = new List<object> { new { kind = "frequency", machine = new { bus = 1, id = "1" } } };
        for (int b = 1; b <= 14; b++) channels.Add(new { kind = "busVoltage", bus = b });
        var doc = new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(Path.Combine(dir, "case14.raw")) },
            dyr = new { path = "case14-study.dyr", sha256 = Sha(Path.Combine(dir, "case14-study.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 3.0 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels,
            cases = new[] { row },
        };
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return path;
    }

    [Fact]
    public void Rms_and_cct_studies_equal_the_dynamics_study_verb_bit_for_bit()
    {
        string dir = TestPaths.TempDir();
        File.Copy(Fixture("psse", "case14.raw"), Path.Combine(dir, "case14.raw"));
        File.Copy(Fixture("dynamics-study", "case14-study.dyr"), Path.Combine(dir, "case14-study.dyr"));
        RunResult run = Runner.Run(new UTF8Encoding(false).GetBytes(string.Join("\n",
            "GRAMMAR 1 WITH STUDIES",
            "LOAD CASE \"case14.raw\"",
            "STUDY RMS DYNAMICS \"case14-study.dyr\" FAULT BUS \"4\" AT 0.1 S CLEAR AT 0.15 S FOR 3 S AS r",
            "LET st = STABLE OF STUDY r",
            "LET fend = FINAL FREQUENCY OF STUDY r",
            "LET vmin = MIN VOLTAGE OF BUS \"4\" IN STUDY r",
            "STUDY CCT DYNAMICS \"case14-study.dyr\" FAULT BUS \"4\" AT 100 MS FOR 3 S AS c",
            "LET tc = CCT OF STUDY c",
            "REPORT STUDY r",
            "REPORT STUDY c") + "\n"), new RunOptions(dir));
        Assert.True(run.Ok, run.Error?.Message);
        using JsonDocument rec = JsonDocument.Parse(run.RecordJson);
        JsonElement rms = rec.RootElement.GetProperty("studies")[0], cct = rec.RootElement.GetProperty("studies")[1];
        Assert.Equal("RMS", rms.GetProperty("study").GetString());
        Assert.Equal("CCT", cct.GetProperty("study").GetString());
        // The DYR file is recorded as an input of each study.
        Assert.Equal(2, rec.RootElement.GetProperty("inputs").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "dynamics"));

        // The verb, RMS row.
        string rmsStudy = StudyFile(dir, "rms.json", new
        {
            name = "r", kind = "events",
            events = new object[] { new { kind = "busFault", at = 0.1, bus = 4 }, new { kind = "faultClear", at = 0.15, bus = 4 } },
        });
        (int exit, string err) = Cli(dir, "dynamics-study", rmsStudy, "--out", Path.Combine(dir, "rms-out.json"));
        Assert.True(exit == 0, $"exit {exit}: {err}");
        using JsonDocument cli = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "rms-out.json")));
        JsonElement row = cli.RootElement.GetProperty("cases")[0];
        Assert.Equal(row.GetProperty("disposition").GetString(), rms.GetProperty("disposition").GetString());
        JsonElement[] traces = row.GetProperty("run").GetProperty("channels").EnumerateArray().ToArray();
        Assert.Equal("frequency", traces[0].GetProperty("kind").GetString());
        double fEnd = traces[0].GetProperty("values").EnumerateArray().Last().GetDouble();
        Assert.Equal(Bits(fEnd), Bits(Num(rms.GetProperty("finalFrequencyHz").GetString()!)));
        // Every bus's least voltage, in bus order (the magnitude traces).
        JsonElement[] mags = traces.Where(t => t.GetProperty("kind").GetString() == "busVoltage" && t.GetProperty("unit").GetString() == "pu").ToArray();
        JsonElement[] mins = rms.GetProperty("minVoltagePu").EnumerateArray().ToArray();
        Assert.Equal(14, mags.Length);
        Assert.Equal(14, mins.Length);
        for (int i = 0; i < 14; i++)
        {
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), mins[i].GetProperty("bus").GetString());
            double least = mags[i].GetProperty("values").EnumerateArray().Min(x => x.GetDouble());
            Assert.Equal(Bits(least), Bits(Num(mins[i].GetProperty("pu").GetString()!)));
        }
        // The readings are those values (a script holds decimals; the record keeps the doubles).
        Assert.Equal(row.GetProperty("disposition").GetString() == "STABLE" ? 1m : 0m, run.Session.Globals["st"].Number);
        Assert.Equal((decimal)fEnd, run.Session.Globals["fend"].Number);

        // The verb, CCT row (onset 100 MS = 0.1 s).
        string cctStudy = StudyFile(dir, "cct.json", new
        {
            name = "c", kind = "cct",
            cct = new { faultBus = 4, clearingTrips = Array.Empty<object>(), onsetSeconds = 0.1, maxClearingSeconds = 1.0 },
        });
        (exit, err) = Cli(dir, "dynamics-study", cctStudy, "--out", Path.Combine(dir, "cct-out.json"));
        Assert.True(exit == 0, $"exit {exit}: {err}");
        using JsonDocument cliC = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "cct-out.json")));
        JsonElement crow = cliC.RootElement.GetProperty("cases")[0];
        Assert.Equal(crow.GetProperty("disposition").GetString(), cct.GetProperty("disposition").GetString());
        JsonElement coarse = crow.GetProperty("cct").GetProperty("coarse");
        Assert.Equal(Bits(coarse.GetProperty("stableSeconds").GetDouble()), Bits(Num(cct.GetProperty("stableSeconds").GetString()!)));
        if (coarse.GetProperty("unstableSeconds").ValueKind == JsonValueKind.Number)
            Assert.Equal(Bits(coarse.GetProperty("unstableSeconds").GetDouble()), Bits(Num(cct.GetProperty("unstableSeconds").GetString()!)));
        Assert.Equal("CCT BRACKETED", cct.GetProperty("disposition").GetString());
        Assert.Equal((decimal)coarse.GetProperty("stableSeconds").GetDouble(), run.Session.Globals["tc"].Number);
    }

    [Fact]
    public void The_new_words_stay_free_names_and_the_studies_refuse_what_they_cannot_run()
    {
        // STABLE, FINAL, CCT, DYNAMICS and VOLTAGE are read in place, so they still name values, with or without STUDIES.
        Session s = TestPaths.Run(TestPaths.TempDir(), "GRAMMAR 1 WITH STUDIES", "LET stable = 1", "LET cct = stable + 1", "LET final = 2",
            "LET voltage = 3", "LET dynamics = MIN(voltage, final)", "PRINT cct");
        Assert.Equal(2m, s.Globals["cct"].Number);
        Assert.Equal(2m, s.Globals["dynamics"].Number);
        Session plain = TestPaths.Run(TestPaths.TempDir(), "LET cct = 3");
        Assert.Equal(3m, plain.Globals["cct"].Number);
        // A MATPOWER case has no RAW identities: refused by name. A clearing before the fault is a syntax error.
        RunResult m = Runner.Run(new UTF8Encoding(false).GetBytes(string.Join("\n", "GRAMMAR 1 WITH STUDIES", TestPaths.Load("case9"),
            "STUDY RMS DYNAMICS \"x.dyr\" FAULT BUS \"4\" AT 0.1 S CLEAR AT 0.15 S FOR 1 S AS r") + "\n"), new RunOptions(TestPaths.TempDir()));
        Assert.False(m.Ok);
        Assert.Contains("the case must be a PSS/E .raw file", m.Error!.Message, StringComparison.Ordinal);
        Assert.NotNull(Runner.Check("GRAMMAR 1 WITH STUDIES\nSTUDY RMS DYNAMICS \"x.dyr\" FAULT BUS \"4\" AT 0.2 S CLEAR AT 0.1 S FOR 1 S AS r\n"));
        Assert.NotNull(Runner.Check("GRAMMAR 1 WITH STUDIES\nSTUDY CCT DYNAMICS \"x.dyr\" FAULT BUS \"4\" AT 0.1 MW FOR 1 S AS c\n"));
        Assert.Null(Runner.Check("GRAMMAR 1 WITH STUDIES\nSTUDY CCT DYNAMICS \"x.dyr\" FAULT BUS \"4\" AT 0.1 S FOR 1 S AS c\nLET t = CCT OF STUDY c\n"));
    }
}
