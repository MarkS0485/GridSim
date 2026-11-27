using System.Globalization;
using System.Text;
using GridSim.Core.Licensing;
using GridSim.Scripting;

namespace GridSim.Scripting.Console;

/// <summary>
/// gridsim-script: the console front end of GridSim.Scripting.
///   gridsim-script run FILE.gss [--base-dir DIR] [--confine] [--record RUN.json]   run a script, optionally write the run record
///   gridsim-script check FILE.gss                                                  parse a script, run nothing
///   gridsim-script [repl] [--base-dir DIR] [--confine] [--record-dir DIR]          interactive session, recorded as a .gss file
/// --confine refuses any path a statement names outside the base folder (and network, device and linked paths). A loaded model is
/// checked against the licence's bus limit exactly as the gridsim command line checks one.
/// Exit codes: 0 ok, 1 the script was refused (the error names line and column), 2 usage or file error, 78 the telemetry
/// guard refused to start (GridSim.Core.Diagnostics.TelemetryGuard, the first thing Main runs).
/// </summary>
public static class Program
{
    private const string Usage =
        "usage:\n" +
        "  gridsim-script run FILE.gss [--base-dir DIR] [--confine] [--record RUN.json]\n" +
        "  gridsim-script check FILE.gss\n" +
        "  gridsim-script [repl] [--base-dir DIR] [--confine] [--record-dir DIR]\n" +
        "  gridsim-script --version | --help\n" +
        "  --confine: refuse any path a statement names outside the base folder (and network, device and linked paths)\n" +
        "exit codes: 0 ok, 1 script refused, 2 usage or file error, 78 telemetry guard\n";

    /// <summary>The app's own assemblies, each of which must carry the TelemetryOff build marker: the console, the scripting
    /// library and GridSim.Core (the only GridSim assembly the library references).</summary>
    internal static readonly string[] AppAssemblies = ["gridsim-script", "GridSim.Scripting", "GridSim.Core"];

    public static int Main(string[] args)
    {
        // The telemetry guard runs before anything else, --version and --help included. It exits with code 78 (one line
        // on stderr and the trace log) when the build marker is missing, a telemetry assembly is loaded or loads later, or
        // an exporter, APM agent or agent-injection variable is set; see GridSim.Core.Diagnostics.TelemetryGuard.
        GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup("gridsim-script", AppAssemblies, foreignEntryAllowed: false);
        // As the gridsim command line does: pin the whole process to the invariant culture before anything runs, so a
        // comma-decimal machine writes the same characters into the console output, the run record and the session
        // files as every other machine (and new threads start in the same culture).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Length == 0) return Repl(args);
            switch (args[0])
            {
                case "--version":
                    System.Console.Out.WriteLine($"gridsim-script {GridSim.Core.BuildInfo.Version}, grammar {Grammar.VersionText}");
                    return 0;
                case "--help" or "-h" or "help":
                    System.Console.Out.Write(Usage);
                    return 0;
                case "run": return RunFile(args[1..]);
                case "check": return CheckFile(args[1..]);
                case "repl": return Repl(args[1..]);
                default:
                    System.Console.Error.Write($"unknown command '{args[0]}'\n{Usage}");
                    return 2;
            }
        }
        catch (IOException ex)
        {
            System.Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static string? Option(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= a.Count) throw new IOException($"{name} needs a value");
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    private static bool Flag(List<string> a, string name) => a.Remove(name);

    private static int RunFile(string[] rest)
    {
        var a = rest.ToList();
        string? baseDir = Option(a, "--base-dir");
        string? record = Option(a, "--record");
        bool confine = Flag(a, "--confine");
        if (a.Count != 1) { System.Console.Error.Write(Usage); return 2; }
        string file = Path.GetFullPath(a[0]);
        byte[] bytes = File.ReadAllBytes(file);
        RunResult r = Runner.Run(bytes, new RunOptions(baseDir ?? Path.GetDirectoryName(file)!, System.Console.Out,
            Licence: LicenceGate.Resolve(), ConfineToBaseDirectory: confine));
        if (record is not null) File.WriteAllBytes(Path.GetFullPath(record), r.RecordBytes);
        if (r.Error is { } e)
        {
            System.Console.Error.WriteLine($"{Path.GetFileName(file)}:{e.Pos.Line}:{e.Pos.Column}: {e.Phase.ToString().ToLowerInvariant()} error: {e.Message}");
            return 1;
        }
        return 0;
    }

    private static int CheckFile(string[] rest)
    {
        if (rest.Length != 1) { System.Console.Error.Write(Usage); return 2; }
        string file = Path.GetFullPath(rest[0]);
        ScriptException? e = Runner.Check(Runner.Decode(File.ReadAllBytes(file)));
        if (e is null)
        {
            System.Console.Out.WriteLine($"{Path.GetFileName(file)}: grammar {Grammar.VersionText}, syntax and procedures ok");
            return 0;
        }
        System.Console.Error.WriteLine($"{Path.GetFileName(file)}:{e.Pos.Line}:{e.Pos.Column}: {e.Phase.ToString().ToLowerInvariant()} error: {e.Message}");
        return 1;
    }

    private static int Repl(string[] rest)
    {
        var a = rest.ToList();
        string baseDir = Path.GetFullPath(Option(a, "--base-dir") ?? ".");
        string recordDir = Path.GetFullPath(Option(a, "--record-dir") ?? Path.Combine(baseDir, "gridsim-sessions"));
        bool confine = Flag(a, "--confine");
        if (a.Count != 0) { System.Console.Error.Write(Usage); return 2; }

        Directory.CreateDirectory(recordDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string gssPath = Path.Combine(recordDir, $"session-{stamp}{Grammar.Extension}");
        using var rec = new StreamWriter(new FileStream(gssPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

        var console = new InteractiveConsole(baseDir, text => rec.WriteLine(text));
        console.Session.Licence = LicenceGate.Resolve();
        console.Session.ConfineToBaseDirectory = confine;
        System.Console.Out.WriteLine($"gridsim-script {GridSim.Core.BuildInfo.Version}, grammar {Grammar.VersionText}; recording to {gssPath}");
        System.Console.Out.WriteLine("type statements, 'help' for the verbs, 'exit' to finish");

        string? input;
        while (true)
        {
            System.Console.Out.Write(!console.InBlock ? "gss> " : "...> ");
            input = System.Console.In.ReadLine();
            if (input is null) break;
            string trimmed = input.Trim();
            if (!console.InBlock)
            {
                if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    System.Console.Out.WriteLine("verbs: " + string.Join(", ", Grammar.Verbs) + "; FOR, IF and PROC open a block closed by END; see the manual chapter \"Scripting\"");
                    continue;
                }
            }
            ConsoleReply reply = console.Submit(input);
            if (reply.Kind == ConsoleReplyKind.Refused) System.Console.Error.WriteLine($"refused: {reply.Error!.Display}");
            else if (reply.Output.Length > 0) System.Console.Out.Write(reply.Output);
        }
        ConsoleRecording done = console.Finish();
        rec.Flush();
        rec.Dispose();
        string runPath = Path.ChangeExtension(gssPath, ".run.json");
        File.WriteAllBytes(runPath, done.RunRecordBytes);
        System.Console.Out.WriteLine($"session recorded: {gssPath}\nrun record: {runPath}");
        return 0;
    }
}
