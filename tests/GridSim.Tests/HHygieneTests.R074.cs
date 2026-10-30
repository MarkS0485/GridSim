using System.Reflection;
using GridSim.Core.Assets;
using Xunit;

namespace GridSim.Tests;

/// <summary>R074: the dead distributed per-domain asset sampling path (no production caller; only StepCoi was used) is
/// deleted. The lumped StepCoi path, which every integrator uses, is untouched. AssetDomainAttributionTests exercised
/// only the deleted path and went with it (recorded in the H-stream notes for main / Mark).</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R074_the_per_domain_sampling_api_is_gone()
    {
        Assembly core = typeof(AssetDynamicsFleet).Assembly;
        Assert.Null(core.GetType("GridSim.Core.Assets.IGridFrequencySampler"));
        Assert.Null(core.GetType("GridSim.Core.Assets.CoiFrequencySampler"));
        Assert.Null(core.GetType("GridSim.Core.Assets.DomainFrequencySampler"));
        Assert.Null(core.GetType("GridSim.Core.Assets.FrequencySample"));   // the sampler's own sample type
        Type fleet = typeof(AssetDynamicsFleet);
        Assert.Null(fleet.GetMethod("AssignDomains"));
        Assert.Null(fleet.GetProperty("DomainOf"));
        Assert.Null(fleet.GetMethod("Step"));
        Assert.NotNull(fleet.GetMethod("StepCoi"));
        Assert.Null(typeof(AssetFleetStep).GetProperty("PowerByDomain"));
    }
}
