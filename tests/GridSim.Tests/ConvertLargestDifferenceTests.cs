using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug #57 (October 2026): when <c>convert</c>'s proof fails it names the largest voltage difference as well as the first.
/// The first difference in bus order can be a last-bit one while the case moved a long way: gb-full written to MATPOWER
/// first differs by one ulp at bus 1010, but its largest difference is 0.0598 pu, because MATPOWER drops the tap changers
/// the OLTC loop moves. The reduced GB case has tap changers too, so its MATPOWER conversion shows the same effect.
/// </summary>
public class ConvertLargestDifferenceTests
{
    [Fact]
    public void A_failed_proof_names_the_largest_voltage_difference_and_how_many_buses_differ()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-convert-largest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string output = Path.Combine(dir, "uk.m");
            CliProcess.Result r = CliProcess.RunBuilt("convert", "uk", output);
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("on-load tap changer(s) not written", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("does NOT solve bit-identically to the input: ", r.StdErr, StringComparison.Ordinal);
            Assert.Matches(@"; the largest voltage difference is \|dVm\| = [0-9.Ee+-]+ pu at bus \d+ \([0-9.Ee+-]+ vs [0-9.Ee+-]+\) and \|dVa\| = [0-9.Ee+-]+ deg at bus \d+, over \d+ of the \d+ buses\. The output is kept", r.StdErr);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
