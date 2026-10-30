using System;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The EXPERIMENTAL, non-hard-gate impedance passivity hook (critique FIX 7): a passive network's
/// driving-point impedance is positive-real (reported Constitutive), and a grid-following converter presents a
/// negative-real band around its PLL bandwidth (reported, adjudicated by the Nyquist test, never gating).
/// </summary>
public class PhysicsImpedanceTests
{
    [Fact]
    public void Passive_grid_driving_point_is_positive_real_and_reported_constitutive()
    {
        var model = GridImpedanceAssembler.CanonicalThevenin(scr: 3.0, xOverR: 10.0, pRatedPu: 1.0, baseMva: 100);
        double[] fHz = ImpedanceStabilityScan.LogGrid(0.5, 300.0, 40);
        LawResult law = ImpedancePassivityCheck.AuditGridPassivity(model, busId: 2, fHz, f0Hz: 50.0);

        Assert.Equal(14, law.Number);
        Assert.Equal(LawStatus.Constitutive, law.Status);   // positive-real => Constitutive, never Violated/gating
    }

    [Fact]
    public void Grid_following_converter_shows_a_negative_resistance_band()
    {
        var op = new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, 0.2);
        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 20.0, 0.707, 0.0);
        double[] fHz = ImpedanceStabilityScan.LogGrid(0.5, 300.0, 60);

        LawResult law = ImpedancePassivityCheck.AuditConverterNonPassiveBand(conv, fHz);
        Assert.Equal(15, law.Number);
        Assert.Equal(LawStatus.Constitutive, law.Status);   // reported, not gating
        // The negative-resistance band exists: min Re(Y_conv) < 0 over the swept band (the residual is |min Re|).
        Assert.True(law.Residual > 0.0, "expected a negative-real (non-passive) band for a GFL converter");
        bool anyNeg = fHz.Any(f => conv.PositiveSequenceAdmittance(2.0 * Math.PI * f).Real < 0.0);
        Assert.True(anyNeg);
    }

    [Fact]
    public void Passivity_hook_is_not_a_hard_gate_law()
    {
        // The hook lives outside PhysicsLaws.Audit's ten headline laws (numbers 1..10), mirroring the
        // ReciprocityCheck (11) / SmallSignalConsistency (12,13) experimental additions. Its law numbers
        // (14,15) must be outside the hard-gate set, so it can never block estimator acceptance. Assert
        // that against the REAL contract (PhysicsCheckRegistry.HardGateLaws), not a compile-time tautology.
        Assert.DoesNotContain(14, GridSim.Core.Physics.PhysicsCheckRegistry.HardGateLaws);
        Assert.DoesNotContain(15, GridSim.Core.Physics.PhysicsCheckRegistry.HardGateLaws);
    }
}
