using GridSim.Core.Dynamics;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N301 (LF3; ruled by main under Ruling R1): RP1 is a NESO GB 50 Hz product page. Both shells hide it, with the same
/// one-line reason, on a case whose nominal is not 50 Hz; the GB computation itself is unchanged.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    [Fact]
    public void N301_rp1_applies_at_exactly_the_gb_nominal_only()
    {
        Assert.True(FrequencyRiskPresenter.Rp1AppliesAt(FrequencyLimits.NominalHz));
        Assert.True(FrequencyRiskPresenter.Rp1AppliesAt(50.0));
        Assert.False(FrequencyRiskPresenter.Rp1AppliesAt(60.0));
        // Exact: a nominal one ulp off 50 Hz is not the GB case (no tolerance hides a mis-read nominal).
        Assert.False(FrequencyRiskPresenter.Rp1AppliesAt(Math.BitIncrement(50.0)));
        Assert.False(FrequencyRiskPresenter.Rp1AppliesAt(Math.BitDecrement(50.0)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    public void N301_a_nominal_that_is_not_a_frequency_fails_loud(double nominalHz)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyRiskPresenter.Rp1AppliesAt(nominalHz));

    [Fact]
    public void N301_the_reason_is_one_ascii_line_naming_the_case_nominal()
    {
        string reason = FrequencyRiskPresenter.Rp1NotApplicableReason(60.0);
        Assert.Equal(
            "NESO RP1 is a GB 50 Hz product page (purchased time to 49.2 Hz, GB RoCoF and MIF); this case runs at 60.0 Hz, "
            + "so the page is not shown.", reason);
        Assert.DoesNotContain('\n', reason);
        Assert.All(reason, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the RP1 reason"));
    }

    [Fact]
    public void N301_both_shells_gate_the_page_on_the_shared_rule()
    {
        // Web: the tab takes the session nominal and gates on the Core rule; the dashboard passes it.
        string tab = Src("src", "GridSim.Web", "Components", "Tabs", "Rp1Tab.razor");
        Assert.Contains("FrequencyRiskPresenter.Rp1AppliesAt(NominalHz)", tab);
        Assert.Contains("FrequencyRiskPresenter.Rp1NotApplicableReason(NominalHz)", tab);
        Assert.Contains("<Rp1Tab Frame=\"_frame\" NominalHz=\"_session.NominalHz\" />",
            Src("src", "GridSim.Web", "Components", "Pages", "Dashboard.razor"));
        // WPF: the view-model applies the model nominal every frame and the view binds both visibilities.
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs (the two visibilities became the bools IsPageVisible and
        // IsNotApplicableVisible); the three model assertions are unchanged and the WPF XAML assertions are unchanged
        // (it is still shipped and binds the same names). Strengthened: the WPF view-model must derive from the model and
        // turn each bool back into the Visibility the XAML binds, and the Avalonia port of the view
        // (src/GridSim.App/Tabs/NesoRp1View.axaml) must gate its reason card and its page body on the same two bools.
        string vm = Src("src", "GridSim.Ui", "Tabs", "NesoRp1", "NesoRp1Model.cs");
        Assert.Contains("ApplyNominal(f.Model.FrequencyHz);", vm);
        Assert.Contains("FrequencyRiskPresenter.Rp1AppliesAt(nominalHz)", vm);
        Assert.Contains("FrequencyRiskPresenter.Rp1NotApplicableReason(nominalHz)", vm);
        string adapter = Src("src", "GridSim.Wpf", "Views", "NesoRp1Vm.cs");
        Assert.Contains("public sealed class NesoRp1Vm : NesoRp1Model", adapter);
        Assert.Contains("public Visibility PageVisibility => IsPageVisible ? Visibility.Visible : Visibility.Collapsed;", adapter);
        Assert.Contains("public Visibility NotApplicableVisibility => IsNotApplicableVisible ? Visibility.Visible : Visibility.Collapsed;", adapter);
        Assert.Contains("[nameof(IsPageVisible)] = nameof(PageVisibility),", adapter);
        Assert.Contains("[nameof(IsNotApplicableVisible)] = nameof(NotApplicableVisibility),", adapter);
        string view = Src("src", "GridSim.Wpf", "Views", "NesoRp1View.xaml");
        Assert.Contains("Visibility=\"{Binding NotApplicableVisibility}\"", view);
        Assert.Contains("<Grid Grid.Row=\"1\" Visibility=\"{Binding PageVisibility}\">", view);
        string axaml = Src("src", "GridSim.App", "Tabs", "NesoRp1View.axaml");
        Assert.Contains("IsVisible=\"{Binding IsNotApplicableVisible}\"", axaml);
        // Retargeted 2.0.161: 2.0.113 put the page body in a ScrollViewer (PageBodyScroll) that now takes Grid.Row 1, so
        // the old single pin no longer matched. Both the row holder and the body inside it must gate on IsPageVisible,
        // which checks more than the old pin did (the scroll host cannot show an empty frame when the page is off).
        Assert.Contains("<ScrollViewer x:Name=\"PageBodyScroll\" Grid.Row=\"1\" IsVisible=\"{Binding IsPageVisible}\"", axaml);
        Assert.Contains("<Grid x:Name=\"PageBody\" MinHeight=\"720\" Margin=\"0,0,18,0\" IsVisible=\"{Binding IsPageVisible}\"", axaml);
        Assert.Contains("Text=\"{Binding NotApplicableReason}\"", axaml);
        // The GB computation is unchanged: the projection is still built for every frame (web session and REST).
        Assert.Contains("Rp1 = TabProjections.Rp1(", Src("src", "GridSim.Web", "Sessions", "SimSession.cs"));
    }
}
