using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S3 G01 (October 2026): the protection defaults grounded in the documents in docs/reference. Each pin names the
/// document id, version and table it comes from, so a later edit that drifts from the source fails here with the
/// citation in front of it. New tests only; no existing expectation moved for G01.
/// </summary>
public class S3ProtectionGroundingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {System.AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    // ---- G99 Issue 2 (S124) Table 10.1, p.125: the interface-protection stages a sustained excursion trips first ----

    [Fact]
    public void Voltage_frequency_and_rocof_defaults_are_the_G99_Issue_2_Table_10_1_settings()
    {
        var s = new ProtectionSettings();
        Assert.Equal(0.80, s.UnderVoltagePu);     // U/V: V - 20% (HV and LV), 2.5 s
        Assert.Equal(1.10, s.OverVoltagePu);      // O/V stage 1: V + 10% (HV), 1.0 s; was an unsourced 1.20
        Assert.Equal(47.5, s.UnderFrequencyHz);   // U/F stage 1: 47.5 Hz, 20 s
        Assert.Equal(52.0, s.OverFrequencyHz);    // O/F: 52.0 Hz, 0.5 s
        Assert.Equal(1.0, s.RocofTripHzPerS);     // LoM (RoCoF): 1 Hz/s, 0.5 s
    }

    [Fact]
    public void Vector_shift_default_is_the_legacy_G59_high_impedance_upper_end()
    {
        // EREC G59 Issue 3 Amendment 8 (S135) p.63: K1 x 6 degrees, K1 = 1.0 (low impedance) or 1.66 - 2.0 (high).
        Assert.Equal(2.0 * 6.0, new ProtectionSettings().VectorShiftTripDeg);
    }

    [Fact]
    public void The_overvoltage_element_picks_up_between_the_old_and_the_grounded_setting()
    {
        // case14 solves with its highest bus at 1.09 pu: inside both the old 1.20 and the grounded 1.10 pickups, so the
        // healthy case still has no 59 operation (ProtectionAssessmentTests pins that too). Lowering the pickup by
        // 0.02 pu below the case's own maximum makes the 59 element operate at exactly the buses above it.
        GridModel m = GridSim.Core.IO.IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        double vmax = r.Vm.Max();
        Assert.True(vmax < new ProtectionSettings().OverVoltagePu, $"case14 max Vm {vmax} must sit below the 1.10 pu pickup");
        ProtectionReport healthy = ProtectionAssessment.Assess(m, r);
        Assert.DoesNotContain(healthy.Operating, v => v.Device == "59");

        var lower = new ProtectionSettings { OverVoltagePu = vmax - 0.02 };
        ProtectionReport rep = ProtectionAssessment.Assess(m, r, lower);
        int above = r.Vm.Count(v => v > vmax - 0.02);
        Assert.Equal(above, rep.Operating.Count(v => v.Device == "59"));
    }

    // ---- grading margin: National Grid PS(T)010 (S158) Appendix A.5 and ENWL EPD350 Issue 6 (S165) section 4.3 ----

    [Fact]
    public void Default_coordination_time_interval_is_400_ms()
    {
        Assert.Equal(0.40, OvercurrentCoordination.DefaultCoordinationTimeIntervalS);
    }

    [Fact]
    public void Grade_without_a_cti_uses_the_400_ms_default()
    {
        // Two definite-time elements a known margin apart: 0.35 s fails the 0.40 s default (it passed the old 0.30 s),
        // 0.45 s passes. The required margin reported back is the default itself.
        var primary = new OvercurrentElement(OvercurrentCurve.DefiniteTime, 100.0, 0.0, DefiniteTimeSeconds: 0.10);
        var near = new OvercurrentElement(OvercurrentCurve.DefiniteTime, 100.0, 0.0, DefiniteTimeSeconds: 0.45);
        var far = new OvercurrentElement(OvercurrentCurve.DefiniteTime, 100.0, 0.0, DefiniteTimeSeconds: 0.55);

        GradingResult g1 = OvercurrentCoordination.Grade(primary, near, faultCurrent: 1000.0);
        Assert.Equal(0.40, g1.RequiredMarginSeconds);
        Assert.False(g1.Coordinated);

        GradingResult g2 = OvercurrentCoordination.Grade(primary, far, faultCurrent: 1000.0);
        Assert.True(g2.Coordinated);

        // The chain form reads the same default.
        var chain = OvercurrentCoordination.GradeChain(new[] { primary, near }, 1000.0);
        Assert.Equal(0.40, Assert.Single(chain).RequiredMarginSeconds);
    }

    [Fact]
    public void The_cli_grade_default_is_the_core_constant_not_its_own_number()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Cli", "CliDefaults.cs"));
        Assert.Contains("GradeCtiS = GridSim.Core.Protection.Coordination.OvercurrentCoordination.DefaultCoordinationTimeIntervalS;", src);
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("[--cti 0.4]", program);
        Assert.DoesNotContain("[--cti 0.3]", program);
    }

    [Fact]
    public void Iec_curve_constants_match_the_ABB_handbook_table()
    {
        // ABB DA Handbook 8.2 (S317) Table 8.2.1, p.4: (alpha, k) per IEC curve.
        Assert.Equal((0.14, 0.02), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecStandardInverse));
        Assert.Equal((13.5, 1.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecVeryInverse));
        Assert.Equal((80.0, 2.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecExtremelyInverse));
        Assert.Equal((120.0, 1.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecLongTimeInverse));
    }

    // ---- bundled asset-class data: the loss-of-mains values carry their citations ----

    private static JsonElement ClassNamed(string caseDir, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", caseDir, "asset-classes.json")));
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c.Clone();
        Assert.Fail($"asset class '{name}' not found in data/{caseDir}/asset-classes.json");
        return default;
    }

    [Theory]
    [InlineData("gb-full")]
    [InlineData("gb-synthetic")]
    public void Legacy_dg_vector_shift_is_the_cited_G59_setting(string caseDir)
    {
        JsonElement c = ClassNamed(caseDir, "legacy-dg-old-lom");
        Assert.Equal(6.0, c.GetProperty("params").GetProperty("vectorShiftTripDeg").GetDouble());   // value unchanged
        JsonElement prov = c.GetProperty("provenance").GetProperty("vectorShiftTripDeg");
        Assert.Equal("Recorded", prov.GetProperty("tag").GetString());
        string note = prov.GetProperty("note").GetString()!;
        Assert.Contains("S135", note);
        Assert.Contains("p.63", note);
        Assert.DoesNotContain("[VERIFY]", note);
    }

    [Fact]
    public void Alomcp_dg_rocof_and_its_delay_cite_G99_Table_10_1()
    {
        JsonElement c = ClassNamed("gb-synthetic", "dg-alomcp");
        Assert.Equal(1.0, c.GetProperty("params").GetProperty("rocofTripHzPerS").GetDouble());
        Assert.Equal(0.5, c.GetProperty("params").GetProperty("rocofTripConfirmS").GetDouble());
        JsonElement prov = c.GetProperty("provenance");
        Assert.Contains("Table 10.1", prov.GetProperty("rocofTripHzPerS").GetProperty("note").GetString());
        Assert.Contains("Table 10.1", prov.GetProperty("rocofTripConfirmS").GetProperty("note").GetString());
    }
}
