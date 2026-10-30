using System;
using System.IO;
using System.Linq;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

public class GdaPathsTests
{
    [Theory]
    [InlineData("tec-register-05-may-2026.csv", 2026, 5, 5)]
    [InlineData("tec-register-12-january-2027.csv", 2027, 1, 12)]
    [InlineData("tec-register-30-december-2026.csv", 2026, 12, 30)]
    [InlineData("tec-register-05-sept-2025.csv", 2025, 9, 5)]     // GR-827: the British "sept" abbreviation
    [InlineData("tec-register-05-SEP-2025.csv", 2025, 9, 5)]
    public void TryParseDateFromName_reads_dd_monthname_yyyy(string name, int y, int m, int d)
    {
        DateTime? dt = GdaPaths.TryParseDateFromName(name);
        Assert.Equal(new DateTime(y, m, d), dt);
    }

    [Fact]
    public void TryParseDateFromName_returns_null_when_no_date_token()
    {
        Assert.Null(GdaPaths.TryParseDateFromName("bus_mapping.csv"));
    }

    [Fact]
    public void PickNewest_orders_chronologically_across_month_and_year_boundary()
    {
        // Lexicographically "05-may-2026" > "12-january-2027" > "30-december-2026",
        // but chronologically January-2027 is the newest.
        string[] candidates =
        {
            "tec-register-05-may-2026.csv",
            "tec-register-12-january-2027.csv",
            "tec-register-30-december-2026.csv",
        };

        string? newest = GdaPaths.PickNewest(candidates);

        Assert.Equal("tec-register-12-january-2027.csv", newest);
    }

    [Fact]
    public void PickNewest_prefers_parseable_dates_over_lexicographic_order_with_real_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-gda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string[] names =
            {
                "tec-register-05-may-2026.csv",
                "tec-register-12-january-2027.csv",
                "tec-register-30-december-2026.csv",
            };
            foreach (string n in names)
                File.WriteAllText(Path.Combine(dir, n), "stub");

            string? newest = GdaPaths.PickNewest(Directory.EnumerateFiles(dir, "tec-register-*.csv"));

            Assert.NotNull(newest);
            Assert.Equal("tec-register-12-january-2027.csv", Path.GetFileName(newest));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Ruling (2026-10-15, review gap closure; the R297 rule Tabular.PickCsv already follows): file modification times
    // are never used to pick a GDA input, because they differ between machines. This test used to assert the mtime
    // fallback (the newer mtime won); two undated candidates are now refused, naming both.
    [Fact]
    public void PickNewest_refuses_to_rank_undated_candidates_by_modification_time()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-gda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string older = Path.Combine(dir, "register-alpha.csv");
            string newer = Path.Combine(dir, "register-beta.csv");
            File.WriteAllText(older, "stub");
            File.WriteAllText(newer, "stub");
            File.SetLastWriteTime(older, new DateTime(2020, 1, 1));
            File.SetLastWriteTime(newer, new DateTime(2024, 6, 1));

            var ex = Assert.Throws<InvalidDataException>(() => GdaPaths.PickNewest(Directory.EnumerateFiles(dir, "register-*.csv")));
            Assert.Contains("register-alpha.csv", ex.Message, StringComparison.Ordinal);
            Assert.Contains("register-beta.csv", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PickNewest_returns_null_for_empty_sequence()
    {
        Assert.Null(GdaPaths.PickNewest(Enumerable.Empty<string>()));
    }

    [Fact]
    public void PickNewest_does_not_exclude_undated_files_by_the_as_of()
    {
        // A file with no date in its name (e.g. the "24 Months Ahead Constraint Limits" export)
        // carries only a local download mtime, which is reset to "now" on every mirror sync.
        // That mtime is NOT a publication date, so it must not be used to exclude the file under
        // an as-of - otherwise the dataset silently vanishes after each sync.
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-gda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string undated = Path.Combine(dir, "24-months-ahead-constraint-limits.csv");
            File.WriteAllText(undated, "stub");
            File.SetLastWriteTimeUtc(undated, DateTime.UtcNow);   // "just synced", long after the as-of

            string? pick = GdaPaths.PickNewest(
                Directory.EnumerateFiles(dir, "*.csv"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal("24-months-ahead-constraint-limits.csv", Path.GetFileName(pick));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PickNewest_excludes_registers_published_after_the_as_of()
    {
        // reconstructing state as-of 01-june-2026 must NOT materialise on a register
        // published 12-january-2027 (inside/after the replayed window) - it must fall back to the
        // newest register that already existed at the as-of.
        string[] candidates =
        {
            "tec-register-05-may-2026.csv",
            "tec-register-12-january-2027.csv",   // newest overall, but published AFTER the as-of
            "tec-register-30-december-2026.csv",  // also after the as-of
        };

        string? asOfNewest = GdaPaths.PickNewest(candidates, new DateTime(2026, 6, 1));

        Assert.Equal("tec-register-05-may-2026.csv", asOfNewest);
    }

    [Fact]
    public void PickNewest_returns_null_when_every_register_is_newer_than_the_as_of()
    {
        string[] candidates =
        {
            "tec-register-12-january-2027.csv",
            "tec-register-30-december-2026.csv",
        };

        Assert.Null(GdaPaths.PickNewest(candidates, new DateTime(2026, 6, 1)));
    }

    [Fact]
    public void TecRegister_never_selects_a_register_published_after_the_as_of()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-gda-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "NESO", "RAWData", "Transmission Entry Capacity (TEC) register");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string n in new[]
            {
                "tec-register-05-may-2026.csv",
                "tec-register-12-january-2027.csv",   // published after the as-of
            })
                File.WriteAllText(Path.Combine(dir, n), "stub");

            // As-of before the January register was published: the guarded lookup must pick May-2026.
            var guarded = new GdaPaths(root, asOfUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("tec-register-05-may-2026.csv", Path.GetFileName(guarded.TecRegister));

            // Unconstrained (no as-of) still takes the absolute newest, as before.
            var unguarded = new GdaPaths(root);
            Assert.Equal("tec-register-12-january-2027.csv", Path.GetFileName(unguarded.TecRegister));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
