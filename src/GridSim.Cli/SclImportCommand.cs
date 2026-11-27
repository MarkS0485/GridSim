using System.Globalization;
using System.Text;
using GridSim.Core.IO.Scl;
using GridSim.Core.Model.Substations;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP30: <c>gridsim scl-import &lt;file.scd&gt; --params &lt;params.json&gt; --out &lt;case.substations.json&gt; [--assume-closed]</c>.
/// Reads the substation section of an IEC 61850 SCL file (Core <see cref="SclReader"/>, safe XML settings), maps it onto
/// the node-breaker model with the electrical values of a <c>gridsim-scl-params/1</c> file (<see cref="SclImporter"/>) and
/// writes a <c>gridsim-substations/1</c> file (never an existing file). The report (what was mapped, kept as measurement
/// points, not imported, read past, and the notes) is printed. A switch with no state in the params file is refused unless
/// --assume-closed is given; each switch so taken is then listed and recorded in the written file's notes. Not on the shared --json/--csv writer. Exit 0 imported;
/// 1 the SCL file or params file is refused or unreadable, or the output cannot be written; 2 usage.
/// </summary>
internal static class SclImportCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim scl-import <file.scd> --params <params.json> --out <case.substations.json> [--assume-closed]";

    public static int Run(List<string> a)
    {
        const string verb = "scl-import";
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, verb, Usage, ["--params", "--out"], ["--assume-closed"]);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 1 || args.Get("--params") is null || args.Get("--out") is null)
        {
            Console.Error.WriteLine(args.Positional.Count > 1 ? $"error: {verb} takes one SCL file. {Usage}" : $"error: {verb} needs an SCL file, --params and --out. {Usage}");
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), verb, "node-breaker case", out string? output)) return CliExit.Usage;

        SclImportResult r;
        try
        {
            SclDocument doc = SclReader.Read(Path.GetFullPath(args.Positional[0]));
            SclParams p = SclParams.Load(Path.GetFullPath(args.Get("--params")!));
            r = SclImporter.Import(doc, p, args.Switches.Contains("--assume-closed"));
        }
        catch (SclImportException ex)
        {
            foreach (string problem in ex.Problems) Console.Error.WriteLine($"error: {verb}: {problem}");
            return CliExit.InputError;
        }
        catch (Exception ex) when (ex is SclFormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return CliExit.InputError;
        }

        Console.WriteLine($"scl-import - {Path.GetFileName(args.Positional[0])}: topology and equipment from the SCL substation section; electrical values from the params file.");
        foreach (string line in SclImporter.ReportLines(r)) Console.WriteLine("  " + line);

        // The projection at the imported switch states, as a check the file is usable (substation-state does the rest).
        SubstationProjectionResult proj = SubstationProjection.ProjectNormal(r.Model);
        Console.WriteLine($"  projection at these switch states: {proj.Buses.Count.ToString(Inv)} bus(es), {proj.Model.Branches.Count.ToString(Inv)} branch(es)");
        foreach (string w in proj.Warnings) Console.WriteLine($"  warning: {w}");

        try
        {
            string? dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(output!, FileMode.CreateNew, FileAccess.Write);
            using var w = new StreamWriter(fs, new UTF8Encoding(false));
            w.Write(SubstationJson.Write(r.Model));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: cannot write the node-breaker case: {ex.Message}");
            return CliExit.InputError;
        }
        Console.WriteLine($"  wrote {output}");
        return CliExit.Ok;
    }
}
