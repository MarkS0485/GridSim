using System.Reflection;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Web.Model;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: licensing. #171 (a replay is bundled by content, not by the name inside its files),
/// #172 (bridge sessions go through the licence gate) and #178 (no stale content-hash cache; checks before and after the
/// load).
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    // A user network above the free tier's bus limit: a radial chain of FreeBusLimit + 100 buses.
    private static GridModel BigUserNetwork(string name)
    {
        int n = Entitlement.FreeBusLimit + 100;
        var buses = new List<Bus>(n);
        for (int i = 1; i <= n; i++) buses.Add(new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: 132));
        var branches = new List<Branch>(n - 1);
        for (int i = 1; i < n; i++) branches.Add(new Branch(i, i + 1, 0.01, 0.1));
        return new GridModel { BaseMva = 100, Buses = buses, Generators = Array.Empty<Generator>(), Branches = branches, Name = name };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #171
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B171_same_topology_compares_bus_ids_and_branch_ends_not_names()
    {
        GridModel a = IeeeCases.Case14();
        Assert.True(BundledNetworks.SameTopology(IeeeCases.Case14(), a));
        Assert.False(BundledNetworks.SameTopology(IeeeCases.Case30(), a));
        // One branch end moved: not the shipped network.
        var moved = a.Branches.ToList();
        moved[0] = moved[0] with { To = moved[1].To };
        GridModel altered = new() { BaseMva = a.BaseMva, Buses = a.Buses, Generators = a.Generators, Branches = moved, Name = a.Name };
        Assert.False(BundledNetworks.SameTopology(altered, a));
    }

    [Fact]
    public void B171_a_renamed_replay_model_is_not_bundled_and_the_free_tier_refuses_it()
    {
        var catalog = new CaseCatalog(RepoRoot());
        // A genuine built-in model under its own name is bundled; the same model under another built-in's name is not.
        Assert.True(catalog.IsBundledModel(IeeeCases.Case118(), "case118"));
        Assert.False(catalog.IsBundledModel(IeeeCases.Case118(), "case9"));
        // The bug: a large user network whose files call it gb-full used to play above the limit.
        GridModel renamed = BigUserNetwork("gb-full");
        Assert.False(catalog.IsBundledModel(renamed, "gb-full"));
        GateResult verdict = catalog.CheckLicence(renamed, "gb-full");
        Assert.False(verdict.Allowed);
        Assert.Contains("buses", verdict.Message, StringComparison.Ordinal);
        // A name the catalogue does not ship is never bundled.
        Assert.False(catalog.IsBundledModel(IeeeCases.Case14(), "my-case"));
        Assert.False(catalog.IsBundledModel(IeeeCases.Case14(), null));
    }

    [Fact]
    public void B171_the_shipped_gb_full_topology_is_still_bundled_by_content()
    {
        var catalog = new CaseCatalog(RepoRoot());
        GridModel gbFull = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "gb-full"));
        Assert.True(gbFull.BusCount > Entitlement.FreeBusLimit);   // the case the exemption exists for
        Assert.True(catalog.IsBundledModel(gbFull, "gb-full"));
        Assert.True(catalog.CheckLicence(gbFull, "gb-full").Allowed);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #172
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B172_a_bridge_session_cannot_be_built_without_a_licence_check_and_a_refused_topology_is_never_shown()
    {
        Assert.Throws<ArgumentNullException>(() => SimSession.CreateBridge("s1", new Uri("wss://bridge.example.invalid/ws"), "gb-full",
            "presolved", null, null!));

        var catalog = new CaseCatalog(RepoRoot());
        await using SimSession session = SimSession.CreateBridge("s2", new Uri("wss://bridge.example.invalid/ws"), "gb-full",
            "presolved", null, model => catalog.CheckLicence(model, "gb-full"));
        // The server sends a large topology that is not the shipped gb-full: the free tier refuses it.
        MethodInfo onTopology = typeof(SimSession).GetMethod("OnBridgeTopology", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onTopology.Invoke(session, new object[] { BigUserNetwork("gb-full") });
        Assert.False(session.TopologyReady);
        Assert.NotNull(session.Error);
        Assert.Contains("buses", session.Error!, StringComparison.Ordinal);
        Assert.False(session.Playing);
        string refusal = session.Error!;
        MethodInfo onFault = typeof(SimSession).GetMethod("OnBridgeFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onFault.Invoke(session, new object[] { new IOException("Transport closed after licence refusal") });
        Assert.Equal(refusal, session.Error);
        Assert.False(session.TopologyReady);

        // The shipped topology is accepted.
        await using SimSession ok = SimSession.CreateBridge("s3", new Uri("wss://bridge.example.invalid/ws"), "gb-full",
            "presolved", null, model => catalog.CheckLicence(model, "gb-full"));
        onTopology.Invoke(ok, new object[] { JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "gb-full")) });
        Assert.True(ok.TopologyReady);
        // The session also tries to reach the (deliberately unreachable) bridge host, so a connect error may be set;
        // what must not be there is a licence refusal.
        Assert.DoesNotContain("buses", ok.Error ?? "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #178
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B178_a_same_length_swap_with_the_write_time_put_back_is_not_bundled()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-data-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "uk");
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "system.json");
            File.WriteAllText(file, "{\"shipped\":1}");
            File.WriteAllText(Path.Combine(root, BundledNetworks.ManifestFileName),
                BundledNetworks.ContentHash(file) + "  uk/system.json\n");
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            Assert.True(BundledNetworks.IsBundledByContent(file, new[] { root }));

            // Same length, different content, write time restored: the old (path, size, mtime) cache said "bundled".
            File.WriteAllText(file, "{\"swapped\":9}");   // the same 13 characters as the shipped text
            File.SetLastWriteTimeUtc(file, stamp);
            Assert.Equal(new FileInfo(file).Length, "{\"shipped\":1}".Length);
            Assert.False(BundledNetworks.IsBundledByContent(file, new[] { root }));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void B178_bundled_across_a_load_needs_both_checks()
    {
        // The shipped reduced GB folder (in the checkout's data root while the tests run) passes before and after.
        string uk = Path.Combine(RepoRoot(), "data", "uk");
        int calls = 0;
        int r = BundledNetworks.BundledAcrossLoad(uk, () => ++calls, out bool bundled);
        Assert.Equal(1, r);
        Assert.True(bundled);
        // A user path is never bundled, whatever the load returns.
        BundledNetworks.BundledAcrossLoad(Path.Combine(Path.GetTempPath(), "not-shipped", "system.json"), () => 0, out bool user);
        Assert.False(user);
    }
}
