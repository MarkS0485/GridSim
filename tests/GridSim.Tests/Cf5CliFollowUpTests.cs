using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Distribution;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 small CF4 follow-ups: N349 (nminus1 --batched-lanes), N351 (RMS events on the CLI, the event-list CCT search and
/// the reclose event), N353 (iec60909 --peak-method c with the unbalanced duties) and N354 (the feeder format's converter
/// units and dist3ph harmonics --afe feeder).
/// </summary>
public class Cf5CliFollowUpTests
{
    // ------------------------------------------------------------------ N349

    [Fact]
    public void Nminus1_batched_prints_exactly_the_sequential_screen()
    {
        CliProcess.Result seq = CliProcess.RunBuilt("nminus1", "case14");
        CliProcess.Result bat = CliProcess.RunBuilt("nminus1", "case14", "--batched-lanes", "3");
        Assert.Equal(seq.ExitCode, bat.ExitCode);
        Assert.Equal(seq.StdOut, bat.StdOut);
        Assert.Contains("batched sweep", bat.StdErr, StringComparison.Ordinal);
        CliProcess.Result auto = CliProcess.RunBuilt("nminus1", "case14", "--batched-lanes", "0");
        Assert.Equal(seq.StdOut, auto.StdOut);
    }

    [Theory]
    [InlineData("nminus1", "case14", "--batched-lanes", "-1")]
    [InlineData("nminus1", "case14", "--batched-lanes", "1025")]
    [InlineData("nminus1", "case14", "--batched-lanes", "two")]
    [InlineData("nminus2", "case14", "--batched-lanes", "2")]
    public void Batched_lanes_is_checked_and_belongs_to_nminus1(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    // ------------------------------------------------------------------ N351: the parser

    [Fact]
    public void The_event_list_parses_every_kind()
    {
        IReadOnlyList<RmsEvent> e = RmsEventSpec.Parse("fault:7@0.1, clear:7@0.183,trip:5@0.183,reclose:5@0.683,load:5:50:-10.5@1,gentrip:2@1.5");
        Assert.Equal(6, e.Count);
        Assert.Equal(new RmsBusFault(0.1, 7), e[0]);
        Assert.Equal(new RmsFaultClear(0.183, 7), e[1]);
        Assert.Equal(new RmsBranchTrip(0.183, 5), e[2]);
        Assert.Equal(new RmsBranchReclose(0.683, 5), e[3]);
        Assert.Equal(new RmsLoadStep(1.0, 5, 50.0, -10.5), e[4]);
        Assert.Equal(new RmsGeneratorTrip(1.5, 2), e[5]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fault:7")]
    [InlineData("fault:7@")]
    [InlineData("fault@0.1")]
    [InlineData("fault:x@0.1")]
    [InlineData("fault:7@soon")]
    [InlineData("load:5:50@1")]
    [InlineData("open:5@1")]
    [InlineData("fault:7@NaN")]
    public void A_malformed_event_list_is_refused(string text)
        => Assert.Throws<FormatException>(() => RmsEventSpec.Parse(text));

    [Fact]
    public void Index_lists_parse_and_refuse_repeats_and_junk()
    {
        Assert.Equal(new[] { 3, 5 }, RmsEventSpec.ParseIndices("3, 5", "x"));
        Assert.Throws<FormatException>(() => RmsEventSpec.ParseIndices("3,3", "x"));
        Assert.Throws<FormatException>(() => RmsEventSpec.ParseIndices("-1", "x"));
        Assert.Throws<FormatException>(() => RmsEventSpec.ParseIndices("", "x"));
    }

    // ------------------------------------------------------------------ N351: reclose and the event CCT

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
        => model.Generators.Select(gen => new SubtransientInit.MachineData(
            H: gen.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
            if (m.Branches[i].InService && ((m.Branches[i].From == a && m.Branches[i].To == b) || (m.Branches[i].From == b && m.Branches[i].To == a))) return i;
        throw new InvalidOperationException($"no branch {a}-{b}");
    }

    [Fact]
    public void A_reclose_without_a_trip_is_refused_and_a_trip_then_reclose_restores_the_base_network()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        var d = GenrouData(m);
        int br = BranchBetween(m, 7, 8);
        var ex = Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEvents(m, pf, d,
            new RmsEvent[] { new RmsBranchReclose(0.2, br) }, stepSeconds: 0.005, durationSeconds: 1.0));
        Assert.Contains("not tripped", ex.Message, StringComparison.Ordinal);

        // A trip and its reclose at the same instant leave the base network: the trajectory equals a run with only the
        // later load step.
        var withStep = RmsTransientStability.StudyEvents(m, pf, d, new RmsEvent[] { new RmsLoadStep(0.5, 5, 20.0, 0.0) }, stepSeconds: 0.005, durationSeconds: 1.0);
        var tripReclose = RmsTransientStability.StudyEvents(m, pf, d, new RmsEvent[]
        {
            new RmsBranchTrip(0.2, br), new RmsBranchReclose(0.2, br), new RmsLoadStep(0.5, 5, 20.0, 0.0),
        }, stepSeconds: 0.005, durationSeconds: 1.0);
        for (int i = 0; i < withStep.Machines.Count; i++)
            for (int k = 0; k < withStep.Machines[i].TimeSeconds.Count; k++)
                Assert.True(Math.Abs(withStep.Machines[i].DeltaRad[k] - tripReclose.Machines[i].DeltaRad[k]) <= 1e-12,
                    $"machine {i} sample {k}");
    }

    [Fact]
    public void A_trip_and_a_later_reclose_moves_the_angle_while_out_and_runs_to_the_end()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        var d = GenrouData(m);
        int br = BranchBetween(m, 7, 8);
        var r = RmsTransientStability.StudyEvents(m, pf, d, new RmsEvent[] { new RmsBranchTrip(0.2, br), new RmsBranchReclose(0.6, br) },
            stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(r.StayedInSynchronism);
        // Trip again after the reclose is allowed (a second shot).
        var again = RmsTransientStability.StudyEvents(m, pf, d, new RmsEvent[]
        {
            new RmsBranchTrip(0.2, br), new RmsBranchReclose(0.6, br), new RmsBranchTrip(1.0, br),
        }, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.Equal(r.Machines[0].TimeSeconds.Count, again.Machines[0].TimeSeconds.Count);
    }

    [Fact]
    public void The_event_cct_with_no_actions_is_the_plain_cct_search()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        var d = GenrouData(m);
        CctBracket plain = RmsTransientStability.CriticalClearingBracket(m, pf, d, 7);
        CctBracket events = RmsTransientStability.CriticalClearingBracketEvents(m, pf, d, 7, Array.Empty<RmsEvent>());
        Assert.Equal(plain.StableSteps, events.StableSteps);
        Assert.Equal(plain.UnstableSteps, events.UnstableSteps);
        Assert.Equal(plain.NonMonotone, events.NonMonotone);
    }

    [Fact]
    public void Clearing_by_tripping_the_faulted_line_gives_a_cct_no_longer_than_a_self_clearing_fault_and_refuses_bad_actions()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        var d = GenrouData(m);
        // Corrected 2026-10-11 (end pass, first run): "7-5" is the Anderson and Fouad numbering of the WSCC 9-bus system;
        // GridSim's case9 is the MATPOWER file (data/matpower/lib/case9.m), where bus 7 is joined only to buses 6 and 8,
        // so there is no 7-5 branch and BranchBetween threw before the CCT was searched. The line at the faulted bus is
        // taken as 7-8 (branch index 5, the line the other tests of this class and the CLI example trip:5 use).
        // Before -> after: BranchBetween(m, 7, 5) -> BranchBetween(m, 7, 8).
        int br = BranchBetween(m, 7, 8);
        CctBracket plain = RmsTransientStability.CriticalClearingBracket(m, pf, d, 7);
        CctBracket tripped = RmsTransientStability.CriticalClearingBracketEvents(m, pf, d, 7, new RmsEvent[] { new RmsBranchTrip(0.0, br) });
        // Losing a line weakens the post-fault network, so the system cannot hold synchronism longer than with it.
        Assert.True(tripped.StableSteps <= plain.StableSteps, $"tripped {tripped.StableSteps} steps vs self-clearing {plain.StableSteps}");
        Assert.Throws<ArgumentException>(() => RmsTransientStability.CriticalClearingBracketEvents(m, pf, d, 7, new RmsEvent[] { new RmsBusFault(0.0, 5) }));
        Assert.Throws<ArgumentException>(() => RmsTransientStability.CriticalClearingBracketEvents(m, pf, d, 7, new RmsEvent[] { null! }));
    }

    [Theory]
    [InlineData("stability", "case9", "--events", "fault:7@0.1,clear:7@0.2")]                              // no --rms
    [InlineData("stability", "case9", "--rms", "--screen", "--events", "fault:7@0.1,clear:7@0.2")]
    [InlineData("stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.2", "--clear", "100")]
    [InlineData("stability", "case9", "--rms", "--events", "fault:7@soon")]
    [InlineData("stability", "case9", "--rms", "--events", "reclose:3@0.5")]                                 // study refuses it
    [InlineData("stability", "case9", "--rms", "--screen", "--clear-by-trip", "3")]
    [InlineData("stability", "case9", "--clear-by-trip", "3")]
    public void Stability_event_flags_are_checked(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    [Fact]
    public void Stability_rms_events_run_from_the_cli()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.183,trip:5@0.183,reclose:5@0.683");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("branch 5 reclose at 0.683 s", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("STABLE", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Scheduled RMS events;", r.StdOut, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ N353

    [Fact]
    public void Iec60909_takes_method_c_with_the_unbalanced_duties()
    {
        CliProcess.Result ll = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", "ll", "--peak-method", "c");
        Assert.True(ll.ExitCode == 0, ll.ToString());
        Assert.Contains("Method C: kappa(R/X_C), R/X_C ", ll.StdOut, StringComparison.Ordinal);
        CliProcess.Result llB = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", "ll");
        Assert.True(llB.ExitCode == 0, llB.ToString());
        Assert.DoesNotContain("Method C", llB.StdOut, StringComparison.Ordinal);
        // An earth fault on case9 still exits 3 for the missing earthing data (N116), now with Method C applied.
        CliProcess.Result slg = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", "slg", "--peak-method", "c");
        Assert.True(slg.ExitCode == 3, slg.ToString());
    }

    // ------------------------------------------------------------------ N354

    private static string FeederPath(string name) => Path.Combine(CliProcess.Root(), "data", "dist3ph", name);

    private static string WithConverters(string file, string converters)
    {
        string text = File.ReadAllText(FeederPath(file)).Replace("\r\n", "\n");
        int at = text.LastIndexOf('}');
        return text[..at].TrimEnd() + ",\n  \"converters\": " + converters + "\n}\n";
    }

    [Fact]
    public void The_feeder_format_reads_converter_units()
    {
        string text = WithConverters("ieee4-gyGy-stepdown-balanced.json", "[ { \"id\": \"afe4\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15, \"rPercent\": 1 } ]");
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(text, "afe.json");
        ThreePhaseActiveFrontEnd u = Assert.Single(f.Converters);
        Assert.Equal(new ThreePhaseActiveFrontEnd(4, 2.0, 0.15, 0.01), u);
        Assert.Equal(new[] { "afe4" }, f.ConverterIds);
        // A feeder without the section has none.
        Assert.Empty(MultiPhaseJsonLoader.Load(FeederPath("ieee4-gyGy-stepdown-balanced.json")).Converters);
        // Moved 2026-10 (END-PASS CF6 row 7, end pass; N360, the gridsim-dist3ph/2 schema gained a `converters` section,
        // Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the v2 form now holds converter units, so the writer's
        // refusal is gone; "never dropped" still holds and is now shown by the unit being written. Before -> after:
        // Assert.Throws<InvalidDataException>(ToJson(...)) whose message names afe4 -> ToJson returns the v2 text, which
        // names afe4 (observed: "No exception was thrown", 1.95.273-275 runs; that the text names afe4 is derived from
        // MultiPhaseV2Writer, which writes each converter's id, not observed).
        string v2 = MultiPhaseV2Writer.ToJson(f, MultiPhaseV2Writer.ConvertedWithoutProvenance("afe.json"));
        Assert.Contains("\"afe4\"", v2, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[ { \"id\": \"a\", \"bus\": 9, \"kva\": 2000, \"xPercent\": 15 } ]", "$.converters[0].bus")]
    [InlineData("[ { \"id\": \"a\", \"bus\": 4, \"kva\": 0, \"xPercent\": 15 } ]", "$.converters[0].kva")]
    [InlineData("[ { \"id\": \"a\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 0 } ]", "$.converters[0].xPercent")]
    [InlineData("[ { \"id\": \"a\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15, \"rPercent\": -1 } ]", "$.converters[0].rPercent")]
    [InlineData("[ { \"id\": \"a\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15 }, { \"id\": \"a\", \"bus\": 4, \"kva\": 1, \"xPercent\": 1 } ]", "$.converters[1].id")]
    [InlineData("[ { \"id\": \"a\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15, \"mva\": 2 } ]", "$.converters[0].mva")]
    public void Malformed_converter_units_are_refused_at_their_path(string converters, string path)
    {
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(WithConverters("ieee4-gyGy-stepdown-balanced.json", converters), "bad.json"));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dist3ph_harmonics_stamps_the_feeder_converters_only_when_asked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-cf5-afe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string withUnits = Path.Combine(dir, "afe.json");
            File.WriteAllText(withUnits, WithConverters("ieee4-gyGy-stepdown-balanced.json", "[ { \"id\": \"afe4\", \"bus\": 4, \"kva\": 2000, \"xPercent\": 15 } ]"));
            CliProcess.Result plain = CliProcess.RunBuilt("dist3ph", withUnits, "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1");
            CliProcess.Result afe = CliProcess.RunBuilt("dist3ph", withUnits, "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--afe", "feeder");
            Assert.True(plain.ExitCode == 0, plain.ToString());
            Assert.True(afe.ExitCode == 0, afe.ToString());
            Assert.DoesNotContain("active-front-end converters in Y(h)", plain.StdOut, StringComparison.Ordinal);
            Assert.Contains("active-front-end converters in Y(h) (N354): 1 unit(s) from the feeder (afe4)", afe.StdOut, StringComparison.Ordinal);
            Assert.NotEqual(plain.StdOut, afe.StdOut);

            CliProcess.Result none = CliProcess.RunBuilt("dist3ph", FeederPath("ieee4-gyGy-stepdown-balanced.json"), "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--afe", "feeder");
            Assert.True(none.ExitCode == 1, none.ToString());
            CliProcess.Result bad = CliProcess.RunBuilt("dist3ph", withUnits, "harmonics", "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--afe", "all");
            Assert.True(bad.ExitCode == 2, bad.ToString());
            CliProcess.Result toV2 = CliProcess.RunBuilt("dist3ph", withUnits, "to-v2", "--to", Path.Combine(dir, "out.json"));
            // Moved 2026-10 (END-PASS CF6 row 8, end pass; N360, the gridsim-dist3ph/2 schema holds converter units, Ruling
            // R1 and Mark's go on the end-pass moves 2026-10-11): a feeder with converters now converts to v2 and reads back
            // the same. Before -> after: exit 1 -> exit 0 (observed: exit 0, stdout "wrote ...out.json (gridsim-dist3ph/2);
            // read back: the same feeder, bit for bit", 1.95.273-275 runs).
            Assert.True(toV2.ExitCode == 0, toV2.ToString());
            Assert.Contains("read back: the same feeder, bit for bit", toV2.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
