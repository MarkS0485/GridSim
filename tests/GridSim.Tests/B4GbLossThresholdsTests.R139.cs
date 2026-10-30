using GridSim.Core.Dynamics;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R139 / R149: the SCADA consoles called 49.9-50.1 Hz "the system frequency operational band" while FrequencyLimits
/// defines the operational band as 49.8-50.2 Hz. The SCADA band is the NORMAL +/-0.1 Hz band (the frequency-quality band
/// NESO measures: FRCR 2025 supplementary report S055 p.16; pre-fault frequency control webinar S050; Grid Code ECP.A.5.8.9),
/// now defined once in FrequencyLimits and referenced by ScadaThresholds. Same values, so no alarm moves. The header lamp
/// and the dials already read the shared operational band (Q10, B2).
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    [Fact]
    public void R139_the_scada_alarm_band_is_the_one_normal_band_and_sits_inside_the_operational_band()
    {
        AssertBits(49.9, FrequencyLimits.NormalBandLowHz, "normal low");
        AssertBits(50.1, FrequencyLimits.NormalBandHighHz, "normal high");
        AssertBits(FrequencyLimits.NormalBandLowHz, ScadaThresholds.FreqOperationalLowHz, "SCADA low");
        AssertBits(FrequencyLimits.NormalBandHighHz, ScadaThresholds.FreqOperationalHighHz, "SCADA high");
        Assert.True(FrequencyLimits.OperationalLowHz < FrequencyLimits.NormalBandLowHz);
        Assert.True(FrequencyLimits.NormalBandHighHz < FrequencyLimits.OperationalHighHz);

        string src = Src("src", "GridSim.Core", "Presentation", "ScadaThresholds.cs");
        Assert.Contains("FreqOperationalLowHz = GridSim.Core.Dynamics.FrequencyLimits.NormalBandLowHz;", src);
        Assert.Contains("FreqOperationalHighHz = GridSim.Core.Dynamics.FrequencyLimits.NormalBandHighHz;", src);
        Assert.DoesNotContain("FreqOperationalLowHz = 49.9", src);
    }

    [Theory]
    [InlineData(49.9, false)]
    [InlineData(50.1, false)]
    [InlineData(49.89, true)]
    [InlineData(50.11, true)]
    [InlineData(49.85, true)]    // inside the operational band, outside the normal band: a SCADA alarm, not a lamp event
    public void R139_scada_alarms_are_unchanged_at_50_hz(double hz, bool alarm)
    {
        Assert.Equal(alarm, ScadaThresholds.IsFrequencyAlarm(hz, 50.0));
        if (hz == 49.85) Assert.False(StatusLampRules.IsFrequencyDeviationEvent(hz));
    }
}
