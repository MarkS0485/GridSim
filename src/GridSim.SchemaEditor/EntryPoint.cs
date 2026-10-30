using System.Globalization;
using GridSim.Core;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Licensing;
using GridSim.SchemaEditor.Ui;

namespace GridSim.SchemaEditor;

/// <summary>
/// The case editor's entry point. Usage: <c>GridSim.SchemaEditor &lt;case-folder | x.case.json | x.network.json&gt; [--check]</c>.
///
/// Exit codes follow the programme's contract (PLAN.md binding rule 3), which the planning note's sketch predates:
/// 0 OK; 1 input error (the case cannot be read: missing folder, malformed file); 2 usage error, or the licence
/// refuses the case (as the CLI's licence gate exits 2), or the console is not interactive; 3 validation failure
/// (--check found errors). With --check the gate runs, the report is printed and nothing is written; without it the
/// full-screen editor opens.
/// </summary>
internal static class EntryPoint
{
    /// <summary>Usage text (printed for --help, and to standard error for a usage error).</summary>
    public const string Usage =
        "Usage: GridSim.SchemaEditor <case-folder | x.case.json | x.network.json> [--check]\n" +
        "  <case-folder>   a v1 case folder (buses.json, generation.json, ...)\n" +
        "  x.case.json     a v2 case manifest (gridsim-case/2) and the network files it lists\n" +
        "  x.network.json  a single v2 network file (gridsim-network/2)\n" +
        "  --check        run the save gate (schemas, loader, licence) and print the report; write nothing\n" +
        "  --version      print the version\n" +
        "Exit codes: 0 ok, 1 input error, 2 usage or licence, 3 check failed.";

    public static int Main(string[] args)
    {
        // Invariant culture for the whole process, as the CLI does: every number typed or shown is culture-free.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return Run(args, LicenceGate.Resolve, Console.Out, Console.Error);
    }

    /// <summary>The testable body of <see cref="Main"/>: the licence resolver and the writers are injected.</summary>
    internal static int Run(string[] args, Func<string?, LicenceGate> resolveLicence, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resolveLicence);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Contains("--version")) { stdout.WriteLine($"GridSim.SchemaEditor {BuildInfo.InformationalVersion}"); return 0; }
        if (args.Contains("--help") || args.Contains("-h")) { stdout.WriteLine(Usage); return 0; }

        bool check = args.Contains("--check");
        var positional = args.Where(a => a != "--check").ToList();
        string? unknown = positional.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
        if (unknown is not null) { stderr.WriteLine($"Unknown option '{unknown}'."); stderr.WriteLine(Usage); return 2; }
        if (positional.Count != 1) { stderr.WriteLine(positional.Count == 0 ? "No case given." : "Give exactly one case."); stderr.WriteLine(Usage); return 2; }
        string path = positional[0];

        CaseSession session;
        try
        {
            // A file is a v2 document (a *.case.json manifest or a *.network.json); a folder is a v1 case.
            session = File.Exists(path) ? CaseSession.OpenV2(path, resolveLicence(null)) : CaseSession.OpenV1(path, resolveLicence(null));
        }
        catch (CaseLicenceDeniedException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine("Cannot open the case: " + ex.Message);
            return 1;
        }

        if (check)
        {
            SaveReport report = SaveGate.Run(session, new SaveOptions(CheckOnly: true));
            foreach (string line in report.Lines()) stdout.WriteLine(line);
            foreach (string other in session.OtherFiles) stdout.WriteLine("  not part of the case (never written): " + other);
            return report.Outcome == SaveOutcome.CheckPassed ? 0 : 3;
        }

        return ConsoleHost.Run(new Editor(session, path));
    }
}
