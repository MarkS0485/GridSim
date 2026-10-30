using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Out-of-phase synchronisation fault: a synchronous machine and a grid-forming inverter, each an ideal EMF
/// behind its own series R-L, are joined by a breaker that closes at 180-degrees phase opposition, at a
/// machine-voltage zero crossing (worst-case DC offset). After the breaker closes the two internal EMFs form a
/// single series R-L loop across their DIFFERENCE voltage Vd(t)=Vm*cos(wt)-Vm*cos(wt+pi)=2*Vm*cos(wt); the loop
/// fault current has the textbook AC + decaying-DC form. The analytic current here is an INDEPENDENT oracle
/// (closed-form series-RL response to a cosine forcing with a zero initial condition at the closing instant),
/// not the production code path, so agreement to &lt;1e-5 is real validation that the Dommel engine solves the
/// switched electromagnetic loop correctly. Everything is per-unit on base impedance = 1.
/// </summary>
public class EmtOutOfPhaseSyncTests
{
    // Normalized per-unit system.
    private const double Vm = 1.0;                       // peak EMF, pu
    private const double W = 2.0 * Math.PI * 50.0;       // 50 Hz
    private const double XdPu = 0.20;                    // machine subtransient reactance, pu
    private const double XinvPu = 0.10;                  // inverter effective reactance, pu
    private const double XoverR = 20.0;                  // X/R of both arms
    private const double LMach = XdPu / W;               // henries (pu-seconds)
    private const double RMach = W * LMach / XoverR;     // = Xd/(w*X/R)  -> R = X/(X/R)
    private const double LInv = XinvPu / W;
    private const double RInv = W * LInv / XoverR;
    private const double Ron = 1e-6;
    private const double Dt = 1e-6;
    private const double Duration = 0.06;                // three 50 Hz cycles
    private const double T0 = 0.005;                     // machine-voltage zero crossing (quarter cycle) -> max DC offset

    // Loop totals after the breaker closes. The closed breaker's on-resistance Ron sits in series in the loop;
    // although only ~7e-5 of Rtot, at X/R=20 it measurably shifts the DC-offset decay time constant over the
    // 60 ms window (it contributes ~1.8e-4 pu to the loop current versus omitting it), so a faithful analytic
    // oracle must include it. It is negligible for the reported AC magnitude and peak.
    private const double Rtot = RMach + RInv + Ron;
    private const double Ltot = LMach + LInv;

    // Element ids (arbitrary but distinct).
    private const int IdSrcMach = 100;
    private const int IdBrMach = 1;                      // probe target (loop current)
    private const int IdSwitch = 11;
    private const int IdBrInv = 2;
    private const int IdSrcInv = 200;

    private static EmtNetwork BuildNet()
    {
        var elements = new List<EmtElement>
        {
            // Node 0 = machine internal EMF (ideal source, Dirichlet).
            new VoltageSource(IdSrcMach, Bus: 0, Rs: 0, Emf: t => Vm * Math.Cos(W * t)),
            // node0 -> node1 machine R-L arm.
            new SeriesBranch(IdBrMach, From: 0, To: 1, R: RMach, L: LMach),
            // Breaker node1 -> node2, initially open.
            new SwitchElement(IdSwitch, From: 1, To: 2, Ron: Ron, Roff: 1e9, InitiallyClosed: false),
            // node2 -> node3 inverter R-L arm.
            new SeriesBranch(IdBrInv, From: 2, To: 3, R: RInv, L: LInv),
            // Node 3 = inverter internal EMF, 180 deg opposition (ideal source, Dirichlet).
            new VoltageSource(IdSrcInv, Bus: 3, Rs: 0, Emf: t => Vm * Math.Cos(W * t + Math.PI)),
        };
        // 4 nodes; both ideal sources pin absolute voltages, so no ground shunt is needed.
        return new EmtNetwork(nodeCount: 4, elements);
    }

    private static EmtResult Run()
    {
        EmtNetwork net = BuildNet();
        var probes = new[] { new EmtProbe("i_loop", EmtProbeKind.BranchCurrent, Target: IdBrMach) };
        var events = new[] { new SwitchEvent(T0, IdSwitch, Close: true) };
        return new EmtEngine(net, Dt).Simulate(new EmtCase(net, Dt, Duration, probes, events));
    }

    /// <summary>Closed-form series-R-L loop current across the difference EMF Vd(t)=2*Vm*cos(wt), valid for
    /// t &gt;= t0, with i(t0)=0 (breaker closes onto a de-energised loop): a steady-state sinusoid plus the
    /// decaying-DC term that enforces the zero initial condition.</summary>
    private static double AnalyticCurrent(double t)
    {
        double ztot = Math.Sqrt(Rtot * Rtot + (W * Ltot) * (W * Ltot));
        double phi = Math.Atan2(W * Ltot, Rtot);
        double tau = Ltot / Rtot;
        double amp = 2.0 * Vm / ztot;
        return amp * (Math.Cos(W * t - phi) - Math.Cos(W * T0 - phi) * Math.Exp(-(t - T0) / tau));
    }

    [Fact]
    public void Loop_fault_current_matches_closed_form_to_1e_minus_5()
    {
        EmtResult res = Run();
        double maxErr = 0.0;
        foreach (EmtPoint p in res.Points)
            if (p.TSeconds >= T0)
                maxErr = Math.Max(maxErr, Math.Abs(p.Probe[0] - AnalyticCurrent(p.TSeconds)));
        Assert.True(maxErr < 1e-5, $"max |i_emt - i_analytic| = {maxErr:E3} exceeded tolerance");
    }

    [Fact]
    public void Run_is_deterministic()
    {
        EmtResult a = Run();
        EmtResult b = Run();
        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int i = 0; i < a.Points.Count; i++)
            Assert.Equal(a.Points[i].Probe[0], b.Points[i].Probe[0], 12);
    }

    [Fact]
    public void Emit_csv_and_report_figures()
    {
        EmtResult res = Run();

        // Peak instantaneous |i| (includes the DC-offset overshoot) and its instant.
        double peakAbs = 0.0, peakT = 0.0, peakSigned = 0.0;
        foreach (EmtPoint p in res.Points)
        {
            double a = Math.Abs(p.Probe[0]);
            if (a > peakAbs) { peakAbs = a; peakSigned = p.Probe[0]; peakT = p.TSeconds; }
        }

        double ztot = Math.Sqrt(Rtot * Rtot + (W * Ltot) * (W * Ltot));
        double acMag = 2.0 * Vm / ztot;                 // subtransient AC magnitude ~= 2/(Xd+Xinv)
        double xRatedPeak = peakAbs / acMag;

        // Recompute the gate error for the report.
        double maxErr = 0.0;
        foreach (EmtPoint p in res.Points)
            if (p.TSeconds >= T0)
                maxErr = Math.Max(maxErr, Math.Abs(p.Probe[0] - AnalyticCurrent(p.TSeconds)));

        // --- CSV the report cites: header t_us,i_pu, one row per step, decimated to <~5000 rows but the exact
        //     peak row is always included. A test run writes only into its own fresh temp folder (removed at the
        //     end): it used to default to another repository's supporting-docs folder and rewrite a file a published
        //     paper cites on every run. Regenerating that artefact is a deliberate act: set EMT_REPORT_DIR to the
        //     folder you mean, and the CSV is written there (and kept). A folder that cannot be created fails loud. ---
        string? reportDir = Environment.GetEnvironmentVariable("EMT_REPORT_DIR");
        string dir = reportDir ?? Path.Combine(Path.GetTempPath(), $"gridsim-emt-oop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string csvPath = Path.Combine(dir, "0026-emt-out-of-phase.csv");

        int n = res.Points.Count;
        int stride = Math.Max(1, (int)Math.Ceiling(n / 5000.0));
        var sb = new StringBuilder();
        sb.AppendLine("t_us,i_pu");
        for (int i = 0; i < n; i++)
        {
            bool isPeak = Math.Abs(res.Points[i].Probe[0]) == peakAbs;
            if (i % stride != 0 && !isPeak) continue;
            double tUs = res.Points[i].TSeconds * 1e6;
            sb.Append(tUs.ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.AppendLine(res.Points[i].Probe[0].ToString("R", CultureInfo.InvariantCulture));
        }
        File.WriteAllText(csvPath, sb.ToString());

        // --- Summary figures for the harness/report (scratchpad, outside the repo). ---
        string summary =
            $"peakInstantaneousPu={peakSigned.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"peakAbsPu={peakAbs.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"peakInstantSeconds={peakT.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"subtransientAcMagnitudePu={acMag.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"xRatedPeak={xRatedPeak.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"gateError={maxErr.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"ztotPu={ztot.ToString("R", CultureInfo.InvariantCulture)}\n" +
            $"points={n}\ncsvRows={sb.ToString().Split('\n').Length - 2}\ncsvPath={csvPath}\n";
        string sumPath = Path.Combine(Environment.GetEnvironmentVariable("EMT_SUMMARY_DIR") ?? dir, "emt_out_of_phase_summary.txt");
        File.WriteAllText(sumPath, summary);

        try
        {
            Assert.True(File.Exists(csvPath));
            // The CSV holds its header, the decimated rows and the exact peak row.
            string[] rows = File.ReadAllLines(csvPath);
            Assert.Equal("t_us,i_pu", rows[0]);
            Assert.True(rows.Length - 1 >= Math.Min(n, 5000) / 2, $"only {rows.Length - 1} data rows");
            Assert.Contains(rows, r => r.EndsWith("," + peakSigned.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Assert.True(peakAbs > acMag);                   // DC offset makes the first peak exceed the AC magnitude
            Assert.True(maxErr < 1e-5);
        }
        finally
        {
            // Only the run's own temp folder is removed; an explicit EMT_REPORT_DIR keeps its artefact.
            if (reportDir is null)
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
