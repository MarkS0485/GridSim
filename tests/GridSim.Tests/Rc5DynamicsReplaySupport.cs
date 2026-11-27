using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Journal;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.127): fixtures and helpers for the dynamics replay tests (stability, dynamics-study, long-term, trip and modal
/// re-run by <c>breaking-change validate</c>).
/// </summary>
internal static class Rc5DynamicsReplaySupport
{
    /// <summary>The 2.1.122 review's DYR for case14: GENROU on every machine, ESST1A, EXST1, SCRX and ESST3A exciters
    /// (ESST3A is one of B27's records) and a GENCLS. No CSVGN1 record, so B3 can never be raised by it.</summary>
    internal static string Case14Dyr()
    {
        const string g = Rc5Wp19BitIdentityTests.Genrou;
        return "     1 'GENROU' 1 " + g + " /\n"
            + "     1 'ESST1A' 1  1  1  0.02  0.1 -0.1  1.0  10.0  0.0  0.0  210.0  0.02  6.43 -6.0  6.43 -6.0  0.038  0.0  1.0  4.54  6.0 /\n"
            + "     2 'GENROU' 1 " + g + " /\n"
            + "     2 'EXST1' 1  0.0  0.2 -0.2  1.0  10.0  200.0  0.02  7.0 -6.4  0.05  0.02  1.0 /\n"
            + "     3 'GENROU' 1 " + g + " /\n"
            + "     3 'SCRX' 1  0.1  10.0  200.0  0.05  0.0  5.0  0  10.0 /\n"
            + "     6 'GENROU' 1 " + g + " /\n"
            + "     6 'ESST3A' 1  0.0  0.2 -0.2  7.93  1.0  6.67  20.0  0.0  10.0 -10.0  1.0  6.15  0.0  6.9  0.2  0.081  5.8  20.0  0.4  1.0  0.0 /\n"
            + "     8 'GENCLS' 1  5.0  0.0 /\n";
    }

    /// <summary>Write case14.raw and case14.dyr (<see cref="Case14Dyr"/>) into <paramref name="work"/>.</summary>
    internal static void WriteCase14(string work)
    {
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "case14.raw"), Rc5Wp19BitIdentityTests.Case14Raw());
        File.WriteAllText(Path.Combine(work, "case14.dyr"), Case14Dyr());
    }

    /// <summary>The stability job with the case14 DYR under both options (B27's condition), with its RMS event export.</summary>
    internal static string[] Case14StabilityJob(string eventsOut) =>
        ["stability", "case14.raw", "--rms", "--dyn-data", "case14.dyr", "--dyr-extended", "--allow-unmodelled",
         "--events", "fault:4@0.1,clear:4@0.15", "--events-out", eventsOut];

    /// <summary>A stability job on case9 with a line-to-line sequence fault, so its export has the fault-point trace
    /// (rms-fault-points.csv, with the z2 columns B10 claims).</summary>
    internal static string[] SequenceFaultJob(string eventsOut) =>
        ["stability", "case9", "--rms", "--events", "sfault:ll:7:0:0.05@0.1,clear:7@0.183", "--events-out", eventsOut];

    /// <summary>The fixture study of the dynamics-study plug-in test (Rc5Wp12): case14 with the study DYR, one bus fault.</summary>
    internal static void WriteStudy(string work)
    {
        Directory.CreateDirectory(work);
        File.Copy(DynamicsStudyTestSupport.Psse("case14.raw"), Path.Combine(work, "case14.raw"));
        File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), Path.Combine(work, "case14.dyr"));
        File.WriteAllText(Path.Combine(work, "study.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(Path.Combine(work, "case14.raw")) },
            dyr = new { path = "case14.dyr", sha256 = Sha(Path.Combine(work, "case14.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 2.0 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = new object[] { new { kind = "speed", machine = new { bus = 1, id = "1" } }, new { kind = "busVoltage", bus = 4 } },
            cases = new object[]
            {
                new { name = "fault-4", kind = "events", events = new object[]
                {
                    new { kind = "busFault", at = 0.1, bus = 4 }, new { kind = "faultClear", at = 0.15, bus = 4 },
                    new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                } },
            },
        }));
    }

    /// <summary>The sample plug-in (the DYR's TGOV1 of bus 1, operation for operation), its configuration and the
    /// assignments file, beside the study (<see cref="WriteStudy"/>).</summary>
    internal static void WritePlugin(string work)
    {
        File.Copy(BuiltDll("GridSim.ModelPlugin.Sample"), Path.Combine(work, "sample.dll"));
        string dll = Sha(Path.Combine(work, "sample.dll"));
        File.WriteAllText(Path.Combine(work, "plugins.json"), JsonSerializer.Serialize(new
        {
            schema = "gridsim-plugins/1",
            assemblies = new[] { new { path = "sample.dll", sha256 = dll, models = new[] { "SAMPLE_TGOV1" } } },
        }));
        File.WriteAllText(Path.Combine(work, "assign.json"), JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            pluginModels = new[] { new { machine = new { bus = 1, id = "1" }, model = "SAMPLE_TGOV1",
                parameters = new Dictionary<string, double> { ["R"] = 0.05, ["T1"] = 0.49, ["T2"] = 2.1, ["T3"] = 7.0, ["Dt"] = 0.0, ["Vmin"] = 0.0, ["Vmax"] = 2.5 } } },
        }));
    }

    /// <summary>A built test plug-in assembly (the solution build puts it beside the test project).</summary>
    internal static string BuiltDll(string project)
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string dll = Path.Combine(CliProcess.Root(), "tests", project, "bin", tfmDir.Parent!.Name, tfmDir.Name, project + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"the test plug-in {project} was not found; build the solution (or that project) first", dll);
        return dll;
    }

    /// <summary>The long-term input of the WP21 CLI test (case9, three governed units, AGC, a 20 MW load step at 2 s).</summary>
    internal const string LongTermCase9 = """
        {
          "schema": "gridsim-long-term/1",
          "stepSeconds": 1,
          "durationSeconds": 10,
          "units": [ { "generator": 0, "droopPu": 0.05 }, { "generator": 1, "droopPu": 0.05 }, { "generator": 2, "droopPu": 0.05 } ],
          "agc": { "kiPerSecond": 0.01, "units": [ { "generator": 1, "participation": 1 } ] },
          "events": [ { "kind": "loadStep", "atSeconds": 2, "bus": 5, "pMw": 20, "qMvar": 0 } ]
        }
        """;

    internal static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Run a journaled job in <paramref name="work"/>; it must end with one of <paramref name="exits"/> (0 by
    /// default) and add one journal line.</summary>
    internal static void Record(string work, string journal, string[] job, params int[] exits)
    {
        int before = JournalTestSupport.Lines(journal);
        CliProcess.Result r = JournalTestSupport.Run(work, new[] { "--journal", journal }.Concat(job).ToArray());
        int[] ok = exits.Length == 0 ? [0] : exits;
        if (!ok.Contains(r.ExitCode)) throw new InvalidOperationException("the recorded job failed: " + string.Join(' ', job) + Environment.NewLine + r);
        if (JournalTestSupport.Lines(journal) != before + 1) throw new InvalidOperationException("the job was not journaled: " + string.Join(' ', job));
    }

    /// <summary>The same with the snapshot store on.</summary>
    internal static void RecordWithSnapshot(string work, string journal, string[] job)
    {
        CliProcess.Result r = JournalTestSupport.Run(work, new[] { "--journal", journal, "--journal-snapshot" }.Concat(job).ToArray());
        if (r.ExitCode != 0) throw new InvalidOperationException("the recorded job failed: " + string.Join(' ', job) + Environment.NewLine + r);
    }

    /// <summary>
    /// The upgrade simulation: every entry is labelled as recorded by <paramref name="version"/> (so validate re-runs it and
    /// the breaking changes after that release are in its window), and the hash of each recorded output in
    /// <paramref name="perturbed"/> is taken again from its (perturbed) bytes, as if the old build had written them.
    /// </summary>
    internal static void Relabel(string journal, string version, params string[] perturbed)
    {
        var lines = new List<string>();
        foreach (string line in File.ReadAllLines(journal).Where(l => l.Length > 0))
        {
            JournalEntry e = JournalJson.Parse(line, out string? error) ?? throw new InvalidDataException(error);
            e.Version = version;
            foreach (JournalOutput o in e.Outputs)
                if (perturbed.Any(p => string.Equals(Path.GetFullPath(p), o.Abs, StringComparison.OrdinalIgnoreCase)))
                {
                    o.Sha256 = Sha(o.Abs);
                    o.Bytes = new FileInfo(o.Abs).Length;
                }
            lines.Add(JournalJson.Line(e));
        }
        File.WriteAllText(journal, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Change one cell of a result CSV (leading <c>#</c> comment lines kept): in <paramref name="column"/>, the first data row
    /// whose value is a finite non-zero number becomes <paramref name="change"/>(value), written in round-trip form. Every
    /// other byte is kept. Returns the value before and after.
    /// </summary>
    internal static (double Before, double After) PerturbCsv(string path, string column, Func<double, double> change)
    {
        string[] lines = File.ReadAllText(path).Split('\n');
        int header = Array.FindIndex(lines, l => !l.StartsWith('#'));
        int col = Array.IndexOf(lines[header].TrimEnd('\r').Split(','), column);
        if (col < 0) throw new InvalidDataException($"{Path.GetFileName(path)} has no column {column}");
        for (int i = header + 1; i < lines.Length; i++)
        {
            string[] cells = lines[i].TrimEnd('\r').Split(',');
            if (cells.Length <= col || !double.TryParse(cells[col], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.IsFinite(x) || x == 0.0) continue;
            double y = change(x);
            cells[col] = y.ToString("R", CultureInfo.InvariantCulture);
            lines[i] = string.Join(",", cells) + (lines[i].EndsWith('\r') ? "\r" : "");
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return (x, y);
        }
        throw new InvalidDataException($"{Path.GetFileName(path)} has no non-zero {column} value");
    }

    /// <summary>Run validate on <paramref name="journal"/> from <paramref name="cwd"/>.</summary>
    internal static CliProcess.Result Validate(string cwd, string journal, params string[] more) =>
        JournalTestSupport.Run(cwd, new[] { "breaking-change", "validate", "--journal", journal }.Concat(more).ToArray());

    /// <summary>The console line of entry <paramref name="seq"/>.</summary>
    internal static string Line(CliProcess.Result r, long seq) =>
        r.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith($"  seq {seq.ToString(CultureInfo.InvariantCulture)} ", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"no line for seq {seq}: {r}");

    /// <summary>A table of a validate JSON report.</summary>
    internal static List<JsonElement> Table(string report, string name)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(report));
        return d.RootElement.GetProperty("tables").TryGetProperty(name, out JsonElement t)
            ? t.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
    }
}
