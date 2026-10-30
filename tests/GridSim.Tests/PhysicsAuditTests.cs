using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The physics-law audit engine (<see cref="PhysicsLaws.Audit"/>) is the feasibility oracle: it
/// defines whether a solved state lies in the physically-admissible region. These tests pin that a
/// genuine machine-epsilon solution passes every hard law, and that a physics-impossible state (a
/// negative bus voltage) is caught by the acceptance gate - the property the constrained estimator
/// will rely on.
/// </summary>
public class PhysicsAuditTests
{
    // The laws whose violation is physically impossible and therefore gate acceptance
    // (energy, Ohm, Joule, KCL, complex power).
    private static readonly int[] HardGate = System.Linq.Enumerable.ToArray(PhysicsCheckRegistry.HardGateLaws);

    public static IEnumerable<object[]> Cases => new[]
    {
        new object[] { "9" }, new object[] { "14" }, new object[] { "30" },
        new object[] { "57" }, new object[] { "118" }, new object[] { "300" },
    };

    private static GridModel Case(string n) => n switch
    {
        "9" => IeeeCases.Case9(),
        "14" => IeeeCases.Case14(),
        "30" => IeeeCases.Case30(),
        "57" => IeeeCases.Case57(),
        "118" => IeeeCases.Case118(),
        "300" => IeeeCases.Case300(),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_true_solution_holds_every_hard_law(string name)
    {
        GridModel m = Case(name);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);

        PhysicsLawReport report = PhysicsLaws.Audit(m, r, tolMva: 1e-4);

        Assert.True(report.Gate(HardGate), $"gate failed: {report.Worst}");
        foreach (LawResult law in report.Laws)
            if (HardGate.Contains(law.Number))
                Assert.Equal(LawStatus.Holds, law.Status);
        Assert.True(report.AllHold);
    }

    [Fact]
    public void All_ten_laws_are_reported_in_order()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        PhysicsLawReport report = PhysicsLaws.Audit(m, r);

        Assert.Equal(10, report.Laws.Count);
        for (int i = 0; i < 10; i++) Assert.Equal(i + 1, report.Laws[i].Number);
        // Field laws are honestly out of scope, not fabricated passes.
        Assert.Equal(LawStatus.Constitutive, report.Laws[3].Status); // Faraday
        Assert.Equal(LawStatus.Constitutive, report.Laws[4].Status); // Ampere
        Assert.Equal(LawStatus.NotModeled, report.Laws[9].Status);   // Gauss
        Assert.Equal(LawStatus.NotApplicable, report.Laws[8].Status); // synchronism (no sample)
    }

    [Fact]
    public void A_negative_bus_voltage_is_caught_by_the_gate()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);

        // A physically-impossible state: flip a bus voltage magnitude negative. The array contents
        // are mutable even though the reference is init-only.
        int victim = r.Vm.Length - 1;
        r.Vm[victim] = -Math.Abs(r.Vm[victim]);

        PhysicsLawReport report = PhysicsLaws.Audit(m, r, tolMva: 1e-4);

        Assert.False(report.Gate(HardGate));
        Assert.NotEmpty(report.Violations);
        Assert.False(report.AllHold);
    }

    [Fact]
    public void Synchronism_scores_a_real_residual_from_a_sample()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);

        // Held: 40 deg spread against a 180 deg pole-slip limit -> margin 0.22 < 1.
        PhysicsLawReport held = PhysicsLaws.Audit(m, r, synchronism: new SynchronismSample(40, 0.2));
        LawResult s1 = held.Laws[8];
        Assert.Equal(LawStatus.Holds, s1.Status);
        Assert.True(s1.Residual < 1.0);

        // Violated: 200 deg spread exceeds the pole-slip limit.
        PhysicsLawReport slipped = PhysicsLaws.Audit(m, r, synchronism: new SynchronismSample(200, 0.2));
        Assert.Equal(LawStatus.Violated, slipped.Laws[8].Status);
    }
}
