using System.Globalization;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Simulation;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using GridSim.Core.Simulation.Health;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: #193, result and refusal text is written the same on every machine (invariant culture),
/// checked by running each site under a comma-decimal culture (de-DE).
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    // Run 'body' with de-DE as the current culture on this thread, then put the old culture back.
    private static T UnderGerman<T>(Func<T> body)
    {
        CultureInfo old = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            return body();
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Fact]
    public void B193_health_probe_breaches_read_the_same_on_a_comma_decimal_machine()
    {
        var summary = new RealtimeRunSummary(100, 90, 20, 0, FirstStateTimeUtc: null, LastStateTimeUtc: null);
        IReadOnlyList<string> german = UnderGerman(() => new HealthProbe().Check(summary).BreachedObjectives);
        IReadOnlyList<string> invariant = new HealthProbe().Check(summary).BreachedObjectives;
        Assert.Equal(invariant, german);
        Assert.Contains(german, b => b.Contains("converged fraction 90.0% is below the objective 99.0%", StringComparison.Ordinal));
        Assert.DoesNotContain(german, b => b.Contains(',', StringComparison.Ordinal));
    }

    [Fact]
    public void B193_hosting_capacity_refusals_format_numbers_invariantly()
    {
        var study = new HostingStudy
        {
            MaxMw = -0.5, ToleranceMw = 0.25,
            Base = new QstsStudy { StepSeconds = 1, Steps = 1, Limits = new QstsLimits(0.94, 1.05, 100) },
        };
        var ex = UnderGerman(() => Assert.Throws<InvalidDataException>(() =>
            HostingCapacity.Run(IeeeCases.Case9(), study, () => new NewtonRaphsonPowerFlow())));
        Assert.Contains("maxMw must be a finite number above 0 (got -0.5)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(got 0.25)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B193_a_frozen_parameter_difference_is_written_invariantly()
    {
        var shared = new DynamicsConfig();
        FrozenParameterSet frozen = FrozenParameterSet.Freeze("anchor", shared);
        IReadOnlyList<string> d = UnderGerman(() => frozen.Differences(shared with { DroopPu = 0.045 }));
        string line = Assert.Single(d);
        Assert.Contains("-> 0.045)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0,045", line, StringComparison.Ordinal);
    }

    [Fact]
    public void B193_the_replay_frame_label_is_built_invariantly()
    {
        string src = ReadRepo("src/GridSim.Core/Simulation/PresolvedReplay.cs");
        Assert.Contains("label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $\"t={ts:F2}s\");", src, StringComparison.Ordinal);
        string probe = ReadRepo("src/GridSim.Core/Assets/AssetValidation.cs");
        Assert.Contains("string.Create(System.Globalization.CultureInfo.InvariantCulture,   // bug #193", probe, StringComparison.Ordinal);
    }
}
