using GridSim.Core.Analysis.DynamicsStudy;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>Post-RC3 CLI-23: a record naming a machine the case does not hold is UNMATCHED whatever its model.</summary>
public sealed class BatchBDynamicsCoverageTests
{
    [Fact]
    public void An_unmapped_model_record_for_a_missing_machine_is_unmatched_and_stops_the_study_under_both_policies()
    {
        string dyr = Text(Study("case14-study.dyr")) + "\n20,'EXST1','1', 0.1, 0.2 /\n";
        foreach (DynamicsUnmodelledPolicy policy in new[] { DynamicsUnmodelledPolicy.Refuse, DynamicsUnmodelledPolicy.LeaveOut })
        {
            DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }, dyrText: dyr, init: Init with { Unmodelled = policy }));
            DyrRecordCoverage rec = r.Coverage.Records.Single(x => x.Bus == 20);
            Assert.Equal(DyrRecordStatus.Unmatched, rec.Status);
            Assert.False(r.Coverage.Simulable);
            Assert.Equal(DynamicsDisposition.NotRun, r.Cases[0].Disposition);
            Assert.Contains("names a machine the RAW case does not hold", r.Reasons[0], StringComparison.Ordinal);
        }
    }
}
