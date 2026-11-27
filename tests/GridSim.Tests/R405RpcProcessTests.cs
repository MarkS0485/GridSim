using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-05: <c>gridsim rpc</c> run as the built CLI. The oracles are the existing paths: the <c>pf</c> verb's result file
/// (for <c>solve</c>), the verb run as a subprocess (for <c>run-verb</c>), the <c>line-constants</c> verb (for the typed
/// operation) and the CLI's own gate message (for a licence refusal).
/// </summary>
public sealed class R405RpcProcessTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-r405-" + Guid.NewGuid().ToString("N"));

    public R405RpcProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Run <c>gridsim rpc</c> over stdin and stdout with the given request lines; returns the exit code and the response lines.</summary>
    private (int Exit, string[] Lines, string StdErr) Session(string[] requests, IReadOnlyDictionary<string, string?>? env = null, params string[] flags)
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
        if (env is not null)
            foreach (var kv in env)
                if (kv.Value is null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value;
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        foreach (string r in requests) p.StandardInput.Write(r + "\n");
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(180_000), "rpc did not finish");
        p.WaitForExit();
        string[] lines = outText.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (p.ExitCode, lines, err.Result);
    }

    private static JsonElement J(string line)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        return d.RootElement.Clone();
    }

    /// <summary>A JSON value as compact text (numbers keep their exact digits).</summary>
    private static string Norm(JsonElement e) => JsonSerializer.Serialize(e);

    private static string Req(int id, string op, string fields = "") => "{\"id\":" + id + ",\"op\":\"" + op + "\"" + (fields.Length > 0 ? "," + fields : "") + "}";

    [Fact]
    public void Hello_names_the_protocol_and_the_operations()
    {
        var (exit, lines, _) = Session([Req(1, "hello")]);
        Assert.Equal(0, exit);
        JsonElement r = J(Assert.Single(lines));
        Assert.Equal("gridsim-rpc/1", r.GetProperty("protocol").GetString());
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("id").GetInt32());
        Assert.Equal(new[] { "hello", "load", "release", "solve", "stats", "run-verb", "line-constants", "shutdown" },
            r.GetProperty("result").GetProperty("operations").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.StartsWith("gridsim ", r.GetProperty("result").GetProperty("tool").GetString());
    }

    [Fact]
    public void Solve_of_a_held_case_matches_the_pf_result_file_bit_for_bit()
    {
        string pfJson = Path.Combine(_dir, "pf.json");
        Assert.Equal(0, CliProcess.RunBuilt("pf", "case9", "--json", pfJson).ExitCode);
        JsonElement file = J(File.ReadAllText(pfJson));

        var (exit, lines, _) = Session([Req(1, "load", "\"case\":\"case9\""), Req(2, "solve", "\"handle\":\"case-1\"")]);
        Assert.Equal(0, exit);
        JsonElement load = J(lines[0]), solve = J(lines[1]);
        Assert.Equal("case-1", load.GetProperty("result").GetProperty("handle").GetString());
        Assert.Equal(64, load.GetProperty("case_sha256").GetString()!.Length);
        Assert.Equal(load.GetProperty("case_sha256").GetString(), solve.GetProperty("case_sha256").GetString());
        JsonElement res = solve.GetProperty("result");
        Assert.True(res.GetProperty("converged").GetBoolean());
        JsonElement sum = file.GetProperty("summary");
        Assert.Equal(sum.GetProperty("iterations").GetInt32(), res.GetProperty("iterations").GetInt32());
        Assert.Equal(BitConverter.DoubleToInt64Bits(sum.GetProperty("loss_mw").GetDouble()), BitConverter.DoubleToInt64Bits(res.GetProperty("loss_mw").GetDouble()));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sum.GetProperty("max_mismatch_pu").GetDouble()), BitConverter.DoubleToInt64Bits(res.GetProperty("max_mismatch_pu").GetDouble()));
        JsonElement[] rows = file.GetProperty("tables").GetProperty("buses").EnumerateArray().ToArray();
        Assert.Equal(rows.Length, res.GetProperty("bus").GetArrayLength());
        foreach ((string column, string key) in new[] { ("vm_pu", "vm_pu"), ("va_deg", "va_deg"), ("p_inj_mw", "p_inj_mw"), ("q_inj_mvar", "q_inj_mvar") })
        {
            JsonElement[] values = res.GetProperty(column).EnumerateArray().ToArray();
            for (int i = 0; i < rows.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(rows[i].GetProperty(key).GetDouble()), BitConverter.DoubleToInt64Bits(values[i].GetDouble()));
        }
        for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i].GetProperty("bus").GetInt32(), res.GetProperty("bus")[i].GetInt32());
    }

    [Fact]
    public void One_load_serves_a_thousand_identical_solves_and_stats_count_it()
    {
        var requests = new List<string> { Req(1, "load", "\"case\":\"case9\"") };
        for (int i = 0; i < 1000; i++) requests.Add(Req(2 + i, "solve", "\"handle\":\"case-1\""));
        requests.Add(Req(2000, "stats"));
        var (exit, lines, _) = Session(requests.ToArray());
        Assert.Equal(0, exit);
        Assert.Equal(1002, lines.Length);
        string Strip(string line) => line.Replace("\"id\":" + J(line).GetProperty("id").GetInt32() + ",", "");
        string first = Strip(lines[1]);
        for (int i = 2; i <= 1000; i++) Assert.Equal(first, Strip(lines[i]));   // same bytes on the 1000th as on the first
        JsonElement stats = J(lines[1001]).GetProperty("result");
        Assert.Equal(1, stats.GetProperty("loads").GetInt32());
        Assert.Equal(1000, stats.GetProperty("solves").GetInt32());
        Assert.Equal(1, stats.GetProperty("handles").GetInt32());
    }

    [Fact]
    public void The_same_requests_give_the_same_bytes()
    {
        string[] script = [Req(1, "hello"), Req(2, "load", "\"case\":\"case14\""), Req(3, "solve", "\"handle\":\"case-1\""),
            Req(4, "run-verb", "\"verb\":\"dcpf\",\"args\":[\"case14\"]"), Req(5, "release", "\"handle\":\"case-1\""), "not json", Req(6, "nonsense")];
        var a = Session(script);
        var b = Session(script);
        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(7, a.Lines.Length);
    }

    [Fact]
    public void Errors_are_responses_the_session_survives_and_map_to_the_exit_codes()
    {
        var (exit, lines, _) = Session([
            "this is not json",
            "[1,2]",
            Req(1, "frobnicate"),
            Req(2, "solve", "\"handle\":\"case-9\""),
            Req(3, "load", "\"case\":\"no-such-case\""),
            Req(4, "load", "\"case\":\"case9\",\"extra\":1"),
            Req(5, "load", "\"case\":\"/no/such/folder/x.m\""),
            Req(6, "run-verb", "\"verb\":\"serve\""),
            Req(7, "run-verb", "\"verb\":\"pf\",\"args\":[\"case9\",\"--json\",\"x.json\"]"),
            Req(8, "run-verb", "\"verb\":\"pf\",\"args\":[\"no-such-case\"]"),
            Req(9, "hello"),
        ]);
        Assert.Equal(0, exit);
        Assert.Equal(11, lines.Length);
        (string Kind, int Exit)[] want =
        [
            ("malformed-request", 2), ("malformed-request", 2), ("unknown-operation", 2), ("unknown-handle", 2), ("case-load-failed", 2),
            ("bad-argument", 2), ("case-load-failed", 1), ("bad-argument", 2), ("bad-argument", 2), ("verb-failed", 2),
        ];
        for (int i = 0; i < want.Length; i++)
        {
            JsonElement r = J(lines[i]);
            Assert.Equal("error", r.GetProperty("status").GetString());
            Assert.Equal(want[i].Kind, r.GetProperty("error").GetProperty("kind").GetString());
            Assert.Equal(want[i].Exit, r.GetProperty("exitCode").GetInt32());
        }
        Assert.Equal("ok", J(lines[10]).GetProperty("status").GetString());   // the session went on
        Assert.Equal(9, J(lines[10]).GetProperty("id").GetInt32());
    }

    [Fact]
    public void Run_verb_returns_the_document_the_subprocess_path_writes()
    {
        string file = Path.Combine(_dir, "pf14.json");
        CliProcess.Result cli = CliProcess.RunBuilt("pf", "case14", "--json", file);
        Assert.Equal(0, cli.ExitCode);
        JsonElement want = J(File.ReadAllText(file));

        var (_, lines, _) = Session([Req(1, "run-verb", "\"verb\":\"pf\",\"args\":[\"case14\"]")]);
        JsonElement r = J(Assert.Single(lines));
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(0, r.GetProperty("exitCode").GetInt32());
        JsonElement doc = r.GetProperty("result").GetProperty("document");
        foreach (string key in new[] { "schema", "tool", "verb", "exitCode", "summary", "tables" })
            Assert.True(Norm(want.GetProperty(key)) == Norm(doc.GetProperty(key)), $"'{key}' differs from the subprocess document");
        // The endpoint's own --json <file> pair is the only difference in "args".
        string[] wantArgs = want.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["pf", "case14"], doc.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["pf", "case14", "--json"], wantArgs[..3]);
        Assert.Equal(cli.StdOut, r.GetProperty("result").GetProperty("stdout").GetString());
        Assert.DoesNotContain(_dir, lines[0], StringComparison.Ordinal);   // no temporary path in the response
    }

    [Fact]
    public void A_licence_refusal_is_an_error_response_with_the_message_the_cli_prints()
    {
        string shipped = Path.Combine(CliProcess.Root(), "data", "matpower", "pegase", "case2869pegase.m");
        string copy = Path.Combine(_dir, "big2869.m");
        File.Copy(shipped, copy);
        var free = new Dictionary<string, string?> { ["GRIDSIM_LICENCE"] = "not-a-key", ["XDG_DATA_HOME"] = _dir };

        CliProcess.Result cli = CliProcess.RunBuiltWithEnvironment(free, "fault", copy, "--bus", "1");
        Assert.Equal(2, cli.ExitCode);
        string cliMessage = cli.StdErr.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("error: ", StringComparison.Ordinal))["error: ".Length..];

        var (_, lines, _) = Session([Req(1, "load", "\"case\":" + JsonSerializer.Serialize(copy))], free);
        JsonElement r = J(Assert.Single(lines));
        Assert.Equal("error", r.GetProperty("status").GetString());
        Assert.Equal("licence-refused", r.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal(2, r.GetProperty("exitCode").GetInt32());
        Assert.Equal(cliMessage, r.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Line_constants_over_rpc_is_the_line_constants_verb()
    {
        string geometry = R404LineConstantsCliTests.ThreePhaseGeometry;
        string gFile = Path.Combine(_dir, "g.json");
        File.WriteAllText(gFile, geometry);
        string outFile = Path.Combine(_dir, "lc.json");
        Assert.Equal(0, CliProcess.RunBuilt("line-constants", gFile, "--length-km", "50", "--base-kv", "132", "--base-mva", "100", "--json", outFile).ExitCode);
        JsonElement want = J(File.ReadAllText(outFile));

        string request = Req(1, "line-constants", "\"geometry\":" + Norm(J(geometry)) + ",\"per_unit\":{\"length_km\":50,\"base_kv\":132,\"base_mva\":100}");
        var (_, lines, _) = Session([request]);
        JsonElement r = J(Assert.Single(lines));
        Assert.Equal("ok", r.GetProperty("status").GetString());
        JsonElement doc = r.GetProperty("result").GetProperty("document");
        Assert.True(Norm(want.GetProperty("summary")) == Norm(doc.GetProperty("summary")));
        Assert.True(Norm(want.GetProperty("tables")) == Norm(doc.GetProperty("tables")));
    }

    [Theory]
    [InlineData("0.0.0.0:0")]
    [InlineData("8.8.8.8:0")]
    [InlineData("[::]:0")]
    [InlineData("192.168.1.10:9")]
    public void A_non_loopback_listen_address_is_refused_by_default(string address)
    {
        var (exit, lines, err) = Session([], null, "--listen", address);
        Assert.Equal(2, exit);
        Assert.Empty(lines);
        Assert.Contains("not a loopback address", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_flags_are_usage_errors()
    {
        Assert.Equal(2, Session([], null, "--allow-non-loopback").Exit);
        Assert.Equal(2, Session([], null, "--listen", "127.0.0.1:99999").Exit);
        Assert.Equal(2, Session([], null, "--listen", "not-an-address:5").Exit);
        Assert.Equal(2, Session([], null, "--top", "3").Exit);
    }

    [Fact]
    public async Task Listening_on_loopback_serves_one_connection_and_a_shutdown_ends_the_server()
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        psi.ArgumentList.Add("rpc");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add("127.0.0.1:0");
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        using Process p = Process.Start(psi)!;
        try
        {
            Task<string> err = p.StandardError.ReadToEndAsync();
            Task<string?> first = p.StandardOutput.ReadLineAsync();
            JsonElement banner = J((await first.WaitAsync(TimeSpan.FromSeconds(120)))!);
            string listening = banner.GetProperty("listening").GetString()!;
            Assert.StartsWith("127.0.0.1:", listening, StringComparison.Ordinal);
            int port = int.Parse(listening[(listening.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            string token = RpcListenSupport.Token(banner);   // CLI-03: every request over --listen carries the host's token
            writer.WriteLine(RpcListenSupport.With(Req(1, "load", "\"case\":\"case9\""), token));
            Assert.Equal("case-1", J(reader.ReadLine()!).GetProperty("result").GetProperty("handle").GetString());
            writer.WriteLine(RpcListenSupport.With(Req(2, "solve", "\"handle\":\"case-1\""), token));
            Assert.True(J(reader.ReadLine()!).GetProperty("result").GetProperty("converged").GetBoolean());
            writer.WriteLine(RpcListenSupport.With(Req(3, "shutdown"), token));
            Assert.True(J(reader.ReadLine()!).GetProperty("result").GetProperty("stopping").GetBoolean());
            Assert.True(p.WaitForExit(30_000), "the server did not stop after shutdown");
            Assert.Equal(0, p.ExitCode);
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }
}
