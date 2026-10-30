using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// QSTS against OpenDSS (reference data in data/qsts, written by tools/crossval/crossval_qsts_opendss.py with OpenDSS
/// through OpenDSSDirect.py; the versions are in each file's header). The same balanced radial feeder (a stiff 33 kV
/// source, the 33/11 kV transformer T1 with its regulator on the HV winding taps, four 11 kV sections, constant-power
/// loads, a voltage-controlled capacitor) is run over a daily load shape with the controls settling at every step
/// (OpenDSS ControlMode=STATIC vs the timed engine with zero delays) and over a 20 minute window at 1 s steps with time
/// delays (ControlMode=TIME vs the timed engine with the same delays). Asserted: the tap position and capacitor state at
/// every step are the same, and the bus voltages agree within 1e-10 pu (both solved to 1e-10; measured 1.8e-11 pu over the
/// day and 7.9e-12 pu over the window). The reference uses an OpenDSS source of 1e13 MVA (a 1e9 MVA source leaves a
/// 5.5e-9 pu drop, the whole residual then), no transformer anti-float shunt (ppm_antifloat=0: its 1 ppm shunt moved bus 6
/// by about 5e-8 pu) and a solution tolerance of 1e-10 (the OpenDSS default 1e-4 left 2e-5 pu). One more window shows the
/// one control-semantics difference found, explained in its test.
/// </summary>
public sealed class QstsOpenDssCrossCheckTests(ITestOutputHelper output)
{
    private sealed record Row(int Step, long TimeS, double LoadMult, double[] V, double Tap, int Cap, int TapOps, int CapOps);

    private static (List<Row> Rows, string[] Header) Read(string file)
    {
        string path = Path.Combine(S10TestSupport.DataDir, "qsts", file);
        var rows = new List<Row>();
        var header = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#')) { header.Add(line); continue; }
            if (line.StartsWith("step", StringComparison.Ordinal)) continue;
            string[] c = line.Split(',');
            double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            rows.Add(new Row(int.Parse(c[0], CultureInfo.InvariantCulture), long.Parse(c[1], CultureInfo.InvariantCulture), D(2),
                new[] { D(4), D(5), D(6), D(7), D(8), D(9) }, D(10), (int)D(11), int.Parse(c[12], CultureInfo.InvariantCulture), (int)D(13)));
        }
        return (rows, header.ToArray());
    }

    private static GridModel Feeder() => QstsEngineTests.Feeder(target: 1.02, deadband: 0.01);

    private static QstsCapacitor Cap(double delay, double off = 0.97) => new("C6", 6, 0.3, 0, 1, 0, CapacitorControl.Voltage, 0.94, off, delay, delay);

    private (double maxDv, int tapMismatch, int capMismatch) Compare(List<Row> rows, QstsResult q, string label)
    {
        Assert.True(q.Completed, label + ": " + q.FailureReason);
        Assert.Equal(rows.Count, q.Steps.Count);
        double maxDv = 0; int tapMis = 0, capMis = 0; int worstStep = -1, worstBus = -1;
        for (int k = 0; k < rows.Count; k++)
        {
            Row r = rows[k];
            QstsStepRecord s = q.Steps[k];
            Assert.Equal(r.TimeS, s.TimeSeconds);
            if (Math.Abs(r.Tap - s.Taps[0]) > 1e-9) { tapMis++; if (tapMis <= 5) output.WriteLine($"{label} step {k}: tap OpenDSS {r.Tap} GridSim {s.Taps[0]}"); }
            if (r.Cap != s.CapacitorSteps[0]) { capMis++; if (capMis <= 5) output.WriteLine($"{label} step {k}: cap OpenDSS {r.Cap} GridSim {s.CapacitorSteps[0]}"); }
            for (int b = 0; b < 6; b++)
            {
                double dv = Math.Abs(r.V[b] - s.Vm![b]);
                if (dv > maxDv) { maxDv = dv; worstStep = k; worstBus = b + 1; }
            }
        }
        output.WriteLine($"{label}: {rows.Count} steps, max |dV| {maxDv:E3} pu (step {worstStep}, bus {worstBus}); tap mismatches {tapMis}, capacitor mismatches {capMis}; " +
            $"tap ops OpenDSS {rows[^1].TapOps} GridSim {q.TotalTapOperations}; capacitor ops OpenDSS {rows[^1].CapOps} GridSim {q.TotalCapacitorOperations}");
        return (maxDv, tapMis, capMis);
    }

    [Fact]
    public void Daily_static_run_matches_opendss_taps_capacitor_and_voltages()
    {
        (List<Row> rows, string[] header) = Read("opendss-feeder-static.csv");
        foreach (string h in header) output.WriteLine(h);
        var prof = new QstsProfile("load", 900, rows.Select(r => r.LoadMult).ToArray());
        var study = new QstsStudy
        {
            StepSeconds = 900, Steps = rows.Count, Profiles = new[] { prof }, Loads = new[] { new QstsLoadBinding(null, "load") },
            Capacitors = new[] { Cap(0) },
        };
        QstsResult q = new QstsEngine(new BackwardForwardSweep(), new PowerFlowOptions { Tolerance = 1e-10 }).Run(Feeder(), study);
        (double maxDv, int tapMis, int capMis) = Compare(rows, q, "static");
        Assert.Equal(0, tapMis);
        Assert.Equal(0, capMis);
        Assert.True(maxDv < 1e-10, $"max |dV| {maxDv}");
    }

    private QstsResult RunWindow(List<Row> rows, double capOff)
    {
        var prof = new QstsProfile("load", 1, rows.Select(r => r.LoadMult).ToArray());
        var study = new QstsStudy
        {
            StepSeconds = 1, Steps = rows.Count, Profiles = new[] { prof }, Loads = new[] { new QstsLoadBinding(null, "load") },
            Capacitors = new[] { Cap(60, capOff) }, OltcDefault = new OltcTiming(DelaySeconds: 30, InterTapSeconds: 5),
        };
        QstsResult q = new QstsEngine(new BackwardForwardSweep(), new PowerFlowOptions { Tolerance = 1e-10 }).Run(Feeder(), study);
        foreach (QstsOperation o in q.Operations) output.WriteLine($"GridSim op t={o.TimeSeconds} {o.Kind} {o.Device} {o.From}->{o.To} sensed {o.Sensed:F6}");
        return q;
    }

    [Fact]
    public void One_second_time_mode_window_matches_opendss_operation_times()
    {
        (List<Row> rows, string[] header) = Read("opendss-feeder-time.csv");
        foreach (string h in header) output.WriteLine(h);
        QstsResult q = RunWindow(rows, 0.97);
        (double maxDv, int tapMis, int capMis) = Compare(rows, q, "time");
        Assert.Equal(0, tapMis);
        Assert.Equal(0, capMis);
        Assert.Equal(new long[] { 30, 35, 90 }, q.Operations.Where(o => o.Kind == "oltc").Select(o => o.TimeSeconds));
        Assert.Equal(new long[] { 120, 660 }, q.Operations.Where(o => o.Kind == "capacitor").Select(o => o.TimeSeconds));
        Assert.True(maxDv < 1e-10, $"max |dV| {maxDv}");
    }

    [Fact]
    public void Opendss_times_a_bank_from_any_exit_of_its_band_the_explained_difference()
    {
        (List<Row> rows, string[] header) = Read("opendss-feeder-time-off096.csv");
        foreach (string h in header) output.WriteLine(h);
        QstsResult q = RunWindow(rows, 0.96);
        (_, int tapMis, _) = Compare(rows, q, "time-off096");
        // Tap changes: the same three operations at the same instants.
        Assert.Equal(0, tapMis);
        Assert.Equal(new long[] { 30, 35, 90 }, q.Operations.Where(o => o.Kind == "oltc").Select(o => o.TimeSeconds));
        // Capacitor: in at 120 s by GridSim and at 95 s by OpenDSS; out at 660 s by both. Explained difference in control
        // semantics (probed with delays of 20, 50, 60 and 70 s, which move OpenDSS's switching to 35 s + delay): OpenDSS
        // 0.14.5 starts the bank's delay at t = 35 s, when bus 6 rose above the OFF setting (0.96 pu) although the bank
        // was already open, and at the end of that delay closes it because the voltage is then below the ON setting.
        // GridSim starts the timer when the bank can act in the direction the voltage asks for: at t = 60 s (the load
        // step takes bus 6 below the ON setting), so it closes at 60 + 60 = 120 s. The states differ only in [95, 120).
        Assert.Equal(new long[] { 120, 660 }, q.Operations.Where(o => o.Kind == "capacitor").Select(o => o.TimeSeconds));
        Assert.Equal(rows[^1].CapOps, q.TotalCapacitorOperations);
        double maxDv = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            bool diffWindow = rows[k].TimeS >= 95 && rows[k].TimeS < 120;
            Assert.Equal(diffWindow ? 1 - rows[k].Cap : rows[k].Cap, q.Steps[k].CapacitorSteps[0]);
            if (diffWindow) continue;
            for (int b = 0; b < 6; b++) maxDv = Math.Max(maxDv, Math.Abs(rows[k].V[b] - q.Steps[k].Vm![b]));
        }
        output.WriteLine($"time-off096: max |dV| outside the capacitor window {maxDv:E3} pu");
        Assert.True(maxDv < 1e-10, $"max |dV| {maxDv}");
    }
}
