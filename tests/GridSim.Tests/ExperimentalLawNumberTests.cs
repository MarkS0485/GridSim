using System.Numerics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R091 (October 2026 review): the harmonic KCL check and the small-signal COI null mode both reported law 12,
/// so any report keyed by law number collided; and the grid passivity audit reported a network "passive" when
/// every swept frequency was singular (nothing audited). Law numbers now come from one registry; the COI null
/// mode is law 16; the passivity audit refuses a verdict on an empty audit and discloses skipped points.
/// </summary>
public class ExperimentalLawNumberTests
{
    [Fact]
    public void Experimental_law_numbers_are_unique_named_and_never_hard_gates()
    {
        int[] numbers =
        {
            PhysicsCheckRegistry.ReciprocityLaw, PhysicsCheckRegistry.HarmonicKclLaw,
            PhysicsCheckRegistry.LaplacianRowSumLaw, PhysicsCheckRegistry.GridPassivityLaw,
            PhysicsCheckRegistry.ConverterNonPassiveBandLaw, PhysicsCheckRegistry.CoiNullModeLaw,
        };
        Assert.Equal(numbers.Length, numbers.Distinct().Count());
        Assert.Equal(numbers.OrderBy(x => x), PhysicsCheckRegistry.ExperimentalLaws.Keys.OrderBy(x => x));
        foreach (int n in numbers)
        {
            Assert.True(n > PhysicsCheckRegistry.Names.Count, $"experimental law {n} overlaps the ten headline laws");
            Assert.DoesNotContain(n, PhysicsCheckRegistry.HardGateLaws);
            Assert.EndsWith("(experimental)", PhysicsCheckRegistry.ExperimentalLaws[n]);
        }
    }

    [Fact]
    public void Coi_null_mode_is_law_16_and_no_longer_collides_with_harmonic_kcl()
    {
        Assert.Equal(16, PhysicsCheckRegistry.CoiNullModeLaw);
        Assert.Equal(12, PhysicsCheckRegistry.HarmonicKclLaw);
        Assert.NotEqual(PhysicsCheckRegistry.CoiNullModeLaw, PhysicsCheckRegistry.HarmonicKclLaw);
    }

    [Fact]
    public void Each_check_reports_its_registry_number_and_name()
    {
        // Harmonic KCL on a small grounded study case.
        GridModel m = IeeeCasesLike();
        var src = new[] { HarmonicSourceLibrary.Make("6-pulse", 2, 0.2) };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, src, HarmonicModelOptions.Study);
        LawResult kcl = HarmonicKclCheck.Evaluate(m, src, pen, HarmonicModelOptions.Study);
        Assert.Equal(PhysicsCheckRegistry.HarmonicKclLaw, kcl.Number);
        Assert.Equal(PhysicsCheckRegistry.ExperimentalLaws[kcl.Number], kcl.Name);
        Assert.Equal(LawStatus.Holds, kcl.Status);

        LawResult rec = ReciprocityCheck.Evaluate(m);
        Assert.Equal(PhysicsCheckRegistry.ReciprocityLaw, rec.Number);
        Assert.Equal(PhysicsCheckRegistry.ExperimentalLaws[rec.Number], rec.Name);

        var thevenin = GridImpedanceAssembler.CanonicalThevenin(scr: 3.0, xOverR: 10.0, pRatedPu: 1.0, baseMva: 100);
        LawResult pas = ImpedancePassivityCheck.AuditGridPassivity(thevenin, 2, ImpedanceStabilityScan.LogGrid(0.5, 300.0, 20), 50.0);
        Assert.Equal(PhysicsCheckRegistry.GridPassivityLaw, pas.Number);
        Assert.Equal(PhysicsCheckRegistry.ExperimentalLaws[pas.Number], pas.Name);
    }

    [Fact]
    public void Passivity_refuses_a_verdict_when_every_frequency_is_singular()
    {
        // Two buses, a line, nothing to ground: Y is singular at every frequency. This used to report "passive".
        GridModel m = new()
        {
            Name = "ungrounded",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33), new Bus(2, BusType.Pq, BaseKv: 33) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ImpedancePassivityCheck.AuditGridPassivity(m, 2, new[] { 10.0, 100.0, 1000.0 }, 50.0));
        Assert.Contains("nothing was audited", ex.Message);
    }

    /// <summary>Three buses, two machines (grounded through their source reactance in the study model), a load.</summary>
    private static GridModel IeeeCasesLike() => new()
    {
        Name = "three-bus",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 40, Qd: 10, BaseKv: 132),
            new Bus(3, BusType.Pv, BaseKv: 132),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.02), new Branch(2, 3, 0.01, 0.1, 0.02), new Branch(1, 3, 0.02, 0.2, 0.04) },
        Generators = new[]
        {
            new Generator(1, Pg: 30, MBase: 100, SubtransientXdPu: 0.2),
            new Generator(3, Pg: 10, MBase: 100, SubtransientXdPu: 0.25),
        },
    };
}
