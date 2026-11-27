using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Harmonics.Planning.Flicker;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 REVIEW-FIX-7 (the WP12 review): plug-in isolation and lint coverage.
/// <list type="bullet">
/// <item>The lint's lists are private immutable arrays. Plug-ins that try to change them (reflection, an UnsafeAccessor, an
/// IgnoresAccessChecksTo attribute), to write static fields of other assemblies (a GridSim.Core static array, the framework's
/// <c>string.Empty</c>), or to reach GridSim.Core outside Dynamics.Models, Dynamics.Plugins and Numerics (a file write
/// through <c>EventCatalog.Save</c>, the loader, the SIMD capabilities) are refused before they load.</item>
/// <item>Each hazard the review named (<c>double.Sin</c>/<c>Exp</c>/<c>FusedMultiplyAdd</c>, <c>Half</c>, the comparer hash
/// codes, a <c>Dictionary</c> or <c>HashSet</c> read through <c>IEnumerable</c>, LINQ or <c>ToArray</c>,
/// <c>ImmutableHashSet</c>, <c>FrozenSet</c>, <c>OperatingSystem</c>, <c>CurrentCulture</c>, <c>TimeZoneInfo</c>,
/// <c>JitInfo</c>, <c>Type.GetType</c> and <c>Expression.Compile</c>) is refused in compiled IL, named with its method.</item>
/// <item>The replay starts its two instances at different points and compares each with a run of its own, so a static written
/// in Initialize and read in Step (which two instances at the same point cannot show) is refused.</item>
/// <item><c>rpc --listen</c> refuses both plug-in routes, the <c>--flag=value</c> form included.</item>
/// </list>
/// The plug-ins are the projects tests/GridSim.ModelPlugin.HostileDenylist, .HostileStatic, .HostileCore, .HazardSample and
/// .LeakSample, built by the solution (the test project does not reference them).
/// </summary>
public sealed class Rc5ReviewFix7PluginIsolationTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-fix7-" + Guid.NewGuid().ToString("N"));

    public Rc5ReviewFix7PluginIsolationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string BuiltDll(string project)
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string dll = Path.Combine(CliProcess.Root(), "tests", project, "bin", tfmDir.Parent!.Name, tfmDir.Name, project + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"the plug-in {project} was not found; build the solution (or that project) first", dll);
        return dll;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Copy the project's DLL into the test folder and write a configuration listing it with <paramref name="ids"/>.</summary>
    private string Configure(string project, params string[] ids)
    {
        string file = project + ".dll";
        File.Copy(BuiltDll(project), Path.Combine(_dir, file), overwrite: true);
        string config = Path.Combine(_dir, project + ".plugins.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            schema = "gridsim-plugins/1",
            assemblies = new[] { new { path = file, sha256 = Sha(Path.Combine(_dir, file)), models = ids } },
        }));
        return config;
    }

    private static void AssertNothingLoaded(string configPath)
    {
        string name = PluginLoader.ContextName(Path.GetFullPath(configPath));
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == name && c.Assemblies.Any());
    }

    /// <summary>Verify the project's bytes, then load its configuration: refused, naming the lint, with nothing loaded.</summary>
    private IReadOnlyList<string> RefusedBeforeLoad(string project, string id)
    {
        PluginVerification v = PluginVerifier.Verify(File.ReadAllBytes(BuiltDll(project)));
        Assert.Equal(1, v.ContractVersion);
        Assert.NotEmpty(v.Refusals);
        string config = Configure(project, id);
        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
        Assert.Contains("the determinism check refuses", ex.Message, StringComparison.Ordinal);
        AssertNothingLoaded(config);
        return v.Refusals;
    }

    private static void HasRefusal(IReadOnlyList<string> refusals, string startsWith)
        => Assert.True(refusals.Any(r => r.StartsWith(startsWith, StringComparison.Ordinal)),
            $"no refusal starts with '{startsWith}'; the refusals were:\n" + string.Join("\n", refusals));

    // ------------------------------------------------------------------------------------------------ 1. security

    [Fact]
    public void The_denylists_are_private_immutable_arrays_that_reflection_cannot_reassign()
    {
        FieldInfo[] fields = typeof(PluginDenylist).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        // SEC-28 (2.1.156): four more lists (the allowed framework namespaces, the attribute-only namespaces, the enum types
        // those attributes take and the allowed assemblies), held to the same rules below; the pin was 7 before they existed.
        Assert.Equal(11, fields.Length);
        foreach (FieldInfo f in fields)
        {
            Assert.True(f.IsPrivate, f.Name + " is not private");
            Assert.True(f.IsStatic && f.IsInitOnly, f.Name + " is not static readonly");
            Assert.Equal(typeof(ImmutableArray<string>), f.FieldType);
            // A static readonly field cannot be set through reflection on .NET.
            Assert.Throws<FieldAccessException>(() => f.SetValue(null, ImmutableArray<string>.Empty));
        }
        Assert.Empty(typeof(PluginDenylist).GetProperties(BindingFlags.Public | BindingFlags.Static));
        // Every list is ordinal-sorted (binary search) and the lists still answer after the attempts above.
        foreach ((string name, ImmutableArray<string> list) in PluginDenylist.Lists)
            Assert.True(list.SequenceEqual(list.OrderBy(x => x, StringComparer.Ordinal)), name + " is not ordinal-sorted");
        Assert.NotNull(PluginDenylist.Refusal("System.IO.File", "ReadAllBytes"));
    }

    [Fact]
    public void A_plugin_that_tries_to_change_the_denylists_is_refused_before_it_loads()
    {
        IReadOnlyList<string> r = RefusedBeforeLoad("GridSim.ModelPlugin.HostileDenylist", "HOSTILE_LINT");
        const string Gov = "GridSim.ModelPlugin.HostileDenylist.HostileLintGovernor";
        // Reflection on the private field, and a write through the immutable array's backing store.
        HasRefusal(r, $"GridSim.Core.Dynamics.Plugins.PluginDenylist (referenced by {Gov}.Initialize)");
        HasRefusal(r, $"System.Type.GetTypeFromHandle (referenced by {Gov}.Initialize)");
        HasRefusal(r, $"System.Type.GetField (referenced by {Gov}.Initialize)");
        HasRefusal(r, $"System.Reflection.FieldInfo.GetValue (referenced by {Gov}.Initialize)");
        HasRefusal(r, $"System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray (referenced by {Gov}.Initialize)");
        // An accessor the runtime supplies, and the host's static it would write.
        HasRefusal(r, $"{Gov}.Namespaces (extern)");
        HasRefusal(r, "System.Runtime.CompilerServices.UnsafeAccessorAttribute");
        HasRefusal(r, $"the static field System.Collections.Immutable.ImmutableArray`1.Empty (read) (referenced by {Gov}.Initialize)");
        // The assembly-level attribute that lifts the runtime's access checks, defined by the plug-in itself.
        HasRefusal(r, "the type System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute defined in GridSim.ModelPlugin.HostileDenylist");
        Assert.NotNull(PluginDenylist.Refusal("System.IO.File", "ReadAllBytes"));
    }

    [Fact]
    public void A_plugin_that_writes_static_fields_of_other_assemblies_is_refused_before_it_loads()
    {
        double before = Flickermeter.PstWeights[0];
        IReadOnlyList<string> r = RefusedBeforeLoad("GridSim.ModelPlugin.HostileStatic", "HOSTILE_STATIC");
        const string Gov = "GridSim.ModelPlugin.HostileStatic.HostileStaticGovernor.Initialize";
        // ldsfld of a GridSim.Core static array (then stelem into it): refused as a static of another assembly that is not an
        // immutable value, and as a GridSim.Core type outside the plug-in namespaces.
        HasRefusal(r, $"the static field GridSim.Core.Harmonics.Planning.Flicker.Flickermeter.PstWeights (read) (referenced by {Gov})");
        HasRefusal(r, $"GridSim.Core.Harmonics.Planning.Flicker.Flickermeter.PstWeights (referenced by {Gov})");
        // ldsflda of the framework's string.Empty, then a write through Unsafe.AsRef.
        HasRefusal(r, $"the static field System.String.Empty (address taken) (referenced by {Gov})");
        HasRefusal(r, "System.Runtime.CompilerServices.Unsafe");
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0314), BitConverter.DoubleToInt64Bits(before));
        Assert.Equal(BitConverter.DoubleToInt64Bits(before), BitConverter.DoubleToInt64Bits(Flickermeter.PstWeights[0]));
        Assert.Equal(0, string.Empty.Length);
    }

    [Fact]
    public void A_plugin_that_reaches_core_outside_the_plugin_namespaces_is_refused_before_it_loads()
    {
        IReadOnlyList<string> r = RefusedBeforeLoad("GridSim.ModelPlugin.HostileCore", "HOSTILE_CORE");
        const string Gov = "GridSim.ModelPlugin.HostileCore.HostileCoreGovernor.Initialize";
        HasRefusal(r, $"GridSim.Core.Events.EventCatalog.Save (referenced by {Gov}): GridSim.Core.Events.EventCatalog is outside the plug-in contract (a plug-in uses GridSim.Core only in");
        HasRefusal(r, "GridSim.Core.Events.GridEvent");
        HasRefusal(r, $"GridSim.Core.Dynamics.Plugins.PluginLoader.Load (referenced by {Gov})");
        HasRefusal(r, $"GridSim.Core.Numerics.SimdCapabilities.get_FmaAvailable (referenced by {Gov})");
        Assert.False(File.Exists(Path.Combine(_dir, "hostile-core.json")));
        Assert.False(File.Exists("hostile-core.json"));

        // The open namespaces, by name; anything else in GridSim.Core is refused, and so are the loader's own types.
        Assert.True(PluginDenylist.IsAllowedCoreType("GridSim.Core.Dynamics.Models.FirstOrderLag"));
        Assert.True(PluginDenylist.IsAllowedCoreType("GridSim.Core.Dynamics.Models.Wecc.WeccBlocks"));
        Assert.True(PluginDenylist.IsAllowedCoreType("GridSim.Core.Numerics.DetMath"));
        Assert.True(PluginDenylist.IsAllowedCoreType("GridSim.Core.Dynamics.Plugins.IUserGovernor"));
        Assert.False(PluginDenylist.IsAllowedCoreType("GridSim.Core.Dynamics.UserModels.ModelInstance"));
        Assert.False(PluginDenylist.IsAllowedCoreType("GridSim.Core.Model.GridModel"));
        Assert.Null(PluginDenylist.Refusal("GridSim.Core.Numerics.DetMath", "Exp"));
        Assert.Null(PluginDenylist.Refusal("GridSim.Core.Dynamics.Plugins.PluginParameter", ".ctor"));
        Assert.NotNull(PluginDenylist.Refusal("GridSim.Core.IO.Json.V2CaseWriter", "Write"));
        Assert.NotNull(PluginDenylist.Refusal("GridSim.Core.Dynamics.Plugins.PluginConfig", "Read"));
        Assert.NotNull(PluginDenylist.Refusal("GridSim.Core.Dynamics.Plugins.PluginVerifier", "Verify"));

        // The samples that keep the contract still pass the extended lint with no refusal at all.
        Assert.Empty(PluginVerifier.Verify(File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.Sample"))).Refusals);
        Assert.Empty(PluginVerifier.Verify(File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.LeakSample"))).Refusals);
    }

    // ------------------------------------------------------------------------------------------------ 2. lint coverage

    [Fact]
    public void Each_named_hazard_is_refused_in_compiled_il_naming_its_method()
    {
        PluginVerification v = PluginVerifier.Verify(File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.HazardSample")));
        IReadOnlyList<string> r = v.Refusals;
        string By(string method) => $" (referenced by GridSim.ModelPlugin.HazardSample.HazardsGovernor.{method})";
        HasRefusal(r, "System.Double.Sin" + By("DoubleSin"));
        HasRefusal(r, "System.Double.Exp" + By("DoubleExp"));
        HasRefusal(r, "System.Double.FusedMultiplyAdd" + By("DoubleFma"));
        HasRefusal(r, "System.Half.op_Explicit" + By("HalfPrecision"));
        HasRefusal(r, "System.StringComparer.GetHashCode" + By("StringComparerHash"));
        HasRefusal(r, "System.Collections.Generic.EqualityComparer`1.GetHashCode" + By("EqualityComparerHash"));
        HasRefusal(r, "System.Collections.Generic.IEnumerable`1.GetEnumerator" + By("DictionaryAsEnumerable"));
        HasRefusal(r, "System.Linq.Enumerable.Sum" + By("HashSetThroughLinq"));
        HasRefusal(r, "System.Linq.Enumerable.ToArray" + By("DictionaryToArray"));
        HasRefusal(r, "System.Collections.Immutable.ImmutableHashSet.Create" + By("ImmutableHashSetRead"));
        HasRefusal(r, "System.Collections.Frozen.FrozenSet.ToFrozenSet" + By("FrozenSetRead"));
        HasRefusal(r, "System.OperatingSystem.IsWindows" + By("OperatingSystemRead"));
        HasRefusal(r, "System.Globalization.CultureInfo.get_CurrentCulture" + By("CurrentCultureRead"));
        HasRefusal(r, "System.TimeZoneInfo.get_Local" + By("TimeZoneRead"));
        HasRefusal(r, "System.Runtime.JitInfo.GetCompiledILBytes" + By("JitInfoRead"));
        HasRefusal(r, "System.Type.GetType" + By("TypeGetTypeRead"));
        HasRefusal(r, "System.Reflection.PropertyInfo.GetValue" + By("TypeGetTypeRead"));
        HasRefusal(r, "System.Linq.Expressions.Expression`1.Compile" + By("ExpressionCompile"));
        // Step and Initialize keep the contract: nothing is reported against them.
        Assert.DoesNotContain(r, x => x.Contains("HazardsGovernor.Step)", StringComparison.Ordinal) || x.Contains("HazardsGovernor.Initialize)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lint_names_by_rule_what_it_refuses_and_what_it_allows()
    {
        // Refused.
        foreach ((string type, string? member) in new (string, string?)[]
        {
            ("System.Double", "Sin"), ("System.Double", "Exp"), ("System.Double", "FusedMultiplyAdd"), ("System.Double", "Parse"),
            ("System.Half", null), ("System.Int32", "Parse"), ("System.Convert", "ToDouble"),
            ("System.String", "GetHashCode"), ("System.ValueTuple`2", "GetHashCode"), ("System.Collections.Generic.IEqualityComparer`1", "GetHashCode"),
            ("System.Collections.Generic.IEnumerable`1", "GetEnumerator"), ("System.Collections.IEnumerable", null),
            ("System.Collections.Generic.ICollection`1", "CopyTo"), ("System.Collections.Generic.IReadOnlyDictionary`2", null),
            ("System.Collections.Generic.Dictionary`2+KeyCollection", "GetEnumerator"), ("System.Collections.Generic.HashSet`1", "CopyTo"),
            ("System.Collections.Immutable.ImmutableHashSet`1+Builder", "Add"), ("System.Collections.Immutable.ImmutableDictionary`2", null),
            ("System.Collections.Frozen.FrozenSet`1", null), ("System.Collections.Frozen.FrozenDictionary`2", null),
            ("System.Linq.ParallelEnumerable", "AsParallel"), ("System.OperatingSystem", "IsLinux"),
            ("System.Globalization.CultureInfo", "get_CurrentUICulture"), ("System.Globalization.NumberFormatInfo", "get_CurrentInfo"),
            ("System.TimeZoneInfo", null), ("System.Runtime.JitInfo", "GetCompiledMethodCount"), ("System.Runtime.GCSettings", null),
            ("System.Type", "GetType"), ("System.Reflection.PropertyInfo", "GetValue"), ("System.Linq.Expressions.LambdaExpression", "Compile"),
            ("System.Delegate", "DynamicInvoke"), ("System.Runtime.CompilerServices.UnsafeAccessorAttribute", ".ctor"),
            ("System.Text.Json.JsonSerializer", "Serialize"), ("System.WeakReference`1", null),
        })
            Assert.True(PluginDenylist.Refusal(type, member) is not null, $"{type}::{member} is not refused");

        // Allowed: the exact double members, number hash codes, the ordered collections, the culture-free constant, and the
        // assembly attributes the SDK writes (System.Reflection attribute types are data, not reflection).
        foreach ((string type, string? member) in new (string, string?)[]
        {
            ("System.Double", "IsNaN"), ("System.Double", "IsFinite"), ("System.Double", "GetHashCode"), ("System.Math", "Sqrt"),
            ("System.Int32", "GetHashCode"), ("System.Collections.Generic.List`1", "GetEnumerator"), ("System.Collections.Generic.Dictionary`2", "TryGetValue"),
            ("System.Collections.Generic.SortedDictionary`2", "GetEnumerator"), ("System.Collections.Generic.IReadOnlyList`1", "get_Item"),
            ("System.Globalization.CultureInfo", "get_InvariantCulture"), ("System.Reflection.AssemblyTitleAttribute", ".ctor"),
            ("System.Reflection.AssemblyInformationalVersionAttribute", null), ("System.Runtime.InteropServices.InAttribute", null),
        })
            Assert.True(PluginDenylist.Refusal(type, member) is null, $"{type}::{member} is refused: {PluginDenylist.Refusal(type, member)}");
        Assert.NotNull(PluginDenylist.Refusal("System.Reflection.Emit.CustomAttributeBuilder", null));
        Assert.NotNull(PluginDenylist.Refusal("System.Reflection.Metadata.BlobAttribute", null));
    }

    // ------------------------------------------------------------------------------------------------ 3. replay

    [Fact]
    public void A_static_written_in_initialize_passes_a_same_point_replay_and_is_refused_by_the_two_point_replay()
    {
        string config = Configure("GridSim.ModelPlugin.LeakSample", "LEAK_INIT");
        PluginSet set = PluginLoader.Load(config);
        {
            PluginModelDefinition def = set.Find("LEAK_INIT")!;
            // The old check: two instances at the same point, side by side, agree bit for bit; the leak does not show.
            var a = (IUserGovernor)def.Factory.Create(Array.Empty<double>());
            var b = (IUserGovernor)def.Factory.Create(Array.Empty<double>());
            a.Initialize(0.5);
            b.Initialize(0.5);
            for (int k = 0; k < PluginReplay.Steps; k++)
            {
                double u = k < PluginReplay.Steps / 2 ? 0.0 : 0.01;
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Step(u, 0.005)), BitConverter.DoubleToInt64Bits(b.Step(u, 0.005)));
            }
            // In a real run the first machine steps on the second machine's set-point: 0.6 in place of 0.5 at zero speed deviation.
            a.Initialize(0.5);
            b.Initialize(0.6);
            Assert.Equal(0.6, a.Step(0.0, 0.005));

            // The replay from two points compares each paired instance with a run of its own, and refuses it at the first step.
            var ex = Assert.Throws<PluginRefusedException>(() => new PluginModel(def));
            Assert.Contains("LEAK_INIT: not deterministic (replay differs at step 1)", ex.Message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------ 4. rpc --listen

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

    private static string RunVerb(int id, string verb, params string[] args)
        => JsonSerializer.Serialize(new { id, op = "run-verb", verb, args });

    private const string ListenRefusal = "model plug-ins (--model-plugins, user-model check-plugin) are not loaded over --listen";

    [Fact]
    public void Rpc_over_listen_refuses_both_plugin_routes_and_the_stdio_host_does_not_apply_that_rule()
    {
        string[][] requests =
        [
            ["dynamics-study", "study.json", "--model-plugins", "plugins.json", "--model-assignments", "assign.json"],
            ["dynamics-study", "study.json", "--model-plugins=plugins.json", "--model-assignments", "assign.json"],
            ["user-model", "check-plugin", "plugins.json"],
            ["USER-MODEL", "Check-Plugin", "plugins.json"],
        ];

        // Level 1 (the default protocol) over --listen: every request is refused by the plug-in rule, as a bad argument.
        using (Process p = Process.Start(Start("--listen", "127.0.0.1:0"))!)
        {
            try
            {
                using JsonDocument banner = JsonDocument.Parse(p.StandardOutput.ReadLine()!);
                int port = int.Parse(banner.RootElement.GetProperty("listening").GetString()!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
                using var client = new TcpClient("127.0.0.1", port);
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                string token = RpcListenSupport.Token(banner.RootElement);   // CLI-03: every request over --listen carries the host's token
                for (int i = 0; i < requests.Length; i++)
                {
                    writer.WriteLine(RpcListenSupport.With(RunVerb(i + 1, requests[i][0], requests[i][1..]), token));
                    using JsonDocument reply = JsonDocument.Parse(reader.ReadLine()!);
                    JsonElement root = reply.RootElement;
                    Assert.Equal("error", root.GetProperty("status").GetString());
                    Assert.Equal("bad-argument", root.GetProperty("error").GetProperty("kind").GetString());
                    Assert.Contains(ListenRefusal, root.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
                }
                writer.WriteLine(RpcListenSupport.With("{\"id\":99,\"op\":\"shutdown\"}", token));
                reader.ReadLine();
            }
            finally
            {
                if (!p.WaitForExit(60_000)) p.Kill(entireProcessTree: true);
            }
        }

        // The stdio host is the user's own process: the rule does not apply there, and the verb runs (and fails on the missing
        // configuration file, not on the network rule).
        using (Process p = Process.Start(Start())!)
        {
            try
            {
                p.StandardInput.Write(RunVerb(1, "user-model", "check-plugin", "plugins.json") + "\n");
                p.StandardInput.Flush();
                string reply = p.StandardOutput.ReadLine()!;
                Assert.DoesNotContain(ListenRefusal, reply, StringComparison.Ordinal);
                Assert.Contains("does not exist", reply, StringComparison.Ordinal);
                p.StandardInput.Close();
            }
            finally
            {
                if (!p.WaitForExit(60_000)) p.Kill(entireProcessTree: true);
            }
        }
    }
}
