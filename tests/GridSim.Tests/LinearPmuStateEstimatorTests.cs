using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The linear (single-shot) PMU state estimator: voltage and current phasors are both linear in the
/// rectangular voltage state, so a full, consistent PMU set recovers the solved truth in one non-iterative
/// solve with zero residual. The current phasors are derived independently (I = conj(S/V) from the solved line
/// flows), so a zero objective also proves the estimator's branch-current model.
/// </summary>
public class LinearPmuStateEstimatorTests
{
    [Fact]
    public void Full_phasor_set_recovers_the_truth_in_one_linear_solve()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);

        var vById = new Dictionary<int, Complex>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            vById[truth.BusIds[i]] = Complex.FromPolarCoordinates(truth.Vm[i], truth.VaDeg[i] * Math.PI / 180.0);

        var voltages = new List<PmuVoltageMeasurement>();
        foreach (int id in truth.BusIds) voltages.Add(new PmuVoltageMeasurement(id, vById[id], 0.001));

        var currents = new List<PmuCurrentMeasurement>();
        foreach (LineFlow lf in truth.LineFlows)
        {
            var sFrom = new Complex(lf.PFromMw, lf.QFromMvar) / model.BaseMva;
            Complex iFrom = Complex.Conjugate(sFrom / vById[lf.From]); // independent of the estimator's admittances
            currents.Add(new PmuCurrentMeasurement(lf.From, lf.To,
                string.IsNullOrEmpty(lf.Name) ? null : lf.Name, iFrom, 0.001));
        }

        LinearPmuResult r = LinearPmuStateEstimator.Estimate(model, voltages, currents);

        Assert.True(r.Converged);
        Assert.True(r.ObjectiveJ < 1e-10, $"objective {r.ObjectiveJ} - current model inconsistent with the truth phasors");
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(r.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm {r.Vm[i]} vs {truth.Vm[i]}");
            Assert.True(Math.Abs(r.VaDeg[i] - truth.VaDeg[i]) < 1e-7, $"bus {truth.BusIds[i]} Va {r.VaDeg[i]} vs {truth.VaDeg[i]}");
        }
    }

    [Fact]
    public void Correlated_measurement_covariance_still_recovers_the_truth()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var vById = new Dictionary<int, Complex>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            vById[truth.BusIds[i]] = Complex.FromPolarCoordinates(truth.Vm[i], truth.VaDeg[i] * Math.PI / 180.0);

        // Full 2x2 block-R with a nonzero real/imaginary correlation on every voltage phasor.
        var voltages = new List<PmuVoltageMeasurement>();
        foreach (int id in truth.BusIds)
            voltages.Add(new PmuVoltageMeasurement(id, vById[id], Sigma: 0.002, CovOffDiag: 0.000001));

        LinearPmuResult r = LinearPmuStateEstimator.Estimate(model, voltages, Array.Empty<PmuCurrentMeasurement>());
        Assert.True(r.Converged);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(r.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm");
    }

    // Renamed 2026-10 (D-DOC-219, docs-session finding): this test was called
    // Is_a_single_solve_that_also_recovers_angles_from_current_anchoring, but it passes no current measurement and asserts
    // only magnitudes. The body and every assertion are unchanged; only the name now says what it proves. Current
    // anchoring itself is proved by Recovers_angles_at_unmeasured_buses_from_current_anchoring below.
    [Fact]
    public void A_voltage_only_set_on_case14_recovers_every_magnitude_in_one_solve()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);

        var vById = new Dictionary<int, Complex>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            vById[truth.BusIds[i]] = Complex.FromPolarCoordinates(truth.Vm[i], truth.VaDeg[i] * Math.PI / 180.0);

        var voltages = new List<PmuVoltageMeasurement>();
        foreach (int id in truth.BusIds) voltages.Add(new PmuVoltageMeasurement(id, vById[id], 0.002));

        LinearPmuResult r = LinearPmuStateEstimator.Estimate(model, voltages, Array.Empty<PmuCurrentMeasurement>());
        Assert.True(r.Converged);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(r.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm");
    }

    /// <summary>
    /// D-DOC-219 (October 2026): current anchoring, the case the old test name claimed. One voltage phasor, at the slack
    /// bus only, plus the from-end current phasor of every in-service branch of case14: each branch current
    /// I = Yff V_from + Yft V_to ties its two end voltages, so the single anchored voltage carries magnitude AND angle to
    /// every other bus through the network (13 of the 14 buses have no voltage measurement at all). The estimate must
    /// recover every bus's magnitude and angle with a zero objective, as the full-set test does on case9.
    /// </summary>
    [Fact]
    public void Recovers_angles_at_unmeasured_buses_from_current_anchoring()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);

        var vById = new Dictionary<int, Complex>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            vById[truth.BusIds[i]] = Complex.FromPolarCoordinates(truth.Vm[i], truth.VaDeg[i] * Math.PI / 180.0);

        // The only voltage phasor: the slack bus (the first bus of case14, angle 0).
        int anchor = truth.BusIds[0];
        var voltages = new List<PmuVoltageMeasurement> { new(anchor, vById[anchor], 0.001) };

        // Every branch's from-end current, derived independently of the estimator's admittances (I = conj(S / V)).
        var currents = new List<PmuCurrentMeasurement>();
        foreach (LineFlow lf in truth.LineFlows)
        {
            var sFrom = new Complex(lf.PFromMw, lf.QFromMvar) / model.BaseMva;
            currents.Add(new PmuCurrentMeasurement(lf.From, lf.To, string.IsNullOrEmpty(lf.Name) ? null : lf.Name,
                Complex.Conjugate(sFrom / vById[lf.From]), 0.001));
        }
        Assert.True(currents.Count >= model.BusCount - 1, "case14 must have a branch to every bus");

        LinearPmuResult r = LinearPmuStateEstimator.Estimate(model, voltages, currents);

        Assert.True(r.Converged);
        Assert.True(r.ObjectiveJ < 1e-10, $"objective {r.ObjectiveJ}");
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(r.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm {r.Vm[i]} vs {truth.Vm[i]}");
            Assert.True(Math.Abs(r.VaDeg[i] - truth.VaDeg[i]) < 1e-7, $"bus {truth.BusIds[i]} Va {r.VaDeg[i]} vs {truth.VaDeg[i]}");
        }
        // The angles were really recovered: case14's angles are not all zero (bus 14 is about -16 degrees).
        Assert.True(r.VaDeg.Min() < -10.0, $"min angle {r.VaDeg.Min()}");
    }
}
