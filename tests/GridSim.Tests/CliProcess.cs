using System.Diagnostics;

namespace GridSim.Tests;

/// <summary>
/// F11: runs the real gridsim CLI as a separate process and captures its exit code, stdout and stderr. Used by the
/// CLI smoke tests (the built gridsim.dll, through the dotnet host) and the published-output tests (a published
/// self-contained executable, run directly).
///
/// Every run gets a fresh temporary working folder (so a verb that writes output files never writes into the source
/// tree) and its own GRIDSIM_CRASH_DIR (so a crash in a test leaves its log in the temp folder, not the user's). The
/// child inherits every other environment variable, deliberately including DOTNET_EnableAVX2 and
/// DOTNET_EnableHWIntrinsic, so the CI SIMD matrix runs the CLI in the same reduced vector tier as the tests.
/// </summary>
internal static class CliProcess
{
    /// <summary>The outcome of one run.</summary>
    internal sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}";
    }

    /// <summary>The repository root (the folder holding GridSim.slnx).</summary>
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repository root (GridSim.slnx) not found above " + AppContext.BaseDirectory);
        return dir.FullName;
    }

    /// <summary>
    /// The CLI built alongside this test assembly: src/GridSim.Cli/bin/&lt;Configuration&gt;/net10.0/gridsim.dll, where
    /// the configuration is the one this test assembly was built in (read from its own bin path). The test project
    /// references the CLI project (without linking it), so the build always produces it first.
    /// </summary>
    internal static string BuiltCliDll()
    {
        // AppContext.BaseDirectory = .../tests/GridSim.Tests/bin/<Configuration>/<tfm>/
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string tfm = tfmDir.Name;
        string configuration = tfmDir.Parent!.Name;
        string dll = Path.Combine(Root(), "src", "GridSim.Cli", "bin", configuration, tfm, "gridsim.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("the built CLI was not found; build the solution first", dll);
        return dll;
    }

    /// <summary>The dotnet host that is running these tests (DOTNET_HOST_PATH is set by `dotnet test`).</summary>
    private static string DotnetHost()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(host) ? "dotnet" : host;
    }

    /// <summary>Run the built CLI (dotnet gridsim.dll args).</summary>
    internal static Result RunBuilt(params string[] args)
        => Run(DotnetHost(), new[] { BuiltCliDll() }.Concat(args).ToArray());

    /// <summary>
    /// Run the built CLI with some environment variables overridden for the child only (D06): a non-null value sets
    /// the variable, a null value removes it. The test process's own environment is never changed.
    /// </summary>
    internal static Result RunBuiltWithEnvironment(IReadOnlyDictionary<string, string?> environment, params string[] args)
        => Run(DotnetHost(), new[] { BuiltCliDll() }.Concat(args).ToArray(), environment: environment);

    /// <summary>Run an executable with arguments, in a fresh temp folder, with a timeout. Never hangs a test run.</summary>
    internal static Result Run(string exe, string[] args, int timeoutMs = 120_000, IReadOnlyDictionary<string, string?>? environment = null)
    {
        string work = Path.Combine(Path.GetTempPath(), "gridsim-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,     // a verb that waits for a key sees end-of-input, never blocks
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(work, "crash");
            if (environment is not null)
                foreach (KeyValuePair<string, string?> kv in environment)
                {
                    if (kv.Value is null) psi.Environment.Remove(kv.Key);
                    else psi.Environment[kv.Key] = kv.Value;
                }

            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
            p.StandardInput.Close();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException($"{exe} {string.Join(' ', args)} did not finish in {timeoutMs} ms");
            }
            p.WaitForExit();   // flush the redirected streams
            return new Result(p.ExitCode, stdout.Result, stderr.Result);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
