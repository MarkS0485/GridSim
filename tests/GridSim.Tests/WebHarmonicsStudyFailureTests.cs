using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R052: the HARMONICS tab's RUN STUDY must surface a failed study instead of throwing out of the Blazor event
/// handler (which left the button stuck on RUNNING and tore the circuit down). The expected engine failure, an
/// ungrounded network (<see cref="SingularHarmonicNetworkException"/>), comes back from the session as a
/// <see cref="HarmonicsDto.Failure"/> carrying the engine's message, distinct from a normal "skipped" state.
/// </summary>
public class WebHarmonicsStudyFailureTests
{
    /// <summary>A two-bus feeder with no line charging, no shunts, no generators and no load: with the study
    /// options (machine sources + load shunts) there is still nothing to ground it, so Y(h) is singular.</summary>
    private static GridModel Ungrounded() => new()
    {
        Name = "ungrounded-2",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33), new Bus(2, BusType.Pq, BaseKv: 33) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = Array.Empty<Generator>(),
    };

    [Fact]
    public void Ungrounded_network_study_is_a_visible_failure_with_the_engine_message()
    {
        GridModel m = Ungrounded();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);

        // The engine itself fails loud on this model (sanity: the failure is real, not invented here).
        Assert.Throws<SingularHarmonicNetworkException>(
            () => HarmonicPenetration.Solve(m,
                new[] { HarmonicSourceLibrary.Make("6-pulse", 1, fundamentalCurrentPu: 0.5) },
                HarmonicModelOptions.Study));

        HarmonicsDto dto = SimSession.StudyHarmonics(m, r);

        Assert.True(dto.Failed);
        Assert.NotNull(dto.Error);
        Assert.Contains("singular", dto.Error);
        Assert.Equal("study failed", dto.ProvenanceTag);
        Assert.Empty(dto.BusSpectra);
        Assert.Empty(dto.Compliance);
    }

    [Fact]
    public void A_normal_study_and_a_skip_are_not_failures()
    {
        GridModel case9 = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(case9);
        HarmonicsDto ok = SimSession.StudyHarmonics(case9, r);
        Assert.False(ok.Failed);
        Assert.Null(ok.Error);
        Assert.False(ok.HeavyModelsSkipped);
        Assert.NotEmpty(ok.BusSpectra);

        Assert.False(HarmonicsDto.Skipped().Failed);
    }

    [Fact]
    public void Failure_requires_a_reason()
    {
        Assert.ThrowsAny<ArgumentException>(() => HarmonicsDto.Failure(""));
        Assert.ThrowsAny<ArgumentException>(() => HarmonicsDto.Failure("   "));
    }
}
