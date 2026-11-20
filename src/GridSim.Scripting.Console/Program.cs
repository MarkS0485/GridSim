using System.Globalization;
using System.Text;
using GridSim.Scripting;

namespace GridSim.Scripting.Console;

/// <summary>
/// gridsim-script: the console front end of GridSim.Scripting.
///   gridsim-script run FILE.gss [--base-dir DIR] [--record RUN.json]   run a script, optionally write the run record
///   gridsim-script check FILE.gss                                      parse a script, run nothing
///   gridsim-script [repl] [--base-dir DIR] [--record-dir DIR]          interactive session, recorded as a .gss file
/// Exit codes: 0 ok, 1 the script was refused (the error names line and column), 2 usage or file error.
/// </summary>
public static class Program
{
    private const string Usage =
        "usage:\n" +
        "  gridsim-script run FILE.gss [--base-dir DIR] [--record RUN.json]\n" +
        "  gridsim-script check FILE.gss\n" +
        "  gridsim-script [repl] [--base-dir DIR] [--record-dir DIR]\n" +
        "  gridsim-script --version | --help\n";

    public static int Main(string[] args)
    {
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

    private static int RunFile(string[] rest)
    {
        var a = rest.ToList();
        string? baseDir = Option(a, "--base-dir");
        string? record = Option(a, "--record");
        if (a.Count != 1) { System.Console.Error.Write(Usage); return 2; }
        string file = Path.GetFullPath(a[0]);
        byte[] bytes = File.ReadAllBytes(file);
        RunResult r = Runner.Run(bytes, new RunOptions(baseDir ?? Path.GetDirectoryName(file)!, System.Console.Out));
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
        if (a.Count != 0) { System.Console.Error.Write(Usage); return 2; }

        Directory.CreateDirectory(recordDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string gssPath = Path.Combine(recordDir, $"session-{stamp}{Grammar.Extension}");
        using var rec = new StreamWriter(new FileStream(gssPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

        var console = new InteractiveConsole(baseDir, text => rec.WriteLine(text));
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
