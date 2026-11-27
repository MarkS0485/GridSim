using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP21: the dynamics study with a step schedule or the error-controlled step (case14 RAW + case14-study.dyr, the
/// fault at bus 4 cleared by opening 4-5). A request without either is the incumbent study; a one-window schedule at the
/// study step gives the incumbent rows exactly; with error control every event lands on its own time.
/// </summary>
public sealed class Rc5Wp21DynamicsStudyTests
{
    private static string Rows(DynamicsStudyResult r) =>
        JsonSerializer.Serialize(r.Cases.Select(c => c with { StepControl = null }).ToList());

    [Fact]
    public void A_one_window_schedule_at_the_study_step_gives_the_incumbent_rows_exactly()
    {
        DynamicsStudyRequest plain = Case14(new[] { Fault4("f4", 0.15) }, duration: 2.0);
        DynamicsStudyResult a = DynamicsStudyRunner.Run(plain);
        DynamicsStudyResult b = DynamicsStudyRunner.Run(plain with { StepSchedule = RmsStepSchedule.Fixed(plain.StepSeconds) });
        Assert.Equal(Rows(a), Rows(b));
        Assert.Equal(a.Status, b.Status);
        Assert.Null(a.Cases[0].StepControl);
        DynamicsStepControlEvidence e = b.Cases[0].StepControl!;
        Assert.Equal("schedule", e.Mode);
        Assert.Single(e.Windows);
        Assert.Equal(400, e.Steps);
        Assert.Null(e.ToleranceMet);
    }

    [Fact]
    public void A_two_window_schedule_runs_and_reports_its_windows()
    {
        // 5 ms over the fault, 10 ms from 1 s: 200 + 100 steps.
        DynamicsStudyRequest plain = Case14(new[] { Fault4("f4", 0.15) }, duration: 2.0);
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.005), new RmsStepWindow(1.0, 0.01) });
        DynamicsStudyResult r = DynamicsStudyRunner.Run(plain with { StepSchedule = s });
        DynamicsCaseResult row = r.Cases[0];
        Assert.NotEqual(DynamicsDisposition.Failed, row.Disposition);
        Assert.Equal(301, row.Run!.Samples);
        Assert.Equal(300, row.StepControl!.Steps);
        Assert.Equal(new[] { 0.0, 1.0 }, row.StepControl.Windows.Select(w => w.FromSeconds));
        Assert.All(row.Events!, e => Assert.True(e.LandsOnRequestedTime));
    }

    [Fact]
    public void The_error_controlled_step_lands_every_event_on_its_time()
    {
        DynamicsStudyRequest plain = Case14(new[] { Fault4("f4", 0.15) }, duration: 2.0);
        var o = new RmsErrorControlOptions(MaxStepSeconds: 0.02, MinStepSeconds: 1e-4, SegmentSeconds: 0.1);
        DynamicsStudyResult r = DynamicsStudyRunner.Run(plain with { ErrorControl = o });
        DynamicsCaseResult row = r.Cases[0];
        Assert.NotEqual(DynamicsDisposition.Failed, row.Disposition);
        DynamicsStepControlEvidence e = row.StepControl!;
        Assert.Equal("errorControl", e.Mode);
        Assert.NotNull(e.ToleranceMet);
        Assert.Equal(e.Runs, 2 * e.Passes);
        Assert.Equal(row.Run!.Samples - 1, e.Steps);
        // The fault at 0.1 s and the clearance and trip at 0.15 s start segments, so each lands with no shift.
        Assert.All(row.Events!, ev =>
        {
            Assert.True(ev.LandsOnRequestedTime);
            Assert.Equal(ev.RequestedSeconds, ev.AppliedSeconds);
            Assert.Equal(0.0, ev.ShiftSeconds);
        });
        // A run that missed its tolerance is never reported STABLE.
        if (e.ToleranceMet == false) Assert.NotEqual(DynamicsDisposition.Stable, row.Disposition);
    }

    [Fact]
    public void Bad_step_options_are_input_errors_before_any_work()
    {
        DynamicsStudyRequest plain = Case14(new[] { Fault4("f4", 0.15) }, duration: 2.0);
        // Both at once.
        Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(plain with
        {
            StepSchedule = RmsStepSchedule.Fixed(0.005),
            ErrorControl = new RmsErrorControlOptions(),
        }));
        // The fault at 0.1 s inside a coarse window that starts at 0.05 s.
        var coarse = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.005), new RmsStepWindow(0.05, 0.01) });
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(plain with { StepSchedule = coarse }));
        Assert.Contains("case 'f4'", ex.Message, StringComparison.Ordinal);
        // A window boundary off the grid of the window before it.
        var offGrid = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.003), new RmsStepWindow(1.0, 0.01) });
        Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(plain with { StepSchedule = offGrid }));
        // Error-control options out of range.
        Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(plain with { ErrorControl = new RmsErrorControlOptions(MaxPasses: 0) }));
    }

    // ---- The CLI -----------------------------------------------------------------------------------------------------

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = TestTemp.Under("gridsim-wp21-study-" + Guid.NewGuid().ToString("N"));
        public Folder()
        {
            Directory.CreateDirectory(Path);
            System.IO.File.Copy(Psse("case14.raw"), System.IO.Path.Combine(Path, "case14.raw"));
            System.IO.File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), System.IO.Path.Combine(Path, "case14.dyr"));
        }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static string Write(Folder f, object integration, string name)
    {
        var study = new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(f.File("case14.raw")) },
            dyr = new { path = "case14.dyr", sha256 = Sha(f.File("case14.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration,
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = new object[] { new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } } },
            cases = new object[]
            {
                new
                {
                    name = "fault-4", kind = "events", events = new object[]
                    {
                        new { kind = "busFault", at = 0.1, bus = 4 },
                        new { kind = "faultClear", at = 0.15, bus = 4 },
                        new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                    },
                },
            },
        };
        string path = f.File(name);
        File.WriteAllText(path, JsonSerializer.Serialize(study));
        return path;
    }

    [Fact]
    public void Cli_takes_a_step_schedule_and_reports_it_and_a_plain_study_is_unchanged()
    {
        using var f = new Folder();
        var plain = CliProcess.RunBuilt("dynamics-study", Write(f, new { stepSeconds = 0.005, durationSeconds = 2.0 }, "plain.json"));
        Assert.True(plain.ExitCode == 0, plain.StdErr + plain.StdOut);
        using (JsonDocument d = JsonDocument.Parse(plain.StdOut))
        {
            Assert.False(d.RootElement.GetProperty("effectiveOptions").GetProperty("integration").TryGetProperty("stepSchedule", out _));
            Assert.False(d.RootElement.GetProperty("cases")[0].TryGetProperty("stepControl", out _));
        }
        var sched = CliProcess.RunBuilt("dynamics-study", Write(f, new
        {
            stepSeconds = 0.005, durationSeconds = 2.0,
            stepSchedule = new object[] { new { fromS = 0.0, dtS = 0.005 }, new { fromS = 1.0, dtS = 0.01 } },
        }, "sched.json"));
        Assert.True(sched.ExitCode == 0, sched.StdErr + sched.StdOut);
        using (JsonDocument d = JsonDocument.Parse(sched.StdOut))
        {
            JsonElement windows = d.RootElement.GetProperty("effectiveOptions").GetProperty("integration").GetProperty("stepSchedule");
            Assert.Equal(2, windows.GetArrayLength());
            JsonElement row = d.RootElement.GetProperty("cases")[0];
            Assert.Equal(301, row.GetProperty("run").GetProperty("samples").GetInt32());
            Assert.Equal("schedule", row.GetProperty("stepControl").GetProperty("mode").GetString());
        }
        // Both options at once, an unknown errorControl member, and an event inside a coarse window are input errors.
        Assert.Equal(1, CliProcess.RunBuilt("dynamics-study", Write(f, new
        {
            stepSeconds = 0.005, durationSeconds = 2.0,
            stepSchedule = new object[] { new { fromS = 0.0, dtS = 0.005 } },
            errorControl = new { maxStepSeconds = 0.02 },
        }, "both.json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dynamics-study", Write(f, new
        {
            stepSeconds = 0.005, durationSeconds = 2.0, errorControl = new { tolerance = 0.1 },
        }, "unknown.json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dynamics-study", Write(f, new
        {
            stepSeconds = 0.005, durationSeconds = 2.0,
            stepSchedule = new object[] { new { fromS = 0.0, dtS = 0.005 }, new { fromS = 0.05, dtS = 0.01 } },
        }, "coarse.json")).ExitCode);
    }
}
