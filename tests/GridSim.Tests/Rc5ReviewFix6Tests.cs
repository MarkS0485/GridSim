using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GridSim.Core.Api;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 REVIEW-FIX-6 (the WP11 review and the external review's case-handle item):
/// <list type="bullet">
/// <item>the level 2 study check compares the re-read model with the held one attribute by attribute, bit for bit
/// (<see cref="CaseEditor.AttributeDifference"/>);</item>
/// <item>level 2 handles carry a random token of the host instance, so a handle of another host, or of the same program
/// before a restart, is refused and releases nothing;</item>
/// <item>level 2 <c>get</c> reads a generator's solved output (<c>p_solved_mw</c>, <c>q_solved_mvar</c>);</item>
/// <item><c>save</c> checks every target over <c>--listen</c>, the v2 sibling <c>.network.json</c> included, and only ever
/// creates files new.</item>
/// </list>
/// Oracles: a one-ulp edit made by hand, the solve's own bus injections (bus 1 and bus 2 of case9 carry no load, so the
/// generation there is the injection exactly), and the file system.
/// </summary>
public sealed class Rc5ReviewFix6Tests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-fix6-" + Guid.NewGuid().ToString("N"));

    public Rc5ReviewFix6Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Lib(string name) => Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name);

    private static GridModel Case9() => MatpowerParser.Parse(File.ReadAllText(Lib("case9.m")), "case9");

    private static string Req(int id, string op, string fields = "") => "{\"id\":" + id + ",\"op\":\"" + op + "\"" + (fields.Length > 0 ? "," + fields : "") + "}";

    private static JsonElement J(string line)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        return d.RootElement.Clone();
    }

    // ---- the attribute comparison --------------------------------------------------------------------------------

    [Fact]
    public void Attribute_difference_is_null_for_the_same_model_and_a_json_v2_round_trip_and_names_a_one_ulp_change()
    {
        GridModel m = Case9();
        Assert.Null(CaseEditor.AttributeDifference(m, m));

        string folder = Path.Combine(_dir, "v2");
        V2CaseWriter.Write(m, Path.Combine(folder, "case.json"));
        GridModel back = V2CaseLoader.LoadWithNotes(Path.Combine(folder, "case.json"), Path.Combine(CliProcess.Root(), "schemas", "v2"),
            Path.Combine(CliProcess.Root(), "data")).Model;
        Assert.Null(CaseEditor.AttributeDifference(m, back));

        // One unit in the last place of one generator's Pg: the comparison is bit for bit, so it is found and named.
        var ed = new CaseEditor(m);
        double pg = (double)ed.Get(ElementKind.Generator, "G2", "pg")!;
        ed.Set(ElementKind.Generator, "G2", [new("pg", Math.BitIncrement(pg))]);
        Assert.Equal("generator 'G2' pg differs", CaseEditor.AttributeDifference(m, ed.Model));

        // -0 and +0 are equal as numbers but not as bits.
        var ed0 = new CaseEditor(m);
        ed0.Set(ElementKind.Branch, "1-4", [new("r", -0.0)]);
        Assert.Equal(0.0, (double)ed0.Get(ElementKind.Branch, "1-4", "r")!);
        Assert.Equal("branch '1-4' r differs", CaseEditor.AttributeDifference(m, ed0.Model));
    }

    // ---- processes -------------------------------------------------------------------------------------------------

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

    /// <summary>A running host spoken to one line at a time.</summary>
    private sealed class Host : IDisposable
    {
        private readonly Process _p;
        private readonly Task<string> _err;

        public Host(ProcessStartInfo psi)
        {
            _p = Process.Start(psi)!;
            _err = _p.StandardError.ReadToEndAsync();
        }

        public string Ask(string line)
        {
            _p.StandardInput.Write(line + "\n");
            _p.StandardInput.Flush();
            return _p.StandardOutput.ReadLine()!;
        }

        public void Dispose()
        {
            try
            {
                _p.StandardInput.Close();
                if (!_p.WaitForExit(120_000)) _p.Kill(entireProcessTree: true);
                _p.WaitForExit();
                _ = _err.Result;
            }
            finally { _p.Dispose(); }
        }
    }

    private static string HandleOf(string loadLine) => J(loadLine).GetProperty("result").GetProperty("handle").GetString()!;

    private static string H(string handle) => "\"handle\":\"" + handle + "\"";

    [Fact]
    public void Two_simultaneous_level_2_hosts_refuse_each_others_handles_and_a_foreign_release_releases_nothing()
    {
        using var a = new Host(Start("--protocol", "2"));
        using var b = new Host(Start("--protocol", "2"));
        string tokenA = J(a.Ask(Req(1, "hello"))).GetProperty("result").GetProperty("host").GetString()!;
        string tokenB = J(b.Ask(Req(1, "hello"))).GetProperty("result").GetProperty("host").GetString()!;
        Assert.NotEqual(tokenA, tokenB);

        string ha = HandleOf(a.Ask(Req(2, "load", "\"case\":\"case9\"")));
        string hb = HandleOf(b.Ask(Req(2, "load", "\"case\":\"case14\"")));
        Assert.Equal("case-" + tokenA + "-1", ha);
        Assert.Equal("case-" + tokenB + "-1", hb);

        // A's handle on B: refused for solve and for release, naming the cause; B's own case and A's case are untouched.
        JsonElement solve = J(b.Ask(Req(3, "solve", H(ha))));
        Assert.Equal("error", solve.GetProperty("status").GetString());
        Assert.Equal("unknown-handle", solve.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("not issued by this host", solve.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        JsonElement release = J(b.Ask(Req(4, "release", H(ha))));
        Assert.Equal("error", release.GetProperty("status").GetString());
        Assert.Equal(1, J(b.Ask(Req(5, "stats"))).GetProperty("result").GetProperty("handles").GetInt32());
        Assert.Equal(14, J(b.Ask(Req(6, "solve", H(hb)))).GetProperty("result").GetProperty("bus").GetArrayLength());
        Assert.Equal(9, J(a.Ask(Req(3, "solve", H(ha)))).GetProperty("result").GetProperty("bus").GetArrayLength());
        Assert.Equal(1, J(a.Ask(Req(4, "stats"))).GetProperty("result").GetProperty("handles").GetInt32());
    }

    [Fact]
    public void A_handle_from_before_a_restart_is_refused_by_the_new_host()
    {
        string old;
        using (var first = new Host(Start("--protocol", "2")))
        {
            old = HandleOf(first.Ask(Req(1, "load", "\"case\":\"case9\"")));
            first.Ask(Req(2, "shutdown"));
        }
        using var again = new Host(Start("--protocol", "2"));
        string fresh = HandleOf(again.Ask(Req(1, "load", "\"case\":\"case9\"")));
        Assert.NotEqual(old, fresh);
        Assert.EndsWith("-1", fresh, StringComparison.Ordinal);   // the same position in the new host's numbering
        JsonElement r = J(again.Ask(Req(2, "solve", H(old))));
        Assert.Equal("error", r.GetProperty("status").GetString());
        Assert.Contains("not issued by this host", r.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("ok", J(again.Ask(Req(3, "solve", H(fresh)))).GetProperty("status").GetString());
    }

    [Fact]
    public void Level_1_handles_stay_numbered_per_session()
    {
        using var host = new Host(Start());
        Assert.Equal("case-1", HandleOf(host.Ask(Req(1, "load", "\"case\":\"case9\""))));
        Assert.Equal("case-2", HandleOf(host.Ask(Req(2, "load", "\"case\":\"case9\""))));
        Assert.False(J(host.Ask(Req(3, "hello"))).GetProperty("result").TryGetProperty("host", out _));
    }

    [Fact]
    public void Generator_solved_output_is_the_bus_generation_of_the_solve()
    {
        using var host = new Host(Start("--protocol", "2"));
        string h = H(HandleOf(host.Ask(Req(1, "load", $"\"case\":\"{Lib("case9.m").Replace("\\", "\\\\", StringComparison.Ordinal)}\""))));
        JsonElement s = J(host.Ask(Req(2, "solve", h))).GetProperty("result");
        // Buses 1 and 2 of case9 carry no load and one unit each, so the unit's output is the bus injection exactly
        // (injection + 0 load). G2's solved P is the solve's 162.99999999999997, not its 163 MW set-point.
        for (int bus = 0; bus < 2; bus++)
        {
            JsonElement v = J(host.Ask(Req(3 + bus, "get", h + $",\"kind\":\"generator\",\"key\":\"G{bus + 1}\",\"attributes\":[\"p_solved_mw\",\"q_solved_mvar\",\"pg\"]")))
                .GetProperty("result").GetProperty("values");
            Assert.Equal(BitConverter.DoubleToInt64Bits(s.GetProperty("p_inj_mw")[bus].GetDouble()), BitConverter.DoubleToInt64Bits(v.GetProperty("p_solved_mw").GetDouble()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(s.GetProperty("q_inj_mvar")[bus].GetDouble()), BitConverter.DoubleToInt64Bits(v.GetProperty("q_solved_mvar").GetDouble()));
            if (bus == 1)
            {
                Assert.Equal(163.0, v.GetProperty("pg").GetDouble());
                Assert.NotEqual(163.0, v.GetProperty("p_solved_mw").GetDouble());
            }
        }
        Assert.Equal("ok", J(host.Ask(Req(5, "set", h + ",\"kind\":\"generator\",\"key\":\"G3\",\"values\":{\"status\":0}"))).GetProperty("status").GetString());
        string off = host.Ask(Req(6, "get", h + ",\"kind\":\"generator\",\"key\":\"G3\",\"attributes\":[\"p_solved_mw\"]"));
        Assert.Contains("out of service", off, StringComparison.Ordinal);
        Assert.Equal("error", J(off).GetProperty("status").GetString());
    }

    /// <summary>A link at <paramref name="link"/> to <paramref name="target"/>: a symbolic link, or on Windows without the
    /// symbolic-link privilege a directory junction (which needs none; its target folder is made first). False when
    /// neither can be made.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }
        Directory.CreateDirectory(target);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string x in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(x);   // /d: no AutoRun commands
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new DirectoryInfo(link).LinkTarget is not null;
    }

    [Fact]
    public void Save_never_writes_through_a_link_and_over_listen_refuses_a_linked_sibling_network_file()
    {
        string outside = TestTemp.Under("gridsim-fix6-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            // stdio: a dangling link where the RAW file would go is never written through (create-new only).
            if (TryLink(Path.Combine(_dir, "linked.raw"), Path.Combine(outside, "x.raw")))
            {
                Directory.CreateDirectory(outside);
                bool junction = Directory.Exists(Path.Combine(outside, "x.raw"));
                using (var host = new Host(Start("--protocol", "2")))
                {
                    string h = H(HandleOf(host.Ask(Req(1, "load", "\"case\":\"case9\""))));
                    JsonElement r = J(host.Ask(Req(2, "save", h + ",\"path\":\"linked.raw\",\"format\":\"raw33\"")));
                    Assert.Equal("error", r.GetProperty("status").GetString());
                }
                Assert.False(File.Exists(Path.Combine(outside, "x.raw")));
                if (junction) Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(outside, "x.raw")));
            }

            // --listen: the v2 sibling c9.network.json is a link (dangling, to outside the folder); the manifest name is fine.
            bool linked = TryLink(Path.Combine(_dir, "c9.network.json"), Path.Combine(outside, "c9.network.json"));
            using Process p = Process.Start(Start("--protocol", "2", "--listen", "127.0.0.1:0"))!;
            try
            {
                JsonElement banner = J(p.StandardOutput.ReadLine()!);
                int port = int.Parse(banner.GetProperty("listening").GetString()!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
                using var client = new TcpClient("127.0.0.1", port);
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                string token = RpcListenSupport.Token(banner);   // CLI-03: every request over --listen carries the host's token
                string Ask(string line) { writer.WriteLine(RpcListenSupport.With(line, token)); return reader.ReadLine()!; }
                string h = H(HandleOf(Ask(Req(1, "load", "\"case\":\"case9\""))));
                if (linked)
                {
                    string r = Ask(Req(2, "save", h + ",\"path\":\"c9.case.json\",\"format\":\"v2\""));
                    Assert.Contains("save target \\\"c9.network.json\\\" is refused over --listen", r, StringComparison.Ordinal);
                    Assert.False(File.Exists(Path.Combine(_dir, "c9.case.json")));   // nothing is written when any target is refused
                }
                // A plain v2 save inside the folder still writes both files.
                string ok = Ask(Req(3, "save", h + ",\"path\":\"plain/c9.case.json\",\"format\":\"v2\""));
                Assert.Contains("\"status\":\"ok\"", ok, StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(_dir, "plain", "c9.network.json")));
                Ask(Req(4, "shutdown"));
            }
            finally
            {
                if (!p.WaitForExit(60_000)) p.Kill(entireProcessTree: true);
            }
            Assert.False(File.Exists(Path.Combine(outside, "c9.network.json")));
            if (Directory.Exists(Path.Combine(outside, "c9.network.json")))
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(outside, "c9.network.json")));
            Assert.True(linked || !OperatingSystem.IsWindows(), "no link could be made, so the linked-sibling refusal was not exercised");
        }
        finally
        {
            try { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }
}
