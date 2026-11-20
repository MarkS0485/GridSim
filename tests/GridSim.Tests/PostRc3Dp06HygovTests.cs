using GridSim.Core.Analysis.DynamicsStudy;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// Post-RC3 DP-06: a mapped HYGOV record keeps SUPPORTED (it does not block a study, and the pinned case14 coverage is
/// unchanged) but no longer reads "exact": its semantics name the structural difference and its derivations state it.
/// </summary>
public sealed class PostRc3Dp06HygovTests
{
    [Fact]
    public void Mapped_hygov_is_supported_but_states_that_its_structure_differs()
    {
        DynamicsModelBinding b = DynamicsModelCoverage.Build(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")),
            "case14-study.dyr", Schemas, DynamicsUnmodelledPolicy.Refuse);
        DyrRecordCoverage hygov = b.Report.Records.Single(x => x.Model == "HYGOV");
        Assert.Equal(DyrRecordStatus.Supported, hygov.Status);
        Assert.Equal(DynamicsModelCoverage.HygovSemantics, hygov.Semantics);
        Assert.Contains(DynamicsModelCoverage.HygovStructureNote, hygov.Derived);
        Assert.Equal("COMPLETE", b.Report.Status);
        Assert.All(b.Report.Records.Where(x => x.Model != "HYGOV"), x => Assert.NotEqual(DynamicsModelCoverage.HygovSemantics, x.Semantics));
        Assert.True(DynamicsModelCoverage.HygovStructureNote.All(c => c < 128));   // plain ASCII
    }
}
