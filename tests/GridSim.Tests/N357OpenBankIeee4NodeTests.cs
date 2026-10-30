using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N357 (endpass-v, 2026-10-11): the open wye / open delta element ("oy-od", CF5 / N334) checked against a PUBLISHED
/// result for the first time. Reference: IEEE PES Distribution System Analysis Subcommittee, "IEEE 4 Node Test Feeder"
/// (Revised Sept. 19, 2006, R. C. Dugan secretary; feeder4.zip from the IEEE PES test feeder site), solution tables
/// "Step-Down with Balanced Loading" and "Step-Down with Unbalanced Loading", column "Open Gr.Y-D". The document gives the
/// open bank as two single-phase units each rated 2000 kVA, R 1 %, X 6 %, with the "Open Connection Load Data" (balanced
/// 1200 kW per phase at 0.9 pf lagging; unbalanced 850 kW at 0.85, 1200 kW at 0.9 and 1583.33 kW at 0.95 pf lagging),
/// the loads in closed delta on the three-wire line 3-4. Its table note: V1 = Vag for wye connections and Vab for delta
/// connections (so node 2, on the grounded primary side, is line to ground and nodes 3 and 4 are line to line).
///
/// The feeder is the bundled Y-D step-down file with two edits made in the test (no new fixture): the connection becomes
/// "oy-od" and the loads become the open-connection loads. The bank keeps "kva": 6000, which is the oy-od element's
/// convention: its impedance is given on three times the unit rating (each winding's Zw = 3 z on the line-to-neutral
/// base, TransformerWindings.OpenDeltaSeriesZ), i.e. on 3 x 2000 kVA. The rated voltages stay 12.47 / 4.16 kV (the
/// document's unit "7.2 kV" is the rounded 12.47 / sqrt 3 = 7.1996 kV; rating the units at exactly 7.2 kV was measured to
/// put four more values outside the half-digit bound, so the rounded figure is not taken literally).
///
/// Tolerance: half of the last printed digit, as in MultiPhaseJsonLoaderTests: 0.5 V for a value printed to 1 V
/// (0.05 V for the one value printed to 0.1 V, 3804.9), 0.05 degree for a value printed to 0.1 degree, and 0.05 A for a
/// current printed to 0.1 A. One of the 36 published values sits outside its bound and is pinned with its measured
/// deviation (never hidden; the MultiPhaseIeee123Tests rule), within 0.01 V and 0.001 degree, so a change in the element
/// shows.
///
/// The step-up open case of the same document is a DIFFERENT wiring (the higher-voltage side leads by 30 degrees and the
/// units feed secondaries ab and ca, not ab and bc): fed to the step-down element it was measured up to 2,669.2 V away from the
/// published line-to-line voltages, so the loader now refuses a step-up "oy-od" by name (see the last test). The IEEE
/// Wye-Delta Center Tapped Transformer Test Feeder (November 2004), the other public open-bank reference, needs a
/// centre-tapped 120 / 240 V four-wire secondary and an induction-motor load (unequal sequence impedances), which the
/// three-phase sweep does not model, so it is not pinned here.
/// </summary>
public class N357OpenBankIeee4NodeTests
{
    private static string FeederPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return Path.Combine(dir!.FullName, "data", "dist3ph", name);
    }

    private static string Text(string file) => File.ReadAllText(FeederPath(file)).Replace("\r\n", "\n");

    // The closed-connection loads as the bundled Y-D files carry them, and the open-connection loads of the IEEE document
    // (kvar = kW tan(acos pf), written out as literals the same way the bundled files are).
    private const string ClosedBalanced = "\"kw\": [1800, 1800, 1800], \"kvar\": [871.7797887081346, 871.7797887081346, 871.7797887081346]";
    private const string ClosedUnbalanced = "\"kw\": [1275, 1800, 2375], \"kvar\": [790.1740314639554, 871.7797887081346, 780.6247497998002]";
    private const string OpenBalanced = "\"kw\": [1200, 1200, 1200], \"kvar\": [581.186525805423, 581.186525805423, 581.186525805423]";
    private const string OpenUnbalanced = "\"kw\": [850, 1200, 1583.33], \"kvar\": [526.7826876426369, 581.186525805423, 520.4154042528495]";

    private static MultiPhaseFeeder OpenFeeder(bool balanced, string yDFile)
    {
        string text = Text(yDFile);
        string closed = balanced ? ClosedBalanced : ClosedUnbalanced;
        Assert.Contains(closed, text, StringComparison.Ordinal);
        Assert.Contains("\"connection\": \"y-d\"", text, StringComparison.Ordinal);
        string open = text.Replace("\"connection\": \"y-d\"", "\"connection\": \"oy-od\"", StringComparison.Ordinal)
            .Replace(closed, balanced ? OpenBalanced : OpenUnbalanced, StringComparison.Ordinal);
        return MultiPhaseJsonLoader.Parse(open, "ieee4-open-gry-d.json");
    }

    // Published "Open Gr.Y-D" node voltages: (bus, line to line?, volts, degrees, volt tolerance per phase).
    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg, double[] VTol)[] PublishedBalanced =
    {
        (2, false, new[] { 6984.0, 7167.0, 7293.0 }, new[] { 0.4, -121.7, 120.5 }, new[] { 0.5, 0.5, 0.5 }),
        (3, true, new[] { 3701.0, 4076.0, 3572.0 }, new[] { -0.9, -126.5, 110.9 }, new[] { 0.5, 0.5, 0.5 }),
        (4, true, new[] { 3384.0, 3804.9, 3246.0 }, new[] { -3.5, -130.2, 106.5 }, new[] { 0.5, 0.05, 0.5 }),
    };

    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg, double[] VTol)[] PublishedUnbalanced =
    {
        (2, false, new[] { 6952.0, 7172.0, 7313.0 }, new[] { 0.7, -122.0, 120.5 }, new[] { 0.5, 0.5, 0.5 }),
        (3, true, new[] { 3632.0, 4121.0, 3450.0 }, new[] { 0.1, -127.6, 108.9 }, new[] { 0.5, 0.5, 0.5 }),
        (4, true, new[] { 3307.0, 3907.0, 3073.0 }, new[] { -1.5, -131.9, 103.1 }, new[] { 0.5, 0.5, 0.5 }),
    };

    // Published "Open Gr.Y-D" line currents (amps, degrees): line 1-2 phases a and b (phase c carries nothing and is not
    // printed), line 3-4 phases a, b and c.
    private static readonly (int From, int To, double[] A, double[] Deg)[] CurrentsBalanced =
    {
        (1, 2, new[] { 380.9, 387.4 }, new[] { -65.2, -125.2 }),
        (3, 4, new[] { 659.3, 665.7, 670.5 }, new[] { -65.2, 175.6, 54.8 }),
    };

    private static readonly (int From, int To, double[] A, double[] Deg)[] CurrentsUnbalanced =
    {
        (1, 2, new[] { 424.8, 440.3 }, new[] { -73.8, -118.5 }),
        (3, 4, new[] { 735.2, 569.9, 762.0 }, new[] { -73.8, 176.3, 61.5 }),
    };

    // Measured deviations (GridSim minus published) of the values outside the half-digit bound, measured 2026-10-11,
    // Debug, Windows x64: (balanced?, bus, phase, dV volts, dAngle degrees).
    private static readonly (bool Balanced, int Bus, int Phase, double DVolts, double DDeg)[] Residuals =
    {
        (true, 2, 0, -0.0441, 0.0577),
    };

    [Theory]
    [InlineData(true, "ieee4-yD-stepdown-balanced.json")]
    [InlineData(false, "ieee4-yD-stepdown-unbalanced.json")]
    public void The_open_wye_open_delta_bank_matches_the_IEEE_4_node_published_open_GrY_D_step_down_results(bool balanced, string yDFile)
    {
        MultiPhaseFeeder f = OpenFeeder(balanced, yDFile);
        PhaseBranch bank = f.Model.Branches.Single(b => b.IsTransformerElement);
        Assert.True(bank.OpenWyeOpenDelta);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"sweep status {r.Status} after {r.Iterations} iterations");
        var failures = new List<string>();
        foreach ((int bus, bool ll, double[] vPub, double[] degPub, double[] vTol) in balanced ? PublishedBalanced : PublishedUnbalanced)
        {
            Complex[] v = r.V[r.BusIds.ToList().IndexOf(bus)];
            Complex[] shown = ll ? new[] { v[0] - v[1], v[1] - v[2], v[2] - v[0] } : v;
            for (int p = 0; p < 3; p++)
            {
                double volts = shown[p].Magnitude * f.VoltBaseLN(bus);
                double deg = shown[p].Phase * 180.0 / Math.PI;
                double dv = volts - vPub[p];
                double dd = Math.IEEERemainder(deg - degPub[p], 360.0);
                var pinned = Residuals.Where(x => x.Balanced == balanced && x.Bus == bus && x.Phase == p).ToList();
                bool ok = pinned.Count == 1
                    ? Math.Abs(dv - pinned[0].DVolts) <= 0.01 && Math.Abs(dd - pinned[0].DDeg) <= 0.001
                    : Math.Abs(dv) <= vTol[p] && Math.Abs(dd) <= 0.05;
                if (!ok) failures.Add($"bus {bus} phase {"abc"[p]}: {volts:F4} V at {deg:F5} deg vs published {vPub[p]} V at {degPub[p]} deg (dV {dv:F4} V, dAngle {dd:F5} deg)");
            }
        }
        foreach ((int from, int to, double[] aPub, double[] degPub) in balanced ? CurrentsBalanced : CurrentsUnbalanced)
        {
            int k = f.Model.Branches.ToList().FindIndex(b => b.From == from && b.To == to && !b.IsTransformerElement);
            Assert.True(k >= 0, $"line {from}-{to} missing");
            Complex[] i = r.BranchCurrents![k];
            double iBase = f.Model.BaseMva * 1000.0 / (Math.Sqrt(3.0) * f.BaseKvLLByBus[from]);
            for (int p = 0; p < aPub.Length; p++)
            {
                double amps = i[p].Magnitude * iBase;
                double deg = i[p].Phase * 180.0 / Math.PI;
                double da = amps - aPub[p];
                double dd = Math.IEEERemainder(deg - degPub[p], 360.0);
                if (!(Math.Abs(da) <= 0.05 && Math.Abs(dd) <= 0.05))
                    failures.Add($"line {from}-{to} phase {"abc"[p]}: {amps:F4} A at {deg:F5} deg vs published {aPub[p]} A at {degPub[p]} deg (dI {da:F4} A, dAngle {dd:F5} deg)");
            }
            if (from == 1)
            {
                // The open wye primary uses phases A and B only: line 1-2 phase c carries no current at all.
                Assert.True(i[2].Magnitude <= 1e-12, $"line 1-2 phase c current {i[2].Magnitude} pu");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData("ieee4-yD-stepup-balanced.json")]
    [InlineData("ieee4-yD-stepup-unbalanced.json")]
    public void A_step_up_open_wye_open_delta_is_refused_by_name(string yDFile)
    {
        string text = Text(yDFile);
        Assert.Contains("\"kvLLFrom\": 12.47, \"kvLLTo\": 24.9", text, StringComparison.Ordinal);
        string open = text.Replace("\"connection\": \"y-d\"", "\"connection\": \"oy-od\"", StringComparison.Ordinal);
        Assert.NotEqual(text, open);
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(open, "stepup-open.json"));
        Assert.Contains("step-down connection only", ex.Message, StringComparison.Ordinal);
        Assert.Contains("12.47 kV to 24.9 kV", ex.Message, StringComparison.Ordinal);
        // The open delta / open delta has no phase shift and stays accepted at a step-up rating.
        MultiPhaseFeeder od = MultiPhaseJsonLoader.Parse(text.Replace("\"connection\": \"y-d\"", "\"connection\": \"od-od\"", StringComparison.Ordinal), "stepup-odod.json");
        Assert.True(od.Model.Branches.Single(b => b.IsTransformerElement).OpenDeltaOpenDelta);
    }
}
