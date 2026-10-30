using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests.Editor;

/// <summary>
/// Shared helpers for the case editor tests (SCHEMA-EDITOR-PLAN.md section 9). The namespace is GridSim.Tests.Editor,
/// not GridSim.Tests.SchemaEditor, because the latter would shadow the GridSim.SchemaEditor project namespace.
/// Case files are read in place from the repository (found by walking up to GridSim.slnx); anything a test writes
/// goes into a fresh temporary copy that is deleted afterwards.
/// </summary>
internal static class EditorTestSupport
{
    /// <summary>The repository root: the nearest folder above the test binaries holding GridSim.slnx.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    /// <summary>data/ in the repository.</summary>
    public static string DataDir => Path.Combine(RepoRoot(), "data");

    /// <summary>schemas/ in the repository.</summary>
    public static string SchemasDir => Path.Combine(RepoRoot(), "schemas");

    /// <summary>
    /// The bundled v1 case folders, relative to data/ with forward slashes: uk, the GB cases and every intl case
    /// folder (the same list the v2 projection tests use, plus uk).
    /// </summary>
    public static IReadOnlyList<string> BundledCaseDirs()
    {
        var cases = new List<string> { "uk", "gb-synthetic", "gb-full" };
        foreach (string dir in Directory.EnumerateDirectories(Path.Combine(DataDir, "intl")).OrderBy(d => d, StringComparer.Ordinal))
            cases.Add("intl/" + Path.GetFileName(dir));
        return cases;
    }

    /// <summary>The bundled case list as xUnit theory data.</summary>
    public static TheoryData<string> BundledCases()
    {
        var data = new TheoryData<string>();
        foreach (string c in BundledCaseDirs()) data.Add(c);
        return data;
    }

    /// <summary>The absolute folder of a bundled case.</summary>
    public static string CaseDir(string relative) => Path.Combine(DataDir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A UTF-8 byte array from text (no BOM).</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Parse a JSON text the lenient v1 way (comments, trailing commas) into a detached element.</summary>
    public static JsonElement Element(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// A temporary copy of a folder (top-level files only, as a case folder has), deleted when disposed. The copy is
    /// OUTSIDE data/, so it is not a Bundled Network for the licence gate.
    /// </summary>
    public sealed class TempCopy : IDisposable
    {
        public TempCopy(string sourceDir)
        {
            Dir = Path.Combine(Path.GetTempPath(), "gridsim-editor-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            foreach (string f in Directory.EnumerateFiles(sourceDir))
                File.Copy(f, Path.Combine(Dir, Path.GetFileName(f)));
        }

        /// <summary>The copy's folder.</summary>
        public string Dir { get; }

        /// <summary>Every file of the copy with its bytes and last-write time, by file name.</summary>
        public Dictionary<string, (byte[] Bytes, DateTime Mtime)> Snapshot()
            => Directory.EnumerateFiles(Dir).ToDictionary(p => Path.GetFileName(p), f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)), StringComparer.Ordinal);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { /* a leftover temp folder is harmless; never fail a test on cleanup */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
