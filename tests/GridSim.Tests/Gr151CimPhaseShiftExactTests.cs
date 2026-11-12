using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-151: the CIM exporter wrote a transformer phase shift only when its magnitude exceeded 1e-12 degrees, while the
/// tap ratio was written whenever it differed from 1 at all. A tiny non-zero phase shift was silently dropped. The phase
/// shift now uses the same exact non-zero compare as the tap. Pinned: a 1e-13 degree shift is written and re-imported
/// bit-for-bit, and an exactly zero shift still writes no phase tap changer.
/// </summary>
public class Gr151CimPhaseShiftExactTests
{
    private static CimNetwork Net(double phaseDeg)
    {
        return new CimNetwork(
            new[] { new CimBus("a", "a", 132), new CimBus("b", "b", 33) },
            Array.Empty<CimBranch>(), Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g", "g", "a", 0.0) },
            new[] { new CimTransformer("t", "t",
                new[] { new CimTransformerEnd(1, "a", 1.0, 10.0, 132), new CimTransformerEnd(2, "b", 0.0, 0.0, 33) },
                1.0, phaseDeg) });
    }

    [Theory]
    [InlineData(1e-13)]
    [InlineData(-1e-13)]
    [InlineData(30.0)]
    public void Gr151_a_non_zero_phase_shift_is_written_and_reads_back_exactly(double phaseDeg)
    {
        string text = CimExporter.Export(Net(phaseDeg));
        Assert.Contains("PhaseTapChangerLinear", text, StringComparison.Ordinal);
        Assert.Equal(phaseDeg, Assert.Single(CimImporter.Parse(text).Transformers).PhaseShiftDeg);
    }

    [Fact]
    public void Gr151_an_exactly_zero_phase_shift_writes_no_phase_tap_changer()
    {
        string text = CimExporter.Export(Net(0.0));
        Assert.DoesNotContain("PhaseTapChangerLinear", text, StringComparison.Ordinal);
    }
}
