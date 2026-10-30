using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R026: the rest initial-condition (IC) solve must treat a pure-resistance series arm (SeriesBranch with
/// L = 0) as a resistor, not as an open inductor; must pin a floating node (one joined to the rest only
/// through inductors) at 0 V instead of silently dropping the WHOLE IC; and must fail loud when the IC
/// matrix is singular for any other reason.
///
/// The observable is the first-step current of an inductive arm driven from a node at rest voltage V0: with
/// the correct IC the trapezoidal step gives i(dt) = dt/(2L) (v(0) + v(dt)) ~ V0 dt / L; with the IC dropped
/// (v(0) taken as 0) it gives about half that - the step-from-rest artefact the IC exists to prevent.
/// </summary>
public class EmtInitialConditionTests
{
    private const double Dt = 1e-6;
    private const double L = 1e-3;

    private static double FirstStepCurrent(EmtNetwork net, int branchId)
    {
        var eng = new EmtEngine(net, Dt);
        EmtResult res = eng.Simulate(new EmtCase(net, Dt, Dt, new[] { new EmtProbe("i", EmtProbeKind.BranchCurrent, Target: branchId) }));
        return res.Points[1].Probe[0];
    }

    [Fact]
    public void A_pure_resistance_series_arm_conducts_in_the_initial_condition()
    {
        // 1 V ideal source -> 1 ohm pure-R arm -> node 1 (1 S to ground) -> inductive arm -> big capacitor.
        // Node 1 rests at the divider value 0.5 V; it used to rest at 0 V (the pure-R arm treated as open).
        var net = new EmtNetwork(3, new List<EmtElement>
        {
            new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: 1.0, L: 0.0),
            new ShuntElement(2, Bus: 1, G: 1.0),
            new SeriesBranch(3, From: 1, To: 2, R: 0.0, L: L),
            new ShuntElement(4, Bus: 2, C: 1.0),
        });
        var eng = new EmtEngine(net, Dt);
        Assert.Equal(0, eng.InitialConditionFloatingNodes);

        double i1 = FirstStepCurrent(net, 3);
        double expected = 0.5 * Dt / L;
        Assert.True(Math.Abs(i1 / expected - 1.0) < 0.01, $"first-step inductor current {i1:E6} vs V0*dt/L = {expected:E6}");
    }

    [Fact]
    public void A_floating_node_is_pinned_and_the_rest_of_the_initial_condition_survives()
    {
        // Node 1 is joined only through inductors (open at rest): it used to leave an empty IC row, the IC
        // factorisation returned null and EVERY history stayed 0 - including the independent part below.
        var net = new EmtNetwork(5, new List<EmtElement>
        {
            new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: 0.0, L: L),
            new SeriesBranch(2, From: 1, To: 2, R: 0.0, L: L),
            new ShuntElement(3, Bus: 2, G: 1.0),
            // Independent resistively-fed part whose IC is observable.
            new SeriesBranch(4, From: 0, To: 3, R: 1.0, L: 0.0),
            new ShuntElement(5, Bus: 3, G: 1.0),
            new SeriesBranch(6, From: 3, To: 4, R: 0.0, L: L),
            new ShuntElement(7, Bus: 4, C: 1.0),
        });
        var eng = new EmtEngine(net, Dt);
        Assert.Equal(1, eng.InitialConditionFloatingNodes);

        double i6 = FirstStepCurrent(net, 6);
        double expected = 0.5 * Dt / L;
        Assert.True(Math.Abs(i6 / expected - 1.0) < 0.01, $"first-step inductor current {i6:E6} vs V0*dt/L = {expected:E6}");
    }

    [Fact]
    public void A_network_with_every_node_anchored_reports_no_floating_nodes()
    {
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(3, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: 0.4, L: L),
            new ShuntElement(2, Bus: 1, C: 1e-3),
        });
        Assert.Equal(0, new EmtEngine(net, Dt).InitialConditionFloatingNodes);
    }

    [Fact]
    public void A_singular_initial_condition_matrix_fails_loud_instead_of_being_skipped()
    {
        // Node 1 is anchored (a 1 S pure-R arm to the source) but a -1 S shunt cancels its IC diagonal to
        // exactly zero; the inductor shunt keeps the time-step matrix non-singular. The IC solve used to give
        // up silently here; it must now throw.
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: 1.0, L: 0.0),
            new ShuntElement(2, Bus: 1, G: -1.0),
            new ShuntElement(3, Bus: 1, L: L),
        });
        var ex = Assert.Throws<InvalidOperationException>(() => new EmtEngine(net, Dt));
        Assert.Contains("initial-condition", ex.Message);
    }
}
