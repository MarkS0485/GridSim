using System.Text.Json;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N180 (October 2026): `modal --enable-inverters` on data/gb-synthetic exits 1 (two right-half-plane eigenvalues) and
/// its state matrix is defective, so participation factors are undefined; the export used to carry all-zero
/// participation rows with nothing saying so, and the console printed "(rigid COI mode)" against every oscillatory
/// mode. The export now states participationAvailable / participationUnavailableReason and the run's exitCode.
/// N179: `modal` on a MATPOWER case exits 2 because a .m file has no inertia data (every H = 0), not because of the
/// nominal-frequency note printed above it; the message now says so. Exit codes are unchanged in both.
/// </summary>
public sealed class N179N180ModalExportAndMessagesTests
{
    private static (SmallSignalModel Ssm, ModalResult Modal) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf, new[] { 0.0608, 0.1198, 0.1813 });
        return (ssm, ModalAnalysis.Analyze(ssm));
    }

    // ---- Core export --------------------------------------------------------------------------------------------

    /// <summary>A non-defective result: participationAvailable true, no reason, and no exitCode unless one is given.
    /// Every field the export had before is still there with the same value (the old call is the new call with the
    /// default argument).</summary>
    [Fact]
    public void Export_states_participation_available_and_omits_exit_code_by_default()
    {
        var (ssm, modal) = Case9();
        Assert.False(modal.ParticipationUnavailable);
        JsonElement doc = JsonDocument.Parse(SmallSignalExport.ToJson("WSCC-9", ssm, modal)).RootElement;
        Assert.True(doc.GetProperty("participationAvailable").GetBoolean());
        Assert.False(doc.TryGetProperty("participationUnavailableReason", out _));
        Assert.False(doc.TryGetProperty("exitCode", out _));
        Assert.Equal(modal.RhpEigenvalueCount, doc.GetProperty("rhpEigenvalueCount").GetInt32());
    }

    /// <summary>With an exit code given, the export carries it; the other fields are byte-for-byte those of the
    /// export without it (the new fields are appended after the old ones).</summary>
    [Fact]
    public void Export_carries_the_exit_code_when_given()
    {
        var (ssm, modal) = Case9();
        string without = SmallSignalExport.ToJson("WSCC-9", ssm, modal);
        string with = SmallSignalExport.ToJson("WSCC-9", ssm, modal, exitCode: 1);
        Assert.Equal(1, JsonDocument.Parse(with).RootElement.GetProperty("exitCode").GetInt32());
        // Same text up to the closing brace of the version without the exit code.
        string head = without.TrimEnd().TrimEnd('}').TrimEnd();
        Assert.StartsWith(head, with, StringComparison.Ordinal);
    }

    // ---- CLI end to end -------------------------------------------------------------------------------------------

    /// <summary>N180: data/gb-synthetic with --enable-inverters: exit 1 unchanged, the export is written and says the
    /// participation is unavailable and the exit code is 1; the console says "unavailable" instead of "(rigid COI
    /// mode)".</summary>
    [Fact]
    public void Defective_inverter_case_export_says_participation_unavailable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n180-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string export = Path.Combine(dir, "ss.json");
            string caseDir = Path.Combine(CliProcess.Root(), "data", "gb-synthetic");
            CliProcess.Result r = CliProcess.RunBuilt("modal", "--dir", caseDir, "--enable-inverters", "--export", export);
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains("participation factors unavailable", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("(participation factors unavailable: participationAvailable false)", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("  unavailable: the state matrix is defective", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("(rigid COI mode)", r.StdOut, StringComparison.Ordinal);

            JsonElement doc = JsonDocument.Parse(File.ReadAllText(export)).RootElement;
            Assert.False(doc.GetProperty("participationAvailable").GetBoolean());
            Assert.Contains("defective", doc.GetProperty("participationUnavailableReason").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, doc.GetProperty("exitCode").GetInt32());
            Assert.Equal(2, doc.GetProperty("rhpEigenvalueCount").GetInt32());
            Assert.Equal(116, doc.GetProperty("states").GetArrayLength());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>N180: a stable, non-defective run (case9 with damping) writes exitCode 0 and participationAvailable
    /// true, and exits 0.</summary>
    [Fact]
    public void Stable_case_export_says_exit_zero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n180-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string export = Path.Combine(dir, "ss.json");
            CliProcess.Result r = CliProcess.RunBuilt("modal", "case9", "--damping", "2", "--export", export);
            Assert.True(r.ExitCode == 0, r.ToString());
            JsonElement doc = JsonDocument.Parse(File.ReadAllText(export)).RootElement;
            Assert.True(doc.GetProperty("participationAvailable").GetBoolean());
            Assert.Equal(0, doc.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, doc.GetProperty("rhpEigenvalueCount").GetInt32());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>N179: a GB MATPOWER case still exits 2, and the message names the missing inertia data and says the
    /// frequency note is not the cause.</summary>
    [Fact]
    public void Matpower_gb_case_names_the_missing_inertia()
    {
        CliProcess.Result r = CliProcess.RunBuilt("modal", "gb-reduced-29");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("has no synchronous machine with an inertia constant (H > 0)", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("a MATPOWER file has no inertia (or frequency) fields", r.StdErr, StringComparison.Ordinal);
        // Moved 2026-10 (B1 N275 / D2 O6, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 notes O6, modal
        // frequencies scale with sqrt(f0)): the note no longer calls the 50 Hz assumption "harmless" (it is harmless only
        // for this exit). Before: "separate and harmless 50 Hz assumption". After: it is "a separate 50 Hz assumption and
        // not the reason for this exit" and names --frequency-hz. The pin's purpose (the message says the frequency note is
        // not the cause) is unchanged.
        Assert.Contains("separate 50 Hz assumption and not the reason for this exit", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("--frequency-hz 60", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("No synchronous machines with inertia", r.StdErr, StringComparison.Ordinal);   // Core's own text kept
    }
}
