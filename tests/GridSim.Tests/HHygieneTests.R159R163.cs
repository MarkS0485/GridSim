using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using Xunit;

namespace GridSim.Tests;

/// <summary>R159 / R163: the swing family's shared literals (device full-delivery 0.5 Hz, inertia floor 1 MW.s, demand
/// floor 1 MW) are one set of SwingKernel constants; the distributed kernel's governor target is SwingKernel's.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R163_swing_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(0.5, SwingKernel.DeviceFullDeliveryHz);
        Assert.Equal(1.0, SwingKernel.MinInertiaMwS);
        Assert.Equal(1.0, SwingKernel.MinDemandMw);
        Assert.Equal(1e-12, DomainCoupling.MinBranchReactancePu);
        Assert.Equal(1e-9, DomainCoupling.MinTotalCouplingMwPerRad);
    }

    [Fact]
    public void R163_no_swing_integrator_keeps_its_own_copy_of_the_literals()
    {
        // Every file that used to spell the literals out. A const is folded into the IL as the same literal, so the
        // naming is bit-identical by construction; this guard stops a copy creeping back in beside the name.
        string[][] files =
        {
            new[] { "Dynamics", "FrequencyDynamics.cs" },
            new[] { "Dynamics", "FrequencyIntegrator.cs" },
            new[] { "Dynamics", "AssetAwareFrequencyIntegrator.cs" },
            new[] { "Dynamics", "AssetFrequencyDynamics.cs" },
            new[] { "Dynamics", "MultiDeviceFrequencyIntegrator.cs" },
            new[] { "Dynamics", "RocofScreening.cs" },
            new[] { "Dynamics", "InertiaEstimator.cs" },
            new[] { "Dynamics", "Distributed", "MultiDomainSwing.cs" },
            new[] { "Fleet", "FleetDynamics.cs" },
        };
        foreach (string[] f in files)
        {
            string src = CoreSrc(f);
            Assert.DoesNotMatch(@"FastResponseMw\s*/\s*0\.5\b", src);
            Assert.DoesNotMatch(@"Math\.Max\(\s*1\.0\s*,", src);
        }
        string kernel = CoreSrc("Dynamics", "SwingKernel.cs");
        Assert.Contains("FastResponseTarget(df, ffrMw, deadbandHz, DeviceFullDeliveryHz)", kernel);
        string coupling = CoreSrc("Dynamics", "Distributed", "DomainCoupling.cs");
        Assert.DoesNotContain("> 1e-12", coupling);
        Assert.DoesNotContain("> 1e-9", coupling);
        Assert.DoesNotContain("Math.Max(density, 1e-6)", CoreSrc("Dynamics", "Distributed", "PropagationDelay.cs"));
    }

    [Fact]
    public void R159_distributed_kernel_uses_the_shared_governor_target()
    {
        string src = CoreSrc("Dynamics", "Distributed", "MultiDomainSwing.cs");
        // Moved 2026-10 (B1 R080, Ruling R1: Mark delegated EE calls 2026-10-04; source: the R080 finding, one shared
        // responder update for the whole swing family): MultiDomainSwing no longer computes the governor itself; it calls
        // the one swing kernel (SwingResponders.Advance at the COI deviation), whose single governor line is
        // SwingKernel.GovernorTarget. Before: this file had to contain "SwingKernel.GovernorTarget(dfCoi, f0, cfg.DroopPu,
        // pResp)". After: it must advance the shared kernel at dfCoi and the kernel must use SwingKernel.GovernorTarget.
        // The guard's purpose (the distributed governor is the lumped one, no inline copy) is unchanged.
        Assert.Contains("resp.Advance(dfCoi, dt)", src);
        Assert.Contains("SwingKernel.GovernorTarget(df, _f0, _droopPu, _responsiveMw)", CoreSrc("Dynamics", "SwingResponders.cs"));
        Assert.DoesNotContain("-(dfCoi / f0) / cfg.DroopPu * pResp", src);
    }

    [Fact]
    public void R159_shared_governor_target_is_bit_identical_to_the_inline_expression_it_replaced()
    {
        // The old MultiDomainSwing line, written out verbatim, against the shared kernel, over awkward inputs and the
        // f0 / droop / capacity values the swing family meets (GB 50 Hz, 60 Hz, the 5 % default droop and others).
        foreach (double f0 in new[] { 50.0, 60.0, 49.8 })
            foreach (double droop in new[] { 0.05, 0.04, 0.1, 1e-3 })
                foreach (double resp in new[] { 0.0, 1.0, 7000.0, 123.456 })
                    foreach (double dfCoi in Sweep())
                    {
                        double old = -(dfCoi / f0) / droop * resp;
                        AssertBits(old, SwingKernel.GovernorTarget(dfCoi, f0, droop, resp), $"f0 {f0} droop {droop} resp {resp} df {dfCoi}");
                    }
    }

    [Fact]
    public void R163_three_argument_fast_response_target_is_bit_identical_to_the_old_literal_form()
    {
        // Before: FastResponseTarget(df, ffrMw, deadbandHz) => FastResponseTarget(df, ffrMw, deadbandHz, 0.5).
        foreach (double ffr in new[] { 0.0, -1.0, 1.0, 500.0, 1234.5 })
            foreach (double db in new[] { 0.0, 0.015, 0.2 })
                foreach (double df in Sweep())
                    AssertBits(SwingKernel.FastResponseTarget(df, ffr, db, 0.5), SwingKernel.FastResponseTarget(df, ffr, db),
                        $"ffr {ffr} db {db} df {df}");
    }

    [Fact]
    public void R163_inertia_floor_still_applies_in_the_forward_rocof_check()
    {
        // InertiaEstimator.PredictRocof floors E at the named 1 MW.s: zero inertia gives exactly f0 * dP / 2.
        Assert.Equal(50.0 / 2.0 * -100.0, InertiaEstimator.PredictRocof(0.0, -100.0));
        Assert.Equal(50.0 / (2.0 * 1000.0) * -100.0, InertiaEstimator.PredictRocof(1000.0, -100.0));
    }
}
