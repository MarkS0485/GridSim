using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GridSim.Core.Security;
using GridSim.Scripting;
using GridSim.Web.Model;
using GridSim.Web.Site;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 security hardening of the hosted surfaces: the web script console (a deeply nested script is a script error, the statement
/// budget stops nested empty loops, the open-block cap, the SELECT ENGINE file settings, catalogue paths shown relative), the page-side
/// role gate the dashboard and the workbench use, and the TCP transport of <c>gridsim rpc --listen</c> (a line that is not a JSON
/// object ends the connection, network paths are refused).
/// </summary>
public sealed class SecurityHardening2174Tests : IDisposable
{
    private static readonly IdentityContext Viewer = new("v", OperationRole.Viewer, "t");
    private static readonly IdentityContext Analyst = new("a", OperationRole.Analyst, "t");
    private static readonly IdentityContext Operator = new("o", OperationRole.Operator, "t");

    private readonly string _dir = TestTemp.Under("gridsim-sec2174-" + Guid.NewGuid().ToString("N"));

    public SecurityHardening2174Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Root() => CliProcess.Root();

    private static WebScriptConsole Console()
    {
        var catalog = new CaseCatalog(Root());
        var access = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false, AccessDenialAudit.None);
        var limits = WebLimits.Create(4, TimeSpan.FromSeconds(120), 5, 20);
        return new WebScriptConsole(catalog, access, limits, Root());
    }

    private static string LoadCase9(WebScriptConsole c) =>
        c.LoadableCases(OperationRole.Viewer).First(x => x.Entry.Path.EndsWith("case9.m", StringComparison.OrdinalIgnoreCase)).Statement;

    private static T OnSmallStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        if (failure is not null) throw new InvalidOperationException("the work threw", failure);
        return result;
    }

    // ---------------------------------------------------------------- web script console

    [Fact]
    public void Check_only_of_a_deeply_nested_script_is_a_script_error_naming_its_line()
    {
        WebScriptConsole c = Console();
        string deep = "GRAMMAR 1\nPRINT " + new string('(', 30_000) + "1" + new string(')', 30_000) + "\n";
        Assert.True(Encoding.UTF8.GetByteCount(deep) <= WebScriptConsole.MaxScriptBytes);
        WebScriptResult r = OnSmallStack(() => c.Check(deep));
        Assert.Equal(ScriptRunStatus.Error, r.Status);
        Assert.Equal(2, r.Error!.Pos.Line);
        Assert.Equal(ErrorPhase.Syntax, r.Error.Phase);
    }

    [Fact]
    public async Task Nested_empty_loops_are_stopped_by_the_statement_budget()
    {
        WebScriptConsole c = Console();
        var lines = new List<string> { "GRAMMAR 1", LoadCase9(c) };
        for (int d = 0; d < 6; d++) lines.Add(new string(' ', 2 * d) + "FOR EACH BUS b" + d);   // 9^6 iterations, more than the budget
        for (int d = 5; d >= 0; d--) lines.Add(new string(' ', 2 * d) + "END");
        WebScriptResult r = await c.RunAsync(string.Join("\n", lines) + "\n", Viewer);
        Assert.Equal(ScriptRunStatus.Error, r.Status);
        Assert.Contains(WebScriptConsole.MaxStatements.ToString(System.Globalization.CultureInfo.InvariantCulture) + " statements, blocks and loop iterations", r.Message, StringComparison.Ordinal);
        Assert.NotNull(r.RunRecordJson);
    }

    [Fact]
    public async Task An_interactive_open_block_is_capped()
    {
        WebScriptConsole c = Console();
        WebScriptConsole.WebConsoleSession s = c.NewSession(Viewer);
        Assert.Equal(ScriptRunStatus.Completed, (await s.SubmitAsync("FOR EACH BUS b")).Status);
        string comment = "  # " + new string('x', 4000);
        WebScriptResult last = new(ScriptRunStatus.Completed, "", "", null, null);
        for (int i = 0; i < 20 && last.Status == ScriptRunStatus.Completed; i++) last = await s.SubmitAsync(comment);
        Assert.Equal(ScriptRunStatus.Error, last.Status);
        Assert.Contains("open block", last.Message, StringComparison.Ordinal);
        Assert.False(s.InBlock);
    }

    [Theory]
    [InlineData("SCHEMAS \"schemas/v2\"")]
    [InlineData("DYNAMICS \"tests/GridSim.Tests/fixtures/dynamics/wscc9-genrou.dynamics.json\"")]
    [InlineData("DYNAMICS \"../outside.json\"")]
    public async Task Select_engine_file_settings_outside_the_catalogue_are_refused(string setting)
    {
        WebScriptConsole c = Console();
        string text = $"GRAMMAR 1\n{LoadCase9(c)}\nSOLVE POWERFLOW\nSELECT ENGINE RMS SUBTRANSIENT WITH {setting}\n";
        WebScriptResult r = await c.RunAsync(text, Operator);
        Assert.Equal(ScriptRunStatus.Refused, r.Status);
        Assert.Contains(WebScriptConsole.RefusalPrefix, r.Message, StringComparison.Ordinal);
        Assert.Equal("", r.Output);   // refused before anything ran
    }

    [Fact]
    public void The_page_lists_models_by_paths_relative_to_the_console_folder()
    {
        WebScriptConsole c = Console();
        string root = Path.GetFullPath(Root());
        foreach ((CaseEntry _, string statement) in c.LoadableCases(OperationRole.Operator))
        {
            Assert.DoesNotContain(root.Replace("\\", "\\\\", StringComparison.Ordinal), statement, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, statement, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- page-side role gate

    [Fact]
    public void The_page_gate_refuses_below_the_band_and_allows_at_it()
    {
        var access = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false, AccessDenialAudit.None);
        Assert.Contains("requires role Analyst", access.RefusalFor(Viewer, AccessControl.SolveRole, AccessControl.WorkbenchStudyOperation), StringComparison.Ordinal);
        Assert.Null(access.RefusalFor(Analyst, AccessControl.SolveRole, AccessControl.WorkbenchStudyOperation));
        Assert.Contains("requires role Operator", access.RefusalFor(Analyst, AccessControl.SimControlRole, AccessControl.DashboardOperationPrefix + "trip"), StringComparison.Ordinal);
        Assert.Null(access.RefusalFor(Operator, AccessControl.SimControlRole, AccessControl.DashboardOperationPrefix + "trip"));
    }

    [Fact]
    public void The_dashboard_and_workbench_actions_go_through_the_page_gate()
    {
        string pages = Path.Combine(Root(), "src", "GridSim.Web", "Components");
        string dash = File.ReadAllText(Path.Combine(pages, "Pages", "Dashboard.razor"));
        string bench = File.ReadAllText(Path.Combine(pages, "Pages", "Workbench.razor"));
        string transport = File.ReadAllText(Path.Combine(pages, "TransportBar.razor"));
        Assert.Contains("TripLargest=\"TripLargestGuarded\"", dash, StringComparison.Ordinal);
        Assert.Contains("Deny=\"HarmonicsDenied\"", dash, StringComparison.Ordinal);
        Assert.Contains("Authorize(GridSim.Web.Site.AccessControl.SolveRole, \"harmonics\")", dash, StringComparison.Ordinal);
        string harm = File.ReadAllText(Path.Combine(pages, "Tabs", "HarmonicsTab.razor"));
        Assert.Contains("if (Deny?.Invoke() is { } refusal) { _result = HarmonicsDto.Failure(refusal); return; }", harm, StringComparison.Ordinal);
        Assert.Contains("Authorize=\"Authorize\"", dash, StringComparison.Ordinal);
        Assert.DoesNotContain("TripLargest=\"_session.TripLargestInfeed\"", dash, StringComparison.Ordinal);
        Assert.Contains("AccessControl.WorkbenchStudyOperation", bench, StringComparison.Ordinal);
        foreach (string op in new[] { "\"play\"", "\"step\"", "\"speed\"", "\"stepsize\"", "\"trip\"" })
            Assert.Contains("Allowed(" + op + ")", transport, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- rpc --listen

    // CLI-03 (2.1.156): every request over --listen carries the session token, read from the file the banner names. This
    // test sent none, so since 2.1.156 its second connection was refused as unauthorised; it now sends the token.
    private string? _token;

    private string Req(int id, string op, string extra = "") =>
        "{\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"op\":\"" + op + "\""
        + (_token is null ? "" : ",\"token\":\"" + _token + "\"") + (extra.Length > 0 ? "," + extra : "") + "}";

    private Process StartListener(out int port)
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
        Process p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        string banner = p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult()!;
        string listening = JsonDocument.Parse(banner).RootElement.GetProperty("listening").GetString()!;
        _token = File.ReadAllText(JsonDocument.Parse(banner).RootElement.GetProperty("token_file").GetString()!).Trim();
        port = int.Parse(listening[(listening.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return p;
    }

    private static (StreamReader Reader, StreamWriter Writer, TcpClient Client) Connect(int port)
    {
        var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = 60_000;
        NetworkStream stream = client.GetStream();
        return (new StreamReader(stream, new UTF8Encoding(false)), new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true }, client);
    }

    [Fact]
    public void An_http_request_at_the_listen_port_ends_the_connection_before_any_operation_runs()
    {
        using Process p = StartListener(out int port);
        try
        {
            (StreamReader r, StreamWriter w, TcpClient client) = Connect(port);
            using (client)
            {
                // An HTTP request line, then headers, then a JSON body: the first line ends the connection.
                w.Write("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: text/plain\r\n\r\n" + Req(1, "shutdown") + "\n");
                JsonElement first = JsonDocument.Parse(r.ReadLine()!).RootElement;
                Assert.Equal("malformed-request", first.GetProperty("error").GetProperty("kind").GetString());
                Assert.Null(r.ReadLine());   // closed: the shutdown in the body never ran
            }
            Assert.False(p.HasExited);

            // The server carries on: a well-formed client is served, and network paths are refused over TCP.
            (StreamReader r2, StreamWriter w2, TcpClient c2) = Connect(port);
            using (c2)
            {
                w2.WriteLine(Req(2, "load", "\"case\":\"\\\\\\\\server.invalid\\\\share\\\\x.m\""));
                JsonElement unc = JsonDocument.Parse(r2.ReadLine()!).RootElement;
                Assert.Equal("bad-argument", unc.GetProperty("error").GetProperty("kind").GetString());
                w2.WriteLine(Req(3, "run-verb", "\"verb\":\"pf\",\"args\":[\"//server.invalid/share/x.m\"]"));
                Assert.Equal("bad-argument", JsonDocument.Parse(r2.ReadLine()!).RootElement.GetProperty("error").GetProperty("kind").GetString());
                w2.WriteLine(Req(4, "hello"));
                Assert.Equal("ok", JsonDocument.Parse(r2.ReadLine()!).RootElement.GetProperty("status").GetString());
                w2.WriteLine(Req(5, "shutdown"));
                Assert.True(JsonDocument.Parse(r2.ReadLine()!).RootElement.GetProperty("result").GetProperty("stopping").GetBoolean());
            }
            Assert.True(p.WaitForExit(30_000), "the server did not stop after shutdown");
            Assert.Equal(0, p.ExitCode);
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }
}
