using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.Distribution;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Frequency;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Qsts;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Post-RC3 audit batch G (identity by display name: AN-05, AN-08, AN-23) and batch I (time handling: AN-22, AN-09, the
/// step half of AN-Q2).
/// </summary>
public class PostRc3BatchGiTests
{
    // ---------------------------------------------------------------- AN-05

    [Fact]
    public void AN05_thermal_lead_time_follows_the_branch_index_not_the_shared_display_name()
    {
        // Two UNNAMED parallel circuits 1-2 (one display name, "branch 1-2"): index 0 rated 45 MVA (about 100 %), index 1
        // rated 52 MVA (about 87 %, above the watch fraction but under its own thermal pickup).
        var model = new GridModel
        {
            BaseMva = 100, Name = "parallel",
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
                new Bus(2, BusType.Pq, Pd: 90, Qd: 0, BaseKv: 132),
            },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -400, Qmax: 400) },
            Branches = new[]
            {
                new Branch(1, 2, 0.005, 0.04, 0.0, RateAMva: 45),
                new Branch(1, 2, 0.005, 0.04, 0.0, RateAMva: 52),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model);
        var verdict = new RelayVerdict("49", "Thermal", ProtectionLocation.Branch, 0, "branch 1-2", true,
            1.0, "xRateA", 0.9, 0.1, 40.0, "thermal", ProtectionConfidence.Generic);
        var report = new ProtectionReport(new[] { verdict }, new ProtectionSettings(), false);

        WatchListReport w = WatchList.Assess(model, r, protection: report);

        var thermal = w.Items.Where(i => i.Category == WatchCategory.ThermalOverload).ToList();
        Assert.Equal(2, thermal.Count);
        WatchItem led = Assert.Single(thermal, i => i.LeadTimeSeconds is not null);
        Assert.Equal(40.0, led.LeadTimeSeconds);
        WatchItem other = Assert.Single(thermal, i => i.LeadTimeSeconds is null);
        Assert.True(other.Proximity < 1.0, "the circuit at its own 87 % must not be forced to proximity 1");
    }

    // ---------------------------------------------------------------- AN-08

    private static GridModel NamesakeModel(string second = "T1") => new()
    {
        BaseMva = 10, Name = "namesakes",
        Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 11), new(2, BusType.Pq, BaseKv: 11) },
        Branches = new List<Branch>
        {
            new(1, 2, 0.05, 0.05, Name: "T1"),
            new(1, 2, 0.05, 0.05, Name: second),
        },
        Generators = new List<Generator> { new(1, 0) },
    };

    [Fact]
    public void AN08_a_monitored_branch_name_held_by_two_branches_is_refused()
    {
        var study = new QstsStudy { StepSeconds = 1, Steps = 1, MonitorBranches = new[] { "T1" } };
        var ex = Assert.Throws<InvalidDataException>(() => study.Validate(NamesakeModel()));
        Assert.Contains("2 branches are named 'T1'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AN08_a_unique_monitored_branch_name_is_still_accepted()
    {
        new QstsStudy { StepSeconds = 1, Steps = 1, MonitorBranches = new[] { "T2" } }.Validate(NamesakeModel("T2"));
    }

    [Fact]
    public void AN08_a_hosting_reverse_flow_branch_with_namesakes_is_refused()
    {
        var s = new HostingStudy
        {
            MaxMw = 5, ToleranceMw = 0.1, ReverseFlowBranch = "T1", Snapshots = new[] { new HostingSnapshot(1, 1) },
            Base = new QstsStudy { StepSeconds = 1, Steps = 1, Limits = new QstsLimits(0.94, 1.05, 100) },
        };
        var ex = Assert.Throws<InvalidDataException>(() =>
            HostingCapacity.Run(NamesakeModel(), s, () => new NewtonRaphsonPowerFlow(), new PowerFlowOptions()));
        Assert.Contains("2 branches are named 'T1'", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- AN-23

    [Fact]
    public void AN23_two_generators_on_one_bus_and_namesake_branches_lift_to_unique_ids_that_round_trip()
    {
        var model = new GridModel
        {
            BaseMva = 10, Name = "dg",
            Buses = new List<Bus>
            {
                new(1, BusType.Slack, BaseKv: 11), new(2, BusType.Pq, Pd: 1.0, Qd: 0.2, BaseKv: 11), new(3, BusType.Pq, Pd: 0.5, Qd: 0.1, BaseKv: 11),
            },
            Branches = new List<Branch>
            {
                new(1, 2, 0.01, 0.02, Name: "T1"),
                new(2, 3, 0.01, 0.02, Name: "T1"),
            },
            Generators = new List<Generator> { new(1, 0), new(3, 0.1), new(3, 0.2) },
        };
        MultiPhaseFeeder feeder = MultiPhaseGridModelAdapter.FromGridModel(model, "dg.m");

        Assert.Equal(feeder.LoadIds.Count, feeder.LoadIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(feeder.BranchIds.Count, feeder.BranchIds.Distinct(StringComparer.Ordinal).Count());

        string json = MultiPhaseV2Writer.ToJson(feeder, MultiPhaseV2Writer.ConvertedWithoutProvenance("dg.m"));
        MultiPhaseFeeder back = MultiPhaseJsonLoader.ParseV2(json, "dg.json");
        Assert.Null(MultiPhaseV2Writer.FirstDifference(feeder, back));
    }

    [Fact]
    public void AN23_ids_that_were_already_unique_keep_their_spelling()
    {
        var model = new GridModel
        {
            BaseMva = 10, Name = "one",
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 11), new(2, BusType.Pq, Pd: 1.0, BaseKv: 11) },
            Branches = new List<Branch> { new(1, 2, 0.01, 0.02) },
            Generators = new List<Generator> { new(1, 0), new(2, 0.1) },
        };
        MultiPhaseFeeder feeder = MultiPhaseGridModelAdapter.FromGridModel(model, "one.m");
        Assert.Contains("generator at bus 2", feeder.LoadIds);
        Assert.Contains("#1", feeder.BranchIds);
    }

    // ---------------------------------------------------------------- AN-22

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string FullTrace() => Path.Combine(Root(), "data", "neso", "gb-2023-12-22-frequency.csv");

    /// <summary>Round-trip the built-in event through a catalog file whose whenUtc is rewritten to <paramref name="stamp"/>.</summary>
    private static GridEvent LoadWithStamp(string stamp)
    {
        GridEvent ev = EventCatalog.Builtin().Single(e => e.Id == "gb-2023-12-22");
        ev = ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = FullTrace() } };
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-cat-{Guid.NewGuid():N}.json");
        try
        {
            EventCatalog.Save(path, new[] { ev });
            string text = File.ReadAllText(path);
            string rewritten = Regex.Replace(text, "\"whenUtc\":\\s*\"[^\"]*\"", $"\"whenUtc\": \"{stamp}\"");
            Assert.Matches("\"whenUtc\":", text);
            File.WriteAllText(path, rewritten);
            return EventCatalog.Load(path, new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)).Single();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("2023-12-22T13:09:51Z")]
    [InlineData("2023-12-22T14:09:51+01:00")]
    [InlineData("2023-12-22T08:09:51-05:00")]
    [InlineData("2023-12-22T23:09:51+10:00")]
    [InlineData("2023-12-22T13:09:51")]
    public void AN22_an_offset_event_time_is_the_same_UTC_instant_whatever_the_host_zone(string stamp)
    {
        // The reader lands the stamp in UTC from its own offset and never consults TimeZoneInfo.Local, so the result is
        // identical on every host; the replay scored on it is the same as for the Z form, to the last bit.
        GridEvent ev = LoadWithStamp(stamp);
        Assert.Equal(DateTimeKind.Utc, ev.WhenUtc.Kind);
        Assert.Equal(new DateTime(2023, 12, 22, 13, 9, 51, DateTimeKind.Utc), ev.WhenUtc);

        ReplayResult reference = ReplayValidator.Validate(LoadWithStamp("2023-12-22T13:09:51Z"));
        ReplayResult got = ReplayValidator.Validate(ev);
        Assert.NotNull(got.PerSampleRmseHz);
        Assert.True(got.TraceSamplesScored > 0);
        Assert.Equal(reference.PerSampleRmseHz, got.PerSampleRmseHz);
        Assert.Equal(reference.TraceSamplesScored, got.TraceSamplesScored);
    }

    [Fact]
    public void AN22_the_same_clock_time_with_two_offsets_is_two_instants_even_across_a_DST_overlap()
    {
        // 01:30 on 2023-10-29 happened twice in London (BST then GMT); read through a local-time DateTime they could not be
        // told apart. Read from the offset they are exactly one hour apart on any host.
        GridEvent a = LoadWithStamp("2023-10-29T01:30:00+01:00");
        GridEvent b = LoadWithStamp("2023-10-29T01:30:00+00:00");
        Assert.Equal(TimeSpan.FromHours(1), b.WhenUtc - a.WhenUtc);
    }

    [Fact]
    public void AN22_a_local_kind_event_time_built_in_code_is_compared_as_UTC_by_the_replay()
    {
        GridEvent z = LoadWithStamp("2023-12-22T13:09:51Z");
        DateTime local = z.WhenUtc.ToLocalTime();          // Kind Local, same instant, on whatever host this runs
        Assert.Equal(DateTimeKind.Local, local.Kind);
        ReplayResult want = ReplayValidator.Validate(z);
        ReplayResult got = ReplayValidator.Validate(z with { WhenUtc = local });
        Assert.Equal(want.PerSampleRmseHz, got.PerSampleRmseHz);
        Assert.Equal(want.TraceSamplesScored, got.TraceSamplesScored);
    }

    [Fact]
    public void AN22_a_non_ISO_event_time_is_refused_by_name()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => LoadWithStamp("22/12/2023 13:09:51"));
    }

    // ---------------------------------------------------------------- AN-09

    private static string WriteCsv(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-neso-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Theory]
    [InlineData("09/08/2019 15:52:33")]
    [InlineData("12/31/2019 15:52:33")]
    [InlineData("9 Aug 2019 15:52:33")]
    public void AN09_a_slash_or_prose_date_is_refused_not_read_month_first(string stamp)
    {
        string path = WriteCsv("dtm,f", "2019-08-09 15:50:00,50.0", $"{stamp},50.1");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => NesoFrequencyCsv.Read(path).ToList());
            Assert.Contains("the timestamp does not parse", ex.Message, StringComparison.Ordinal);
            Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("2019-08-09 15:52:33", 15, 52, 33)]
    [InlineData("2019-08-09T15:52:33", 15, 52, 33)]
    [InlineData("2019-08-09T16:52:33+01:00", 15, 52, 33)]
    [InlineData("2019-08-09 15:52:33Z", 15, 52, 33)]
    public void AN09_the_published_and_ISO_forms_still_land_in_UTC(string stamp, int h, int m, int s)
    {
        string path = WriteCsv("dtm,f", $"{stamp},50.1");
        try
        {
            FrequencySample one = Assert.Single(NesoFrequencyCsv.Read(path).ToList());
            Assert.Equal(new DateTime(2019, 8, 9, h, m, s, DateTimeKind.Utc), one.Time);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- AN-Q2 (step half)

    [Fact]
    public void ANQ2_a_two_second_replay_step_is_accepted_and_a_longer_one_still_refused()
    {
        GridEvent ev = LoadWithStamp("2023-12-22T13:09:51Z");
        // 2.0 s is "at most 2 s": it was refused because Math.Round(0.5) is 0 (banker's rounding).
        ReplayResult r = ReplayValidator.Validate(ev, new DynamicsConfig { StepSeconds = 2.0 });
        Assert.NotNull(r);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayValidator.Validate(ev, new DynamicsConfig { StepSeconds = 2.5 }));
    }
}
