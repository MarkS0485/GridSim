using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-6 (October 2026): the EMT engine items of the docs-session findings, stream 6 (review-progress/2026-10/
/// DOCS-FINDINGS-TRIAGE.md): D-DOC-019 (event times), 022 (runs of no steps), 024 (network checks), 025 (probes that
/// name nothing), 122 (switch resistances) and 184 (the factorisation count); D-DOC-026 is in Doc6CsrStableOrderTests.
/// Every refusal is checked for the exception type and for the words that name what is wrong, and every accepted edge (an event exactly at the end, a single-part shunt probe, a non-passive network by default) is checked
/// to still run, so the checks cannot creep into refusing what used to be valid.
/// </summary>
public sealed class Doc6EmtEngineTests
{
    private const double Dt = 2e-6;

    // V source 10 -> switch 11 (initially closed) -> series R-L 12 -> node 2 -> C 13 to ground (the R241 test network).
    private static EmtNetwork Net(params EmtElement[] extra)
    {
        var elements = new List<EmtElement>
        {
            new VoltageSource(10, 0, 0, _ => 1.0),
            new SwitchElement(11, 0, 1, Ron: 1e-6, Roff: 1e9, InitiallyClosed: true),
            new SeriesBranch(12, 1, 2, 0.01, 1e-3),
            new ShuntElement(13, 2, C: 1e-3),
        };
        elements.AddRange(extra);
        return new EmtNetwork(3, elements);
    }

    private static readonly EmtProbe[] V2 = { new("v:2", EmtProbeKind.NodeVoltage, 2) };

    private static EmtResult Run(EmtNetwork net, double duration, IReadOnlyList<EmtProbe>? probes = null,
        IReadOnlyList<EmtEvent>? events = null, EmtOptions? options = null)
        => new EmtEngine(net, Dt, options).Simulate(new EmtCase(net, Dt, duration, probes ?? V2, events));

    // ---- D-DOC-024: network checks ------------------------------------------------------------------------------

    [Fact]
    public void D024_duplicate_element_ids_are_refused_naming_the_id_and_both_kinds()
    {
        // Id 12 twice: a probe used to read the FIRST element with that id and a switch event toggle the LAST.
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new ShuntElement(12, 2, G: 1.0)), Dt));
        Assert.Contains("duplicate element ids", ex.Message);
        Assert.Contains("id 12 is used by 2 elements (SeriesBranch, ShuntElement)", ex.Message);
    }

    [Theory]
    [InlineData(-1)]   // a shunt on ground stamps nothing at all
    [InlineData(3)]    // one past the last node: used to be an index error
    public void D024_a_one_terminal_element_off_the_network_is_refused(int bus)
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new ShuntElement(20, bus, G: 1.0)), Dt));
        Assert.Contains($"ShuntElement 20 is at node {bus}", ex.Message);
    }

    [Fact]
    public void D024_a_two_terminal_element_past_the_last_node_is_refused_but_ground_is_accepted()
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new SeriesBranch(20, 2, 7, 0.1, 1e-3)), Dt));
        Assert.Contains("SeriesBranch 20 joins node 2 to node 7", ex.Message);
        // A series arm to ground is a legitimate element (EmtTapAndSeriesCapacitorTests builds one).
        _ = new EmtEngine(Net(new SeriesBranch(20, 2, EmtNetwork.Ground, 0.1, 1e-3)), Dt);
    }

    [Fact]
    public void D024_two_ideal_sources_on_one_bus_are_refused_naming_both()
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new VoltageSource(20, 0, 0, _ => 0.5)), Dt));
        Assert.Contains("ideal voltage sources 10 and 20 are both at node 0", ex.Message);
        // A Thevenin source beside an ideal one is a different thing (a Norton conductance plus a current) and runs.
        _ = new EmtEngine(Net(new VoltageSource(20, 0, 0.5, _ => 0.5)), Dt);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void D024_a_source_resistance_that_is_negative_or_not_finite_is_refused(double rs)
    {
        // A negative or NaN Rs used to fall through to the ideal-source branch as if it were 0.
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new VoltageSource(20, 2, rs, _ => 1.0)), Dt));
        Assert.Contains("voltage source 20 at node 2 has source resistance Rs =", ex.Message);
    }

    [Theory]
    [InlineData(0.0, 1e9, "Ron")]
    [InlineData(-1e-3, 1e9, "Ron")]
    [InlineData(double.NaN, 1e9, "Ron")]
    [InlineData(1e-6, 0.0, "Roff")]
    [InlineData(1e-6, double.PositiveInfinity, "Roff")]
    public void D024_D122_a_switch_resistance_that_is_not_positive_and_finite_is_refused(double ron, double roff, string which)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new EmtEngine(Net(new SwitchElement(20, 2, EmtNetwork.Ground, Ron: ron, Roff: roff, InitiallyClosed: false)), Dt));
        Assert.Contains($"switch 20 (2->{EmtNetwork.Ground}) has", ex.Message);
        Assert.Contains(which + " =", ex.Message);
    }

    [Theory]
    [InlineData(0.0, -1e-3, 0.0, "capacitance")]
    [InlineData(0.0, 0.0, -1e-3, "inductance")]
    [InlineData(double.NaN, 0.0, 0.0, "non-finite conductance")]
    [InlineData(0.0, double.PositiveInfinity, 0.0, "capacitance")]
    public void D024_a_shunt_with_a_negative_or_non_finite_part_is_refused(double g, double c, double l, string what)
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new ShuntElement(20, 2, G: g, C: c, L: l)), Dt));
        Assert.Contains("shunt 20 at node 2 has", ex.Message);
        Assert.Contains(what, ex.Message);
    }

    [Theory]
    [InlineData(0.0, 1e-4, "surge impedance")]
    [InlineData(-50.0, 1e-4, "surge impedance")]
    [InlineData(50.0, 0.0, "travel time")]
    [InlineData(50.0, double.NaN, "travel time")]
    public void D024_bad_travelling_wave_line_parameters_are_refused(double zc, double tau, string what)
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(Net(new BergeronLine(20, 2, 1, zc, tau)), Dt));
        Assert.Contains("travelling-wave line 20 (2->1) has " + what, ex.Message);
    }

    [Fact]
    public void D024_every_problem_is_listed_in_one_refusal()
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(
            Net(new ShuntElement(20, 2, C: -1.0), new SwitchElement(21, 2, EmtNetwork.Ground, Ron: 0.0)), Dt));
        Assert.Contains("shunt 20", ex.Message);
        Assert.Contains("switch 21", ex.Message);
    }

    [Fact]
    public void D024_a_non_passive_element_is_accepted_and_listed_by_default_and_refused_on_request()
    {
        // A negative shunt conductance and a negative series resistance: network-reduction equivalents carry both.
        EmtNetwork net = Net(new ShuntElement(20, 2, G: -0.5), new SeriesBranch(21, 2, EmtNetwork.Ground, -0.01, 1e-3));
        var engine = new EmtEngine(net, Dt);
        Assert.Equal(2, engine.NonPassiveElements.Count);
        Assert.Contains("shunt 20 at node 2 has negative conductance G = -0.5", engine.NonPassiveElements);
        Assert.Contains("series branch 21 (2->-1) has negative resistance R = -0.01", engine.NonPassiveElements);

        var ex = Assert.Throws<EmtNonPassiveNetworkException>(() => new EmtEngine(net, Dt, new EmtOptions { RefuseNonPassive = true }));
        Assert.Contains("2 non-passive element(s)", ex.Message);
        Assert.Contains("shunt 20", ex.Message);
        Assert.Contains("series branch 21", ex.Message);
        Assert.Equal(2, ex.Elements.Count);
        Assert.IsAssignableFrom<ArgumentException>(ex);   // every existing handler of a refused network still catches it

        // A passive network lists nothing and is accepted with the option on.
        Assert.Empty(new EmtEngine(Net(), Dt, new EmtOptions { RefuseNonPassive = true }).NonPassiveElements);
    }

    [Fact]
    public void D024_the_bundled_network_reduction_cases_list_exactly_their_negative_r_and_g_elements()
    {
        // Oracle from the case data itself, not from the engine: every in-service branch with R < 0 becomes one series
        // arm with that R (R078: an X < 0 branch is a series R-C arm with the same R), and every bus with Gs < 0 one
        // shunt with G = Gs / BaseMva < 0. Loads never give a negative G (N029: Pd < 0 is a current source).
        foreach ((string name, GridModel m) in new[]
        {
            ("case145", IeeeCases.Case145()),
            ("pglib_opf_case588_sdet", MatpowerParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "data", "matpower", "nesta", "pglib_opf_case588_sdet.m"))),
        })
        {
            int expected = m.Branches.Count(b => b.InService && b.R < 0) + m.Buses.Count(b => b.Gs < 0);
            Assert.True(expected > 0, name);
            EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(m.Generators[0].Bus, FrequencyHz: 50) }));
            Assert.Equal(expected, new EmtEngine(net, 20e-6).NonPassiveElements.Count);
            var ex = Assert.Throws<EmtNonPassiveNetworkException>(() => new EmtEngine(net, 20e-6, new EmtOptions { RefuseNonPassive = true }));
            Assert.Equal(expected, ex.Elements.Count);
        }
    }

    [Fact]
    public void D024_the_shipped_passive_cases_have_no_non_passive_element()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case30(), IeeeCases.Case300() })
        {
            EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(m.Generators[0].Bus) }));
            Assert.Empty(new EmtEngine(net, 20e-6, new EmtOptions { RefuseNonPassive = true }).NonPassiveElements);
        }
    }

    // ---- D-DOC-022: a run must take at least one step ----------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-7)]        // used to round to 0 steps: a "run" of the t = 0 sample only
    [InlineData(-1.0)]         // used to fail with the raw list message "(Parameter 'capacity')"
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.5 * Dt)]     // round(0.5) = 0 (to even): no step
    public void D022_a_duration_that_gives_no_step_is_refused(double duration)
    {
        var ex = Assert.Throws<ArgumentException>(() => Run(Net(), duration));
        Assert.Contains("EmtCase.DurationSeconds", ex.Message);
        Assert.DoesNotContain("capacity", ex.Message);
    }

    [Fact]
    public void D022_a_refused_duration_does_not_consume_the_run()
    {
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        Assert.Throws<ArgumentException>(() => engine.Simulate(new EmtCase(net, Dt, 0.0, V2)));
        EmtResult r = engine.Simulate(new EmtCase(net, Dt, 3 * Dt, V2));
        Assert.Equal(3, r.Stats.Steps);
    }

    [Fact]
    public void D022_the_step_count_is_unchanged_for_a_valid_duration()
    {
        // round(duration / dt) exactly as before: 0.6 dt is one step, 1e-3 s is 500.
        Assert.Equal(1, Run(Net(), 0.6 * Dt).Stats.Steps);
        Assert.Equal(500, Run(Net(), 1e-3).Stats.Steps);
        Assert.Equal(501, Run(Net(), 1e-3).Points.Count);
    }

    // ---- D-DOC-019: event times --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1.0, "0 or later")]
    [InlineData(-1e-9, "0 or later")]
    [InlineData(double.NaN, "0 or later")]
    [InlineData(double.NegativeInfinity, "0 or later")]
    [InlineData(2.5e-3, "never be applied")]   // after the 2 ms run's last step
    public void D019_an_event_time_the_run_cannot_apply_is_refused(double at, string why)
    {
        var ex = Assert.Throws<ArgumentException>(() => Run(Net(), 2e-3, events: new EmtEvent[] { new SwitchEvent(at, 11, Close: false) }));
        Assert.Contains("switch event on element 11", ex.Message);
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void D019_events_at_zero_and_exactly_at_the_end_still_apply()
    {
        // An event at 0 is applied at the first step (as before); one exactly at the duration at the last step.
        EmtResult atZero = Run(Net(), 2e-3, events: new EmtEvent[] { new SwitchEvent(0.0, 11, Close: false) });
        EmtResult none = Run(Net(), 2e-3);
        Assert.Equal(1 + 2, atZero.Stats.Refactorizations);   // it fired: one CDA burst
        Assert.Equal(1, none.Stats.Refactorizations);
        EmtResult atEnd = Run(Net(), 2e-3, events: new EmtEvent[] { new SwitchEvent(2e-3, 11, Close: false) });
        Assert.Equal(1 + 2, atEnd.Stats.Refactorizations);
        // Up to the last step, the run with the event at the end is the run without it, bit for bit.
        for (int k = 0; k < none.Points.Count - 1; k++) Assert.Equal(none.Points[k].Probe[0], atEnd.Points[k].Probe[0]);
    }

    // ---- D-DOC-025: probes that name nothing -----------------------------------------------------------------------

    [Theory]
    [InlineData(EmtProbeKind.NodeVoltage, -1, "reads node -1")]
    [InlineData(EmtProbeKind.NodeVoltage, 3, "reads node 3")]
    [InlineData(EmtProbeKind.BranchCurrent, 99, "element 99, which is not in the network")]
    [InlineData(EmtProbeKind.BranchCurrent, 10, "VoltageSource 10, which does not carry one branch current")]
    public void D025_a_probe_that_names_nothing_is_refused(EmtProbeKind kind, int target, string why)
    {
        // An unknown branch id used to record a column of NaN; a node past the end crashed with an index error.
        var ex = Assert.Throws<ArgumentException>(() => Run(Net(), 1e-4, new[] { new EmtProbe("p", kind, target) }));
        Assert.Contains("probes that name nothing", ex.Message);
        Assert.Contains("'p' ", ex.Message);
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void D025_a_multi_part_shunt_current_probe_is_refused_and_a_single_part_one_runs()
    {
        // A shunt with G and C is stamped as two branches; the probe used to read only the first (the G part).
        var ex = Assert.Throws<ArgumentException>(() =>
            Run(Net(new ShuntElement(20, 2, G: 1.0, C: 1e-3)), 1e-4, new[] { new EmtProbe("i20", EmtProbeKind.BranchCurrent, 20) }));
        Assert.Contains("ShuntElement 20, which does not carry one branch current", ex.Message);
        EmtResult ok = Run(Net(), 1e-4, new[] { new EmtProbe("i13", EmtProbeKind.BranchCurrent, 13), new EmtProbe("i11", EmtProbeKind.BranchCurrent, 11) });
        Assert.All(ok.Points, p => Assert.True(double.IsFinite(p.Probe[0]) && double.IsFinite(p.Probe[1])));
    }

    // ---- D-DOC-184: the factorisation count ----------------------------------------------------------------------------

    [Fact]
    public void D184_refactorizations_are_one_plus_two_per_event_with_cda_and_one_per_event_without()
    {
        var events = new EmtEvent[] { new SwitchEvent(5e-4, 11, Close: false), new SwitchEvent(1e-3, 11, Close: true), new SwitchEvent(1.5e-3, 11, Close: true) };
        // The third event re-closes a closed switch: a no-op (R241), so it is not counted.
        Assert.Equal(1 + 2 * 2, Run(Net(), 2e-3, events: events).Stats.Refactorizations);
        Assert.Equal(1 + 2, Run(Net(), 2e-3, events: events, options: new EmtOptions { UseCda = false }).Stats.Refactorizations);
        // Backward Euler never runs the CDA burst, whatever UseCda says.
        Assert.Equal(1 + 2, Run(Net(), 2e-3, events: events, options: new EmtOptions { Rule = IntegrationRule.BackwardEuler }).Stats.Refactorizations);
    }
}
