using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N044: the Constrained estimation method row-equilibrates its hard constraint block with the same exact power of
/// two as the Feasible method (ConstrainedDenseWlsStep.ConstraintRowScale). These tests pin:
///   - the scale itself: sqrt(max diag G) rounded down to a power of two, 1 for an empty or non-finite gain;
///   - case57 and case118 with synthetic telemetry whose zero-injection buses carry HARD virtual-zero-injection
///     rows converge (before N044 the unscaled constraint pivots fell under the relative pivot floor and the solve
///     was reported Unobservable at iteration 0), and every hard row is met;
///   - without constraint rows nothing changes (exact J from before N044), and case14 with its one hard row gives
///     exactly the pre-N044 answer (scaling by a power of two left its pivot order and every product unchanged).
/// </summary>
public class ConstrainedKktEquilibrationTests
{
    [Fact]
    public void The_scale_is_sqrt_of_the_largest_gain_diagonal_rounded_down_to_a_power_of_two()
    {
        var kkt = new double[3, 3];
        kkt[0, 0] = 1e7; kkt[1, 1] = 3.0; kkt[2, 2] = -5.0;   // index 2 is outside s = 2: never read
        double alpha = ConstrainedDenseWlsStep.ConstraintRowScale(kkt, 2);
        Assert.Equal(2048.0, alpha);                           // ILogB(1e7) = 23, 2^(23 >> 1) = 2^11
        double ratio = Math.Sqrt(1e7) / alpha;
        Assert.InRange(ratio, 1.0, 2.0);

        Assert.Equal(1.0, ConstrainedDenseWlsStep.ConstraintRowScale(new double[2, 2], 2));    // no information
        var nan = new double[1, 1]; nan[0, 0] = double.NaN;
        Assert.Equal(1.0, ConstrainedDenseWlsStep.ConstraintRowScale(nan, 1));                // NaN never wins
        var inf = new double[1, 1]; inf[0, 0] = double.PositiveInfinity;
        Assert.Equal(1.0, ConstrainedDenseWlsStep.ConstraintRowScale(inf, 1));                // left for the solve to reject
    }

    // Synthetic telemetry with every zero-injection bus (no load, no generator) carrying HARD zero-injection rows in
    // place of its measured P and Q injections, as SparseEstimatorTests does for case14 bus 7.
    private static (GridModel M, MeasurementSet Set, int ZeroInjectionBuses) WithHardZeroInjections(string name, double noise)
    {
        GridModel m = name switch { "14" => IeeeCases.Case14(), "57" => IeeeCases.Case57(), _ => IeeeCases.Case118() };
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = noise });
        var genBuses = new HashSet<int>(m.Generators.Select(g => g.Bus));
        var zi = m.Buses.Where(b => b.Pd == 0 && b.Qd == 0 && !genBuses.Contains(b.Id)).Select(b => b.Id).ToHashSet();
        var ms = set.Measurements
            .Where(x => !(zi.Contains(x.Bus) && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar)).ToList();
        foreach (int id in zi.OrderBy(i => i))
        {
            ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 10.0, Bus: id, Source: ConstrainedDenseWlsStep.ConstraintSource));
            ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 20.0, Bus: id, Source: ConstrainedDenseWlsStep.ConstraintSource));
        }
        return (m, set with { Measurements = ms }, zi.Count);
    }

    // Before N044 every one of these was Unobservable at iteration 0.
    [Theory]
    // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the four J values
    // below moved because the truth power flow, the Box-Muller noise (log, cos) and the measurement functions now use
    // GridSim's deterministic fdlibm sin / cos / atan2 / log instead of the Windows C runtime. Before -> after:
    // 57/0 8.866349637239787E-20 -> 8.882245439379705E-20; 57/1 388.9259565332666 -> 388.92595653326845;
    // 118/0 1.207657143242615E-22 -> 1.2314429581710474E-22; 118/1 873.9071335437395 -> 873.9071335437176.
    [InlineData("57", 0.0, 15, 5, 8.882245439379705E-20)]
    [InlineData("57", 1.0, 15, 6, 388.92595653326845)]
    [InlineData("118", 0.0, 10, 5, 1.2314429581710474E-22)]
    [InlineData("118", 1.0, 10, 6, 873.9071335437176)]
    public void Hard_zero_injections_on_larger_cases_now_converge(string name, double noise, int ziBuses, int iterations, double j)
    {
        (GridModel m, MeasurementSet set, int nZi) = WithHardZeroInjections(name, noise);
        Assert.Equal(ziBuses, nZi);
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Equal(iterations, est.Iterations);
        Assert.Equal(j, est.ObjectiveJ);
        Assert.Empty(est.Removed);
        // Every hard row is met (linearised residual at convergence, MW / MVAr).
        foreach (MeasurementResidual r in est.Residuals)
            if (string.Equals(r.Measurement.Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal))
                Assert.True(Math.Abs(r.Residual) < 1e-5, $"case{name}: hard row at bus {r.Measurement.Bus} missed by {r.Residual:R}");
    }

    [Fact]
    public void Case14_with_its_one_hard_row_is_exactly_the_pre_N044_answer()
    {
        (GridModel m, MeasurementSet set, int nZi) = WithHardZeroInjections("14", 0.0);
        Assert.Equal(1, nZi);   // bus 7
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Equal(5, est.Iterations);
        Assert.Equal(3.9106414552062613E-20, est.ObjectiveJ);
    }

    [Theory]
    // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the case57 and
    // case118 J values moved because the truth power flow, the Box-Muller noise (log, cos) and the measurement functions now
    // use GridSim's deterministic fdlibm routines instead of the Windows C runtime (case14 did not move). The case57 noisy J
    // is the value N175 saw differ on Linux (407.14427112449448 there). Before -> after: 57/0 1.7150735191148933E-23 ->
    // 1.0915633400202937E-23; 57/1 407.14427112449795 -> 407.14427112449533; 118/0 1.50600047982138E-22 ->
    // 3.0931116976745245E-22; 118/1 877.8012390625307 -> 877.8012390625249.
    [InlineData("14", 0.0, 5, 9.489482721436637E-25)]
    [InlineData("14", 1.0, 5, 80.01052231478428)]
    [InlineData("57", 0.0, 5, 1.0915633400202937E-23)]
    [InlineData("57", 1.0, 5, 407.14427112449533)]
    [InlineData("118", 0.0, 5, 3.0931116976745245E-22)]
    [InlineData("118", 1.0, 6, 877.8012390625249)]
    public void Without_constraint_rows_nothing_changes(string name, double noise, int iterations, double j)
    {
        GridModel m = name switch { "14" => IeeeCases.Case14(), "57" => IeeeCases.Case57(), _ => IeeeCases.Case118() };
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = noise });
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Equal(iterations, est.Iterations);
        Assert.Equal(j, est.ObjectiveJ);   // the pre-N044 value, bit for bit
    }
}
