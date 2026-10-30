using GridSim.Core.Dynamics.SmallSignal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R085 (October 2026 review): the small-signal assembler accepted includeSoc and discarded it, the GFL reduction
/// labelled its frequency-rate state as a PLL angle (exported on the wire), and Kext was documented as MW/rad while it
/// is per-unit on the system base.
/// </summary>
public class SmallSignalLabelAndSocTests
{
    private static string Src(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "src" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void New_state_kind_is_appended_so_existing_ordinals_are_unchanged()
    {
        Assert.Equal(0, (int)StateKind.SwingAngle);
        Assert.Equal(4, (int)StateKind.PllAngle);
        Assert.Equal(7, (int)StateKind.StateOfCharge);
        Assert.Equal(8, (int)StateKind.PllFrequencyRate);
        Assert.True(new StateLabel(0, StateKind.PllFrequencyRate, 1, "x").IsSynthetic);
    }

    [Fact]
    public void Gfl_reduction_labels_its_second_state_as_the_frequency_rate()
    {
        string src = Src("GridSim.Core", "Dynamics", "SmallSignal", "LinearInverterModel.cs");
        Assert.Contains("new[] { StateKind.PllSpeed, StateKind.PllFrequencyRate, StateKind.DroopActuatorLag }", src);
        Assert.DoesNotContain("new[] { StateKind.PllSpeed, StateKind.PllAngle, StateKind.DroopActuatorLag }", src);
    }

    [Fact]
    public void Include_soc_fails_loud_in_core_and_cli()
    {
        Assert.Contains("throw new NotSupportedException(", Src("GridSim.Core", "Dynamics", "SmallSignal", "SmallSignalAssembler.cs"));
        Assert.DoesNotContain("_ = includeSoc;", Src("GridSim.Core", "Dynamics", "SmallSignal", "SmallSignalAssembler.cs"));
        Assert.Contains("error: modal --include-soc is not implemented", Src("GridSim.Cli", "Program.cs"));
    }

    [Fact]
    public void Kext_is_documented_in_per_unit()
    {
        string src = Src("GridSim.Core", "Dynamics", "SmallSignal", "SmallSignalAssembler.cs");
        Assert.DoesNotContain("(MW/rad), row sums", src);
        Assert.Contains("per-unit power per radian on the system", src);
    }
}
