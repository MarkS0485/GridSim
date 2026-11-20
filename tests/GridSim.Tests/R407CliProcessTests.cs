using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-07 verbs run as the built CLI: output, result files, exit codes and usage refusals (case9: branch 0 is 1-4,
/// branch 1 is 4-5; a bridge from the generator at bus 1 to bus 4, so the devices on branch 0 have a defined direction).</summary>
public sealed class R407CliProcessTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-r407-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private const string Devices = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "R1", "type": "relay", "branch": { "index": 0, "from": 1, "to": 4, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.1, "timeDial": 0.2 } ] },
      { "id": "R2", "type": "relay", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "shots": [ { "curve": "ieee-vi", "pickupA": 50, "timeDial": 0.1 } ] },
      { "id": "Z1", "type": "relay", "function": "21", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "shots": [ { "curve": "iec-si", "pickupA": 1 } ] } ] }
    """;

    [Fact]
    public void Devices_check_trace_fault_line_and_audit_run_and_write_results()
    {
        string d = Dir();
        string dev = Path.Combine(d, "devices.json");
        File.WriteAllText(dev, Devices);

        Assert.Equal(0, CliProcess.RunBuilt("devices-check", "case9", "--devices", dev).ExitCode);

        string tj = Path.Combine(d, "trace.json");
        CliProcess.Result t = CliProcess.RunBuilt("sc-trace", "case9", "--devices", dev, "--bus", "5", "--json", tj);
        Assert.Equal(0, t.ExitCode);
        Assert.Contains("sequence:", t.StdOut, StringComparison.Ordinal);
        Assert.Contains("Z1", t.StdOut, StringComparison.Ordinal);
        Assert.Contains("not evaluated", t.StdOut, StringComparison.Ordinal);   // the distance function is listed, not silently passed
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(tj)))
            Assert.Equal(3, doc.RootElement.GetProperty("tables").GetProperty("devices").GetArrayLength());

        Assert.Equal(0, CliProcess.RunBuilt("sc-trace", "case9", "--devices", dev, "--branch", "0", "--fraction", "0.5", "--objective", "min").ExitCode);

        CliProcess.Result fl = CliProcess.RunBuilt("fault-line", "case9", "--devices", dev, "--branch", "0", "--fractions", "0,0.5,1");
        Assert.Equal(0, fl.ExitCode);
        Assert.Contains("d = 0.5", fl.StdOut, StringComparison.Ordinal);

        string study = Path.Combine(d, "study.json");
        File.WriteAllText(study, """{ "manifestVersion": "gridsim-protection-audit/1", "case": "case9", "devices": "devices.json", "coordinationTimeIntervalS": 0.3 }""");
        string aj = Path.Combine(d, "audit.json");
        CliProcess.Result a = CliProcess.RunBuilt("protection-audit", study, "--json", aj);
        Assert.True(a.ExitCode is 0 or 3, a.ToString());
        Assert.Contains("protection-audit - ", a.StdOut, StringComparison.Ordinal);
        using JsonDocument ad = JsonDocument.Parse(File.ReadAllText(aj));
        Assert.Equal("gridsim-protection-audit/1", ad.RootElement.GetProperty("summary").GetProperty("format").GetString());
        Assert.True(ad.RootElement.GetProperty("tables").TryGetProperty("not_evaluated", out _));
    }

    [Fact]
    public void Refusals_are_exit_1_for_bad_input_and_2_for_usage()
    {
        string d = Dir();
        string dev = Path.Combine(d, "devices.json");
        File.WriteAllText(dev, Devices.Replace("\"from\": 1, \"to\": 4", "\"from\": 1, \"to\": 5"));
        Assert.Equal(1, CliProcess.RunBuilt("devices-check", "case9", "--devices", dev).ExitCode);   // identity does not match the case
        string good = Path.Combine(d, "good.json");
        File.WriteAllText(good, Devices);
        Assert.Equal(2, CliProcess.RunBuilt("devices-check", "case9").ExitCode);                                        // no --devices
        Assert.Equal(2, CliProcess.RunBuilt("sc-trace", "case9", "--devices", good).ExitCode);                          // no fault
        Assert.Equal(2, CliProcess.RunBuilt("sc-trace", "case9", "--devices", good, "--bus", "5", "--branch", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("sc-trace", "case9", "--devices", good, "--bus", "99").ExitCode);           // no such bus
        Assert.Equal(2, CliProcess.RunBuilt("fault-line", "case9", "--devices", good, "--branch", "0", "--fractions", "1.5").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-line", "case9", "--devices", good, "--branch", "99").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("sc-trace", "case9", "--devices", good, "--bus", "5", "--objective", "mid").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("protection-audit").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("protection-audit", Path.Combine(d, "missing.json")).ExitCode);
        string bad = Path.Combine(d, "bad.json");
        File.WriteAllText(bad, """{ "manifestVersion": "gridsim-protection-audit/1", "case": "case9", "devices": "good.json", "surprise": 1 }""");
        Assert.Equal(1, CliProcess.RunBuilt("protection-audit", bad).ExitCode);
    }
}
