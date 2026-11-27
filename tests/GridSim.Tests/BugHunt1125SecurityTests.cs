using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Events;
using GridSim.Core.Frequency;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.Qsts;
using GridSim.Core.Replay;
using GridSim.Core.Security;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 1 (security, rulings 1 to 4):
/// <list type="bullet">
/// <item>NUM-01, SEC-01, ANA-23: a file named inside an input file is a side file (<see cref="SideFile"/>): no network or
/// device path, resolved against the naming file's folder, a size limit, and its content never quoted;</item>
/// <item>CLI-03, CLI-09: <c>rpc --listen</c> needs the host's token on every request, confines every file an argument names
/// to the working folder, and finds a network path inside a comma list;</item>
/// <item>CLI-01, CLI-08: the licence's bus limit holds for a case edited over rpc and for a dist3ph-mesh case;</item>
/// <item>SEC-27, SEC-28: a re-run never loads plug-in code, and the plug-in lint allows only listed framework namespaces.</item>
/// </list>
/// </summary>
public sealed class BugHunt1125SecurityTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-sec");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    // ---- the side-file rule ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\host\share\x.csv")]
    [InlineData("//host/share/x.csv")]
    [InlineData(@"\\?\C:\x.csv")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\??\UNC\h\s\x.csv")]   // R1-01: the NT object-manager prefix
    [InlineData(@"\??\C:\x.csv")]
    [InlineData(@"\??\GLOBALROOT\x")]
    [InlineData("/??/UNC/h/s/x.csv")]
    public void A_network_or_device_side_file_is_refused_before_anything_is_opened(string written)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SideFile.Resolve(written, _dir, "member"));
        Assert.Contains("network or device path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_name_is_refused_and_local_relative_and_absolute_paths_resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("NUL", _dir, "member"));
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("con.csv", _dir, "member"));
            // R1-04: a trailing colon or a stream suffix is still the device; the superscript names are reserved too.
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("NUL:", _dir, "member"));
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("CON:x", _dir, "member"));
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("CONIN$:", _dir, "member"));
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("COM\u00B9.csv", _dir, "member"));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("/dev/zero", _dir, "member"));
            Assert.Throws<InvalidDataException>(() => SideFile.Resolve("/DEV/tty", _dir, "member"));   // R1-04: macOS volumes ignore case
        }
        Assert.Equal(Path.GetFullPath(P("sub", "x.csv")), SideFile.Resolve(Path.Combine("sub", "x.csv"), _dir, "member"));
        string outside = Path.GetFullPath(Path.Combine(_dir, "..", "y.csv"));
        Assert.Equal(outside, SideFile.Resolve(Path.Combine("..", "y.csv"), _dir, "member"));
        Assert.Equal(outside, SideFile.Resolve(outside, _dir, "member"));
    }

    /// <summary>R1-03: a link anywhere on the path is read one hop at a time; one that leads to a device or network path is
    /// refused before anything opens it. On Windows a link needs a privilege the test may not have; then nothing is checked
    /// there (the Linux run covers the rule).</summary>
    [Fact]
    public void A_link_on_the_way_to_a_device_or_network_path_is_refused()
    {
        string target = OperatingSystem.IsWindows() ? @"\\host\share" : "/dev";
        string dirLink = P("d"), fileLink = P("f.csv");
        try
        {
            Directory.CreateSymbolicLink(dirLink, target);
            File.CreateSymbolicLink(fileLink, OperatingSystem.IsWindows() ? @"\\host\share\x.csv" : "/dev/zero");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.True(OperatingSystem.IsWindows(), "a link could not be made: " + ex.Message);
            return;
        }
        Assert.Throws<InvalidDataException>(() => SideFile.Resolve(Path.Combine("d", OperatingSystem.IsWindows() ? "x.csv" : "zero"), _dir, "member"));
        Assert.Throws<InvalidDataException>(() => SideFile.Resolve("f.csv", _dir, "member"));
        // A link to an ordinary local file is followed as before.
        File.WriteAllText(P("plain.csv"), "1\n");
        File.CreateSymbolicLink(P("ok.csv"), P("plain.csv"));
        Assert.Equal(Path.GetFullPath(P("ok.csv")), SideFile.Resolve("ok.csv", _dir, "member"));
    }

    [Fact]
    public void A_side_file_over_its_limit_is_refused_without_quoting_it()
    {
        string f = P("big.csv");
        File.WriteAllText(f, "secret-header-token\n" + new string('9', 200));
        var ex = Assert.Throws<InvalidDataException>(() => SideFile.ReadAllBytes(f, 100, "member"));
        Assert.Contains("100-byte limit", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(f), SideFile.ReadAllBytes(f, 1000, "member"));
        Assert.Equal(["secret-header-token", new string('9', 200)], SideFile.ReadAllLines(f, 1000, "member"));
    }

    // ---- NUM-01: the QSTS profile CSV ------------------------------------------------------------------------------

    [Fact]
    public void Num01_a_qsts_profile_csv_on_a_network_path_is_refused_and_a_missing_column_does_not_list_the_header()
    {
        GridModel model = GridSim.Core.IO.IeeeCases.Case9();
        string study = "{\"format\":\"gridsim-qsts/1\",\"stepSeconds\":60,\"steps\":2,\"profiles\":{\"p\":{\"stepSeconds\":60,"
            + "\"csv\":\"\\\\\\\\host\\\\share\\\\x.csv\",\"column\":\"nope\"}},\"loads\":[]}";
        var ex = Assert.Throws<InvalidDataException>(() => QstsFile.Parse(study, "s.json", _dir, model));
        Assert.Contains("network or device path", ex.Message, StringComparison.Ordinal);

        string csv = P("outside.csv");
        File.WriteAllText(csv, "alpha_secret_token,second_secret\n1,2\n");
        using JsonDocument column = JsonDocument.Parse("\"nope\"");
        var missing = Assert.Throws<InvalidDataException>(() => QstsFile.ReadCsvColumn(csv, column.RootElement.Clone(), "profile 'p'"));
        Assert.Contains("has no column 'nope'", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", missing.Message, StringComparison.Ordinal);
        // A local file outside the study's folder is still read (local paths stay allowed).
        using JsonDocument byName = JsonDocument.Parse("\"second_secret\"");
        Assert.Equal([2.0], QstsFile.ReadCsvColumn(csv, byName.RootElement.Clone(), "profile 'p'"));
    }

    // ---- SEC-01: the event catalogue's trace -----------------------------------------------------------------------

    private string Catalog(string? trace)
    {
        string path = P("cat", "catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EventCatalog.Save(path, EventCatalog.Builtin());
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        node["events"]![0]!["outcome"]!["nesoTraceCsvPath"] = trace;
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void Sec01_a_catalogue_trace_on_a_network_path_is_refused_at_load()
    {
        var ex = Assert.Throws<InvalidDataException>(() => EventCatalog.Load(Catalog(@"\\host\share\trace.csv")));
        Assert.Contains("nesoTraceCsvPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("network or device path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec01_a_relative_trace_resolves_against_the_catalogue_folder_and_a_missing_one_is_said()
    {
        GridEvent ev = EventCatalog.Load(Catalog("trace.csv"))[0];
        Assert.Equal(Path.GetFullPath(P("cat", "trace.csv")), ev.Outcome.NesoTraceCsvPath);
        Assert.True(EventCatalog.IsCatalogTrace(ev.Outcome));
        Assert.False(EventCatalog.IsCatalogTrace(ev.Outcome with { NesoTraceCsvPath = "x.csv" }));
        ReplayResult r = ReplayValidator.Validate(ev);
        Assert.Contains("the catalogue names the trace 'trace.csv', which was not found; no RMSE", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec01_a_side_file_trace_is_never_quoted_in_an_error()
    {
        string f = P("bad-trace.csv");
        File.WriteAllText(f, "dtm,f\n2019-08-09 16:52:00,50.0\nsecret-line-content,abc\n");
        var ex = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.ReadSideFile(f, SideFile.TraceMaxBytes, "event 'x'").ToList());
        Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        // The user's own file (named on the command line) is still quoted, as before.
        var own = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(f).ToList());
        Assert.Contains("secret-line-content", own.Message, StringComparison.Ordinal);
    }

    // ---- ANA-23: model assignment files ----------------------------------------------------------------------------

    [Fact]
    public void Ana23_a_model_file_on_a_network_path_is_refused_and_a_pin_mismatch_does_not_print_the_files_hash()
    {
        string assign = P("assign.json");
        byte[] unc = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "gridsim-model-assignments/1",
            userModels = new[] { new { machine = new { bus = 1, id = "1" }, file = @"\\host\share\m.model.json", sha256 = new string('0', 64) } },
        });
        var ex = Assert.Throws<InvalidDataException>(() => ModelAssignmentsFile.Parse(unc, assign, null, 0.01));
        Assert.Contains("network or device path", ex.Message, StringComparison.Ordinal);

        File.WriteAllText(P("m.model.json"), "{}");
        string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(P("m.model.json"))));
        byte[] wrong = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "gridsim-model-assignments/1",
            userModels = new[] { new { machine = new { bus = 1, id = "1" }, file = "m.model.json", sha256 = new string('0', 64) } },
        });
        var pin = Assert.Throws<InvalidDataException>(() => ModelAssignmentsFile.Parse(wrong, assign, null, 0.01));
        Assert.Contains("not the pinned " + new string('0', 64), pin.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actual, pin.Message, StringComparison.Ordinal);
    }

    // ---- SEC-27 / SEC-28: plug-ins -----------------------------------------------------------------------------------

    [Fact]
    public void Sec27_a_job_that_loads_plugin_code_is_never_re_run()
    {
        var writers = new HashSet<string>(StringComparer.Ordinal);
        Assert.Contains("loads plug-in code", ReplayPolicy.Refusal("dynamics-study", ["dynamics-study", "s.json", "--model-plugins", "p.json"], writers));
        Assert.Contains("loads plug-in code", ReplayPolicy.Refusal("dynamics-study", ["dynamics-study", "s.json", "--model-plugins=p.json"], writers));
        Assert.Contains("loads plug-in code", ReplayPolicy.Refusal("dynamics-study", ["dynamics-study", "s.json", "--MODEL-PLUGINS", "p.json"], writers));
        Assert.Contains("loads plug-in code", ReplayPolicy.Refusal("user-model", ["user-model", "Check-Plugin", "p.json"], writers));
        Assert.Null(ReplayPolicy.Refusal("dynamics-study", ["dynamics-study", "s.json", "--model-assignments", "a.json"], writers));
    }

    [Theory]
    [InlineData("System.Formats.Tar.TarFile", "ExtractToDirectory")]
    [InlineData("System.Data.DataSet", "WriteXml")]
    [InlineData("System.Data.DataTable", "ReadXml")]
    [InlineData("System.Diagnostics.TextWriterTraceListener", ".ctor")]
    [InlineData("System.Diagnostics.Trace", "WriteLine")]
    [InlineData("System.Resources.ResourceWriter", ".ctor")]
    [InlineData("System.Diagnostics.FileVersionInfo", "GetVersionInfo")]
    [InlineData("System.Buffers.ArrayPool`1", "get_Shared")]
    [InlineData("Microsoft.Win32.SafeHandles.SafeFileHandle", ".ctor")]
    public void Sec28_a_framework_route_outside_the_listed_namespaces_is_refused(string type, string member)
        => Assert.NotNull(PluginDenylist.Refusal(type, member));

    [Theory]
    [InlineData("System.Math", "Sqrt")]
    [InlineData("System.Collections.Generic.List`1", "Add")]
    [InlineData("System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray")]
    [InlineData("System.Runtime.Versioning.TargetFrameworkAttribute", ".ctor")]
    [InlineData("System.Diagnostics.DebuggableAttribute", ".ctor")]
    [InlineData("System.Diagnostics.DebuggableAttribute+DebuggingModes", null)]
    [InlineData("System.Reflection.AssemblyTitleAttribute", ".ctor")]
    [InlineData("System.Double[,]", "Get")]
    [InlineData("System.Diagnostics.DebuggerBrowsableState", null)]
    public void Sec28_what_a_numerical_model_needs_stays_allowed(string type, string? member)
        => Assert.Null(PluginDenylist.Refusal(type, member));

    [Fact]
    public void Sec28_only_listed_assemblies_may_be_referenced()
    {
        foreach (string ok in new[] { "GridSim.Core", "System.Runtime", "System.Collections", "System.Collections.Immutable", "netstandard" })
            Assert.True(PluginDenylist.AllowedAssemblyReference(ok), ok);
        foreach (string no in new[] { "System.Formats.Tar", "System.Data.Common", "System.Diagnostics.TextWriterTraceListener", "System.Resources.Writer", "System.Net.Http", "Newtonsoft.Json" })
            Assert.False(PluginDenylist.AllowedAssemblyReference(no), no);
    }

    // ---- CLI-08: dist3ph-mesh and the bus limit ----------------------------------------------------------------------

    [Fact]
    public void Cli08_a_dist3ph_mesh_case_over_the_free_bus_limit_is_refused()
    {
        const int n = 1501;
        var nodes = new JsonArray();
        var branches = new JsonArray();
        for (int i = 0; i < n; i++)
        {
            nodes.Add(new JsonObject { ["name"] = "N" + i, ["wires"] = new JsonArray("a") });
            if (i > 0)
                branches.Add(new JsonObject
                {
                    ["name"] = "B" + i, ["from"] = "N" + (i - 1), ["to"] = "N" + i, ["wires"] = new JsonArray("a"),
                    ["r_ohm"] = new JsonArray(new JsonArray(0.01)), ["x_ohm"] = new JsonArray(new JsonArray(0.01)),
                });
        }
        var doc = new JsonObject
        {
            ["schema"] = "gridsim-dist3ph-mesh/1", ["name"] = "big", ["nodes"] = nodes,
            ["sources"] = new JsonArray(new JsonObject { ["node"] = "N0", ["wires"] = new JsonArray("a"), ["magnitude_v"] = new JsonArray(1000), ["angle_deg"] = new JsonArray(0) }),
            ["branches"] = branches,
        };
        string file = P("big-mesh.json");
        File.WriteAllText(file, doc.ToJsonString());
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", file);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("1,501 buses", r.StdErr, StringComparison.Ordinal);
    }

    // ---- CLI-01: rpc edits and the bus limit ----------------------------------------------------------------------------

    private ProcessStartInfo Rpc(params string[] flags)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        psi.ArgumentList.Add("rpc");
        foreach (string f in flags) psi.ArgumentList.Add(f);
        psi.Environment["GRIDSIM_CRASH_DIR"] = P("crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = P(CliProcess.IsolatedLicenceFileName);
        return psi;
    }

    private static JsonElement J(string line)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        return d.RootElement.Clone();
    }

    [Fact]
    public async Task Cli01_a_case_grown_past_the_bus_limit_over_rpc_is_refused_at_solve_get_save_and_study()
    {
        using Process p = Process.Start(Rpc("--protocol", "2"))!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write("{\"id\":1,\"op\":\"load\",\"case\":\"case9\"}\n");
        p.StandardInput.Flush();
        string handle = J(p.StandardOutput.ReadLine()!).GetProperty("result").GetProperty("handle").GetString()!;
        p.StandardInput.Write("{\"id\":8,\"op\":\"elements\",\"handle\":\"" + handle + "\",\"kind\":\"branch\"}\n");
        p.StandardInput.Flush();
        string branch = J(p.StandardOutput.ReadLine()!).GetProperty("result").GetProperty("rows")[0].GetProperty("key").GetString()!;
        var sb = new StringBuilder();
        // case9 plus 1,492 buses is 1,501, one over the free tier's 1,500.
        for (int k = 0; k < 1492; k++)
            sb.Append("{\"id\":").Append(100 + k).Append(",\"op\":\"add\",\"handle\":\"").Append(handle).Append("\",\"kind\":\"bus\",\"key\":\"")
              .Append(100000 + k).Append("\",\"values\":{\"base_kv\":345}}\n");
        string h = "\"handle\":\"" + handle + "\"";
        sb.Append("{\"id\":2,\"op\":\"solve\",").Append(h).Append("}\n");
        sb.Append("{\"id\":3,\"op\":\"get\",").Append(h).Append(",\"kind\":\"branch\",\"key\":").Append(JsonSerializer.Serialize(branch)).Append(",\"attributes\":[\"p_from_mw\"]}\n");
        sb.Append("{\"id\":4,\"op\":\"save\",").Append(h).Append(",\"path\":\"grown.raw\",\"format\":\"raw33\"}\n");
        sb.Append("{\"id\":5,\"op\":\"study\",").Append(h).Append(",\"verb\":\"pf\",\"args\":[]}\n");
        // One bus deleted: 1,500 buses, within the limit, so the solve is not refused for the licence.
        sb.Append("{\"id\":6,\"op\":\"delete\",").Append(h).Append(",\"kind\":\"bus\",\"key\":\"100000\"}\n");
        sb.Append("{\"id\":7,\"op\":\"solve\",").Append(h).Append("}\n");
        // Read while writing: the host answers each add as it comes, and a full output pipe would otherwise stall both sides.
        Task<string> rest = p.StandardOutput.ReadToEndAsync();
        await p.StandardInput.WriteAsync(sb.ToString());
        p.StandardInput.Close();
        string[] lines = (await rest.WaitAsync(TimeSpan.FromMinutes(10))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p.WaitForExit(600_000), "rpc did not finish");
        _ = await err;
        Assert.Equal(1492 + 6, lines.Length);
        Assert.All(lines.Take(1492), l => Assert.Equal("ok", J(l).GetProperty("status").GetString()));
        foreach (string l in lines.Skip(1492).Take(4))
        {
            JsonElement e = J(l);
            Assert.Equal("licence-refused", e.GetProperty("error").GetProperty("kind").GetString());
            Assert.Contains("1,501 buses", e.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.False(File.Exists(P("grown.raw")));
        Assert.Equal("ok", J(lines[1492 + 4]).GetProperty("status").GetString());
        JsonElement last = J(lines[1492 + 5]);
        Assert.False(last.TryGetProperty("error", out JsonElement le) && le.GetProperty("kind").GetString() == "licence-refused", last.ToString());
    }

    // ---- CLI-03 / CLI-09: rpc --listen ----------------------------------------------------------------------------------

    [Fact]
    public void Cli03_listen_needs_the_token_on_every_request_and_confines_every_file_to_the_working_folder()
    {
        string outside = Path.GetFullPath(Path.Combine(_dir, "..", "outside-" + Guid.NewGuid().ToString("N") + ".m"));
        File.Copy(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"), P("case9.m"));
        using Process p = Process.Start(Rpc("--listen", "127.0.0.1:0"))!;
        string? tokenFile = null;
        try
        {
            _ = p.StandardError.ReadToEndAsync();
            JsonElement banner = J(p.StandardOutput.ReadLine()!);
            Assert.False(banner.TryGetProperty("token", out _));
            tokenFile = banner.GetProperty("token_file").GetString()!;
            if (!OperatingSystem.IsWindows())
            {
                // R1-06: the file is 0600 and its folder 0700.
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(tokenFile)!));
            }
            string token = RpcListenSupport.Token(banner);
            Assert.Equal(64, token.Length);
            int port = RpcListenSupport.Port(banner);

            // No token: refused, and the connection ends.
            using (var c1 = new TcpClient("127.0.0.1", port))
            {
                var (r1, w1) = RpcListenSupport.Streams(c1);
                w1.WriteLine("{\"id\":1,\"op\":\"hello\"}");
                JsonElement e = J(r1.ReadLine()!);
                Assert.Equal("unauthorised", e.GetProperty("error").GetProperty("kind").GetString());
                try { Assert.Null(r1.ReadLine()); } catch (IOException) { /* closed by the server */ }
            }
            // A wrong token: the same.
            using (var c2 = new TcpClient("127.0.0.1", port))
            {
                var (r2, w2) = RpcListenSupport.Streams(c2);
                w2.WriteLine("{\"id\":1,\"op\":\"hello\",\"token\":\"" + new string('0', 64) + "\"}");
                Assert.Equal("unauthorised", J(r2.ReadLine()!).GetProperty("error").GetProperty("kind").GetString());
            }
            using var client = new TcpClient("127.0.0.1", port);
            var (reader, writer) = RpcListenSupport.Streams(client);
            string Ask(string request) { writer.WriteLine(RpcListenSupport.With(request, token)); return reader.ReadLine()!; }

            Assert.Equal("ok", J(Ask("{\"id\":2,\"op\":\"hello\"}")).GetProperty("status").GetString());
            Assert.Equal("ok", J(Ask("{\"id\":3,\"op\":\"load\",\"case\":\"case9.m\"}")).GetProperty("status").GetString());
            JsonElement load = J(Ask("{\"id\":4,\"op\":\"load\",\"case\":" + JsonSerializer.Serialize(outside) + "}"));
            Assert.Equal("bad-argument", load.GetProperty("error").GetProperty("kind").GetString());
            Assert.Contains("outside the base folder", load.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
            string[][] refused =
            [
                ["pf", outside],
                ["pf", "case9", "--json=" + outside],
                ["case14", "--enableExport=" + outside],
                ["pf", Path.Combine("..", "x.m")],
                [outside],
                ["bench", "--cases", "case9," + @"\\attacker\share\x.m"],
                ["bench", "--cases", "case9,//attacker/share/x.m"],
                ["bench", "--cases=case9.m," + outside],   // R1-08 (g): only the second item is outside
            ];
            int id = 10;
            foreach (string[] job in refused)
            {
                JsonElement e = J(Ask(JsonSerializer.Serialize(new { id = id++, op = "run-verb", verb = job[0], args = job[1..] })));
                Assert.True(e.GetProperty("status").GetString() == "error", string.Join(" ", job));
                Assert.Equal("bad-argument", e.GetProperty("error").GetProperty("kind").GetString());
            }
            Assert.False(File.Exists(outside));
            // A file inside the working folder passes the rule.
            JsonElement ok = J(Ask(JsonSerializer.Serialize(new { id = 30, op = "run-verb", verb = "pf", args = new[] { "case9.m" } })));
            Assert.Equal("ok", ok.GetProperty("status").GetString());
            Ask("{\"id\":99,\"op\":\"shutdown\"}");
            Assert.True(p.WaitForExit(60_000));
            Assert.False(File.Exists(tokenFile), "the token file is removed when the host stops");
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            if (tokenFile is not null) try { File.Delete(tokenFile); } catch (IOException) { }
        }
    }

    // ---- batch 1b ----------------------------------------------------------------------------------------------------

    /// <summary>R1-07 and R1-05: a connection that sends nothing is closed (here after --idle-seconds 2, below the 10 s first-request
    /// limit), so the next client is served; and on Windows a probe value "x:5" whose drive does not exist is not a path.</summary>
    [Fact]
    public async Task Listen_closes_a_silent_connection_and_takes_a_drive_shaped_value_for_a_value()
    {
        using Process p = Process.Start(Rpc("--listen", "127.0.0.1:0", "--idle-seconds", "2"))!;
        string? tokenFile = null;
        try
        {
            _ = p.StandardError.ReadToEndAsync();
            JsonElement banner = J(p.StandardOutput.ReadLine()!);
            tokenFile = banner.GetProperty("token_file").GetString();
            string token = RpcListenSupport.Token(banner);
            int port = RpcListenSupport.Port(banner);
            using (var silent = new TcpClient("127.0.0.1", port))
            {
                var (r, _) = RpcListenSupport.Streams(silent);
                silent.ReceiveTimeout = 30_000;
                string? line = null;
                try { line = await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch (IOException) { }
                Assert.Null(line);   // closed by the host, not answered
            }
            using var client = new TcpClient("127.0.0.1", port);
            var (reader, writer) = RpcListenSupport.Streams(client);
            string Ask(string request) { writer.WriteLine(RpcListenSupport.With(request, token)); return reader.ReadLine()!; }
            Assert.Equal("ok", J(Ask("{\"id\":1,\"op\":\"hello\"}")).GetProperty("status").GetString());
            if (OperatingSystem.IsWindows())
            {
                char free = "QRSTUVWXYZ".First(c => !Directory.Exists(c + ":\\"));
                JsonElement e = J(Ask(JsonSerializer.Serialize(new { id = 2, op = "run-verb", verb = "emt", args = new[] { "case9", "--probe", free + ":5", "--duration", "0.001" } })));
                Assert.False(e.TryGetProperty("error", out JsonElement err) && err.GetProperty("kind").GetString() == "bad-argument", e.ToString());
            }
            Ask("{\"id\":99,\"op\":\"shutdown\"}");
            Assert.True(p.WaitForExit(60_000));
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            if (tokenFile is not null) try { File.Delete(tokenFile); } catch (IOException) { }
        }
    }

    /// <summary>R1-08 (f): an edited bundled network is a user network: gb-full (3,539 buses, bundled) loads at the free tier,
    /// and after one set its solve is refused.</summary>
    [Fact]
    public async Task Cli01_an_edited_bundled_case_over_the_limit_is_refused_at_solve()
    {
        using Process p = Process.Start(Rpc("--protocol", "2"))!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync("{\"id\":1,\"op\":\"load\",\"case\":\"gb-full\"}\n");
        await p.StandardInput.FlushAsync();
        JsonElement load = J((await p.StandardOutput.ReadLineAsync())!);
        Assert.True(load.GetProperty("status").GetString() == "ok", load.ToString());
        string h = "\"handle\":\"" + load.GetProperty("result").GetProperty("handle").GetString() + "\"";
        await p.StandardInput.WriteAsync("{\"id\":2,\"op\":\"elements\"," + h + ",\"kind\":\"bus\"}\n");
        await p.StandardInput.FlushAsync();
        string bus = J((await p.StandardOutput.ReadLineAsync())!).GetProperty("result").GetProperty("rows")[0].GetProperty("key").GetString()!;
        Task<string> rest = p.StandardOutput.ReadToEndAsync();
        await p.StandardInput.WriteAsync("{\"id\":3,\"op\":\"set\"," + h + ",\"kind\":\"bus\",\"key\":" + JsonSerializer.Serialize(bus) + ",\"values\":{\"vmax\":1.1}}\n"
            + "{\"id\":4,\"op\":\"solve\"," + h + "}\n");
        p.StandardInput.Close();
        string[] lines = (await rest.WaitAsync(TimeSpan.FromMinutes(10))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p.WaitForExit(120_000));
        _ = await err;
        Assert.Equal("ok", J(lines[0]).GetProperty("status").GetString());
        Assert.Equal("licence-refused", J(lines[1]).GetProperty("error").GetProperty("kind").GetString());
    }

    /// <summary>R1-08 (e): SEC-27's import route end to end. A received folder holds a result file whose command line names
    /// a plug-in configuration, the configuration and its DLL. Import, then validate: CANNOT-REPLAY by the policy, and no
    /// re-run starts.</summary>
    [Fact]
    public void Sec27_an_imported_result_that_names_plugin_code_is_never_re_run()
    {
        string folder = P("received");
        Rc5DynamicsReplaySupport.WriteStudy(folder);
        Rc5DynamicsReplaySupport.WritePlugin(folder);
        string[] args = ["dynamics-study", "study.json", "--model-plugins", "plugins.json", "--model-assignments", "assign.json", "--out", "result.json"];
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { schema = "gridsim-cli-result/1", tool = "gridsim 2.1.100", args, exitCode = 0 }));
        string journal = P("j", "journal.jsonl");
        CliProcess.Result imp = JournalTestSupport.Run(_dir, "breaking-change", "import", "received", "--journal", journal);
        Assert.True(imp.ExitCode == 0, imp.ToString());
        string report = P("v.json");
        CliProcess.Result v = Rc5DynamicsReplaySupport.Validate(_dir, journal, "--json", report);
        Assert.True(v.ExitCode == 1, v.ToString());
        Assert.EndsWith(": CANNOT-REPLAY: not replayable: --model-plugins loads plug-in code, and a re-run never loads code", Rc5DynamicsReplaySupport.Line(v, 1), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Rc5DynamicsReplaySupport.Table(report, "entries")[0].GetProperty("replay_exit").ValueKind);
    }

    /// <summary>R1-02: the files a study names are side files in every study verb.</summary>
    [Fact]
    public void R1_02_study_verbs_refuse_a_network_path_inside_the_study()
    {
        const string Unc = @"\\\\host\\share\\x";
        File.WriteAllText(P("dyn.json"), "{\"schemaVersion\":1,\"raw\":{\"path\":\"" + Unc + ".raw\",\"sha256\":\"00\"},\"dyr\":{\"path\":\"x.dyr\",\"sha256\":\"00\"},"
            + "\"initialisation\":{},\"integration\":{},\"criteria\":{},\"angleReference\":{},\"channels\":[],\"cases\":[]}");
        CliProcess.Result dyn = JournalTestSupport.Run(_dir, "dynamics-study", "dyn.json", "--out", "dyn-out.json");
        Assert.True(dyn.ExitCode == 1, dyn.ToString());
        Assert.Contains("network or device path", dyn.StdErr, StringComparison.Ordinal);
    }

    // ---- batch 2b ----------------------------------------------------------------------------------------------------

    /// <summary>R2-02: a link to an ancestor of a device folder (d -> /) is refused when the rebuilt path is a device path.</summary>
    [UnixFact("no device folder under a drive root on Windows; the drive-letter rule covers it")]
    public void R2_02_a_link_to_the_root_cannot_reach_a_device()
    {
        Directory.CreateSymbolicLink(P("root"), "/");
        Assert.Throws<InvalidDataException>(() => SideFile.Resolve(Path.Combine("root", "dev", "zero"), _dir, "member"));
        Assert.Throws<InvalidDataException>(() => SideFile.Resolve(Path.Combine("root", "proc", "1", "environ"), _dir, "member"));
    }

    /// <summary>R2-04: token file names carry the machine, the process and a random part; the sweep reads only this machine's.</summary>
    [Fact]
    public void R2_04_token_file_names_are_per_machine_and_the_sweep_filter_reads_only_this_one()
    {
        // R4-06 (2.1.162, re-pinned): the cleaned name is followed by 8 hex digits of SHA-256 of the machine name, because
        // the cleaned names alone collide (NODE_07! and node07 both gave node07, so each machine swept the other's files).
        Assert.StartsWith("node07-", GridSim.Cli.RpcTokenFiles.HostName("NODE_07!"), StringComparison.Ordinal);
        Assert.Matches("^node-07-[0-9a-f]{8}$", GridSim.Cli.RpcTokenFiles.HostName("node-07"));
        Assert.StartsWith("host-", GridSim.Cli.RpcTokenFiles.HostName("___"), StringComparison.Ordinal);
        Assert.NotEqual(GridSim.Cli.RpcTokenFiles.HostName("node_1"), GridSim.Cli.RpcTokenFiles.HostName("node1"));
        Assert.NotEqual(GridSim.Cli.RpcTokenFiles.HostName("Node1"), GridSim.Cli.RpcTokenFiles.HostName("node1"));
        Assert.Equal(GridSim.Cli.RpcTokenFiles.HostName("node1"), GridSim.Cli.RpcTokenFiles.HostName("node1"));
        // R4-06: the old 2.1.156-2.1.158 shape is recognised for the sweep, and nothing else is.
        Assert.Equal(1234, GridSim.Cli.RpcTokenFiles.OldShapePid("rpc-1234.token"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.OldShapePid("rpc-node-07-1234-a1b2c3d4.token"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.OldShapePid("rpc-.token"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.OldShapePid("rpc-12x.token"));
        string name = GridSim.Cli.RpcTokenFiles.Name("node-07", 1234, "a1b2c3d4");
        Assert.Equal("rpc-node-07-1234-a1b2c3d4.token", name);
        Assert.Equal(1234, GridSim.Cli.RpcTokenFiles.Pid(name, "node-07"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.Pid(name, "node-08"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.Pid("rpc-node-07-1234.token", "node-07"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.Pid("rpc-1234.token", "node-07"));
        Assert.Null(GridSim.Cli.RpcTokenFiles.Pid(name, "node"));   // another machine whose name begins the same
    }

    /// <summary>R2-03: without the token nothing earns an answer that keeps the connection (a bad id is refused as
    /// unauthorised), and a peer that trickles bytes is closed at the first-request deadline.</summary>
    [Fact]
    public async Task R2_03_an_unauthenticated_peer_cannot_hold_the_endpoint()
    {
        using Process p = Process.Start(Rpc("--listen", "127.0.0.1:0", "--idle-seconds", "2"))!;
        string? tokenFile = null;
        try
        {
            _ = p.StandardError.ReadToEndAsync();
            JsonElement banner = J(p.StandardOutput.ReadLine()!);
            tokenFile = banner.GetProperty("token_file").GetString();
            int port = RpcListenSupport.Port(banner);
            using (var c1 = new TcpClient("127.0.0.1", port))
            {
                var (r1, w1) = RpcListenSupport.Streams(c1);
                w1.WriteLine("{\"id\":[],\"op\":\"hello\"}");
                Assert.Equal("unauthorised", J((await r1.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!).GetProperty("error").GetProperty("kind").GetString());
                string? after = null;
                try { after = await r1.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch (IOException) { }
                Assert.Null(after);
            }
            using (var c2 = new TcpClient("127.0.0.1", port))
            {
                NetworkStream s = c2.GetStream();
                var sw = Stopwatch.StartNew();
                bool closed = false;
                for (int k = 0; k < 20 && !closed; k++)
                {
                    try { s.WriteByte((byte)'{'); s.Flush(); await Task.Delay(500); }
                    catch (IOException) { closed = true; }
                    if (c2.Client.Poll(0, SelectMode.SelectRead) && c2.Client.Available == 0) closed = true;
                }
                Assert.True(closed, "a trickling peer was not closed");
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), $"closed only after {sw.Elapsed}");
            }
            using var client = new TcpClient("127.0.0.1", port);
            var (reader, writer) = RpcListenSupport.Streams(client);
            writer.WriteLine(RpcListenSupport.With("{\"id\":99,\"op\":\"shutdown\"}", RpcListenSupport.Token(banner)));
            reader.ReadLine();
            Assert.True(p.WaitForExit(60_000));
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            if (tokenFile is not null) try { File.Delete(tokenFile); } catch (IOException) { }
        }
    }
}

/// <summary>CLI-03: the client side of <c>rpc --listen</c>'s token, for the tests that talk to a listening host.</summary>
internal static class RpcListenSupport
{
    /// <summary>The host's token, read from the file its first output line names.</summary>
    internal static string Token(JsonElement banner) => File.ReadAllText(banner.GetProperty("token_file").GetString()!).Trim();

    /// <summary>The port of the first output line's <c>listening</c> address.</summary>
    internal static int Port(JsonElement banner)
        => int.Parse(banner.GetProperty("listening").GetString()!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><paramref name="request"/> (one JSON object) with <c>"token"</c> added as its first member.</summary>
    internal static string With(string request, string token)
    {
        int brace = request.IndexOf('{', StringComparison.Ordinal);
        return request[..(brace + 1)] + "\"token\":\"" + token + "\"" + (request[(brace + 1)..].TrimStart().StartsWith('}') ? "" : ",") + request[(brace + 1)..];
    }

    internal static (StreamReader Reader, StreamWriter Writer) Streams(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        return (new StreamReader(stream, new UTF8Encoding(false)), new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true });
    }
}
