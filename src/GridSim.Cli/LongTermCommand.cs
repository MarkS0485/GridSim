using System.Globalization;
using GridSim.Core.Dynamics.LongTerm;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP21: <c>long-term</c>, the quasi-steady-state (QSS) long-term simulation (GridSim.Core.Dynamics.LongTerm.QssEngine).
/// <c>gridsim long-term &lt;case&gt; &lt;long-term.json&gt; [--out &lt;long-term.csv&gt;]</c>. The case is loaded as every verb
/// loads it; the second argument is the <c>gridsim-long-term/1</c> input (units, AGC, tap changer timers, recovering loads,
/// disturbances). The CLI prints a summary and writes the samples to <c>--out</c> (default <c>long-term.csv</c> beside the
/// input); all arithmetic is in Core. Not on the shared --json/--csv writer list (CliResultWriterTests pins it); the CSV is
/// the result file. Exit codes: 0 the run reached its end; 1 the case or the input cannot be read, or is refused; 2 usage;
/// 3 the run stopped early (loss of short-term equilibrium).
/// </summary>
internal static class LongTermCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim long-term <case> <long-term.json> [--out <long-term.csv>]";

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        string? casePath = null, inputPath = null, outPath = null;
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (t == "--out")
            {
                if (outPath is not null || i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("error: long-term: --out expected one file name. " + Usage);
                    return CliExit.Usage;
                }
                outPath = a[++i];
                continue;
            }
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: long-term does not use {t}; it accepts --out. " + Usage);
                return CliExit.Usage;
            }
            if (casePath is null) casePath = t;
            else if (inputPath is null) inputPath = t;
            else
            {
                Console.Error.WriteLine($"error: long-term takes a case and an input file, but got a third argument: {t}. " + Usage);
                return CliExit.Usage;
            }
        }
        if (casePath is null || inputPath is null)
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string output = Path.GetFullPath(outPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".", "long-term.csv"));
        // RC5 (2.1.127): in a re-run by breaking-change validate, the CSV must lie in the re-run's scratch folder. Inert in
        // every other run.
        CliFiles.ReplayGuard(output, "long-term CSV");
        if (File.Exists(output) || Directory.Exists(output))
        {
            Console.Error.WriteLine($"error: long-term: '{output}' already exists; name a new file (it is never overwritten).");
            return CliExit.Usage;
        }

        GridModel model;
        LongTermCase input;
        try
        {
            model = load(casePath);
            if (!File.Exists(inputPath)) throw new InvalidDataException($"long-term input '{inputPath}' does not exist.");
            input = LongTermCase.Parse(File.ReadAllBytes(inputPath), model);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error: long-term: " + ex.Message);
            return CliExit.InputError;
        }

        LongTermResult r;
        try
        {
            r = QssEngine.Run(model, input);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: long-term: " + ex.Message);
            return CliExit.InputError;
        }

        try
        {
            using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                w.Write(r.ToCsv());
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("error: long-term: cannot write the result: " + ex.Message);
            return CliExit.InputError;
        }

        LongTermSample end = r.Samples[^1];
        double vmin = r.Samples.Min(s => s.MinVoltagePu);
        Console.WriteLine($"long-term (quasi-steady-state): step {Num(r.StepSeconds)} s, {r.Samples.Count - 1} steps, beta {Num(r.BetaMwPerHz)} MW/Hz");
        Console.WriteLine($"  at {Num(end.TimeSeconds)} s: df {Num(end.FrequencyDeviationHz)} Hz, lowest voltage {Num(end.MinVoltagePu)} pu at bus {end.MinVoltageBus.ToString(Inv)}");
        Console.WriteLine($"  lowest voltage over the run: {Num(vmin)} pu; tap moves: {r.TapMoves.Count.ToString(Inv)}");
        foreach (LongTermAppliedEvent e in r.Events)
            Console.WriteLine(double.IsNaN(e.AppliedSeconds)
                ? $"  event {e.InputIndex.ToString(Inv)}: {e.Description} at {Num(e.RequestedSeconds)} s: not reached"
                : $"  event {e.InputIndex.ToString(Inv)}: {e.Description} at {Num(e.RequestedSeconds)} s, applied at {Num(e.AppliedSeconds)} s");
        Console.WriteLine($"  wrote {output}");
        if (!r.Completed)
        {
            Console.WriteLine("  STOPPED: " + r.StopReason);
            return CliExit.Verdict;
        }
        return CliExit.Ok;
    }

    private static string Num(double v) => v.ToString("R", Inv);
}
