using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using GridSim.UiSmoke;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1 item 7: tools/UiSmoke, the headless page capture that succeeds AssetSmoke. Its capture routine
/// runs here in-process, on the same headless Skia platform the tool sets up, for MATPOWER case9 with 2 ticks and 3 pages
/// at a size that is not the default: every PNG exists, has exactly the asked size and is not blank; the manifest lists
/// each one with its true size, length and SHA-256; the contact sheet links each one by a relative path; the run played
/// exactly the asked ticks; and the user's own settings file is neither read through the window nor written (the window
/// used the isolated store in the output folder, and the user's file is byte for byte as it was).
/// The command line is checked too: its defaults, and that every malformed value is refused with the reason.
/// </summary>
public sealed class UiSmokeTests
{
    private static readonly string[] ThreePages = { "network-map-fitted", "scada", "home" };

    // ---------------------------------------------------------------- helpers

    // What the user's settings file looks like now (absent, or its length, write time and SHA-256), to compare after.
    private static string SettingsFingerprint(string path)
    {
        if (!File.Exists(path)) return "absent";
        var info = new FileInfo(path);
        return $"{info.Length} {info.LastWriteTimeUtc.Ticks} {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
    }

    // Decode a PNG and return its size and how many pixels differ from its most common colour by more than a little.
    private static (int Width, int Height, long Ink) Decode(string png)
    {
        using var bitmap = new Bitmap(png);
        PixelSize size = bitmap.PixelSize;
        int stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { pin.Free(); }

        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            uint px = BitConverter.ToUInt32(pixels, i);
            counts[px] = counts.GetValueOrDefault(px) + 1;
        }
        uint bg = counts.MaxBy(kv => kv.Value).Key;
        long ink = 0;
        foreach ((uint px, int n) in counts)
        {
            int d = 0;
            for (int s = 0; s < 32; s += 8) d = Math.Max(d, Math.Abs((int)((px >> s) & 0xFF) - (int)((bg >> s) & 0xFF)));
            if (d > 12) ink += n;
        }
        return (size.Width, size.Height, ink);
    }

    // =================================================================================================================

    [AvaloniaFact]
    public void UiSmoke_captures_case9_pages_at_the_asked_size_with_a_true_manifest_and_never_touches_the_user_settings()
    {
        using var temp = new TempFolder();
        string userSettings = ShellSettingsStore.Default.FilePath;
        string fingerprint = SettingsFingerprint(userSettings);
        string outDir = temp.File("out");
        var options = new UiSmokeOptions
        {
            Cases = new[] { "case9" },
            OutputFolder = outDir,
            Ticks = 2,
            Width = 1280,
            Height = 800,
            Pages = ThreePages,
        };
        var log = new StringWriter();

        UiSmokeReport report = UiSmokeRunner.Run(options, log);

        // ---- the run: every asked page, nothing failed, exactly 2 ticks, no dialog or picker ----
        Assert.True(report.Ok, "UiSmoke reported failures:\n" + log);
        Assert.Empty(report.Failures);
        Assert.Equal(ThreePages, report.Pages.Select(p => p.Page));
        UiSmokeCase c = Assert.Single(report.Cases);
        Assert.Equal("case9", c.Case);
        Assert.Equal("MATPOWER  case9", c.Label);
        Assert.Equal(2, c.Ticks);
        Assert.Empty(report.ShellCalls);
        Assert.Equal(Path.GetFullPath(outDir), report.OutputFolder);

        // ---- every PNG: on disk, the asked size, not blank, and its page area drew something ----
        foreach (UiSmokePage p in report.Pages)
        {
            string file = Path.Combine(outDir, p.File.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"{p.Page}: {file} was not written");
            Assert.Equal($"case9/{p.Page}.png", p.File);
            (int w, int h, long ink) = Decode(file);
            Assert.Equal(1280, w);
            Assert.Equal(800, h);
            Assert.Equal(1280, p.Width);
            Assert.Equal(800, p.Height);
            Assert.True(ink > 1000, $"{p.Page}: only {ink} pixels differ from the background; the picture is blank");
            Assert.True(p.ContentInk > 0, $"{p.Page}: the page's own area drew nothing");
            byte[] bytes = File.ReadAllBytes(file);
            Assert.Equal(bytes.LongLength, p.Bytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), p.Sha256);
        }

        // ---- manifest.json: lists exactly those files with their true size, length and hash ----
        Assert.Equal(Path.Combine(report.OutputFolder, "manifest.json"), report.ManifestPath);
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(report.ManifestPath)))
        {
            JsonElement root = manifest.RootElement;
            Assert.Equal("UiSmoke", root.GetProperty("tool").GetString());
            Assert.Equal("1280x800", root.GetProperty("size").GetString());
            Assert.Equal(2, root.GetProperty("ticks").GetInt32());
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(0, root.GetProperty("failures").GetArrayLength());
            List<JsonElement> pages = root.GetProperty("pages").EnumerateArray().ToList();
            Assert.Equal(ThreePages, pages.Select(e => e.GetProperty("page").GetString()));
            foreach (JsonElement e in pages)
            {
                string rel = e.GetProperty("file").GetString()!;
                string file = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                byte[] bytes = File.ReadAllBytes(file);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), e.GetProperty("sha256").GetString());
                Assert.Equal(bytes.LongLength, e.GetProperty("bytes").GetInt64());
                Assert.Equal(1280, e.GetProperty("width").GetInt32());
                Assert.Equal(800, e.GetProperty("height").GetInt32());
            }
        }

        // ---- index.html: a plain contact sheet, every picture linked by a relative path ----
        string index = File.ReadAllText(report.IndexPath);
        Assert.StartsWith("<!doctype html>", index, StringComparison.Ordinal);
        foreach (string page in ThreePages)
            Assert.Contains($"<a href=\"case9/{page}.png\">", index, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outDir, index, StringComparison.OrdinalIgnoreCase);   // no absolute link to the pictures

        // ---- settings: the isolated store in the output folder was used; the user's file is untouched ----
        string isolated = Path.Combine(Path.GetFullPath(outDir), "case9", "settings.json");
        Assert.Equal(isolated, c.SettingsFile);
        Assert.True(File.Exists(isolated), "the window saved its settings somewhere other than the isolated file");
        Assert.NotEqual(Path.GetFullPath(userSettings), Path.GetFullPath(c.SettingsFile), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fingerprint, SettingsFingerprint(userSettings));
        // The options UiSmoke gives the window: the store is the file it is handed, nothing loads at start-up, no bridge.
        var calls = new List<string>();
        GridSim.App.Shell.ShellOptions shell = UiSmokeRunner.ShellOptionsFor(new ModelSource("x", temp.Path), isolated, temp.Path, calls);
        Assert.Equal(isolated, shell.SettingsStore.FilePath);
        Assert.NotSame(ShellSettingsStore.Default, shell.SettingsStore);
        Assert.False(shell.AutoLoadAtStart);
        Assert.False(shell.BridgeEnabled);
        Assert.Empty(calls);
    }

    [AvaloniaFact]
    public void UiSmoke_reports_a_pages_entry_that_matches_nothing_as_a_failure()
    {
        using var temp = new TempFolder();
        var log = new StringWriter();
        UiSmokeReport report = UiSmokeRunner.Run(new UiSmokeOptions
        {
            Cases = new[] { "case9" },
            OutputFolder = temp.File("out"),
            Ticks = 1,
            Width = 640,
            Height = 480,
            Pages = new[] { "no-such-page" },
        }, log);
        Assert.False(report.Ok);
        Assert.Empty(report.Pages);
        UiSmokeFailure f = Assert.Single(report.Failures);
        Assert.Equal("no-such-page", f.Page);
        Assert.Contains("matched no page", f.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_line_defaults_are_the_WPF_window_size_and_both_bundled_cases()
    {
        UiSmokeOptions o = UiSmokeOptions.Parse(Array.Empty<string>());
        Assert.Equal(new[] { "uk", "case9" }, o.Cases);
        Assert.Equal(1580, o.Width);
        Assert.Equal(940, o.Height);
        Assert.Equal(UiSmokeOptions.DefaultTicks, o.Ticks);
        Assert.Null(o.Pages);
        Assert.Null(o.OutputFolder);
        Assert.True(o.Wants("anything"));

        UiSmokeOptions p = UiSmokeOptions.Parse(new[] { "--case", "uk", "--ticks", "12", "--out", "x", "--size", "800x600", "--pages", "scada, generator-detail" });
        Assert.Equal(new[] { "uk" }, p.Cases);
        Assert.Equal(12, p.Ticks);
        Assert.Equal("x", p.OutputFolder);
        Assert.Equal((800, 600), (p.Width, p.Height));
        Assert.True(p.Wants("scada"));
        Assert.True(p.Wants("generator-detail-(Peterhead)"));
        Assert.False(p.Wants("generator-fleet"));
        Assert.False(p.Wants("scada2"));
    }

    [Theory]
    [InlineData("--ticks", "0")]
    [InlineData("--ticks", "-3")]
    [InlineData("--ticks", "ten")]
    [InlineData("--size", "1580")]
    [InlineData("--size", "100x100")]
    [InlineData("--size", "1580x940x2")]
    [InlineData("--pages", ",")]
    [InlineData("--bogus", "1")]
    public void The_command_line_refuses_a_malformed_value_with_the_reason(string option, string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => UiSmokeOptions.Parse(new[] { option, value }));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Throws<ArgumentException>(() => UiSmokeOptions.Parse(new[] { "--case" }));   // a value is missing
    }

    [Fact]
    public void Page_names_follow_AssetSmoke_so_the_two_sets_line_up()
    {
        // AssetSmoke's Shot: label.Replace(' ', '-').Replace("/", "-").
        Assert.Equal("substation-detail-(bus-1)", UiSmokeRunner.PageName("substation detail (bus 1)"));
        Assert.Equal("generator-detail-()", UiSmokeRunner.PageName("generator detail ()"));
        Assert.Equal("circuit-detail-(A-B)", UiSmokeRunner.PageName("circuit detail (A/B)"));
        Assert.Equal("generation-tab", UiSmokeRunner.TabFileName("GENERATION & INERTIA"));
        Assert.Equal("the-machine", UiSmokeRunner.TabFileName("THE MACHINE"));
        Assert.Equal("neso-rp1", UiSmokeRunner.TabFileName("NESO RP1"));
        Assert.Equal("out-of-service-unit", UiSmokeRunner.Kebab("OutOfServiceUnit"));
    }

    [Fact]
    public void The_scratch_copy_lives_inside_the_case_output_folder_never_the_user_temp_folder()
    {
        // 2026-10-24: the AUTHOR home picture prints the temporary copy's path, which used to carry the Windows account
        // name (it was under the user's temp folder); it is now made inside the case's own output folder.
        string caseDir = Path.Combine(Path.GetFullPath("uismoke-out"), "uk");
        string a = UiSmokeRunner.ScratchFolderFor(caseDir), b = UiSmokeRunner.ScratchFolderFor(caseDir);
        Assert.Equal(caseDir, Path.GetDirectoryName(a));
        Assert.StartsWith(".scratch-", Path.GetFileName(a), StringComparison.Ordinal);
        Assert.NotEqual(a, b);   // one per run, never shared
        Assert.False(a.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && !caseDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
    }
}
