using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Hvdc;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-19: the partitioned parallel EMT run. The oracle is the incumbent single-partition solver (NodalSolver on the same model built as one
/// network): the partitioned run must equal it bit for bit, and equal itself at every thread count.
/// </summary>
public sealed class R419PartitionTests(ITestOutputHelper output)
{
    private const double Dt = 5e-5;

    // ---- the test network: four areas in a ring joined by Bergeron lines, one with a thyristor bridge, one a VSC station ----

    private static PartitionedModel BuildRing(bool single, double dt, out List<NodalProbe> probes, out List<NodalEvent> events)
    {
        string[] names = ["A0", "A1", "A2", "A3"];
        PartitionedModel m = PartitionedModel.Create(names, single);
        double w = 2.0 * Math.PI * 50.0;
        probes = new List<NodalProbe>();
        events = new List<NodalEvent>();
        for (int p = 0; p < 4; p++)
        {
            string a = names[p];
            if (p == 3)
            {
                // A VSC station (matrix-varying converter, ideal sources, controllers): its PCC nodes are the area's bus nodes.
                var spec = new VscStationSpec
                {
                    GridPeakVolts = 8000.0, FrequencyHz = 50.0, GridAngleRad = 0.0, GridROhm = 0.05, GridLHenry = 1e-3, FilterROhm = 0.05, FilterLHenry = 8e-3,
                    ConverterRsOhm = 0.01, DcNominalVolts = 20000.0, DcCapF = 5e-4, Converter = VscStationConverter.TwoLevelAverage, Mode = VscStationMode.DcVoltage,
                    SetPoint = 20000.0, CurrentBandwidthRadPerS = 1000.0, OuterBandwidthRadPerS = 60.0, ControlEvery = 2,
                };
                VscHvdcStation.Add(m.Net(a), "S3", a + "/", spec, dt);
                // the station supplies a DC bus load so the loop has something to do
                int dcp = m.Node(a, "dc.p"), dcn = m.Node(a, "dc.n");
                m.Add(a, NodalBranch.Resistor("S3.dcload", dcp, dcn, 60.0));
                for (int k = 0; k < 3; k++)
                {
                    probes.Add(new NodalProbe($"{a}.v{k}", NodalProbeKind.NodeVoltage, $"{a}/pcc{k}"));
                    probes.Add(new NodalProbe($"{a}.if{k}", NodalProbeKind.ElementCurrent, $"S3.f{k}"));
                }
                probes.Add(new NodalProbe($"{a}.vdc", NodalProbeKind.NodeVoltage, $"{a}/dc.p"));
                continue;
            }
            for (int k = 0; k < 3; k++)
            {
                int bus = m.Node(a, $"bus{k}");
                double ph = -k * 2.0 * Math.PI / 3.0, amp = 9000.0 + 500.0 * p;
                m.Add(a, new NodalVoltageSource($"{a}.e{k}", bus, 0.5, t => amp * DetMath.Sin(w * t + ph)));
                m.Add(a, NodalBranch.SeriesRl($"{a}.load{k}", bus, NodalNetwork.Ground, 5.0 + p, 0.02));
                m.Add(a, NodalBranch.Capacitor($"{a}.c{k}", bus, NodalNetwork.Ground, 2e-6));
                probes.Add(new NodalProbe($"{a}.v{k}", NodalProbeKind.NodeVoltage, $"{a}/bus{k}"));
            }
            if (p == 2)
            {
                // a thyristor bridge (switch logic) behind commutation reactors, DC load with smoothing
                int[] ac = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    ac[k] = m.Node(a, $"br{k}");
                    m.Add(a, NodalBranch.SeriesRl($"{a}.lc{k}", m.Node(a, $"bus{k}"), ac[k], 0.01, 2e-3));
                }
                int dcp = m.Node(a, "dcp"), dcm = m.Node(a, "dcm");
                TwoLevelBridge.Add(m.Net(a), $"{a}.br", dcp, NodalNetwork.Ground, ac, ValveKind.Thyristor, new ValveParameters(1e-3, 1e7, 50.0, 1e-6),
                    ThyristorFiring.Bridge(50.0, -Math.PI / 2.0, 0.5), "test bridge");
                m.Add(a, NodalBranch.SeriesRl($"{a}.dcl", dcp, dcm, 1.0, 0.05));
                m.Add(a, NodalBranch.Resistor($"{a}.dcr", dcm, NodalNetwork.Ground, 4.0));
                probes.Add(new NodalProbe($"{a}.id", NodalProbeKind.ElementCurrent, $"{a}.dcl"));
            }
            // a fault switch on phase a of this area
            m.Add(a, new NodalSwitch($"{a}.fault", m.Node(a, "bus0"), NodalNetwork.Ground, 1e-3, 1e9, false));
            probes.Add(new NodalProbe($"{a}.if", NodalProbeKind.ElementCurrent, $"{a}.fault"));
            events.Add(new NodalEvent(0.0123 + 0.003 * p, $"{a}.fault", true));
            events.Add(new NodalEvent(0.0273 + 0.003 * p, $"{a}.fault", false));
        }
        // ring of lines, three phases each: one with a travel time that is not a whole number of steps (interpolated)
        double[] tau = [3.0e-4, 4.1e-4, 5.0e-4, 3.5e-4];
        for (int p = 0; p < 4; p++)
        {
            int q = (p + 1) % 4;
            for (int k = 0; k < 3; k++)
            {
                string na = p == 3 ? $"pcc{k}" : $"bus{k}", nb = q == 3 ? $"pcc{k}" : $"bus{k}";
                m.AddLine($"L{p}{q}.{k}", names[p], na, names[q], nb, 350.0, tau[p], 0.999);
            }
        }
        return m;
    }

    private static ulong Hash(NodalResult r)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long bits) { for (int i = 0; i < 8; i++) { h ^= (ulong)((bits >> (8 * i)) & 0xff); h *= 1099511628211UL; } }
        foreach (double[] s in r.Series) foreach (double x in s) Mix(BitConverter.DoubleToInt64Bits(x));
        foreach (double x in r.Times) Mix(BitConverter.DoubleToInt64Bits(x));
        Mix(r.Stats.Steps);
        return h;
    }

    private static int FirstDifference(NodalResult a, NodalResult b)
    {
        if (a.Series.Length != b.Series.Length) return -2;
        for (int k = 0; k < a.Series.Length; k++)
        {
            if (a.Series[k].Length != b.Series[k].Length) return k;
            for (int i = 0; i < a.Series[k].Length; i++)
                if (BitConverter.DoubleToInt64Bits(a.Series[k][i]) != BitConverter.DoubleToInt64Bits(b.Series[k][i])) return k;
        }
        return -1;
    }

    [Fact]
    public void Plan_cuts_only_at_lines_and_states_the_window()
    {
        PartitionedModel m = BuildRing(false, Dt, out _, out _);
        PartitionPlan plan = LinePartitioner.Plan(m, Dt);
        output.WriteLine(plan.Description);
        Assert.Equal(4, plan.Partitions);
        Assert.Equal(12, plan.CutLines.Count);
        Assert.Equal(6, plan.WindowSteps);              // 3.0e-4 / 5e-5 = 6 steps is the shortest travel time
        // the monolithic form has no cut
        PartitionedModel one = BuildRing(true, Dt, out _, out _);
        Assert.Equal(1, LinePartitioner.Plan(one, Dt).Partitions);
        Assert.Empty(LinePartitioner.Plan(one, Dt).CutLines);
    }

    [Fact]
    public void A_cut_line_shorter_than_the_step_is_refused()
    {
        PartitionedModel m = PartitionedModel.Create(["P", "Q"], false);
        int a = m.Node("P", "a"), b = m.Node("Q", "b");
        m.Add("P", NodalBranch.Resistor("RP", a, NodalNetwork.Ground, 100.0));
        m.Add("Q", NodalBranch.Resistor("RQ", b, NodalNetwork.Ground, 100.0));
        m.AddLine("L", "P", "a", "Q", "b", 300.0, 2e-5);
        var ex = Assert.Throws<ArgumentException>(() => PartitionedEngine.Run(m, 5e-5, 1e-3, [new NodalProbe("v", NodalProbeKind.NodeVoltage, "P/a")]));
        Assert.Contains("shorter than the step", ex.Message);
    }

    [Fact]
    public void Newton_elements_and_the_backward_Euler_event_step_are_refused_for_partitioned_runs()
    {
        PartitionedModel m = PartitionedModel.Create(["P"], true);
        int a = m.Node("P", "a");
        m.Add("P", NodalBranch.Resistor("R", a, NodalNetwork.Ground, 100.0));
        var probe = new[] { new NodalProbe("v", NodalProbeKind.NodeVoltage, "P/a") };
        var ex = Assert.Throws<ArgumentException>(() => PartitionedEngine.Run(m, 5e-5, 1e-3, probe, null,
            new PartitionedOptions { Nodal = new NodalOptions { EventRule = NodalEventRule.BackwardEulerStep } }));
        Assert.Contains("trapezoidal event rule", ex.Message);
    }

    [Fact]
    public void Bergeron_line_matches_the_closed_form_square_wave_and_the_matched_plateau()
    {
        // Ideal source E behind a lossless line, open far end: the far end sees 0 until tau, then 2E for 2 tau, then 0 for 2 tau ...
        const double E = 1000.0, Tau = 5e-4, Zc = 300.0, dt = 5e-5;
        PartitionedModel m = PartitionedModel.Create(["S", "R"], false);
        int s = m.Node("S", "s"), r = m.Node("R", "r");
        m.Add("S", new NodalVoltageSource("E", s, 0.0, _ => E));
        m.AddLine("L", "S", "s", "R", "r", Zc, Tau);
        m.Add("R", NodalBranch.Resistor("Rleak", r, NodalNetwork.Ground, 1e15));
        PartitionedResult res = PartitionedEngine.Run(m, dt, 4.2e-3, [new NodalProbe("vr", NodalProbeKind.NodeVoltage, "R/r")]);
        double[] v = res.Run.Series[0];
        int q = 10;
        Assert.Equal(0.0, v[q - 1], 6);                 // before the wave arrives (sample index = step)
        Assert.Equal(2.0 * E, v[q + 3], 6);
        Assert.Equal(2.0 * E, v[3 * q - 2], 6);
        Assert.Equal(0.0, v[3 * q + 3], 6);
        Assert.Equal(0.0, v[5 * q - 2], 6);
        Assert.Equal(2.0 * E, v[5 * q + 3], 6);
        output.WriteLine($"open end: v = {v[q + 3]:R} V (2E = {2 * E}); next window {v[3 * q + 3]:R}");

        // Matched far end (R = Zc): E arrives at tau and stays.
        PartitionedModel m2 = PartitionedModel.Create(["S", "R"], false);
        m2.Add("S", new NodalVoltageSource("E", m2.Node("S", "s"), 0.0, _ => E));
        m2.AddLine("L", "S", "s", "R", "r", Zc, Tau);
        m2.Add("R", NodalBranch.Resistor("Rm", m2.Node("R", "r"), NodalNetwork.Ground, Zc));
        PartitionedResult res2 = PartitionedEngine.Run(m2, dt, 4.2e-3, [new NodalProbe("vr", NodalProbeKind.NodeVoltage, "R/r")]);
        Assert.Equal(0.0, res2.Run.Series[0][q - 1], 6);
        Assert.Equal(E, res2.Run.Series[0][q + 3], 6);
        Assert.Equal(E, res2.Run.Series[0][4 * q], 6);
    }

    [Fact]
    public void Partitioned_run_is_bit_identical_to_the_single_partition_incumbent_and_across_thread_counts()
    {
        const double Duration = 0.06;
        PartitionedModel mono = BuildRing(true, Dt, out List<NodalProbe> probes, out List<NodalEvent> events);
        NodalNetwork net = mono.Net("A0");
        NodalResult incumbent = NodalSolver.Run(net, Dt, Duration, probes, events);
        Assert.Equal(NodalStatus.Completed, incumbent.Status);
        ulong hInc = Hash(incumbent);
        output.WriteLine($"incumbent single-partition hash {hInc}, {incumbent.Times.Length} samples, {probes.Count} probes");

        // single partition through the engine (checks the engine's copy of the loop against the incumbent)
        PartitionedResult one = PartitionedEngine.Run(BuildRing(true, Dt, out _, out _), Dt, Duration, probes, events);
        Assert.Equal(-1, FirstDifference(incumbent, one.Run));
        Assert.Equal(hInc, Hash(one.Run));

        var hashes = new List<ulong>();
        foreach (int threads in new[] { 1, 2, 3, 4, 16 })
        {
            PartitionedResult part = PartitionedEngine.Run(BuildRing(false, Dt, out _, out _), Dt, Duration, probes, events, new PartitionedOptions { Threads = threads });
            Assert.Equal(NodalStatus.Completed, part.Run.Status);
            int diff = FirstDifference(incumbent, part.Run);
            output.WriteLine($"4 partitions, {threads} thread(s) (used {part.Threads}): first differing probe {diff}, hash {Hash(part.Run)}, plan: {part.Plan.Description}");
            Assert.Equal(-1, diff);
            hashes.Add(Hash(part.Run));
        }
        Assert.All(hashes, h => Assert.Equal(hInc, h));
        // The scenario is not trivial: faults happened, the bridge conducted, the station regulated.
        int fi = probes.FindIndex(p => p.Name == "A1.if");
        Assert.True(incumbent.Series[fi].Max() > 100.0, "fault current flowed");
        int di = probes.FindIndex(p => p.Name == "A2.id");
        Assert.True(incumbent.Series[di].Max() > 10.0, "bridge DC current flowed");
    }

    [Fact]
    public void A_shorter_run_is_a_bit_identical_prefix_of_a_longer_one_at_different_thread_counts()
    {
        PartitionedModel m1 = BuildRing(false, Dt, out List<NodalProbe> probes, out List<NodalEvent> events);
        PartitionedResult a = PartitionedEngine.Run(m1, Dt, 0.03, probes, events, new PartitionedOptions { Threads = 4 });
        PartitionedResult b = PartitionedEngine.Run(BuildRing(false, Dt, out _, out _), Dt, 0.02, probes, events, new PartitionedOptions { Threads = 2 });
        for (int k = 0; k < probes.Count; k++)
            for (int i = 0; i < b.Run.Series[k].Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Run.Series[k][i]), BitConverter.DoubleToInt64Bits(b.Run.Series[k][i]));
    }
}
