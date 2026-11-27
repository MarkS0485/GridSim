using System.Net;
using GridSim.Core.Licensing;
using GridSim.Core.Security;
using GridSim.Scripting;
using GridSim.Web.Model;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4: the web script console. The role gate (read-only verbs for a Viewer, state-changing verbs from the Operator band), the
/// refusals (a file the catalogue does not list, EXPORT, a private model, a script over the limit, a STEP SIM over the tick cap),
/// the determinism of a web run against the gridsim-script run record, and the /console route.
/// </summary>
public sealed class WebScriptConsoleTests
{
    private static readonly IdentityContext Viewer = new("v", OperationRole.Viewer, "t");
    private static readonly IdentityContext Operator = new("o", OperationRole.Operator, "t");

    private static string Root() => CliProcess.Root();

    private static WebScriptConsole Console(CaseCatalog? catalog = null, string? baseDir = null)
    {
        catalog ??= new CaseCatalog(Root());
        var access = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false, AccessDenialAudit.None);
        var limits = WebLimits.Create(4, TimeSpan.FromSeconds(60), 5, 20);
        return new WebScriptConsole(catalog, access, limits, baseDir ?? Root());
    }

    /// <summary>The LOAD CASE statement of the bundled case9 file, as the page lists it.</summary>
    private static string LoadCase9(WebScriptConsole c)
    {
        (CaseEntry entry, string statement) = c.LoadableCases(OperationRole.Viewer).First(x => x.Entry.Path.EndsWith("case9.m", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CaseKind.Matpower, entry.Kind);
        return statement;
    }

    [Fact]
    public void The_bands_are_the_operator_for_change_and_the_private_listing_band_for_private_models()
    {
        Assert.Equal(OperationRole.Operator, WebScriptConsole.ChangeRole);
        Assert.Equal(CaseCatalog.PrivateListingRole, OperationRole.Operator);
    }

    [Fact]
    public async Task A_viewer_may_check_load_report_and_print()
    {
        WebScriptConsole c = Console();
        Assert.True(c.Check("GRAMMAR 1\nSOLVE POWERFLOW\n").Ok);   // check-only runs nothing, so it is open to every caller
        WebScriptResult r = await c.RunAsync($"GRAMMAR 1\n{LoadCase9(c)}\nLET a = 2 MW\nPRINT a\n", Viewer);
        Assert.True(r.Ok, r.Message);
        // REPORT is a read-only verb: the role lets it through. It needs a solved state, which a Viewer cannot make (SOLVE is state-changing).
        WebScriptResult report = await c.RunAsync($"GRAMMAR 1\n{LoadCase9(c)}\nREPORT BUSES\n", Viewer);
        Assert.Equal(ScriptRunStatus.Error, report.Status);
        Assert.Contains("no solved state", report.Message, StringComparison.Ordinal);
        Assert.Contains("loaded \"case9\"", r.Output, StringComparison.Ordinal);
        Assert.Contains("a = 2 MW", r.Output, StringComparison.Ordinal);
        Assert.NotNull(r.RunRecordJson);
    }

    [Theory]
    [InlineData("SOLVE POWERFLOW")]
    [InlineData("TRIP LINE \"4-5\"")]
    [InlineData("SET LOAD \"L5\" P 90 MW")]
    [InlineData("SELECT ENGINE LIVE")]
    [InlineData("AT 100 MS TRIP GENERATOR \"G2\"")]
    [InlineData("IF 1 MW < 2 MW\n  SOLVE POWERFLOW\nEND")]
    [InlineData("PROC shed(MW by)\n  SOLVE POWERFLOW\nEND")]
    public async Task A_viewer_is_refused_every_state_changing_verb_and_nothing_runs(string statement)
    {
        WebScriptConsole c = Console();
        WebScriptResult r = await c.RunAsync($"GRAMMAR 1\n{LoadCase9(c)}\n{statement}\n", Viewer);
        Assert.Equal(ScriptRunStatus.Refused, r.Status);
        Assert.True(r.Message.Contains("Operator", StringComparison.Ordinal) || r.Message.Contains("Analyst", StringComparison.Ordinal), r.Message);
        Assert.Contains(WebScriptConsole.RefusalPrefix, r.Message, StringComparison.Ordinal);
        Assert.Contains("line 3, column 1", r.Message, StringComparison.Ordinal);
        Assert.Equal("", r.Output);          // not even the read-only LOAD ran
        Assert.Null(r.RunRecordJson);
    }

    [Fact]
    public async Task An_analyst_may_solve_as_on_api_solve_but_not_change_the_model()
    {
        WebScriptConsole c = Console();
        var analyst = new IdentityContext("a", OperationRole.Analyst, "t");
        Assert.Equal(AccessControl.SolveRole, WebScriptConsole.SolveRole);
        string head = "GRAMMAR 1" + (char)10 + LoadCase9(c) + (char)10;
        WebScriptResult ok = await c.RunAsync(head + "SOLVE POWERFLOW" + (char)10 + "REPORT BUSES" + (char)10, analyst);
        Assert.True(ok.Ok, ok.Message);
        WebScriptResult no = await c.RunAsync(head + "SOLVE POWERFLOW" + (char)10 + "TRIP LINE \"4-5\"" + (char)10, analyst);
        Assert.Equal(ScriptRunStatus.Refused, no.Status);
        Assert.Contains("Operator", no.Message, StringComparison.Ordinal);
        WebScriptResult viewer = await c.RunAsync(head + "SOLVE POWERFLOW" + (char)10, Viewer);
        Assert.Contains("Analyst", viewer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_runs_state_changing_verbs_and_the_run_record_is_the_one_gridsim_script_writes()
    {
        WebScriptConsole c = Console();
        string text = string.Join("\n", "GRAMMAR 1", LoadCase9(c), "SOLVE POWERFLOW", "SELECT ENGINE LIVE STEP 20 MS",
            "AT 100 MS TRIP GENERATOR \"G2\"", "STEP SIM 500 MS", "REPORT SIM", "");
        WebScriptResult web = await c.RunAsync(text, Operator);
        Assert.True(web.Ok, web.Message);

        RunResult console = Runner.Run(new System.Text.UTF8Encoding(false).GetBytes(text), new RunOptions(Root()));
        Assert.True(console.Ok, console.Error?.Display);
        Assert.Equal(console.RecordJson, web.RunRecordJson);
    }

    [Fact]
    public async Task EXPORT_and_a_file_the_catalogue_does_not_list_are_refused_even_to_an_operator()
    {
        WebScriptConsole c = Console();
        WebScriptResult export = await c.RunAsync($"GRAMMAR 1\n{LoadCase9(c)}\nSOLVE POWERFLOW\nEXPORT RESULT \"x.json\"\n", Operator);
        Assert.Equal(ScriptRunStatus.Refused, export.Status);
        Assert.Contains("EXPORT RESULT", export.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root(), "x.json")));

        string copy = TestTemp.Under("gridsim-console-" + Guid.NewGuid().ToString("N") + ".m");
        File.Copy(Path.Combine(Root(), "data", "matpower", "lib", "case9.m"), copy);
        try
        {
            WebScriptResult load = await c.RunAsync($"GRAMMAR 1\nLOAD CASE \"{copy.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n", Operator);
            Assert.Equal(ScriptRunStatus.Refused, load.Status);
            Assert.Contains("catalogue", load.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(copy); }
    }

    [Fact]
    public async Task A_private_model_needs_the_private_listing_role_to_load()
    {
        string baseDir = TestTemp.Under("gridsim-console-priv-" + Guid.NewGuid().ToString("N"));
        string model = Path.Combine(baseDir, "gda-out", "Secret");
        Directory.CreateDirectory(model);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(Root(), "data", "uk"), "*.json")) File.Copy(f, Path.Combine(model, Path.GetFileName(f)));
        var catalog = new CaseCatalog(baseDir);
        CaseEntry secret = catalog.Entries.Single(e => e.Path.StartsWith(baseDir, StringComparison.Ordinal));
        Assert.True(catalog.IsPrivate(secret.Id));
        WebScriptConsole c = Console(catalog, baseDir);

        Assert.DoesNotContain(c.LoadableCases(OperationRole.Viewer), x => x.Entry.Id == secret.Id);   // not even listed to a viewer
        string load = $"GRAMMAR 1\nLOAD CASE \"{secret.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n";
        WebScriptResult viewer = await c.RunAsync(load, Viewer);
        Assert.Equal(ScriptRunStatus.Refused, viewer.Status);
        Assert.Contains("needs the Operator role", viewer.Message, StringComparison.Ordinal);

        WebScriptResult op = await c.RunAsync(load, Operator);
        // Allowed by role; the licence gate (the free tier's bus limit) or the load may still answer, and says so as an error, not a role refusal.
        Assert.DoesNotContain("needs the Operator role", op.Message, StringComparison.Ordinal);
        if (op.Status == ScriptRunStatus.Refused) Assert.Contains("LOAD CASE of", op.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_licence_refusal_is_said_as_a_refusal_not_a_crash()
    {
        // The free tier admits the bundled models; a catalogue entry the licence does not cover is refused by the same gate the pages use.
        var gate = new LicenceGate(Entitlement.FreeDefault);
        var catalog = new CaseCatalog(Root(), gate);
        WebScriptConsole c = Console(catalog);
        CaseEntry? over = catalog.Entries.FirstOrDefault(e => !e.IsReplay && e.Path.Length > 0 && Refused(catalog, e.Id));
        if (over is null) return;   // every listed model is covered on this checkout
        string load = "LOAD CASE \"" + over.Path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
        WebScriptResult r = await c.RunAsync("GRAMMAR 1\n" + load + "\n", Operator);
        Assert.Equal(ScriptRunStatus.Refused, r.Status);
        Assert.Contains("LOAD CASE of", r.Message, StringComparison.Ordinal);

        // The interactive console says the same.
        WebScriptResult line = await c.NewSession(Operator).SubmitAsync(load);
        Assert.Equal(ScriptRunStatus.Refused, line.Status);
        Assert.Contains("LOAD CASE of", line.Message, StringComparison.Ordinal);

        static bool Refused(CaseCatalog cat, string id)
        {
            try { _ = cat.LoadModel(id); return false; }
            catch (LicenceException) { return true; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return false; }
        }
    }

    [Fact]
    public async Task The_limits_are_enforced_and_said()
    {
        WebScriptConsole c = Console();
        WebScriptResult big = await c.RunAsync("GRAMMAR 1\n" + new string('#', WebScriptConsole.MaxScriptBytes) + "\n", Operator);
        Assert.Equal(ScriptRunStatus.TooLarge, big.Status);
        Assert.Equal(ScriptRunStatus.TooLarge, c.Check(new string('#', WebScriptConsole.MaxScriptBytes + 1)).Status);

        string text = string.Join("\n", "GRAMMAR 1", LoadCase9(c), "SOLVE POWERFLOW", "SELECT ENGINE LIVE STEP 1 MS", "STEP SIM 1000 S", "");
        WebScriptResult r = await c.RunAsync(text, Operator);
        Assert.Equal(ScriptRunStatus.Error, r.Status);
        Assert.Equal(5, r.Error!.Pos.Line);
        Assert.Contains("limit of 100000 ticks", r.Message, StringComparison.Ordinal);
        Assert.Contains("loaded \"case9\"", r.Output, StringComparison.Ordinal);   // the statements before it ran and are shown
        Assert.NotNull(r.RunRecordJson);
        Assert.Contains("solve timeout", c.LimitsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grammar_error_names_line_and_column_and_runs_nothing()
    {
        WebScriptConsole c = Console();
        WebScriptResult r = await c.RunAsync("GRAMMAR 1\nPRINT PRINT\n", Operator);
        Assert.Equal(ScriptRunStatus.Error, r.Status);
        Assert.Equal(2, r.Error!.Pos.Line);
        Assert.Contains("line 2", r.Message, StringComparison.Ordinal);
        Assert.Null(r.RunRecordJson);
    }

    [Fact]
    public async Task An_interactive_session_applies_the_gate_per_line_and_records_exactly_as_the_console_does()
    {
        WebScriptConsole c = Console();
        string load = LoadCase9(c);

        WebScriptConsole.WebConsoleSession viewer = c.NewSession(Viewer);
        Assert.Equal(ScriptRunStatus.Completed, (await viewer.SubmitAsync(load)).Status);
        WebScriptResult denied = await viewer.SubmitAsync("SOLVE POWERFLOW");
        Assert.Equal(ScriptRunStatus.Refused, denied.Status);
        Assert.Contains("Analyst", denied.Message, StringComparison.Ordinal);
        Assert.NotNull(denied.Error);
        Assert.Equal(4, denied.Error!.Pos.Line);   // header comment, GRAMMAR 1, the load, then this line
        ConsoleRecording rec = viewer.Finish();
        Assert.Contains("# REFUSED (4:", rec.GssText, StringComparison.Ordinal);

        // The same lines through the library console give the same recording and run record as a web session of an Operator.
        WebConsoleSessionLines(c, Operator, out ConsoleRecording web, load, "SOLVE POWERFLOW", "REPORT BUSES");
        var direct = new InteractiveConsole(Root());
        foreach (string l in new[] { load, "SOLVE POWERFLOW", "REPORT BUSES" }) direct.Submit(l);
        ConsoleRecording expected = direct.Finish();
        Assert.Equal(expected.GssText, web.GssText);
        Assert.Equal(expected.RunRecordJson, web.RunRecordJson);
    }

    private static void WebConsoleSessionLines(WebScriptConsole c, IdentityContext who, out ConsoleRecording rec, params string[] lines)
    {
        WebScriptConsole.WebConsoleSession s = c.NewSession(who);
        foreach (string l in lines)
        {
            WebScriptResult r = s.SubmitAsync(l).GetAwaiter().GetResult();
            Assert.True(r.Ok, r.Message);
        }
        rec = s.Finish();
    }

    // ---------------------------------------------------------------- the route

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = TestTemp.Under("gridsim-console-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    [Fact]
    public async Task The_console_route_serves_the_page_and_says_the_role()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"), ("GridSim:AnonymousRole", "Viewer"));
        using HttpClient client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage r = await client.GetAsync(new Uri("/console", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("SCRIPT CONSOLE", html, StringComparison.Ordinal);
        Assert.Contains("you hold Viewer", html, StringComparison.Ordinal);
        Assert.Contains("MODELS YOU MAY LOAD", html, StringComparison.Ordinal);
        Assert.Contains("CHECK ONLY", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/console")]
    [InlineData("/Console")]
    public async Task A_replay_only_host_refuses_the_console_route(string path)
    {
        await using var f = Factory(("GridSim:ReplayOnly", "true"));
        using HttpClient client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage r = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("SCRIPT CONSOLE", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
