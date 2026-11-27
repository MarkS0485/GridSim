using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Api;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP15: adding and deleting elements in the object model (<see cref="CaseEditor.Add"/>, <see cref="CaseEditor.Delete"/>)
/// and the level 2 rpc <c>add</c> and <c>delete</c> operations. Oracles: the same model built directly (bit for bit), the
/// closed-form two-bus solution, <see cref="CaseEditor.AttributeDifference"/> against the case as loaded, and transcript hashes
/// of rpc levels 1 and 2 taken from the 2.1.108 build before any WP15 edit.
/// </summary>
public sealed class Rc5Wp15ObjectModelAddDeleteTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-wp15-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp15ObjectModelAddDeleteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static KeyValuePair<string, double>[] V(params (string Name, double Value)[] v) =>
        v.Select(x => new KeyValuePair<string, double>(x.Name, x.Value)).ToArray();

    private static PowerFlowResult Solve(GridModel m) => new SteadyStateSolver().Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });

    private static GridModel Case9() => MatpowerParser.Parse(File.ReadAllText(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m")), "case9");

    /// <summary>One slack bus (bus 1, 132 kV, V 1.0) with one unit and nothing else.</summary>
    private static GridModel OneBus() => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack, BaseKv: 132)],
        Generators = [new Generator(1, 0)],
        Branches = [],
        Name = "one-bus",
    };

    /// <summary>The two-bus model of test 1 built with Add: bus 2 (132 kV), branch 1-2:1 (X 0.1 pu), load at 2 (50 MW, 20 MVAr).</summary>
    private static CaseEditor TwoBusByAdd()
    {
        var ed = new CaseEditor(OneBus());
        Assert.Equal(1, ed.Add(ElementKind.Bus, "2", V(("base_kv", 132))));
        Assert.Equal(2, ed.Add(ElementKind.Branch, "1-2:1", V(("x", 0.1))));
        Assert.Equal(3, ed.Add(ElementKind.Load, "2", V(("pd", 50), ("qd", 20))));
        return ed;
    }

    private static void SameBits(PowerFlowResult a, PowerFlowResult b)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void T1_add_equals_the_model_built_directly_and_the_closed_form()
    {
        CaseEditor ed = TwoBusByAdd();
        GridModel built = new()
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 50, Qd: 20, BaseKv: 132)],
            Generators = [new Generator(1, 0)],
            Branches = [new Branch(1, 2, 0, 0.1, Name: "1-2:1")],
            Name = "one-bus",
        };
        Assert.Equal(built.Buses, ed.Model.Buses);
        Assert.Equal(built.Branches, ed.Model.Branches);
        PowerFlowResult a = Solve(ed.Model), b = Solve(built);
        Assert.True(a.Converged);
        SameBits(a, b);

        // Closed form (lossless line, V1 = 1, S2 = 0.5 + j0.2 pu, X = 0.1): V2^2 = [(1 - 2QX) + sqrt((1 - 2QX)^2 -
        // 4X^2(P^2 + Q^2))]/2 and sin(theta2) = -PX/V2, evaluated with mpmath at 40 digits: V2 = 0.97824823061862617640,
        // theta2 = -2.92976536015813502106 degrees. Newton stops at a mismatch of about 1e-12 pu or below, so 1e-9 holds.
        int i2 = a.BusIds.ToList().IndexOf(2);
        Assert.Equal(0.9782482306186262, a.Vm[i2], 1e-9);
        Assert.Equal(-2.929765360158135, a.VaDeg[i2], 1e-9);
    }

    [Fact]
    public void T2_delete_undoes_add()
    {
        CaseEditor ed = TwoBusByAdd();
        ed.Delete(ElementKind.Load, "2");
        ed.Delete(ElementKind.Branch, "1-2:1");   // bus 2 is then an island with no slack: a structural edit accepts it
        ed.Delete(ElementKind.Bus, "2");
        Assert.Equal(6, ed.Revision);
        Assert.Null(CaseEditor.AttributeDifference(ed.Original, ed.Model));
        Assert.Equal(["1"], ed.Elements(ElementKind.Bus).Select(k => k.Key));
        Assert.Empty(ed.Elements(ElementKind.Branch));
        Assert.Empty(ed.Elements(ElementKind.Load));
    }

    [Fact]
    public void T3_cascade_logs_branch_load_bus_in_order_and_without_it_nothing_changes()
    {
        CaseEditor ed = TwoBusByAdd();
        GridModel before = ed.Model;
        CaseEditException e = Assert.Throws<CaseEditException>(() => ed.Delete(ElementKind.Bus, "2"));
        Assert.Contains("cascade", e.Message, StringComparison.Ordinal);
        Assert.Equal(3, ed.Revision);
        Assert.Same(before, ed.Model);

        Assert.Equal(4, ed.Delete(ElementKind.Bus, "2", cascade: true));
        CaseEdit[] last = ed.Edits.Where(x => x.Revision == 4).ToArray();
        Assert.Equal(3, last.Length);
        Assert.Equal([(ElementKind.Branch, "1-2:1"), (ElementKind.Load, "2"), (ElementKind.Bus, "2")], last.Select(x => (x.Kind, x.Key)));
        Assert.All(last, x => Assert.Equal("delete", x.Attribute));
        Assert.Null(CaseEditor.AttributeDifference(ed.Original, ed.Model));
    }

    [Fact]
    public void T4_bus_type_follows_units_and_the_last_slack_unit_stays()
    {
        CaseEditor ed = TwoBusByAdd();
        ed.Add(ElementKind.Generator, "2:1", V(("pg", 10), ("vg", 1.0)));
        Assert.Equal(2.0, ed.Get(ElementKind.Bus, "2", "type"));
        CaseEdit auto = ed.Edits[^1];
        Assert.Equal((4, ElementKind.Bus, "2", "type", (object?)1.0, 2.0), (auto.Revision, auto.Kind, auto.Key, auto.Attribute, auto.Before, auto.After));

        ed.Delete(ElementKind.Generator, "2:1");
        Assert.Equal(1.0, ed.Get(ElementKind.Bus, "2", "type"));
        Assert.Equal((ElementKind.Bus, "type", 1.0), (ed.Edits[^1].Kind, ed.Edits[^1].Attribute, ed.Edits[^1].After));

        int rev = ed.Revision;
        CaseEditException e = Assert.Throws<CaseEditException>(() => ed.Delete(ElementKind.Generator, "G1"));
        Assert.Contains("slack", e.Message, StringComparison.Ordinal);
        Assert.Equal(rev, ed.Revision);
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Bus, "3", V(("base_kv", 132), ("type", 3))));
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Generator, "2:1#x", V(("pg", 1))));
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Generator, "7:1", V(("pg", 1))));
    }

    [Fact]
    public void T5_after_a_delete_every_other_key_returns_the_same_element()
    {
        var ed = new CaseEditor(Case9());
        string[] keys = ed.Elements(ElementKind.Branch).Select(k => k.Key).ToArray();
        Dictionary<string, long[]> before = keys.ToDictionary(k => k, k => Bits(ed.GetAll(ElementKind.Branch, k)));
        string gone = keys[3];
        ed.Delete(ElementKind.Branch, gone);
        Assert.Equal(keys.Where(k => k != gone), ed.Elements(ElementKind.Branch).Select(k => k.Key));
        foreach (string k in keys.Where(k => k != gone))
            Assert.Equal(before[k], Bits(ed.GetAll(ElementKind.Branch, k)));
        // Indices after the deleted one moved down by one, those before it did not.
        Assert.Equal(Enumerable.Range(0, keys.Length - 1), ed.Elements(ElementKind.Branch).Select(k => k.Index));
        Assert.Throws<CaseEditException>(() => ed.Find(ElementKind.Branch, gone));
    }

    private static long[] Bits(IReadOnlyList<KeyValuePair<string, object?>> v) =>
        v.Select(x => BitConverter.DoubleToInt64Bits((double)x.Value!)).ToArray();

    [Fact]
    public void T6_one_load_per_bus()
    {
        var ed = new CaseEditor(Case9());
        CaseEditException e = Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Load, "5", V(("pd", 1))));
        Assert.Equal("bus 5 already has a load: GridSim holds one load per bus; use set to change it.", e.Message);
        Assert.Equal(0, ed.Revision);
        // A bus with no demand takes one, listed in bus order.
        ed.Add(ElementKind.Load, "4", V(("pd", 1), ("qd", 0.5)));
        Assert.Equal(["4", "5", "7", "9"], ed.Elements(ElementKind.Load).Select(k => k.Key));
        Assert.Equal(1.0, ed.Get(ElementKind.Load, "4", "pd"));
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Shunt, "4", V(("gs", 0), ("bs", 0))));
        ed.Add(ElementKind.Shunt, "4", V(("bs", 10)));
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Shunt, "4", V(("bs", 1))));
        ed.Delete(ElementKind.Shunt, "4");
        ed.Delete(ElementKind.Load, "4");
        Assert.Null(CaseEditor.AttributeDifference(ed.Original, ed.Model));
    }

    [Fact]
    public void T7_an_add_the_validator_refuses_changes_nothing()
    {
        var ed = new CaseEditor(Case9());
        GridModel before = ed.Model;
        CaseEditException e = Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Branch, "4-5:2", V(("x", 0), ("r", 0))));
        Assert.Contains("zero series impedance", e.Message, StringComparison.Ordinal);
        Assert.Same(before, ed.Model);
        Assert.Equal(0, ed.Revision);
        Assert.Empty(ed.Edits);
        Assert.Throws<CaseEditException>(() => ed.Find(ElementKind.Branch, "4-5:2"));
        // Unknown and missing attributes are refused by name.
        Assert.Contains("needs 'x'", Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Branch, "4-5:2", V(("r", 0.01)))).Message, StringComparison.Ordinal);
        Assert.Contains("takes no attribute 'name'", Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Bus, "10", V(("base_kv", 1), ("name", 1)))).Message, StringComparison.Ordinal);
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Bus, "9", V(("base_kv", 345))));     // id in use
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Bus, "010", V(("base_kv", 345))));   // not written plainly
        Assert.Throws<CaseEditException>(() => ed.Add(ElementKind.Branch, "4-99:1", V(("x", 0.1))));   // no bus 99
    }

    [Fact]
    public void T7b_a_branch_a_tap_changer_names_is_kept_unless_cascade()
    {
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 33), new Bus(3, BusType.Pq, Pd: 5, BaseKv: 33)],
            Generators = [new Generator(1, 0)],
            Branches = [new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true), new Branch(1, 2, 0, 0.1, Name: "T2", IsTransformer: true), new Branch(2, 3, 0, 0.1)],
            Oltcs = [new Oltc("T1", 2)],
            Name = "oltc",
        };
        var ed = new CaseEditor(m);
        Assert.Contains("tap changer", Assert.Throws<CaseEditException>(() => ed.Delete(ElementKind.Branch, "T1")).Message, StringComparison.Ordinal);
        Assert.Same(m, ed.Model);
        ed.Delete(ElementKind.Branch, "T2");
        Assert.Single(ed.Model.Oltcs);
        Assert.Equal(2, ed.Delete(ElementKind.Branch, "T1", cascade: true));
        Assert.Empty(ed.Model.Oltcs);
        Assert.Equal(["2-3"], ed.Elements(ElementKind.Branch).Select(k => k.Key));
        Assert.Equal(0, ed.Elements(ElementKind.Branch)[0].Index);
    }

    [Fact]
    public void T7c_deleting_a_bus_moves_later_bus_load_and_shunt_indices_and_keeps_tripped_loads()
    {
        var ed = new CaseEditor(Case9());
        ed.Set(ElementKind.Load, "9", V(("status", 0)));   // bus 9's demand kept for re-energising
        ed.Add(ElementKind.Bus, "10", V(("base_kv", 345)));
        ed.Delete(ElementKind.Bus, "4", cascade: true);
        Assert.Equal(["1", "2", "3", "5", "6", "7", "8", "9", "10"], ed.Elements(ElementKind.Bus).Select(k => k.Key));
        Assert.Equal(Enumerable.Range(0, 9), ed.Elements(ElementKind.Bus).Select(k => k.Index));
        Assert.Equal([3, 5, 7], ed.Elements(ElementKind.Load).Select(k => k.Index));
        Assert.Equal(125.0, ed.Get(ElementKind.Load, "9", "pd"));
        Assert.Equal(0.0, ed.Get(ElementKind.Load, "9", "status"));
        // Set still works on the case as it now stands (its islands were accepted by the structural edit).
        ed.Set(ElementKind.Load, "9", V(("status", 1)));
        Assert.Equal(125.0, ed.Model.Buses[7].Pd);
    }

    // ---- rpc ------------------------------------------------------------------------------------------------------------

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

    /// <summary>Send each request and read its response line, in turn; "HANDLE" in a request is replaced by the handle the
    /// load (the request with "op":"load") returned.</summary>
    private List<string> Converse(IEnumerable<string> requests, params string[] flags)
    {
        using Process p = Process.Start(Start(flags))!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        string handle = "HANDLE";
        foreach (string r in requests)
        {
            p.StandardInput.Write(r.Replace("HANDLE", handle, StringComparison.Ordinal) + "\n");
            p.StandardInput.Flush();
            string line = p.StandardOutput.ReadLine()!;
            lines.Add(line);
            if (r.Contains("\"op\":\"load\"", StringComparison.Ordinal))
            {
                using JsonDocument d = JsonDocument.Parse(line);
                handle = d.RootElement.GetProperty("result").GetProperty("handle").GetString()!;
            }
        }
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(600_000), "rpc did not finish");
        p.WaitForExit();
        _ = err.Result;
        return lines;
    }

    /// <summary>The transcript hash as the pinning script computed it: tool version, host token and handle token replaced,
    /// lines joined with LF, SHA-256 in lower-case hex. Line 0 must be hello and line 1 the load of case9.</summary>
    private static string TranscriptSha(IReadOnlyList<string> lines)
    {
        // A built-in case's case_sha256 is SHA-256("built-in:<name>:<tool version>") (RpcCommand), so it moves with every
        // version by design. The host's own value for this build is replaced by the value the 2.1.108 build sent
        // (2.1.108+582956e1a44766f7826d54fbc02d11ce4eec6c00, the build the pins were taken from), after checking that it is
        // exactly that function of this build's version; every other byte is compared as sent.
        string tool = J(lines[0]).GetProperty("result").GetProperty("tool").GetString()!;
        Assert.StartsWith("gridsim ", tool, StringComparison.Ordinal);
        string now = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("built-in:case9:" + tool["gridsim ".Length..])));
        Assert.Equal(now, J(lines[1]).GetProperty("case_sha256").GetString());
        const string Then = "ad9c8466a3b3ab9b9a32f4efcd9ca97d608d6f203df0c29e28019574252c098b";
        IEnumerable<string> norm = lines.Select(l =>
        {
            l = Rc5LineEndings.EscapedToCrLf(l).Replace(now, Then, StringComparison.Ordinal);
            l = Regex.Replace(l, "\"tool\":\"gridsim [^\"]*\"", "\"tool\":\"<tool>\"");
            l = Regex.Replace(l, "case-[0-9a-f]{16}-", "case-<host>-");
            return Regex.Replace(l, "\"host\":\"[0-9a-f]{16}\"", "\"host\":\"<host>\"");
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", norm)))).ToLowerInvariant();
    }

    /// <summary>
    /// Bit identity of rpc levels 1 and 2 (R405 pins level 1 elsewhere). Both transcripts, and these hashes, were taken from
    /// the 2.1.108 build (dev main 4282db51) before any WP15 source edit, by a script that sent these exact lines. Level 1
    /// still answers add and delete as unknown operations with the level 1 list; level 2's hello, elements, get, set (and a
    /// refused set), edits, solve, study pf and the unknown-operation list are unchanged.
    /// </summary>
    [Fact]
    public void T8a_level_1_and_level_2_transcripts_equal_the_2_1_108_build()
    {
        string[] level1 =
        [
            @"{""id"":1,""op"":""hello""}",
            @"{""id"":2,""op"":""load"",""case"":""case9""}",
            @"{""id"":3,""op"":""solve"",""handle"":""case-1""}",
            @"{""id"":4,""op"":""stats""}",
            @"{""id"":5,""op"":""add"",""handle"":""case-1"",""kind"":""bus"",""key"":""10"",""values"":{""base_kv"":345}}",
            @"{""id"":6,""op"":""delete"",""handle"":""case-1"",""kind"":""bus"",""key"":""9""}",
            @"{""id"":7,""op"":""elements"",""handle"":""case-1"",""kind"":""bus""}",
            @"{""id"":8,""op"":""release"",""handle"":""case-1""}",
        ];
        string[] level2 =
        [
            @"{""id"":1,""op"":""hello""}",
            @"{""id"":2,""op"":""load"",""case"":""case9""}",
            @"{""id"":10,""op"":""elements"",""handle"":""HANDLE"",""kind"":""bus""}",
            @"{""id"":11,""op"":""elements"",""handle"":""HANDLE"",""kind"":""generator""}",
            @"{""id"":12,""op"":""elements"",""handle"":""HANDLE"",""kind"":""load""}",
            @"{""id"":13,""op"":""elements"",""handle"":""HANDLE"",""kind"":""branch""}",
            @"{""id"":14,""op"":""elements"",""handle"":""HANDLE"",""kind"":""shunt""}",
            @"{""id"":20,""op"":""get"",""handle"":""HANDLE"",""kind"":""branch"",""key"":""4-5"",""attributes"":[""p_from_mw"",""q_from_mvar"",""loading_pct"",""x""]}",
            @"{""id"":21,""op"":""get"",""handle"":""HANDLE"",""kind"":""generator"",""key"":""G1"",""attributes"":[""p_solved_mw"",""pg""]}",
            @"{""id"":22,""op"":""set"",""handle"":""HANDLE"",""kind"":""load"",""key"":""5"",""values"":{""pd"":95.5}}",
            @"{""id"":23,""op"":""set"",""handle"":""HANDLE"",""kind"":""branch"",""key"":""4-5"",""values"":{""x"":0,""r"":0}}",
            @"{""id"":24,""op"":""edits"",""handle"":""HANDLE""}",
            @"{""id"":25,""op"":""solve"",""handle"":""HANDLE""}",
            @"{""id"":26,""op"":""study"",""handle"":""HANDLE"",""verb"":""pf"",""args"":[]}",
            @"{""id"":27,""op"":""frobnicate"",""handle"":""HANDLE""}",
            @"{""id"":28,""op"":""elements"",""handle"":""HANDLE"",""kind"":""load""}",
            @"{""id"":29,""op"":""release"",""handle"":""HANDLE""}",
        ];
        Assert.Equal("99eca702eda83799e06ad6a86addb2489cd5f60abe86603fc3d325dd2036434d", TranscriptSha(Converse(level1)));
        // 2.1.110 lists add and delete last in level 2's hello and so in its unknown-operation message (a structural change
        // the coordinator approved). Those two names are taken out again here, so every other byte is still compared with
        // the 2.1.108 transcript.
        List<string> two = Converse(level2, "--protocol", "2");
        Assert.Contains(",\"save\",\"add\",\"delete\"]", two[0], StringComparison.Ordinal);
        Assert.Contains(two, l => l.Contains(", save, add, delete.", StringComparison.Ordinal));
        Assert.Equal("3156fccb99ab92aa77249de2afff54704f80e3175d39de775a2b73b16e56295e", TranscriptSha(two.Select(l => l
            .Replace(",\"save\",\"add\",\"delete\"]", ",\"save\"]", StringComparison.Ordinal)
            .Replace(", save, add, delete.", ", save.", StringComparison.Ordinal)).ToList()));
    }

    private static JsonElement J(string line)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        return d.RootElement.Clone();
    }

    [Fact]
    public void T8b_level_2_add_and_delete_round_trip_and_a_study_runs_on_the_added_elements()
    {
        List<string> r = Converse(
        [
            @"{""id"":1,""op"":""load"",""case"":""case9""}",
            @"{""id"":2,""op"":""elements"",""handle"":""HANDLE"",""kind"":""bus""}",
            @"{""id"":3,""op"":""add"",""handle"":""HANDLE"",""kind"":""bus"",""key"":""10"",""values"":{""base_kv"":345}}",
            @"{""id"":4,""op"":""add"",""handle"":""HANDLE"",""kind"":""branch"",""key"":""9-10:1"",""values"":{""x"":0.1}}",
            @"{""id"":5,""op"":""add"",""handle"":""HANDLE"",""kind"":""load"",""key"":""10"",""values"":{""pd"":50,""qd"":20}}",
            @"{""id"":6,""op"":""solve"",""handle"":""HANDLE""}",
            @"{""id"":7,""op"":""study"",""handle"":""HANDLE"",""verb"":""pf"",""args"":[]}",
            @"{""id"":8,""op"":""delete"",""handle"":""HANDLE"",""kind"":""bus"",""key"":""10""}",
            @"{""id"":9,""op"":""delete"",""handle"":""HANDLE"",""kind"":""bus"",""key"":""10"",""cascade"":true}",
            @"{""id"":10,""op"":""edits"",""handle"":""HANDLE""}",
            @"{""id"":11,""op"":""elements"",""handle"":""HANDLE"",""kind"":""bus""}",
            @"{""id"":12,""op"":""add"",""handle"":""case-0000000000000000-1"",""kind"":""bus"",""key"":""11"",""values"":{""base_kv"":1}}",
            @"{""id"":13,""op"":""delete"",""handle"":""HANDLE"",""kind"":""bus"",""key"":""1"",""cascade"":""yes""}",
            @"{""id"":14,""op"":""add"",""handle"":""HANDLE"",""kind"":""bus"",""key"":""12"",""values"":{""base_kv"":1},""extra"":1}",
        ], "--protocol", "2");
        Assert.Equal("ok", J(r[2]).GetProperty("status").GetString());
        Assert.Equal(3, J(r[4]).GetProperty("result").GetProperty("revision").GetInt32());
        JsonElement solve = J(r[5]).GetProperty("result");
        Assert.True(solve.GetProperty("converged").GetBoolean());
        Assert.Equal(10, solve.GetProperty("bus").GetArrayLength());
        JsonElement study = J(r[6]);
        Assert.Equal("ok", study.GetProperty("status").GetString());
        // The study (written as JSON v2, read back, checked) gives the solve's own bus voltages, bit for bit.
        JsonElement rows = study.GetProperty("result").GetProperty("document").GetProperty("tables").GetProperty("buses");
        Assert.Equal(10, rows.GetArrayLength());
        for (int i = 0; i < 10; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(solve.GetProperty("vm_pu")[i].GetDouble()), BitConverter.DoubleToInt64Bits(rows[i].GetProperty("vm_pu").GetDouble()));
        Assert.Equal("refused", J(r[7]).GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal(4, J(r[8]).GetProperty("result").GetProperty("revision").GetInt32());
        JsonElement[] last = J(r[9]).GetProperty("result").GetProperty("edits").EnumerateArray().Where(e => e.GetProperty("revision").GetInt32() == 4).ToArray();
        Assert.Equal(["branch", "load", "bus"], last.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal(J(r[1]).GetProperty("result").GetProperty("rows").ToString(), J(r[10]).GetProperty("result").GetProperty("rows").ToString());
        // The host token check and the argument checks of set apply: another host's handle, a cascade that is not a
        // boolean and an unknown field are refused, and nothing changes.
        Assert.Equal("error", J(r[11]).GetProperty("status").GetString());
        Assert.Equal("bad-argument", J(r[12]).GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("bad-argument", J(r[13]).GetProperty("error").GetProperty("kind").GetString());
    }

    /// <summary>The second link of the STUDY SCOPF chain (GridSim.Scripting.Tests' Rc5Wp15ScopfStudyTests is the first): the
    /// Core call a script's STUDY SCOPF makes equals the scopf verb's total cost on the same MATPOWER file, bit for bit.</summary>
    [Fact]
    public void T10_scopf_study_core_call_equals_the_scopf_verb()
    {
        string file = Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m");
        GridSim.Core.Analysis.ScopfResult r = GridSim.Core.Analysis.DcScopf.Solve(MatpowerParser.Parse(File.ReadAllText(file), "case9", 60.0),
            new GridSim.Core.Analysis.ScopfOptions
            {
                Contingencies = null, PostContingencyRating = PostFaultRating.RateA, CostPerGen = null, ConstraintGeneration = true, MaxRounds = 50,
            });
        string json = Path.Combine(_dir, "scopf.json");
        CliProcess.Result run = CliProcess.RunBuilt("scopf", file, "--json", json);
        Assert.True(File.Exists(json), run.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(json));
        JsonElement summary = d.RootElement.GetProperty("summary");
        Assert.Equal(BitConverter.DoubleToInt64Bits(r.TotalCost), BitConverter.DoubleToInt64Bits(summary.GetProperty("total_cost_per_h").GetDouble()));
        Assert.Equal(r.Contingencies.Count, summary.GetProperty("outages_secured").GetInt32());
    }

    [Fact]
    public void T8c_level_1_has_no_add_or_delete()
    {
        List<string> r = Converse([@"{""id"":1,""op"":""load"",""case"":""case9""}", @"{""id"":2,""op"":""add"",""handle"":""case-1"",""kind"":""bus"",""key"":""10"",""values"":{""base_kv"":1}}"]);
        Assert.Equal("unknown-operation", J(r[1]).GetProperty("error").GetProperty("kind").GetString());
    }
}
