using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP11: the RC5 verbs joined the shared <c>--json</c> / <c>--csv</c> writer (coordinator ruling; one structural edit to
/// CliResultWriterTests). The existing writer verbs' files must be byte for byte what they were: the SHA-256 of every CSV
/// file, and of the JSON document with its <c>tool</c> line (the build stamp) left out, are pinned below from the 2.1.090
/// binary (dev main 8e4accd0), run exactly as here (working folder a fresh temporary folder, file names relative).
/// </summary>
public sealed class Rc5Wp11WriterVerbsTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-wp11-writer-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp11WriterVerbsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Run the built CLI in this test's folder (relative output names, as the pins were made).</summary>
    private (int Exit, string StdOut) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(_dir, CliProcess.IsolatedLicenceFileName);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(300_000));
        p.WaitForExit();
        _ = e.Result;
        return (p.ExitCode, o.Result);
    }

    private string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(Rc5LineEndings.ToCrLf(File.ReadAllBytes(Path.Combine(_dir, file)))));

    /// <summary>The SHA-256 of a JSON result file's bytes with its "tool" line (the version and commit of the build) taken
    /// out: the bytes are split at each line feed, the line starting <c>  "tool": </c> is dropped (and the build status line
    /// <c>  "buildStatus": </c> that qualifies it, a later additive field of the same build identity), and the rest are joined
    /// with line feeds again (every other byte, the writer's carriage returns included, kept as written).</summary>
    private string ShaWithoutTool(string file)
    {
        byte[] bytes = Rc5LineEndings.ToCrLf(File.ReadAllBytes(Path.Combine(_dir, file)));
        byte[] tool = Encoding.ASCII.GetBytes("  \"tool\": ");
        byte[] status = Encoding.ASCII.GetBytes("  \"buildStatus\": ");
        var kept = new List<byte>(bytes.Length);
        bool first = true;
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            ReadOnlySpan<byte> line = bytes.AsSpan(start, i - start);
            if (!line.StartsWith(tool) && !line.StartsWith(status))
            {
                if (!first) kept.Add((byte)'\n');
                kept.AddRange(line.ToArray());
                first = false;
            }
            start = i + 1;
        }
        return Convert.ToHexStringLower(SHA256.HashData(kept.ToArray()));
    }

    [Theory]
    [InlineData("pf", "case9", 0, "66796ee686596ad144b629e3da4bfa1bdeab2b823cb94240007023b3b3981ee9",
        "pf.csv=90db0b43ecd0f8bfe554f6f69c6e807fd22ee5bc1812e26be530821795e86de8;pf-summary.csv=99e5dc08c0e1919897857d5a0026ca298a486dc66fb8fcbc25e3b1212192e08e")]
    [InlineData("nminus1", "case9", 3, "d7e2f43316658613736a2d774fe0a70852047b453e5ed1a05908e1b75f272baf",
        "nminus1.csv=5e55253adc600971fc88db04558c5fabadf6c592bd1d808bbc7dea38921f98e0;nminus1-summary.csv=b34c504a326d8d504f97f9ec6367498f7ee263169093f85f3a907b0bc762de23;nminus1-excluded_single_outages.csv=15e74d5f80019e5b25dc3ba520500e03946f67d8786c26c95d110f04b6f56c1c")]
    [InlineData("dcopf", "case30", 0, "34959ee17e412a121cb0841ff993e56238bf06f3c5b707153cd5d738a3c8280f",
        "dcopf.csv=f6df5d118f682f60307497a12c2cee1acb48041d638a66985b3316c845e19842;dcopf-summary.csv=56e6939a048e2219df66dc151b2f454d2a6144b655011822bb7278fd92c55187;dcopf-binding_lines.csv=c0eef351cf675a3059f9b09b1627e1d3d1ccfb6a84fed66e3c45373d24034611;dcopf-islands.csv=f14ec585ceb56257e3fab691734556761be0f3219cef9332bfcf75ed907b536d")]
    public void Existing_writer_verbs_write_the_same_bytes_as_2_1_090(string verb, string caseName, int exit, string jsonSha, string csvShas)
    {
        var (code, _) = Run(verb, caseName, "--json", verb + ".json", "--csv", verb + ".csv");
        Assert.Equal(exit, code);
        Assert.Equal(jsonSha, ShaWithoutTool(verb + ".json"));
        var expected = csvShas.Split(';').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(_dir, verb + "*.csv").Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        foreach ((string file, string sha) in expected) Assert.Equal(sha, Sha(file));
    }

    private JsonElement Doc(string file)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, file)));
        return d.RootElement.Clone();
    }

    [Fact]
    public void Reduce_writes_its_result_and_its_console_text_does_not_change()
    {
        var (plainExit, plain) = Run("reduce", "case14", "--keep", "1,2,3,4,5", "--method", "ward");
        var (exit, withJson) = Run("reduce", "case14", "--keep", "1,2,3,4,5", "--method", "ward", "--json", "r.json", "--csv", "r.csv");
        Assert.Equal(0, plainExit);
        Assert.Equal(0, exit);
        Assert.Equal(plain, withJson);
        JsonElement d = Doc("r.json");
        Assert.Equal("reduce", d.GetProperty("verb").GetString());
        Assert.Equal("ward", d.GetProperty("summary").GetProperty("method").GetString());
        Assert.Equal(5, d.GetProperty("summary").GetProperty("reduced_buses").GetInt32());
        Assert.True(d.GetProperty("tables").GetProperty("elements").GetArrayLength() > 0);
        Assert.True(File.Exists(Path.Combine(_dir, "r-summary.csv")));
    }

    [Fact]
    public void Acopf_opt_writes_the_rows_of_its_own_result_file()
    {
        var (exit, _) = Run("acopf-opt", "case9", "--objective", "losses", "--json", "o.json", "--out", "own.json");
        Assert.Equal(0, exit);
        JsonElement d = Doc("o.json");
        Assert.Equal("acopf-opt", d.GetProperty("verb").GetString());
        Assert.Equal("losses", d.GetProperty("summary").GetProperty("objective_kind").GetString());
        // The verb's own --out is still its own file; the shared table holds exactly its rows.
        JsonElement own = Doc("own.json");
        Assert.Equal(own.GetProperty("rows").GetArrayLength(), d.GetProperty("tables").GetProperty("rows").GetArrayLength());
        JsonElement firstOwn = own.GetProperty("rows")[3], firstShared = d.GetProperty("tables").GetProperty("rows")[3];
        Assert.Equal(firstOwn[2].GetString(), firstShared.GetProperty("field").GetString());
        Assert.Equal(firstOwn[3].GetRawText(), firstShared.GetProperty("value").GetRawText());
    }

    [Fact]
    public void Cgmes_shacl_writes_its_result_and_exits_as_cgmes_conform_does()
    {
        Assert.Equal(0, Run("cgmes-export", Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"), "--dir", "bundle", "--profiles", "sc").Exit);
        File.WriteAllText(Path.Combine(_dir, "none.ttl"), "@prefix sh: <http://www.w3.org/ns/shacl#> .\n@prefix ex: <http://example.org/> .\n[] a sh:NodeShape ; sh:targetClass ex:Nothing ; sh:property [ sh:path ex:p ; sh:minCount 1 ] .\n");
        File.WriteAllText(Path.Combine(_dir, "fail.ttl"), "@prefix sh: <http://www.w3.org/ns/shacl#> .\n@prefix ex: <http://example.org/> .\n[] a sh:NodeShape ; sh:targetNode ex:x ; sh:property [ sh:path ex:p ; sh:minCount 1 ] .\n");
        var (ok, _) = Run("cgmes-shacl", "bundle", "--shapes", "none.ttl", "--json", "ok.json");
        Assert.Equal(0, ok);
        Assert.True(Doc("ok.json").GetProperty("summary").GetProperty("conforms").GetBoolean());
        var (bad, _) = Run("cgmes-shacl", "bundle", "--shapes", "fail.ttl", "--json", "bad.json");
        Assert.Equal(3, bad);
        JsonElement d = Doc("bad.json");
        Assert.Equal(3, d.GetProperty("exitCode").GetInt32());
        Assert.False(d.GetProperty("summary").GetProperty("conforms").GetBoolean());
        Assert.Equal(1, d.GetProperty("tables").GetProperty("results").GetArrayLength());
        Assert.Equal(1, Run("cgmes-shacl", "no-such-bundle", "--shapes", "none.ttl").Exit);
    }
}
