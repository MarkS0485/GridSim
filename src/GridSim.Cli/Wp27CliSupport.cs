using System.Text.Json;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP27: the argument and result-file handling shared by harmonic-pf, harmonic-se, cosim and emt-partition. Each verb
/// writes its own JSON result (<c>--out</c>), never replaces an existing file, and is not on the shared
/// <c>--json</c>/<c>--csv</c> writer.
/// </summary>
internal static class Wp27CliSupport
{
    /// <summary>The parsed command line of one verb.</summary>
    internal sealed class Args
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Switches { get; } = new(StringComparer.Ordinal);
        public string? Get(string flag) => Values.TryGetValue(flag, out string? v) ? v : null;
    }

    /// <summary>Parse <paramref name="a"/> (verb first). Returns null and prints the reason (exit 2) on a usage error.</summary>
    internal static Args? Parse(List<string> a, string verb, string usage, string[] valueFlags, string[] switchFlags)
    {
        var r = new Args();
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (Array.IndexOf(valueFlags, t) >= 0)
            {
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"error: {verb}: {t} expected a value. {usage}");
                    return null;
                }
                if (r.Values.ContainsKey(t)) { Console.Error.WriteLine($"error: {verb}: {t} was given twice. {usage}"); return null; }
                r.Values[t] = a[++i];
                continue;
            }
            if (Array.IndexOf(switchFlags, t) >= 0)
            {
                if (!r.Switches.Add(t)) { Console.Error.WriteLine($"error: {verb}: {t} was given twice. {usage}"); return null; }
                continue;
            }
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: {verb} does not use {t}. {usage}");
                return null;
            }
            r.Positional.Add(t);
        }
        return r;
    }

    /// <summary>Check an output file: the re-run guard, and never an existing file or folder. Returns the full path, or null
    /// after printing the refusal (exit 2).</summary>
    internal static bool PrepareOutput(string? path, string verb, string what, out string? full)
    {
        full = null;
        if (path is null) return true;
        full = Path.GetFullPath(path);
        CliFiles.ReplayGuard(full, what);
        if (File.Exists(full) || Directory.Exists(full))
        {
            Console.Error.WriteLine($"error: {verb}: '{full}' already exists; name a new file (it is never overwritten).");
            return false;
        }
        return true;
    }

    /// <summary>Write a JSON result to a new file (indented, numbers in their shortest round-trip form). CLI-17: the document
    /// is built in memory and written to a temporary sibling that is then moved to the name (never over an existing file),
    /// so a body that throws (a non-finite number, a full disk) leaves no half-written file to block the next run.</summary>
    internal static void WriteJson(string full, Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                buffer.Position = 0;
                buffer.CopyTo(fs);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A finite number, or null for NaN (JSON has no NaN).</summary>
    internal static void Number(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    /// <summary>R8-01: an array element: a finite number, or null for NaN or an infinity (JSON has neither).</summary>
    internal static void NumberValue(Utf8JsonWriter w, double v)
    {
        if (double.IsFinite(v)) w.WriteNumberValue(v);
        else w.WriteNullValue();
    }

    /// <summary>R8-01: a named array of numbers (a time axis or a trace), each through <see cref="NumberValue"/>, so a
    /// diverged run's NaN or infinite sample is null instead of an exception inside the writer.</summary>
    internal static void Series(Utf8JsonWriter w, string name, IEnumerable<double> values)
    {
        w.WriteStartArray(name);
        foreach (double v in values) NumberValue(w, v);
        w.WriteEndArray();
    }
}
