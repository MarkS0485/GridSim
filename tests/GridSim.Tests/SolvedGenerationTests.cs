using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 docs-programme finding: the <c>--once</c> report's "Generation" was the sum of the POSITIVE bus net
/// injections, not generator output, so IEEE case14 read 0.25 GW against 0.26 GW of demand plus 13.4 MW of losses.
/// <see cref="SolvedGeneration.GeneratorOutputMw"/> reports the generators' real output instead; these tests pin the
/// power balance it must satisfy and the defect it replaces.
/// </summary>
public class SolvedGenerationTests
{
    private static PowerFlowResult Solve(GridModel m) =>
        new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });

    [Theory]
    [InlineData("14")]
    [InlineData("9")]
    [InlineData("30")]
    [InlineData("118")]
    [InlineData("300")]
    public void Generator_output_balances_demand_plus_losses(string name)
    {
        GridModel m = name switch
        {
            "9" => IeeeCases.Case9(),
            "14" => IeeeCases.Case14(),
            "30" => IeeeCases.Case30(),
            "118" => IeeeCases.Case118(),
            _ => IeeeCases.Case300(),
        };
        PowerFlowResult r = Solve(m);
        Assert.True(r.Converged, $"case{name} must converge");

        double gen = SolvedGeneration.GeneratorOutputMw(m, r);
        double demand = m.Buses.Sum(b => b.Pd);
        // Power balance: generation = demand + losses (TotalLossMw is the sum of all net injections, so it includes
        // shunt consumption). The only residual is the solver's P mismatch at the buses without a generator: each is
        // below Tolerance (1e-8 p.u.) at the converged point, so the sum is below bus count x 1e-8 x base MVA (a
        // rigorous bound, about 3e-4 MW on case300 and far below the 10 MW reporting precision of the report).
        double bound = m.BusCount * 1e-8 * m.BaseMva;
        Assert.True(Math.Abs(gen - (demand + r.TotalLossMw)) < bound,
            $"case{name}: generator output {gen:R} MW vs demand {demand:R} + losses {r.TotalLossMw:R} MW");
    }

    [Fact]
    public void Case14_generator_output_is_not_the_positive_injection_sum()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = Solve(m);
        double gen = SolvedGeneration.GeneratorOutputMw(m, r);
        double oldPositiveInjections = r.PInjMw.Where(p => p > 0).Sum();
        double demand = m.Buses.Sum(b => b.Pd);

        // The old figure read BELOW demand (impossible for generation); the new one is above demand by the losses.
        Assert.True(oldPositiveInjections < demand, $"the defect this replaces: {oldPositiveInjections} < {demand}");
        Assert.True(gen > demand, $"generator output {gen} must exceed demand {demand}");
        // Printed to 2 d.p. in GW, as the --once report does: 0.27 GW (259 MW demand + 13.4 MW losses).
        Assert.Equal("0.27", (gen / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_result_from_another_model_is_refused()
    {
        PowerFlowResult r9 = Solve(IeeeCases.Case9());
        Assert.Throws<ArgumentException>(() => SolvedGeneration.GeneratorOutputMw(IeeeCases.Case14(), r9));
    }
}
