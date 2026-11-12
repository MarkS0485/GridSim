using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Security;
using GridSim.Web.Api;
using GridSim.Web.Model;
using GridSim.Web.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Web batch 3 (November 2026). New regression tests only; no existing test is touched.
/// <list type="bullet">
/// <item>GR-709: /health names the estimator state and the bridge sessions alive and faulted.</item>
/// <item>GR-712: an over-limit MATPOWER file is refused by the licence before the parser runs.</item>
/// <item>GR-715: GridSim:CaseRoots limits discovery; models under results/ and gda-out/ are listed, and their topology
/// read, only from the Operator band, with one AccessDenied audit entry per refusal.</item>
/// <item>GR-717: the converter reads numeric strings as the Web defaults do and still writes non-finite values as null.</item>
/// <item>GR-720: the exception handler is registered before the custom middleware (source-order check).</item>
/// </list>
/// </summary>
public sealed class GA7E3Batch3WebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-ga7e3b3-" + Guid.NewGuid().ToString("N"));

    public GA7E3Batch3WebTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>A discovery root holding gda-out/gda-model/system.json (an empty object stands in for a model: the
    /// catalogue only checks the file exists while it scans) and a real replay run under results/.</summary>
    private string MakeRoot(string name)
    {
        string root = Path.Combine(_dir, name);
        string gda = Path.Combine(root, "gda-out", "gda-model");
        Directory.CreateDirectory(gda);
        File.WriteAllText(Path.Combine(gda, "system.json"), "{}");

        string exports = Path.Combine(root, "results", "b3-run", "exports");
        Directory.CreateDirectory(exports);
        var trace = new SolveTrace();
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        var meta = new SolveExportMeta("b3-run", "cpu", "c", "2026-10-05T00:00:00Z", 1.0, "b3-test");
        SolveExport.WriteJson(Path.Combine(exports, "slice-000000001.json"), m, r, trace, meta);
        return root;
    }

    private static string[] ListedIds(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    private int AccessDeniedEntries() =>
        !File.Exists(LogPath) ? 0 : AuditLogFile.Load(LogPath).Count(e =>
            SecurityAudit.TryRead(e, out SecurityEventKind kind, out _) && kind == SecurityEventKind.AccessDenied);

    /// <summary>A host with the resident estimator on (an empty windows folder) and an audit log, so a role denial is
    /// recorded; anonymous callers hold <paramref name="role"/> and discovery is limited to <paramref name="root"/>.</summary>
    private WebApplicationFactory<Program> AuditedHost(OperationRole role, string root)
    {
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        return Factory(("GridSim:AnonymousRole", role.ToString()), ("GridSim:CaseRoots", root),
            ("Realtime:Enabled", "true"), ("Realtime:Case", "case9"), ("Realtime:WindowsDir", windows),
            ("Audit:LogPath", LogPath));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-709: /health
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gr709_health_without_an_estimator_names_it_not_configured_and_counts_no_bridge_sessions()
    {
        await using var f = Factory();
        using HttpClient c = Client(f);
        using HttpResponseMessage res = await c.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("not-configured", doc.RootElement.GetProperty("estimator").GetString());
        JsonElement bridge = doc.RootElement.GetProperty("bridgeSessions");
        Assert.Equal(0, bridge.GetProperty("active").GetInt32());
        Assert.Equal(0, bridge.GetProperty("faulted").GetInt32());
    }

    [Fact]
    public async Task Gr709_health_counts_a_live_bridge_session_and_forgets_it_when_it_is_disposed()
    {
        await using var f = Factory(("GridSim:BridgeEndpoint", "ws://127.0.0.1:9"), ("GridSim:BridgeAllowUnencrypted", "true"));
        using HttpClient c = Client(f);
        var manager = f.Services.GetRequiredService<SessionManager>();
        Assert.Equal((0, 0), manager.BridgeSessionCounts());

        SimSession session = manager.Create("bridge:gb-full@raw");
        try
        {
            Assert.Equal(1, manager.BridgeSessionCounts().Active);
            using JsonDocument doc = JsonDocument.Parse(await c.GetStringAsync(new Uri("/health", UriKind.Relative)));
            Assert.Equal(1, doc.RootElement.GetProperty("bridgeSessions").GetProperty("active").GetInt32());
        }
        finally
        {
            await session.DisposeAsync();
        }
        Assert.Equal(0, manager.BridgeSessionCounts().Active);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-712: licence before the parse
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gr712_bus_rows_are_counted_without_parsing_and_comments_are_ignored()
    {
        const string text = "function mpc = tiny\nmpc.baseMVA = 100;\nmpc.bus = [\n"
            + "1 3 0 0 0 0 1 1 0 100 1 1.1 0.9; % first\n"
            + "% a whole comment line 9 9 9;\n"
            + "2 1 10 5 0 0 1 1 0 100 1 1.1 0.9;\n];\nmpc.gen = [\n1 5 0 0 0 1 100 1 10 0;\n];\n";
        Assert.Equal(2, CaseCatalog.CountMatpowerBusRows(text));
        Assert.Equal(-1, CaseCatalog.CountMatpowerBusRows("function mpc = none\nmpc.baseMVA = 100;\n"));
    }

    [Fact]
    public void Gr712_an_over_limit_file_is_refused_by_the_licence_before_the_parser_runs()
    {
        string baseDir = Path.Combine(_dir, "gr712");
        string folder = Path.Combine(baseDir, "data", "matpower");
        Directory.CreateDirectory(folder);
        var text = new StringBuilder("function mpc = big\nmpc.baseMVA = 100;\nmpc.bus = [\n");
        for (int i = 1; i <= Entitlement.FreeBusLimit + 1; i++)
            text.Append(i).Append(" 1 0 0 0 0 1 1 0 100 1 1.1 0.9;\n");
        text.Append("];\n");
        // Garbage after the bus block: the parser would fail on it with some other error, so only a refusal made BEFORE
        // the parse can be a LicenceException.
        text.Append("mpc.gen = [ this is not a matrix {{{ ;\n");
        File.WriteAllText(Path.Combine(folder, "big.m"), text.ToString());

        var catalog = new CaseCatalog(baseDir);
        Assert.NotNull(catalog.Find("mp-big"));
        Assert.Throws<LicenceException>(() => catalog.LoadModel("mp-big"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-715: discovery roots and the role gate
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gr715_configured_roots_replace_the_ancestor_walk()
    {
        string parent = Path.Combine(_dir, "walk");
        string baseDir = Path.Combine(parent, "install");
        Directory.CreateDirectory(baseDir);
        string up = Path.Combine(parent, "gda-out", "up-model");   // an ancestor's gda-out
        Directory.CreateDirectory(up);
        File.WriteAllText(Path.Combine(up, "system.json"), "{}");
        string root = MakeRoot("configured");

        var walked = new CaseCatalog(baseDir);
        Assert.Contains(walked.Entries, e => e.Id == "gda-up-model");

        var limited = new CaseCatalog(baseDir, null, new[] { root });
        Assert.DoesNotContain(limited.Entries, e => e.Id == "gda-up-model");
        Assert.Contains(limited.Entries, e => e.Id == "gda-gda-model");
        Assert.Contains(limited.Entries, e => e.Id == "run-b3-run");
    }

    [Fact]
    public void Gr715_listing_hides_results_and_gda_models_below_operator()
    {
        string root = MakeRoot("listing");
        string install = Path.Combine(_dir, "install2");
        Directory.CreateDirectory(install);
        var catalog = new CaseCatalog(install, null, new[] { root });

        string[] viewer = catalog.EntriesFor(OperationRole.Viewer).Select(e => e.Id).ToArray();
        string[] analyst = catalog.EntriesFor(OperationRole.Analyst).Select(e => e.Id).ToArray();
        string[] op = catalog.EntriesFor(OperationRole.Operator).Select(e => e.Id).ToArray();
        Assert.Contains("case9", viewer);
        Assert.DoesNotContain("gda-gda-model", viewer);
        Assert.DoesNotContain("run-b3-run", viewer);
        Assert.DoesNotContain("run-b3-run", analyst);
        Assert.Contains("gda-gda-model", op);
        Assert.Contains("run-b3-run", op);
        Assert.True(catalog.IsPrivate("run-b3-run"));
        Assert.False(catalog.IsPrivate("case9"));
    }

    [Fact]
    public void Gr715_case_roots_setting_fails_loud_and_is_null_when_unset()
    {
        Assert.Null(CaseCatalog.ParseCaseRoots(null));
        Assert.Null(CaseCatalog.ParseCaseRoots("   "));
        string missing = Path.Combine(_dir, "no-such-folder");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CaseCatalog.ParseCaseRoots(missing));
        Assert.Contains(CaseCatalog.CaseRootsSetting, ex.Message, StringComparison.Ordinal);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        InvalidOperationException none = Assert.Throws<InvalidOperationException>(() => CaseCatalog.ParseCaseRoots(";"));
        Assert.Contains(CaseCatalog.CaseRootsSetting, none.Message, StringComparison.Ordinal);
        Assert.Single(CaseCatalog.ParseCaseRoots(_dir)!);
    }

    [Fact]
    public async Task Gr715_api_cases_hides_private_models_from_a_viewer_and_shows_them_to_an_operator()
    {
        string root = MakeRoot("api-cases");
        await using (var viewerHost = Factory(("GridSim:AnonymousRole", "Viewer"), ("GridSim:CaseRoots", root)))
        {
            using HttpClient c = Client(viewerHost);
            string[] ids = ListedIds(await c.GetStringAsync(new Uri("/api/cases", UriKind.Relative)));
            Assert.Contains("case9", ids);
            Assert.DoesNotContain("run-b3-run", ids);
            Assert.DoesNotContain("gda-gda-model", ids);
        }
        await using (var operatorHost = Factory(("GridSim:AnonymousRole", "Operator"), ("GridSim:CaseRoots", root)))
        {
            using HttpClient c = Client(operatorHost);
            string[] ids = ListedIds(await c.GetStringAsync(new Uri("/api/cases", UriKind.Relative)));
            Assert.Contains("run-b3-run", ids);
            Assert.Contains("gda-gda-model", ids);
        }
    }

    [Fact]
    public async Task Gr715_topology_of_a_results_model_is_403_with_one_audit_entry_for_a_viewer_and_200_for_an_operator_and_bundled_stays_viewer()
    {
        string root = MakeRoot("api-topology");
        await using (var viewerHost = AuditedHost(OperationRole.Viewer, root))
        {
            using HttpClient c = Client(viewerHost);
            Assert.Equal(0, AccessDeniedEntries());
            using (HttpResponseMessage bundled = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
                Assert.Equal(HttpStatusCode.OK, bundled.StatusCode);
            Assert.Equal(0, AccessDeniedEntries());
            using (HttpResponseMessage denied = await c.GetAsync(new Uri("/api/topology?case=run-b3-run", UriKind.Relative)))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(1, AccessDeniedEntries());
            // An unknown id is still a 404 for everyone, and writes no entry.
            using (HttpResponseMessage unknown = await c.GetAsync(new Uri("/api/topology?case=no-such-case", UriKind.Relative)))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(1, AccessDeniedEntries());
        }
        await using (var operatorHost = Factory(("GridSim:AnonymousRole", "Operator"), ("GridSim:CaseRoots", root)))
        {
            using HttpClient c = Client(operatorHost);
            using HttpResponseMessage ok = await c.GetAsync(new Uri("/api/topology?case=run-b3-run", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-717: the converter reads numeric strings
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions WebWithConverter =
        new(JsonSerializerDefaults.Web) { Converters = { new NonFiniteDoubleConverter() } };

    private static readonly JsonSerializerOptions StrictWithConverter =
        new() { Converters = { new NonFiniteDoubleConverter() } };

    private static readonly JsonSerializerOptions NamedLiterals = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new NonFiniteDoubleConverter() },
    };

    [Fact]
    public void Gr717_a_numeric_string_is_read_under_the_web_defaults()
    {
        Assert.Equal(1.5, JsonSerializer.Deserialize<double>("\"1.5\"", WebWithConverter));
        Assert.Equal(-2.0e3, JsonSerializer.Deserialize<double>("\"-2e3\"", WebWithConverter));
        Assert.Equal(7.25, JsonSerializer.Deserialize<double>("7.25", WebWithConverter));   // a plain number is unchanged
    }

    [Fact]
    public void Gr717_a_string_is_refused_when_the_options_do_not_allow_it()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<double>("\"1.5\"", StrictWithConverter));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"\"")]
    [InlineData("\" 1.5\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"1e999\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    public void Gr717_a_string_that_is_not_a_plain_finite_number_is_refused_under_the_web_defaults(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<double>(json, WebWithConverter));
    }

    [Fact]
    public void Gr717_named_literals_are_read_only_when_the_options_allow_them()
    {
        Assert.True(double.IsNaN(JsonSerializer.Deserialize<double>("\"NaN\"", NamedLiterals)));
        Assert.True(double.IsPositiveInfinity(JsonSerializer.Deserialize<double>("\"Infinity\"", NamedLiterals)));
        Assert.True(double.IsNegativeInfinity(JsonSerializer.Deserialize<double>("\"-Infinity\"", NamedLiterals)));
    }

    [Fact]
    public void Gr717_non_finite_values_are_still_written_as_null()
    {
        Assert.Equal("null", JsonSerializer.Serialize(double.NaN, WebWithConverter));
        Assert.Equal("null", JsonSerializer.Serialize(double.PositiveInfinity, WebWithConverter));
        Assert.Equal("1.5", JsonSerializer.Serialize(1.5, WebWithConverter));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-720: pipeline order, read from the source
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gr720_the_exception_handler_is_registered_before_every_custom_middleware()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "GridSim.Web", "Program.cs"));

        int handler = source.IndexOf("app.UseExceptionHandler(", StringComparison.Ordinal);
        int firstCustom = source.IndexOf("app.Use(async", StringComparison.Ordinal);
        int forwarded = source.IndexOf("app.UseForwardedHeaders(", StringComparison.Ordinal);
        Assert.True(handler >= 0, "UseExceptionHandler is not registered");
        Assert.True(firstCustom >= 0 && forwarded >= 0);
        Assert.True(handler < firstCustom, "the exception handler must come before the first custom middleware");
        Assert.True(handler < forwarded, "the exception handler must come before the forwarded-headers middleware");
    }
}
