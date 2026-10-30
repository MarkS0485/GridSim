using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The validation gate: an asset-fleet simulation is compared against a recorded trace, and only passes
/// when nadir/RoCoF/timing fall within tolerance. A perturbed "recording" fails the gate - the harness
/// discriminates, it does not rubber-stamp.
/// </summary>
public class AssetValidationTests
{
    private static readonly IReadOnlyList<FrequencyEvent> Loss = new[] { new FrequencyEvent(1.0, -1000.0) };

    private static (FrequencyResponse sim, RecordedFrequencyTrace rec) SimAndSyntheticRecording(double recordedNadirOffset)
    {
        // Baseline sim (no fleet) stands in as both the "simulation" and the seed for a synthetic recording.
        GridModel m = AssetTestHelpers.Model(assetDynamics: null);
        FrequencyResponse sim = FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig());
        var recPoints = sim.Points.Select((p, i) => (i * 0.02, p.Hz - recordedNadirOffset)).ToList();
        return (sim, new RecordedFrequencyTrace(recPoints));
    }

    [Fact]
    public void A_matching_simulation_passes_the_gate()
    {
        (FrequencyResponse sim, RecordedFrequencyTrace rec) = SimAndSyntheticRecording(recordedNadirOffset: 0.0);
        ValidationReport report = AssetValidation.Compare(sim, rec);
        Assert.True(report.Passed, report.Note);
        Assert.True(report.NadirErrHz < 1e-6);
    }

    [Fact]
    public void A_perturbed_recording_fails_the_gate()
    {
        // Shift the whole recording 0.3 Hz lower => nadir error far exceeds the 0.05 Hz tolerance.
        (FrequencyResponse sim, RecordedFrequencyTrace rec) = SimAndSyntheticRecording(recordedNadirOffset: 0.3);
        ValidationReport report = AssetValidation.Compare(sim, rec);
        Assert.False(report.Passed);
        Assert.True(report.NadirErrHz > 0.05);
    }

    [Fact]
    public void Recorded_2019_08_09_initial_rocof_is_reproduced_at_the_reported_inertia()
    {
        // The real GB 2019-08-09 event (GDA reports/rocof_events-20190809...): recorded 1 s-window peak
        // RoCoF -0.151 Hz/s, loss 1618 MW. Initial RoCoF depends ONLY on E and dP (governor/damping don't
        // enter), so this anchors the swing kernel to the recorded event. The report's swing-equation
        // inertia estimate (267.8 GVA.s) is exactly the E that reproduces the 1 s-averaged RoCoF; NESO's
        // Outturn 215 GVA.s gives the higher instantaneous 0.188 Hz/s (1 s averaging lowers the peak).
        const double lossMw = 1618.0, recordedRocof = -0.151;
        FrequencyResponse atSwingInertia = FrequencyDynamics.Simulate(
            267_800.0, 30_000.0, new[] { new FrequencyEvent(1.0, -lossMw) }, new DynamicsConfig());
        Assert.Equal(recordedRocof, atSwingInertia.InitialRocofHzPerS, 2);

        FrequencyResponse atNesoOutturn = FrequencyDynamics.Simulate(
            215_000.0, 30_000.0, new[] { new FrequencyEvent(1.0, -lossMw) }, new DynamicsConfig());
        Assert.Equal(-0.188, atNesoOutturn.InitialRocofHzPerS, 2);   // instantaneous > 1 s-averaged, as expected
    }
}
