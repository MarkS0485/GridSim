using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N216 (docs session, 2026-10-04): the bundled-network licence bypass. <see cref="BundledNetworks.IsBundledPath(string?)"/>
/// treated ANY path with a <c>data/&lt;bundled folder&gt;</c> segment as bundled, wherever it sat, so a user network copied
/// under some other <c>.../data/uk/</c> or <c>.../data/matpower/</c> escaped the tier's bus limit. The match is now
/// anchored to the shipped data roots (<see cref="BundledNetworks.DataRoots"/>), and the stale <c>gb-reduced</c> folder
/// entry is gone. Both directions are proved: a copy elsewhere is NOT bundled (the limit applies), the shipped folders
/// still are.
/// </summary>
public sealed class N216BundledPathAnchorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-n216-" + Guid.NewGuid().ToString("N"));

    public N216BundledPathAnchorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string InstallData => Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void A_user_case_copied_under_some_other_data_uk_folder_is_not_bundled()
    {
        // The finding's reproduction: C:\anything\data\uk\mycase.m was bundled. A real copy of a shipped case, so the
        // file exists and only its location differs.
        string userUk = Path.Combine(_dir, "anything", "data", "uk");
        Directory.CreateDirectory(userUk);
        string shippedSystem = Path.Combine(InstallData, "uk", "system.json");
        Assert.True(File.Exists(shippedSystem), shippedSystem);
        File.Copy(shippedSystem, Path.Combine(userUk, "system.json"));
        File.WriteAllText(Path.Combine(userUk, "mycase.m"), "function mpc = mycase\n");

        Assert.False(BundledNetworks.IsBundledPath(userUk));
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(userUk, "system.json")));
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(userUk, "mycase.m")));
        foreach (string folder in BundledNetworks.DataFolders)
            Assert.False(BundledNetworks.IsBundledPath(Path.Combine(_dir, "x", "data", folder, "big.m")), folder);
    }

    [Theory]
    // Moved here from BundledNetworksTests.Matching_ignores_case_and_separator, where they were asserted TRUE: paths
    // under an arbitrary C:\Repo\DATA\... tree, which is exactly the bypass N216 reports.
    [InlineData(@"C:\Repo\DATA\GB-FULL\buses.json")]
    [InlineData("C:/Repo/data/matpower/case118.m")]
    [InlineData("C:/anything/data/uk/mycase.m")]
    [InlineData("/home/user/data/gb-synthetic/system.json")]
    public void Arbitrary_data_segment_paths_are_not_bundled(string path)
    {
        Assert.False(BundledNetworks.IsBundledPath(path));
    }

    [Fact]
    public void The_shipped_data_folders_are_still_bundled_by_folder_and_file_in_either_case_and_separator()
    {
        int checkedFolders = 0;
        foreach (string folder in BundledNetworks.DataFolders)
        {
            string dir = Path.Combine(InstallData, folder);
            if (!Directory.Exists(dir)) continue;
            checkedFolders++;
            Assert.True(BundledNetworks.IsBundledPath(dir), dir);
            string? file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (file is null) continue;
            Assert.True(BundledNetworks.IsBundledPath(file), file);
            Assert.True(BundledNetworks.IsBundledPath(file.Replace('\\', '/')), file);
            if (OperatingSystem.IsWindows())
                Assert.True(BundledNetworks.IsBundledPath(file.ToUpperInvariant()), file);
        }
        Assert.True(checkedFolders >= 4, $"expected matpower, uk, gb-synthetic and gb-full under {InstallData}; found {checkedFolders}");

        // The source checkout's data folder (this test runs from tests/GridSim.Tests/bin/...): a development run loads
        // the shipped cases from there, so it is a data root too.
        string repoData = Path.Combine(CliProcess.Root(), "data");
        Assert.Contains(BundledNetworks.DataRoots, r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(repoData), StringComparison.OrdinalIgnoreCase));
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(repoData, "gb-full", "buses.json")));
    }

    [Fact]
    public void The_stale_gb_reduced_folder_is_off_the_list_and_the_gb_reduced_case_id_stays_bundled()
    {
        Assert.DoesNotContain("gb-reduced", BundledNetworks.DataFolders);
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(InstallData, "gb-reduced", "system.json")));
        Assert.True(BundledNetworks.IsBundledJsonCase("gb-reduced"));   // the id loads data/uk, which is bundled
        Assert.Equal(new[] { "matpower", "uk", "gb-synthetic", "gb-full", "intl", "dist3ph" }, BundledNetworks.DataFolders);
    }

    [Fact]
    public void Dot_dot_segments_and_look_alike_folders_cannot_climb_into_or_out_of_a_root()
    {
        string root = Path.Combine(_dir, "install", "data");
        var roots = new[] { root };
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(root, "uk", "system.json"), roots));
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(root, "uk"), roots));
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(root, "uk", "..", "..", "evil", "data", "uk", "x.m"), roots));
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(root, "ukx", "x.m"), roots));
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(root, "gb-fullish"), roots));
        Assert.False(BundledNetworks.IsBundledPath(root, roots));                        // the root itself is not a network
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(root, "other", "x.m"), roots));   // not a listed folder
        Assert.False(BundledNetworks.IsBundledPath(Path.Combine(_dir, "install", "uk", "x.m"), roots));
        Assert.False(BundledNetworks.IsBundledPath("", roots));
        Assert.False(BundledNetworks.IsBundledPath(null, roots));
    }

    [Fact]
    public void An_installed_program_has_exactly_one_data_root_and_a_build_output_in_a_checkout_has_two()
    {
        // Installed: no GridSim.slnx above the program folder.
        string install = Path.Combine(_dir, "Program Files", "GridSim");
        Directory.CreateDirectory(Path.Combine(install, "data", "uk"));
        IReadOnlyList<string> one = BundledNetworks.ResolveDataRoots(install);
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(install, "data")) }, one);

        // A build output under a checkout's src/: the checkout's data/ is a root too.
        string checkout = Path.Combine(_dir, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "data", "uk"));
        File.WriteAllText(Path.Combine(checkout, "GridSim.slnx"), "<Solution />");
        string bin = Path.Combine(checkout, "src", "GridSim.Cli", "bin", "Release", "net10.0");
        Directory.CreateDirectory(bin);
        IReadOnlyList<string> two = BundledNetworks.ResolveDataRoots(bin);
        Assert.Equal(2, two.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(checkout, "data")), two[1]);

        // A program merely placed somewhere beneath a folder holding a GridSim.slnx (not in its src/ or tests/ build
        // tree) does not get that folder's data/ as a root: a planted GridSim.slnx is not enough.
        string planted = Path.Combine(checkout, "apps", "gridsim");
        Directory.CreateDirectory(planted);
        Assert.Single(BundledNetworks.ResolveDataRoots(planted));
    }

    [Fact]
    public void A_checkout_tool_build_output_has_the_checkout_data_root_and_a_lookalike_folder_does_not()
    {
        // 2026-10-24: the checkout's own tools (tools/UiSmoke, tools/AssetSmoke) build under tools/ and load its cases.
        string checkout = Path.Combine(_dir, "checkout-tools");
        Directory.CreateDirectory(Path.Combine(checkout, "data", "gb-full"));
        File.WriteAllText(Path.Combine(checkout, "GridSim.slnx"), "<Solution />");
        string bin = Path.Combine(checkout, "tools", "UiSmoke", "bin", "Release", "net10.0");
        Directory.CreateDirectory(bin);
        IReadOnlyList<string> roots = BundledNetworks.ResolveDataRoots(bin);
        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(checkout, "data")), roots[1]);

        // A folder only named like it (toolsx/) is not the tools tree.
        string lookalike = Path.Combine(checkout, "toolsx", "UiSmoke", "bin");
        Directory.CreateDirectory(lookalike);
        Assert.Single(BundledNetworks.ResolveDataRoots(lookalike));
    }

    // ---- process level: the tier limit applies to the copy and not to the shipped file -----------------------------

    // Force the free tier whatever licence the machine has installed: GRIDSIM_LICENCE takes precedence over the
    // installed licence file, and a key that does not verify resolves to the free Personal tier (1,500 buses), never
    // to the installed file. (Pointing LOCALAPPDATA elsewhere does not work: the file is found through the Windows
    // known-folder API, so a developer machine with an unlimited licence ran the copy.)
    private static Dictionary<string, string?> FreeTier(string dir) => new()
    {
        ["GRIDSIM_LICENCE"] = "not-a-key",
        ["XDG_DATA_HOME"] = dir,
    };

    [Fact]
    public void Cli_a_2869_bus_case_copied_under_a_user_data_matpower_folder_is_refused_by_the_free_tier()
    {
        string shipped = Path.Combine(CliProcess.Root(), "data", "matpower", "pegase", "case2869pegase.m");
        Assert.True(File.Exists(shipped), shipped);
        string userCopy = Path.Combine(_dir, "work", "data", "matpower", "case2869pegase.m");
        Directory.CreateDirectory(Path.GetDirectoryName(userCopy)!);
        File.Copy(shipped, userCopy);

        // Before N216 the copy was "bundled" (it sits under a data/matpower segment) and ran on the free tier.
        CliProcess.Result copy = CliProcess.RunBuiltWithEnvironment(FreeTier(_dir), "dcpf", userCopy);
        Assert.True(copy.ExitCode == 2, copy.ToString());
        Assert.Contains("2,869", copy.StdErr, StringComparison.Ordinal);

        // The shipped file itself is still bundled: the gate lets it through and the DC power flow runs. (The study's own
        // verdict is not this test's business, so only the licence refusal is ruled out.)
        CliProcess.Result ok = CliProcess.RunBuiltWithEnvironment(FreeTier(_dir), "dcpf", shipped);
        Assert.True(ok.ExitCode != 2, ok.ToString());
        Assert.DoesNotContain("licence allows", ok.StdErr, StringComparison.Ordinal);
        Assert.Contains("DC power flow - ", ok.StdOut, StringComparison.Ordinal);
    }
}
