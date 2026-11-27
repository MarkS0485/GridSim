using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.IO.Psse;
using GridSim.Core.Protection.Devices;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-14 through the dynamics study: the fixed-step answer is bit-identical to the incumbent with the new feature off
/// and on; the adaptive companion run lands events exactly, runs relays from a placed device set, and is repeatable across
/// thread counts.
/// </summary>
public sealed class R414AdaptiveStudyTests(ITestOutputHelper output)
{
    /// <summary>SHA-256 of the fixed-step case14 fixture below, captured on rc4 b8ba9b58 BEFORE any R4-14 source existed.</summary>
    private const string IncumbentDigest = "0F4D6F6A3ED0E6AA3A3F16C6C6FB04466E2C29610DC6F30E2D0840FCC99EF7DC";

    private static string FixedStepDigest(DynamicsStudyResult r)
    {
        var bytes = new List<byte>();
        foreach (DynamicsCaseResult c in r.Cases)
        {
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(c.Disposition));
            foreach (double t in c.Run!.TimeSeconds) bytes.AddRange(BitConverter.GetBytes(t));
            foreach (DynamicsChannelTrace ch in c.Run.Channels)
                foreach (double v in ch.Values) bytes.AddRange(BitConverter.GetBytes(v));
            foreach (DynamicsAppliedEvent e in c.Events!) bytes.AddRange(BitConverter.GetBytes(e.AppliedSeconds));
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static DynamicsStudyRequest Baseline() => Case14(new[] { Fault4("f4", 0.15) }, duration: 2.0);

    private static readonly DynamicsAdaptiveRequest Adaptive = new(
        new AdaptiveOptions(RelativeTolerance: 1e-8, AbsoluteTolerance: 1e-10, MaxStepSeconds: 0.02, EventTimeToleranceSeconds: 1e-7), CctToleranceSeconds: 1e-3);

    [Fact]
    public void The_fixed_step_study_is_bit_identical_to_the_incumbent_with_the_adaptive_feature_off_and_on()
    {
        DynamicsStudyResult off = DynamicsStudyRunner.Run(Baseline());
        Assert.Null(off.Adaptive);
        Assert.Equal(IncumbentDigest, FixedStepDigest(off));
        DynamicsStudyResult on = DynamicsStudyRunner.Run(Baseline() with { Adaptive = Adaptive });
        Assert.NotNull(on.Adaptive);
        Assert.Equal(IncumbentDigest, FixedStepDigest(on));
        Assert.Equal(off.Status, on.Status);
        Assert.Equal(JsonSerializer.Serialize(off.Cases), JsonSerializer.Serialize(on.Cases));
        Assert.Equal(JsonSerializer.Serialize(off.Engine), JsonSerializer.Serialize(on.Engine));
    }

    private static DynamicsCaseRequest OffGrid(string name) => new(name, DynamicsCaseKind.Events, new[]
    {
        new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1234, Bus: 4),
        new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.1734, Bus: 4),
        new DynamicsEventRequest(DynamicsEventKind.BranchTrip, 0.1734, Branch: Br(4, 5)),
    });

    [Fact]
    public void Events_land_on_their_requested_times_where_the_fixed_step_run_quantises_them()
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { OffGrid("off-grid") }) with { Adaptive = Adaptive });
        DynamicsCaseResult fixedRow = Assert.Single(r.Cases);
        DynamicsAdaptiveRow row = Assert.Single(r.Adaptive!.Rows);
        Assert.Equal("adaptive", r.Adaptive.Integration);
        Assert.Equal(new[] { 0.1234, 0.1734 }, row.Events!.Where(e => e.Source == "scheduled").Select(e => e.AppliedSeconds).ToArray());   // exact bits
        double[] fixedApplied = fixedRow.Events!.Select(e => e.AppliedSeconds).Distinct().ToArray();   // the incumbent's 5 ms grid (k x h)
        Assert.Equal(2, fixedApplied.Length);
        Assert.True(Math.Abs(fixedApplied[0] - 0.125) < 1e-12 && Math.Abs(fixedApplied[1] - 0.175) < 1e-12);
        DynamicsAdaptiveEventComparison q0 = row.Quantisation![0];
        Assert.Equal(0.1234, q0.AdaptiveSeconds);
        Assert.True(Math.Abs(q0.FixedStepAppliedSeconds - 0.125) < 1e-12);
        Assert.True(Math.Abs(q0.QuantisationSeconds - 0.0016) < 1e-12);
        Assert.Contains(0.1234, row.TimeSeconds!);
        Assert.Contains(0.1734, row.TimeSeconds!);
        Assert.True(row.AcceptedSteps > 0 && row.AcceptedSteps < 3.0 / 0.005, $"{row.AcceptedSteps} adaptive steps against {3.0 / 0.005} fixed steps");
        output.WriteLine($"{row.Disposition}; accepted {row.AcceptedSteps}, rejected {row.RejectedSteps}, steps {row.MinStepSeconds:E2}..{row.MaxStepSeconds:E2} s, fixed rows say {fixedRow.Disposition}");
        Assert.NotEqual(DynamicsDisposition.Failed, row.Disposition);
        Assert.NotNull(row.Channels);
        Assert.Equal(row.TimeSeconds!.Count, row.Channels![0].Values.Count);
    }

    private static PsseParseResult Case14Raw() => Raw(Psse("case14.raw"));

    /// <summary>The same case with a 132 kV base on every bus (the shipped file leaves BASKV at 0, so a relay there cannot convert
    /// per-unit current to amperes): only the base voltage differs, so the dynamics are the case's own.</summary>
    private static PsseParseResult Case14RawWithKv() =>
        RawText(System.Text.RegularExpressions.Regex.Replace(Text(Psse("case14.raw")), @"(?m)^(\d+, '', )0(, \d, 1, 1, 1, )", "${1}132${2}"), "case14");

    private static DeviceSet Set(PlacedDevice d) => new(new[] { d });

    private static PlacedDevice Device(string curve, double pickupA, double timeDial, double definiteSeconds, int branch = 6) =>
        new("R4-5", DeviceType.Relay, "51", new DeviceLocation(branch, 4, 5, "from"), 1.0, 1.0, 0.0, 0.0,
            new[] { new ShotSetting(curve, pickupA, timeDial, definiteSeconds) }, null, Array.Empty<double>(), null, null);

    private static DynamicsCaseRequest FaultOnly(string name) => new(name, DynamicsCaseKind.Events, new[]
    {
        new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1234, Bus: 4),
    });

    [Fact]
    public void A_placed_relay_trips_its_branch_at_a_time_bounded_by_the_curve_equation_at_the_simulated_current()
    {
        PsseParseResult raw = Case14RawWithKv();
        Assert.Equal((4, 5), (raw.Model.Branches[6].From, raw.Model.Branches[6].To));
        string dyr = Text(Study("case14-study.dyr"));

        // Pass 1: a definite-time probe that never operates, to read the branch current before and during the fault.
        var probe = new DynamicsAdaptiveRequest(Adaptive.Options, Adaptive.CctToleranceSeconds, Set(Device("dt", 1.0, 1.0, 100.0)));
        DynamicsStudyResult p1 = DynamicsStudyRunner.Run(Request(raw, dyr, new[] { FaultOnly("fault") }, duration: 1.0) with { Adaptive = probe });
        AdaptiveRelayRecord probeRec = Assert.Single(Assert.Single(p1.Adaptive!.Rows).Relays!);
        double iMin = probeRec.MinActiveCurrentA!.Value, iMax = probeRec.MaxActiveCurrentA!.Value;
        output.WriteLine($"branch 4-5 current at bus 4: {iMin:F3} A to {iMax:F3} A");
        Assert.True(iMax > 3.0 * iMin, "the fault must raise the branch current well above its pre-fault value");

        // Pass 2: an IEC standard inverse relay set between them, its time multiplier chosen for about 0.2 s at the peak current.
        double pickup = 2.0 * iMin;
        double tms = 0.2 * (Math.Pow(iMax / pickup, 0.02) - 1.0) / 0.14;
        var set = new DynamicsAdaptiveRequest(Adaptive.Options, Adaptive.CctToleranceSeconds, Set(Device("iec-si", pickup, tms, 0.0)));
        DynamicsStudyResult p2 = DynamicsStudyRunner.Run(Request(raw, dyr, new[] { FaultOnly("fault") }, duration: 1.0) with { Adaptive = set });
        DynamicsAdaptiveRow row = Assert.Single(p2.Adaptive!.Rows);
        AdaptiveRelayRecord rec = Assert.Single(row.Relays!);
        Assert.True(rec.Tripped, "the relay should trip within the second");
        double delay = rec.TripSeconds!.Value - rec.PickupSeconds!.Value;
        // t_op(I) = TMS * 0.14 / ((I / Is)^0.02 - 1), derived here. The disc integral of 1 / t_op over the delay is 1, and 1 / t_op
        // grows with the current, so the delay lies between t_op at the largest and at the smallest current seen while picked up.
        double Top(double amps) => tms * 0.14 / (Math.Pow(amps / pickup, 0.02) - 1.0);
        double lowBound = Top(rec.MaxActiveCurrentA!.Value), highBound = Top(rec.MinActiveCurrentA!.Value);
        output.WriteLine($"pick-up {rec.PickupSeconds:R} s, trip {rec.TripSeconds:R} s, delay {delay:R} s within [{lowBound:R}, {highBound:R}] (current {rec.MinActiveCurrentA:F2}..{rec.MaxActiveCurrentA:F2} A)");
        Assert.True(delay >= lowBound * (1.0 - 1e-3) - Adaptive.Options.EventTimeToleranceSeconds, $"delay {delay} below {lowBound}");
        Assert.True(delay <= highBound * (1.0 + 1e-3) + Adaptive.Options.EventTimeToleranceSeconds, $"delay {delay} above {highBound}");
        Assert.Equal(0.1234, rec.PickupSeconds);   // the fault is the scheduled event that raises the current above pick-up
        // The trip is an event of the run, ordered after the pick-up, and the branch then carries nothing.
        Assert.Equal(new[] { "scheduled", "pickup", "trip" }, row.Events!.Select(e => e.Kind == "scheduled" ? "scheduled" : e.Kind).Take(3).ToArray());
        Assert.NotNull(rec.TripCurrentA);
        // The fixed-step engine would have applied that trip on its 5 ms grid; the adaptive time carries no quantisation.
        DynamicsAdaptiveEventComparison tripQ = row.Quantisation!.Single(c => c.Source == "relay");
        Assert.Equal(rec.TripSeconds, tripQ.AdaptiveSeconds);
        Assert.True(tripQ.FixedStepAppliedSeconds >= tripQ.AdaptiveSeconds && tripQ.FixedStepAppliedSeconds - tripQ.AdaptiveSeconds < 0.005);
    }

    [Fact]
    public void A_definite_time_relay_trips_exactly_its_delay_after_pickup()
    {
        PsseParseResult raw = Case14RawWithKv();
        string dyr = Text(Study("case14-study.dyr"));
        var set = new DynamicsAdaptiveRequest(Adaptive.Options, Adaptive.CctToleranceSeconds, Set(Device("dt", 1.0, 1.0, 0.25)));
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(raw, dyr, new[] { FaultOnly("fault") }, duration: 1.0) with { Adaptive = set });
        AdaptiveRelayRecord rec = Assert.Single(Assert.Single(r.Adaptive!.Rows).Relays!);
        // The probe sits above 1 A from t = 0, so a definite-time delay of 0.25 s after the start would trip at 0.25 s if the current stayed up.
        Assert.True(rec.Tripped);
        Assert.Equal(0.0, rec.PickupSeconds);
        Assert.True(Math.Abs(rec.TripSeconds!.Value - 0.25) <= Adaptive.Options.EventTimeToleranceSeconds + 1e-12, $"trip {rec.TripSeconds:R}");
    }

    [Fact]
    public void A_device_on_a_bus_without_a_base_voltage_is_listed_as_not_evaluated_never_silently_dropped()
    {
        var set = new DynamicsAdaptiveRequest(Adaptive.Options, Adaptive.CctToleranceSeconds, Set(Device("dt", 1.0, 1.0, 0.25)));
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(Case14Raw(), Text(Study("case14-study.dyr")), new[] { FaultOnly("fault") }, duration: 1.0) with { Adaptive = set });
        RelayNotEvaluated skipped = Assert.Single(r.Adaptive!.RelaysNotEvaluated);
        Assert.Equal("R4-5", skipped.DeviceId);
        Assert.Contains("no base voltage", skipped.Reason);
        Assert.Empty(Assert.Single(r.Adaptive.Rows).Relays!);
    }

    [Fact]
    public void The_adaptive_rows_are_bit_identical_whatever_the_row_parallelism()
    {
        var cases = new[]
        {
            OffGrid("a"),
            new DynamicsCaseRequest("b", DynamicsCaseKind.Events, new[]
            {
                new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.2, Bus: 9),
                new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.27, Bus: 9),
            }),
            new DynamicsCaseRequest("c", DynamicsCaseKind.Events, new[]
            {
                new DynamicsEventRequest(DynamicsEventKind.LoadStep, 0.3, Bus: 5, PMw: 30.0, QMvar: 5.0),
            }),
        };
        string One(int parallelism) => JsonSerializer.Serialize(DynamicsStudyRunner.Run(Case14(cases, parallelism: parallelism) with { Adaptive = Adaptive }).Adaptive);
        string serial = One(1);
        Assert.Equal(serial, One(1));
        Assert.Equal(serial, One(2));
        Assert.Equal(serial, One(4));
    }

    [Fact]
    public void The_adaptive_critical_clearing_time_is_a_bracket_not_tied_to_the_step_grid()
    {
        var cct = new DynamicsCaseRequest("cct", DynamicsCaseKind.CriticalClearing, Cct: new DynamicsCctRequest(4, new[] { Br(4, 5) }, 0.1, 0.6));
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { cct }, duration: 3.0) with { Adaptive = Adaptive });
        DynamicsAdaptiveRow row = Assert.Single(r.Adaptive!.Rows);
        Assert.NotNull(row.Cct);
        DynamicsCctBracket fixedBracket = r.Cases[0].Cct!.Coarse;
        output.WriteLine($"fixed step (5 ms grid): stable {fixedBracket.StableSeconds:R}, unstable {fixedBracket.UnstableSeconds}; adaptive: {row.Disposition}, stable {row.Cct!.StableSeconds:R}, unstable {row.Cct.UnstableSeconds}, trials {row.Cct.Trials}");
        Assert.Equal(row.Cct.Disposition, row.Disposition);
        if (row.Cct.UnstableSeconds is double hi) Assert.True(hi - row.Cct.StableSeconds <= Adaptive.CctToleranceSeconds + 1e-12);
    }

    [Fact]
    public void An_invalid_adaptive_block_or_device_set_is_an_input_error_before_any_work()
    {
        PsseParseResult raw = Case14Raw();
        string dyr = Text(Study("case14-study.dyr"));
        var bad = new DynamicsAdaptiveRequest(Adaptive.Options with { RelativeTolerance = -1.0 });
        Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(Request(raw, dyr, new[] { Fault4("f") }) with { Adaptive = bad }));
        var wrongBranch = new DynamicsAdaptiveRequest(Adaptive.Options, 1e-3, Set(Device("iec-si", 100.0, 0.1, 0.0, branch: 99)));
        Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(Request(raw, dyr, new[] { Fault4("f") }) with { Adaptive = wrongBranch }));
    }

    // -----------------------------------------------------------------------------------------------------------
    // The CLI: an `adaptive` member is additive; a study without it is serialised exactly as before.
    // -----------------------------------------------------------------------------------------------------------

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void The_cli_adds_an_adaptive_section_only_when_asked_and_leaves_the_fixed_step_cases_untouched()
    {
        string dir = TestTemp.Under("gridsim-r414-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Psse("case14.raw"), Path.Combine(dir, "case14.raw"));
            File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), Path.Combine(dir, "case14.dyr"));
            File.WriteAllText(Path.Combine(dir, "devices.json"), """
                {"manifestVersion":"gridsim-devices/1","devices":[{"id":"R4-5","type":"relay","function":"51","branch":{"index":6,"from":4,"to":5,"end":"from"},
                 "shots":[{"curve":"dt","pickupA":1.0,"definiteTimeS":50.0}]}]}
                """);
            object Doc(object? adaptive)
            {
                var d = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = 1,
                    ["raw"] = new { path = "case14.raw", sha256 = Sha(Path.Combine(dir, "case14.raw")) },
                    ["dyr"] = new { path = "case14.dyr", sha256 = Sha(Path.Combine(dir, "case14.dyr")) },
                    ["initialisation"] = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
                    ["integration"] = new { stepSeconds = 0.005, durationSeconds = 2.0 },
                    ["criteria"] = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
                    ["angleReference"] = new { bus = 1, id = "1" },
                    ["channels"] = new object[] { new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } } },
                    ["cases"] = new object[]
                    {
                        new { name = "f", kind = "events", events = new object[]
                        {
                            new { kind = "busFault", at = 0.1234, bus = 4 }, new { kind = "faultClear", at = 0.1734, bus = 4 },
                            new { kind = "branchTrip", at = 0.1734, branch = new { from = 4, to = 5, circuit = "1" } },
                        } },
                    },
                };
                if (adaptive is not null) d["adaptive"] = adaptive;
                return d;
            }
            string plain = Path.Combine(dir, "plain.json"), with = Path.Combine(dir, "with.json");
            File.WriteAllText(plain, JsonSerializer.Serialize(Doc(null)));
            File.WriteAllText(with, JsonSerializer.Serialize(Doc(new
            {
                relativeTolerance = 1e-8, absoluteTolerance = 1e-10, maxStepSeconds = 0.02, eventToleranceSeconds = 1e-7,
                devices = new { path = "devices.json", sha256 = Sha(Path.Combine(dir, "devices.json")) },
            })));
            var a = CliProcess.RunBuilt("dynamics-study", plain);
            var b = CliProcess.RunBuilt("dynamics-study", with);
            Assert.True(a.ExitCode == 0, a.StdErr + a.StdOut);
            Assert.True(b.ExitCode == 0, b.StdErr + b.StdOut);
            using JsonDocument ja = JsonDocument.Parse(a.StdOut), jb = JsonDocument.Parse(b.StdOut);
            Assert.False(ja.RootElement.TryGetProperty("adaptive", out _));
            Assert.True(jb.RootElement.TryGetProperty("adaptive", out JsonElement adaptive));
            Assert.Equal("adaptive", adaptive.GetProperty("integration").GetString());
            Assert.Equal(ja.RootElement.GetProperty("cases").GetRawText(), jb.RootElement.GetProperty("cases").GetRawText());
            Assert.Equal(ja.RootElement.GetProperty("status").GetString(), jb.RootElement.GetProperty("status").GetString());
            Assert.Equal(0.1234, adaptive.GetProperty("rows")[0].GetProperty("events")[0].GetProperty("appliedSeconds").GetDouble());
            // A bad adaptive member is refused by the strict reader.
            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, JsonSerializer.Serialize(Doc(new { relativeTolerance = 1e-8, nonsense = 1 })));
            var c = CliProcess.RunBuilt("dynamics-study", bad);
            Assert.NotEqual(0, c.ExitCode);
            Assert.Contains("unknown field 'nonsense'", c.StdErr);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
