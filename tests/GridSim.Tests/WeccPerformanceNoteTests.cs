using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// Performance note for the WECC renewable models: the RMS step cost each renewable unit adds on IEEE 118 (54 units), with
/// N of its generating units (the largest by output) given a REGC_A + REEC_A chain or a REGC_B + REEC_D chain. It times
/// each run with a Stopwatch and prints the numbers; nothing is asserted about time (machine dependent). It asserts that
/// every run converges at every step.
/// </summary>
public class WeccPerformanceNoteTests
{
    private readonly ITestOutputHelper _out;
    public WeccPerformanceNoteTests(ITestOutputHelper output) => _out = output;

    private static WeccPlant Gfl() => new(new RegcaParameters { Lvplsw = 0, Accel = 0.0 },
        new ReecParameters { VFlag = 0, QFlag = 0, Vdip = 0.8, Vup = 1.2, Qmax = 1.0, Qmin = -1.0, Imax = 1.3 });

    private static WeccPlant Gfv() => new(null)
    {
        RegcB = new RegcbParameters { Tg = 0.02, Rrpwr = 10.0, Imax = 1.3, Te = 0.02, Re = 0.0025, Xe = 0.2 },
        ReecD = new ReecdParameters { Vdip = 0.8, Vup = 1.2, Qvmax = 1.0, Qvmin = -1.0, Imax = 1.3, Vblkl = 0.2, Vblkh = 1.3,
            Vq = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Iq = Enumerable.Repeat(1.3, 10).ToArray(),
            Vp = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Ip = Enumerable.Repeat(1.3, 10).ToArray() },
    };

    [Fact]
    public void Rms_step_cost_per_renewable_unit_on_ieee118()
    {
        GridModel m0 = IeeeCases.Case118();
        PowerFlowResult pf0 = new NewtonRaphsonPowerFlow().Solve(m0);
        Assert.True(pf0.Converged);
        // The units to convert: the largest generating units (not the slack), with MBASE = 2 max(P, |Q|, 10 MW) so the start is
        // well inside every current limit.
        var busIds = pf0.BusIds.ToList();
        int slackBus = m0.Buses.Single(b => b.Type == BusType.Slack).Id;
        List<int> order = Enumerable.Range(0, m0.Generators.Count).Where(k => m0.Generators[k].Bus != slackBus && m0.Generators[k].Pg > 1.0)
            .OrderByDescending(k => m0.Generators[k].Pg).ThenBy(k => k).ToList();
        const double dt = 0.005, seconds = 2.0;
        int steps = (int)Math.Round(seconds / dt);
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"IEEE 118, {m0.Generators.Count} units, {steps} steps of {dt} s, 100 ms bolted fault at bus 30 from 0.1 s; best of 3 runs (Debug build)"));
        double baseMs = 0.0;
        var failures = new List<(string F, int N, int Count)>();
        foreach ((string family, Func<WeccPlant> plant) in new (string, Func<WeccPlant>)[] { ("REGC_A+REEC_A", Gfl), ("REGC_B+REEC_D", Gfv) })
            foreach (int n in new[] { 0, 4, 8, 16 })
            {
                if (family == "REGC_B+REEC_D" && n == 0) continue;
                var conv = new HashSet<int>(order.Take(n));
                var gens = m0.Generators.Select((g, k) => conv.Contains(k)
                    ? g with { MBase = 2.0 * Math.Max(Math.Max(g.Pg, Math.Abs(g.Qg)), 10.0) } : g).ToList();
                GridModel m = m0.CopyWith(generators: gens);
                PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
                var data = m.Generators.Select((g, k) =>
                {
                    var d = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
                    return conv.Contains(k) ? d with { Renewable = plant() } : d;
                }).ToList();
                double best = double.PositiveInfinity;
                MultiMachineSubtransient.Result? r = null;
                for (int rep = 0; rep < 3; rep++)
                {
                    var sw = Stopwatch.StartNew();
                    r = RmsTransientStability.Study(m, pf, data, 30, 0.1, 0.1, stepSeconds: dt, durationSeconds: seconds);
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                failures.Add((family, n, r!.ConverterIterationFailures));
                if (n == 0) baseMs = best;
                double perStep = best / steps * 1000.0;   // microseconds
                double perUnit = n == 0 ? 0.0 : (best - baseMs) / steps / n * 1000.0;
                sb.AppendLine(FormattableString.Invariant($"{family,-14} N {n,2}: {best,9:F1} ms  {perStep,8:F1} us/step  added per unit {perUnit,7:F2} us/step  iteration failures {r.ConverterIterationFailures}, rescued steps {r.ConverterRescuedSteps}"));
            }
        _out.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gridsim-wecc-perf.txt"), sb.ToString());
        Assert.All(failures, f => Assert.True(f.Count == 0, $"{f.F} N {f.N}: {f.Count} steps hit the iteration cap"));
    }
}
