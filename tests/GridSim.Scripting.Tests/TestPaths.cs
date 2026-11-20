namespace GridSim.Scripting.Tests;

internal static class TestPaths
{
    public static string Case(string name) =>
        Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", name + ".m");

    /// <summary>A script line that loads a bundled case, with the 60 Hz both test cases are.</summary>
    public static string Load(string name) => $"LOAD CASE \"{Case(name).Replace("\\", "\\\\", StringComparison.Ordinal)}\" FREQUENCY 60 HZ";

    /// <summary>The GB synthetic case folder (2227 buses, a protection bundle, per-unit inertia).</summary>
    public static string GbFolder => Path.Combine(AppContext.BaseDirectory, "data", "gb-synthetic");

    public static string LoadGb() => $"LOAD CASE \"{GbFolder.Replace("\\", "\\\\", StringComparison.Ordinal)}\"";

    private static readonly Lazy<string> FrequencyOnlyGb = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-script-gb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(GbFolder, "*.json"))
        {
            string text = File.ReadAllText(f);
            if (Path.GetFileName(f) == "asset-classes.json")
                text = System.Text.RegularExpressions.Regex.Replace(text, "\"lvrtVoltagePu\": 0[.][1-9][0-9]*", "\"lvrtVoltagePu\": 0.0");
            File.WriteAllText(Path.Combine(dir, Path.GetFileName(f)), text);
        }
        return dir;
    });

    /// <summary>The GB synthetic case with the ride-through (voltage) stages of its inverters switched off, so every stage left is a frequency stage.</summary>
    public static string LoadGbFrequencyOnly() => $"LOAD CASE \"{FrequencyOnlyGb.Value.Replace("\\", "\\\\", StringComparison.Ordinal)}\"";

    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-script-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Run script lines in a fresh session; throws the first error.</summary>
    public static Session Run(string baseDir, params string[] lines)
    {
        var s = new Session(baseDir);
        foreach (Statement st in Parser.ParseScript(string.Join("\n", lines))) s.Execute(st);
        return s;
    }

    /// <summary>Execute more script text in an existing session; returns everything it printed.</summary>
    public static string Exec(Session s, params string[] lines)
    {
        var o = new System.Text.StringBuilder();
        foreach (Statement st in Parser.ParseScript(string.Join("\n", lines))) o.Append(s.Execute(st));
        return o.ToString();
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string RawDynamicsFixture => Path.Combine(AppContext.BaseDirectory, "dynamics", "wscc9-genrou.dynamics.json");

    private static readonly Lazy<string> PatchedFixture = new(() =>
    {
        // The bus 1 machine of the fixture takes its inertia from the case, which a MATPOWER file does not carry: give it one.
        string text = File.ReadAllText(RawDynamicsFixture).Replace("\"damping\": 0.0,", "\"inertiaH\": 12.0, \"damping\": 0.0,", StringComparison.Ordinal)
            .Replace("\"valveMaxPu\": 1.5", "\"valveMaxPu\": 2.5", StringComparison.Ordinal);   // the 163 MW unit needs a wider valve than the fixture gives
        string f = Path.Combine(TempDir(), "wscc9-genrou.dynamics.json");
        File.WriteAllText(f, text);
        return f;
    });

    public static string DynamicsFixture => PatchedFixture.Value;

    public static string SchemasDir => Path.Combine(AppContext.BaseDirectory, "schemas", "v2");

    /// <summary>The WITH clause of an RMS SUBTRANSIENT or GOVERNED selection that names the case9 dynamic data and the schemas.</summary>
    public static string WithDynamics(string? file = null) => $"WITH DYNAMICS \"{Esc(file ?? DynamicsFixture)}\" SCHEMAS \"{Esc(SchemasDir)}\"";

    /// <summary>The fixture with every governor removed, in a temp file.</summary>
    public static string DynamicsWithoutGovernors()
    {
        string text = File.ReadAllText(DynamicsFixture);
        text = System.Text.RegularExpressions.Regex.Replace(text, ",\\s*\"governor\": \"[^\"]*\"", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\{\\s*\"governor\": \"[^\"]*\"\\s*\\}", "{ }");
        string f = Path.Combine(TempDir(), "nogov.dynamics.json");
        File.WriteAllText(f, text);
        return f;
    }
}
