using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP11: <c>gridsim rpc --protocol 2</c> (<c>gridsim-rpc/2</c>) run as the built CLI. Oracles: the level 1 transcript
/// (level 1 is byte for byte unchanged, and level 2 differs from it only in the protocol label), the <c>pf</c> verb on a
/// MATPOWER file edited by text (for an edit then solve), and <c>pf</c> on the original file (for a study of a written and
/// re-read copy). Numbers are compared bit for bit.
/// </summary>
public sealed class Rc5Wp11RpcV2ProcessTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-wp11-rpc-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp11RpcV2ProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Lib(string name) => Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name);

    private static string Esc(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private ProcessStartInfo Start(params string[] flags)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        psi.ArgumentList.Add("rpc");
        foreach (string f in flags) psi.ArgumentList.Add(f);
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(_dir, CliProcess.IsolatedLicenceFileName);
        return psi;
    }

    /// <summary>Run <c>gridsim rpc [flags]</c> over stdin and stdout; returns the exit code and the response lines.</summary>
    private (int Exit, string[] Lines) Session(string[] requests, params string[] flags)
    {
        using Process p = Process.Start(Start(flags))!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        foreach (string r in requests) p.StandardInput.Write(r + "\n");
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(600_000), "rpc did not finish");
        p.WaitForExit();
        _ = err.Result;
        return (p.ExitCode, outText.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>RC5 REVIEW-FIX-6: level 2 handles carry the host's token, so a transcript cannot name them in advance. Send the
    /// load (id 1), read its handle, then send <paramref name="rest"/> (given the <c>"handle":"..."</c> field) and close.</summary>
    private (int Exit, string[] Lines, string Handle) LoadThen(string loadFields, Func<string, string[]> rest, params string[] flags)
    {
        using Process p = Process.Start(Start(flags))!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(Req(1, "load", loadFields) + "\n");
        p.StandardInput.Flush();
        string first = p.StandardOutput.ReadLine()!;
        string handle = J(first).GetProperty("result").GetProperty("handle").GetString()!;
        foreach (string r in rest("\"handle\":\"" + handle + "\"")) p.StandardInput.Write(r + "\n");
        p.StandardInput.Close();
        string remaining = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(600_000), "rpc did not finish");
        p.WaitForExit();
        _ = err.Result;
        return (p.ExitCode, [first, .. remaining.Split('\n', StringSplitOptions.RemoveEmptyEntries)], handle);
    }

    private static JsonElement J(string line)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        return d.RootElement.Clone();
    }

    private static string Req(int id, string op, string fields = "") => "{\"id\":" + id + ",\"op\":\"" + op + "\"" + (fields.Length > 0 ? "," + fields : "") + "}";

    private static string Tool(string line) => Regex.Replace(line, "\"tool\":\"gridsim [^\"]*\"", "\"tool\":\"<tool>\"");

    /// <summary>The hello line with its build status (RELEASE or UNSUPPORTED BUILD, the one value that depends on how the binary
    /// was built) written as &lt;status&gt;.</summary>
    private static string Status(string line) => Regex.Replace(line, "\"build_status\":\"(RELEASE|UNSUPPORTED BUILD)\"", "\"build_status\":\"<status>\"");

    private static void SameBits(JsonElement a, JsonElement b)
    {
        Assert.Equal(a.GetArrayLength(), b.GetArrayLength());
        for (int i = 0; i < a.GetArrayLength(); i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].GetDouble()), BitConverter.DoubleToInt64Bits(b[i].GetDouble()));
    }

    /// <summary>The bus table of a pf result document as columns.</summary>
    private static (double[] Vm, double[] Va) PfColumns(JsonElement doc)
    {
        JsonElement[] rows = doc.GetProperty("tables").GetProperty("buses").EnumerateArray().ToArray();
        return (rows.Select(r => r.GetProperty("vm_pu").GetDouble()).ToArray(), rows.Select(r => r.GetProperty("va_deg").GetDouble()).ToArray());
    }

    private static void SameBits(double[] a, JsonElement b)
    {
        Assert.Equal(a.Length, b.GetArrayLength());
        for (int i = 0; i < a.Length; i++) Assert.Equal(BitConverter.DoubleToInt64Bits(a[i]), BitConverter.DoubleToInt64Bits(b[i].GetDouble()));
    }

    private static void SameBits(double[] a, double[] b)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(BitConverter.DoubleToInt64Bits(a[i]), BitConverter.DoubleToInt64Bits(b[i]));
    }

    [Fact]
    public void Protocol_1_is_byte_for_byte_unchanged_with_or_without_the_flag()
    {
        string[] requests =
        [
            Req(1, "hello"), Req(2, "load", "\"case\":\"case9\""), Req(3, "solve", "\"handle\":\"case-1\""), Req(4, "stats"),
            Req(5, "elements", "\"handle\":\"case-1\",\"kind\":\"bus\""), Req(6, "release", "\"handle\":\"case-1\""),
        ];
        var (e0, plain) = Session(requests);
        var (e1, one) = Session(requests, "--protocol", "1");
        Assert.Equal(0, e0);
        Assert.Equal(0, e1);
        Assert.Equal(plain, one);
        // The hello line as RC4 (2.1.079) wrote it, the tool version aside, plus the additive build_status after the tool.
        Assert.Equal("{\"protocol\":\"gridsim-rpc/1\",\"id\":1,\"op\":\"hello\",\"status\":\"ok\",\"exitCode\":0,\"case_sha256\":null,\"result\":"
            + "{\"protocol\":\"gridsim-rpc/1\",\"tool\":\"<tool>\",\"build_status\":\"<status>\",\"operations\":[\"hello\",\"load\",\"release\",\"solve\",\"stats\",\"run-verb\",\"line-constants\",\"shutdown\"]}}",
            Status(Tool(plain[0])));
        // A level 2 operation is unknown at level 1, with the level 1 list of operations.
        Assert.Equal("{\"protocol\":\"gridsim-rpc/1\",\"id\":5,\"op\":\"elements\",\"status\":\"error\",\"exitCode\":2,\"case_sha256\":null,\"error\":{\"kind\":\"unknown-operation\","
            + "\"message\":\"unknown operation 'elements'; the operations are: hello, load, release, solve, stats, run-verb, line-constants, shutdown.\"}}", plain[4]);

        // RC5 REVIEW-FIX-6: the level 1 load and solve lines pinned in full, each built from an independent source. The load
        // line: the documented identity of a built-in case, SHA-256 of "built-in:<name>:<build>" (the build read from hello's
        // tool), handle case-1. The solve line: every number is the raw text of the same number in the pf verb's result file
        // (the shared writer's round-trip spelling), in the documented field order.
        string build = J(plain[0]).GetProperty("result").GetProperty("tool").GetString()!["gridsim ".Length..];
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("built-in:case9:" + build)));
        string pfFile = Path.Combine(_dir, "pf-case9.json");
        Assert.Equal(0, CliProcess.RunBuilt("pf", "case9", "--json", pfFile).ExitCode);
        JsonElement pf = J(File.ReadAllText(pfFile));
        JsonElement sum = pf.GetProperty("summary");
        JsonElement[] rows = pf.GetProperty("tables").GetProperty("buses").EnumerateArray().ToArray();
        string Col(string name) => "[" + string.Join(",", rows.Select(r => r.GetProperty(name).GetRawText())) + "]";
        string name9 = sum.GetProperty("case").GetString()!;
        Assert.Equal("{\"protocol\":\"gridsim-rpc/1\",\"id\":2,\"op\":\"load\",\"status\":\"ok\",\"exitCode\":0,\"case_sha256\":\"" + sha + "\",\"result\":"
            + "{\"handle\":\"case-1\",\"case\":\"" + name9 + "\",\"buses\":9,\"branches\":9,\"case_identity\":\"built-in-name\"}}", plain[1]);
        Assert.Equal("{\"protocol\":\"gridsim-rpc/1\",\"id\":3,\"op\":\"solve\",\"status\":\"ok\",\"exitCode\":0,\"case_sha256\":\"" + sha + "\",\"result\":"
            + "{\"case\":\"" + name9 + "\",\"converged\":true,\"iterations\":" + sum.GetProperty("iterations").GetRawText()
            + ",\"max_mismatch_pu\":" + sum.GetProperty("max_mismatch_pu").GetRawText() + ",\"loss_mw\":" + sum.GetProperty("loss_mw").GetRawText()
            + ",\"loss_mvar\":" + sum.GetProperty("loss_mvar").GetRawText() + ",\"bus\":" + Col("bus") + ",\"vm_pu\":" + Col("vm_pu")
            + ",\"va_deg\":" + Col("va_deg") + ",\"p_inj_mw\":" + Col("p_inj_mw") + ",\"q_inj_mvar\":" + Col("q_inj_mvar") + "}}", plain[2]);

        // Level 2 answers the level 1 operations as level 1 does apart from the protocol label and the handle, which
        // carries the host's token (RC5 REVIEW-FIX-6).
        var (e2, two, handle2) = LoadThen("\"case\":\"case9\"", h => [Req(3, "solve", h)], "--protocol", "2");
        Assert.Equal(0, e2);
        Assert.Matches("^case-[0-9a-f]{16}-1$", handle2);
        string[] level1 = [plain[1].Replace("\"id\":2,", "\"id\":1,", StringComparison.Ordinal), plain[2]];
        for (int i = 0; i < 2; i++)
            Assert.Equal(level1[i], two[i].Replace("\"protocol\":\"gridsim-rpc/2\"", "\"protocol\":\"gridsim-rpc/1\"", StringComparison.Ordinal)
                .Replace("\"" + handle2 + "\"", "\"case-1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Level_2_hello_lists_the_level_1_operations_then_the_object_model()
    {
        var (exit, lines) = Session([Req(1, "hello")], "--protocol", "2");
        Assert.Equal(0, exit);
        JsonElement r = J(Assert.Single(lines));
        Assert.Equal("gridsim-rpc/2", r.GetProperty("protocol").GetString());
        Assert.Equal("gridsim-rpc/2", r.GetProperty("result").GetProperty("protocol").GetString());
        Assert.Equal(new[] { "hello", "load", "release", "solve", "stats", "run-verb", "line-constants", "shutdown", "elements", "get", "set", "edits", "study", "save", "add", "delete" },
            r.GetProperty("result").GetProperty("operations").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Matches("^[0-9a-f]{16}$", r.GetProperty("result").GetProperty("host").GetString()!);
        Assert.Equal(2, CliProcess.RunBuilt("rpc", "--protocol", "3").ExitCode);
    }

    [Fact]
    public void An_edit_then_solve_equals_pf_on_the_file_edited_the_same_way_and_the_undo_gives_the_original()
    {
        string edited = Path.Combine(_dir, "case9-bus9-off.m");
        File.WriteAllText(edited, File.ReadAllText(Lib("case9.m")).Replace("\t9\t1\t125\t50\t", "\t9\t1\t0\t0\t", StringComparison.Ordinal));
        string pfEdited = Path.Combine(_dir, "edited.json"), pfOriginal = Path.Combine(_dir, "original.json");
        Assert.Equal(0, CliProcess.RunBuilt("pf", edited, "--json", pfEdited).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("pf", Lib("case9.m"), "--json", pfOriginal).ExitCode);

        var (exit, lines, _) = LoadThen($"\"case\":\"{Esc(Lib("case9.m"))}\"", h =>
        [
            Req(2, "get", h + ",\"kind\":\"load\",\"key\":\"9\""),
            Req(3, "set", h + ",\"kind\":\"load\",\"key\":\"9\",\"values\":{\"pd\":0}"),
            Req(4, "set", h + ",\"kind\":\"load\",\"key\":\"9\",\"values\":{\"qd\":0}"),
            Req(5, "solve", h),
            Req(6, "set", h + ",\"kind\":\"load\",\"key\":\"9\",\"values\":{\"pd\":125,\"qd\":50}"),
            Req(7, "solve", h),
            Req(8, "edits", h),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        JsonElement got = J(lines[1]).GetProperty("result").GetProperty("values");
        Assert.Equal(125.0, got.GetProperty("pd").GetDouble());
        Assert.Equal(50.0, got.GetProperty("qd").GetDouble());
        Assert.Equal(1.0, got.GetProperty("status").GetDouble());
        Assert.Equal(1, J(lines[2]).GetProperty("result").GetProperty("revision").GetInt32());
        Assert.Equal(2, J(lines[3]).GetProperty("result").GetProperty("revision").GetInt32());

        (double[] vmE, double[] vaE) = PfColumns(J(File.ReadAllText(pfEdited)));
        JsonElement s1 = J(lines[4]).GetProperty("result");
        SameBits(vmE, s1.GetProperty("vm_pu"));
        SameBits(vaE, s1.GetProperty("va_deg"));

        (double[] vmO, double[] vaO) = PfColumns(J(File.ReadAllText(pfOriginal)));
        JsonElement s2 = J(lines[6]).GetProperty("result");
        SameBits(vmO, s2.GetProperty("vm_pu"));
        SameBits(vaO, s2.GetProperty("va_deg"));

        JsonElement edits = J(lines[7]).GetProperty("result");
        Assert.Equal(3, edits.GetProperty("revision").GetInt32());
        Assert.Equal(4, edits.GetProperty("edits").GetArrayLength());
        Assert.Equal("{\"revision\":1,\"kind\":\"load\",\"key\":\"9\",\"attribute\":\"pd\",\"before\":125,\"after\":0}", edits.GetProperty("edits")[0].GetRawText());
    }

    [Theory]
    [InlineData("case9.m")]
    [InlineData("case118.m")]
    [InlineData("sample-v35.raw")]
    public void A_study_of_an_edited_copy_equals_pf_on_the_original_file(string name)
    {
        string path = name.EndsWith(".raw", StringComparison.Ordinal)
            ? Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name) : Lib(name);
        string pfFile = Path.Combine(_dir, "pf-" + name + ".json");
        CliProcess.Result pf = CliProcess.RunBuilt("pf", path, "--json", pfFile);
        Assert.True(File.Exists(pfFile), pf.ToString());
        var (exit, lines, _) = LoadThen($"\"case\":\"{Esc(path)}\"", h =>
        [
            Req(2, "elements", h + ",\"kind\":\"generator\""),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        JsonElement first = J(lines[1]).GetProperty("result").GetProperty("rows")[0];
        string key = first.GetProperty("key").GetString()!;
        string pg = first.GetProperty("pg").GetRawText();
        var (exit2, lines2, _) = LoadThen($"\"case\":\"{Esc(path)}\"", h =>
        [
            Req(2, "set", h + $",\"kind\":\"generator\",\"key\":\"{key}\",\"values\":{{\"pg\":{pg}}}"),
            Req(3, "study", h + ",\"verb\":\"pf\",\"args\":[]"),
        ], "--protocol", "2");
        Assert.Equal(0, exit2);
        Assert.Equal(1, J(lines2[1]).GetProperty("result").GetProperty("revision").GetInt32());
        JsonElement study = J(lines2[2]);
        Assert.True(study.GetProperty("status").GetString() == "ok", lines2[2]);
        JsonElement doc = study.GetProperty("result").GetProperty("document");
        Assert.Equal("pf", doc.GetProperty("verb").GetString());
        Assert.Equal("[\"pf\"]", doc.GetProperty("args").GetRawText());   // the host's own case path is not echoed
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), lines2[2], StringComparison.OrdinalIgnoreCase);
        (double[] vmS, double[] vaS) = PfColumns(doc);
        (double[] vmO, double[] vaO) = PfColumns(J(File.ReadAllText(pfFile)));
        SameBits(vmO, vmS);
        SameBits(vaO, vaS);
    }

    [Fact]
    public void A_study_with_no_edit_is_the_clis_own_document_and_case14_cannot_be_written_as_v2()
    {
        var (exit, lines, _) = LoadThen($"\"case\":\"{Esc(Lib("case14.m"))}\"", h =>
        [
            Req(2, "study", h + ",\"verb\":\"pf\",\"args\":[]"),
            Req(3, "set", h + ",\"kind\":\"generator\",\"key\":\"G2\",\"values\":{\"pg\":40}"),
            Req(4, "study", h + ",\"verb\":\"pf\",\"args\":[]"),
            Req(5, "study", h + ",\"verb\":\"cpf\",\"args\":[]"),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        JsonElement plain = J(lines[1]).GetProperty("result").GetProperty("document");
        Assert.Equal(Lib("case14.m"), plain.GetProperty("args")[1].GetString());
        // The JSON v2 writer refuses a bus with no positive base voltage (case14 has baseKV 0), so the edited case is refused.
        JsonElement refused = J(lines[3]);
        Assert.Equal("error", refused.GetProperty("status").GetString());
        Assert.Contains("cannot be written as a JSON v2 case", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("study runs pf, nminus1", J(lines[4]).GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refusals_change_nothing()
    {
        // A UNC path cannot be written on Windows; elsewhere the same refusal is shown by a path whose parent folder does not
        // exist as a folder (the case file is named as the folder; a backslash name would be an ordinary file name there).
        string unc = OperatingSystem.IsWindows() ? Esc(@"\\server\share\x.raw") : Esc(Lib("case9.m")) + "/x.raw";
        var (exit, lines, _) = LoadThen($"\"case\":\"{Esc(Lib("case9.m"))}\"", h =>
        [
            Req(2, "get", h + ",\"kind\":\"transformer\",\"key\":\"1\""),
            Req(3, "set", h + ",\"kind\":\"load\",\"key\":\"9\",\"values\":{\"p\":1}"),
            Req(4, "set", h + ",\"kind\":\"generator\",\"key\":\"G2\",\"values\":{\"pg\":10,\"status\":2}"),
            Req(5, "set", h + ",\"kind\":\"bus\",\"key\":\"5\",\"values\":{\"base_kv\":1}"),
            Req(6, "edits", h),
            Req(7, "get", h + ",\"kind\":\"generator\",\"key\":\"G2\",\"attributes\":[\"pg\",\"status\"]"),
            Req(8, "save", h + $",\"path\":\"{unc}\",\"format\":\"raw33\""),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        Assert.Contains("unknown kind 'transformer'; the kinds are: bus, generator, load, branch, shunt.", lines[1], StringComparison.Ordinal);
        Assert.Contains("its attributes are: pd, qd, status", lines[2], StringComparison.Ordinal);
        Assert.Contains("status is 1 (in service) or 0", lines[3], StringComparison.Ordinal);
        Assert.Contains("read only", lines[4], StringComparison.Ordinal);
        foreach (int i in new[] { 1, 2, 3, 4 }) Assert.Equal("error", J(lines[i]).GetProperty("status").GetString());
        Assert.Equal("{\"revision\":0,\"edits\":[]}", J(lines[5]).GetProperty("result").GetRawText());
        Assert.Equal("{\"pg\":163,\"status\":1}", J(lines[6]).GetProperty("result").GetProperty("values").GetRawText());
        Assert.Equal("error", J(lines[7]).GetProperty("status").GetString());   // a UNC path cannot be written here either
    }

    [Fact]
    public void Save_writes_once_never_over_other_content_and_is_confined_over_listen()
    {
        var (exit, lines, _) = LoadThen("\"case\":\"case9\"", h =>
        [
            Req(2, "save", h + ",\"path\":\"out/c9.raw\",\"format\":\"raw33\""),
            Req(3, "save", h + ",\"path\":\"out/c9.raw\",\"format\":\"raw33\""),
            Req(4, "save", h + ",\"path\":\"out/c9.raw\",\"format\":\"raw34\""),
            Req(5, "save", h + ",\"path\":\"out/c9.case.json\",\"format\":\"v2\""),
            Req(6, "save", h + ",\"path\":\"out/c9.x\",\"format\":\"raw30\""),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        Assert.Equal("ok", J(lines[1]).GetProperty("status").GetString());
        Assert.Equal(J(lines[1]).GetProperty("result").GetRawText(), J(lines[2]).GetProperty("result").GetRawText());   // same bytes: accepted, unchanged
        Assert.Contains("already exists with different content", lines[3], StringComparison.Ordinal);
        JsonElement v2 = J(lines[4]).GetProperty("result").GetProperty("files");
        Assert.Equal(new[] { "c9.network.json", "c9.case.json" }, v2.EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());
        Assert.True(File.Exists(Path.Combine(_dir, "out", "c9.case.json")));
        Assert.Contains("format is one of raw33, raw34, raw35, v2", lines[5], StringComparison.Ordinal);
        Assert.Equal(J(lines[1]).GetProperty("result").GetProperty("files")[0].GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_dir, "out", "c9.raw")))));

        // Over --listen the path must stay inside the host's working folder, and UNC paths are refused.
        using Process p = Process.Start(Start("--protocol", "2", "--listen", "127.0.0.1:0"))!;
        try
        {
            JsonElement banner = J(p.StandardOutput.ReadLine()!);
            Assert.Equal("gridsim-rpc/2", banner.GetProperty("protocol").GetString());
            int port = int.Parse(banner.GetProperty("listening").GetString()!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            using var client = new TcpClient("127.0.0.1", port);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            string token = RpcListenSupport.Token(banner);   // CLI-03: every request over --listen carries the host's token
            string Ask(string line) { writer.WriteLine(RpcListenSupport.With(line, token)); return reader.ReadLine()!; }
            string loaded = Ask(Req(1, "load", "\"case\":\"case9\""));
            Assert.Contains("\"status\":\"ok\"", loaded, StringComparison.Ordinal);
            string h = "\"handle\":\"" + J(loaded).GetProperty("result").GetProperty("handle").GetString() + "\"";
            string outside = Esc(TestTemp.Under("gridsim-wp11-outside-" + Guid.NewGuid().ToString("N") + ".raw"));
            string r1 = Ask(Req(2, "save", h + $",\"path\":\"{outside}\",\"format\":\"raw33\""));
            Assert.Contains("it is outside the base folder", r1, StringComparison.Ordinal);
            // A backslash is a separator, and \\server\share a network path, only on Windows; elsewhere those are ordinary
            // file names, so the portable forms of the same two refusals are asserted there: "../" and an absolute path.
            string up = OperatingSystem.IsWindows() ? "..\\\\up.raw" : "../up.raw";
            string r2 = Ask(Req(3, "save", h + $",\"path\":\"{up}\",\"format\":\"raw33\""));
            Assert.Contains("it is outside the base folder", r2, StringComparison.Ordinal);
            string r3 = OperatingSystem.IsWindows()
                ? Ask(Req(4, "save", h + $",\"path\":\"{Esc(@"\\server\share\x.raw")}\",\"format\":\"raw33\""))
                : Ask(Req(4, "save", h + $",\"path\":\"/gridsim-wp11-absolute-{Guid.NewGuid():N}.raw\",\"format\":\"raw33\""));
            Assert.Contains(OperatingSystem.IsWindows() ? "network (UNC) or device path" : "it is outside the base folder", r3, StringComparison.Ordinal);
            string r4 = Ask(Req(5, "save", h + ",\"path\":\"inside.raw\",\"format\":\"raw33\""));
            Assert.Contains("\"status\":\"ok\"", r4, StringComparison.Ordinal);
            Assert.Contains("\"revision\":0", Ask(Req(6, "edits", h)), StringComparison.Ordinal);
            Ask(Req(7, "shutdown"));
        }
        finally
        {
            if (!p.WaitForExit(60_000)) p.Kill(entireProcessTree: true);
        }
        Assert.True(File.Exists(Path.Combine(_dir, "inside.raw")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "up.raw")));
    }

    [Fact]
    public void Branch_flows_read_the_solved_case_as_it_stands()
    {
        var (exit, lines, _) = LoadThen($"\"case\":\"{Esc(Lib("case9.m"))}\"", h =>
        [
            Req(2, "solve", h),
            Req(3, "get", h + ",\"kind\":\"branch\",\"key\":\"1-4\",\"attributes\":[\"p_from_mw\",\"q_from_mvar\",\"r\"]"),
            Req(4, "set", h + ",\"kind\":\"generator\",\"key\":\"G2\",\"values\":{\"pg\":150}"),
            Req(5, "solve", h),
            Req(6, "get", h + ",\"kind\":\"branch\",\"key\":\"1-4\",\"attributes\":[\"p_from_mw\"]"),
        ], "--protocol", "2");
        Assert.Equal(0, exit);
        // Bus 1 (the slack) has no load and no shunt and one branch, 1-4: its injection is that branch's from-end flow. The two
        // come from different formulas (the bus mismatch and the branch admittance), so they agree to rounding, not bits.
        // RC5 REVIEW-FIX-6, the rounding bound. Both evaluate P = SBASE Re(V1 conj(y (V1 - V4))) at the same solved state,
        // with y = 1/(r + jx) of branch 1-4 (r = 0, x = 0.0576, b = 0, no tap). Let T = SBASE |V1| |y| (|V1| + |V4|), the sum of
        // the magnitudes of the terms. Per side, with unit roundoff u = 2^-53: the two complex products y V1, y V4 err by at
        // most sqrt(5) u of their magnitude each (Brent, Percival and Zimmermann, 2007), the complex sum by u, the product
        // with conj V1 by sqrt(5) u and the scaling by SBASE by u: (2 sqrt 5 + 2) u T < 6.5 u T. Each voltage may also be
        // rebuilt from (vm, va in degrees) on one side: the degree-to-radian product and the cosine and sine each within one
        // ulp give at most 4 u of |V| per voltage, so at most 8 u T per side. The two sides differ by at most
        // 2 (6.5 + 8) u T = 29 u T; the assert allows 32 u T.
        static void Agrees(string solve, string get)
        {
            JsonElement r = J(solve).GetProperty("result");
            double inj = r.GetProperty("p_inj_mw")[0].GetDouble();
            double flow = J(get).GetProperty("result").GetProperty("values").GetProperty("p_from_mw").GetDouble();
            double v1 = r.GetProperty("vm_pu")[0].GetDouble(), v4 = r.GetProperty("vm_pu")[3].GetDouble();
            double t = 100.0 * v1 * (1.0 / 0.0576) * (v1 + v4);
            double bound = 32.0 * Math.Pow(2, -53) * t;
            Assert.True(Math.Abs(inj - flow) <= bound, $"{inj} vs {flow}: {Math.Abs(inj - flow)} above {bound}");
        }
        Agrees(lines[1], lines[2]);
        Assert.Equal(0.0, J(lines[2]).GetProperty("result").GetProperty("values").GetProperty("r").GetDouble());
        Agrees(lines[4], lines[5]);   // re-solved after the edit, not the cached flows of revision 0
        Assert.NotEqual(J(lines[2]).GetProperty("result").GetProperty("values").GetProperty("p_from_mw").GetDouble(),
            J(lines[5]).GetProperty("result").GetProperty("values").GetProperty("p_from_mw").GetDouble());
    }
}
