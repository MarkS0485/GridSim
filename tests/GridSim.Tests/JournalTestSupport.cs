using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal): runs the built CLI in a chosen working folder, isolated from the machine's licence and journal
/// configuration, and the helpers the journal and breaking-change tests share.
/// </summary>
internal static class JournalTestSupport
{
    /// <summary>A fresh temporary folder.</summary>
    internal static string NewDir(string tag)
    {
        string d = TestTemp.Under("gridsim-" + tag + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    internal static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Run the built CLI with <paramref name="cwd"/> as its working folder. The child never sees the machine's licence
    /// or journal: GRIDSIM_LICENCE is removed, GRIDSIM_LICENCE_FILE and GRIDSIM_JOURNAL_CONFIG point at files that do
    /// not exist, and GRIDSIM_JOURNAL is removed, unless <paramref name="env"/> sets them.
    /// </summary>
    internal static CliProcess.Result Run(string cwd, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        string isolation = Path.Combine(Path.GetTempPath(), "gridsim-journal-isolation");
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(isolation, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(isolation, "no-licence.gridsim");
        psi.Environment["GRIDSIM_JOURNAL_CONFIG"] = Path.Combine(isolation, "no-journal-config.json");
        psi.Environment.Remove("GRIDSIM_JOURNAL");
        psi.Environment.Remove("GRIDSIM_REPLAY_SCRATCH");
        if (env is not null)
            foreach (KeyValuePair<string, string?> kv in env)
            {
                if (kv.Value is null) psi.Environment.Remove(kv.Key);
                else psi.Environment[kv.Key] = kv.Value;
            }
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(160_000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("gridsim " + string.Join(' ', args) + " did not finish in 160 s");
        }
        p.WaitForExit();
        return new CliProcess.Result(p.ExitCode, o.Result, e.Result);
    }

    internal static CliProcess.Result Run(string cwd, params string[] args) => Run(cwd, null, args);

    /// <summary>
    /// Make every entry of a journal look recorded by another build of the same version (its commit replaced), so that
    /// <c>breaking-change validate</c> re-runs it instead of skipping it as recorded by this very build.
    /// </summary>
    internal static void FromAnotherBuild(string journal, string commit = "0000000000000000000000000000000000000000")
    {
        string text = File.ReadAllText(journal);
        text = Regex.Replace(text, "\"commit\":\"[^\"]*\"", "\"commit\":\"" + commit + "\"");
        File.WriteAllText(journal, text, new UTF8Encoding(false));
    }

    /// <summary>The number of lines in a journal file.</summary>
    internal static int Lines(string journal) => File.Exists(journal) ? File.ReadAllLines(journal).Count(l => l.Length > 0) : 0;
}
