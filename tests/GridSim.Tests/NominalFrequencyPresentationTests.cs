using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R196 (October 2026 review, S5): where the model frequency is available, a display reads it instead of a literal
/// 50 Hz. Source-level, like <see cref="WpfPresentationSourceContractTests"/> (the test project does not reference
/// GridSim.Wpf).
/// </summary>
public class NominalFrequencyPresentationTests
{
    [Fact]
    public void Wpf_operator_frequency_colour_is_the_deviation_from_the_model_nominal()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/OperatorVm.cs moved to
        // src/GridSim.Ui/Tabs/Operator/OperatorModel.cs; assertions unchanged, and strengthened: the WPF view-model must
        // derive from that model and compute no deviation of its own (neither form).
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Operator", "OperatorModel.cs");
        Assert.Contains("double devHz = f.FrequencyHz - f.Model.FrequencyHz;", src);
        Assert.DoesNotContain("f.FrequencyHz - 50.0", src);
        string adapter = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "OperatorVm.cs");
        Assert.Contains("public sealed class OperatorVm : OperatorModel", adapter);
        Assert.DoesNotContain("devHz", adapter);
        Assert.DoesNotContain("f.FrequencyHz - 50.0", adapter);
    }
}
