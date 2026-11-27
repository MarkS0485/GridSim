using System.Numerics;
using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 (last candidate): the open-conductor (series) and simultaneous shunt fault verbs (<c>fault-series</c>, <c>fault-simultaneous</c>) and
/// the Core network side they stand on (<see cref="FaultAnalysis.SeriesBreak"/>, <see cref="FaultAnalysis.SequencePorts"/>; the fault maths is
/// <see cref="SeriesFault"/> and <see cref="SimultaneousFault"/>, unchanged). The oracle is a four-bus chain whose sequence networks are small
/// enough to write down and invert here, independently of the Core assembly:
///   bus 1 generator (X''d 0.2) - T1 (delta / grounded wye, x 0.1) - bus 2 - line (0.01 + j0.2) - bus 3 - T2 (grounded wye / delta, x 0.15) - bus 4 load.
/// Positive and negative sequence: the generator as j0.2 to earth, the load at its prefault voltage as a constant impedance; zero sequence: T1 shunts
/// bus 2 to earth through j0.1, T2 shunts bus 3 through j0.15, the line is 3 x its positive impedance, buses 1 and 4 (delta sides) float.
/// No published reference exists for an open-conductor or cross-country fault on a standard test case, so the references are the hand-built
/// networks above, the textbook sequence connections, the boundary conditions themselves, and agreement with the existing <c>fault --type</c>.
/// </summary>
public sealed class SeriesAndSimultaneousFaultTests : IDisposable
{
    private static readonly Complex Zline = new(0.01, 0.2);

    private static GridModel Chain() => new()
    {
        Name = "chain4",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132),
            new Bus(4, BusType.Pq, Pd: 60, Qd: 20, BaseKv: 20),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, SubtransientXdPu: 0.2) },
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true, RateAMva: 100, FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded),
            new Branch(2, 3, 0.01, 0.2, Name: "L23", RateAMva: 100),
            new Branch(3, 4, 0, 0.15, Name: "T2", IsTransformer: true, RateAMva: 100, FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.Delta),
        },
    };

    private static PowerFlowResult Prefault(GridModel m)
    {
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        return r;
    }

    private static Complex V(PowerFlowResult p, int bus)
    {
        int i = p.BusIds.ToList().IndexOf(bus);
        return Complex.FromPolarCoordinates(p.Vm[i], p.VaDeg[i] * Math.PI / 180.0);
    }

    private static void Near(Complex expected, Complex actual, double tol, string what)
        => Assert.True((expected - actual).Magnitude <= tol * Math.Max(1.0, expected.Magnitude), $"{what}: expected {expected} but got {actual}");

    /// <summary>Gauss-Jordan inverse of a small complex matrix with partial pivoting (the independent oracle).</summary>
    private static Complex[,] Invert(Complex[,] a)
    {
        int n = a.GetLength(0);
        var w = new Complex[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) w[i, j] = a[i, j]; w[i, n + i] = Complex.One; }
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (w[r, c].Magnitude > w[p, c].Magnitude) p = r;
            if (p != c) for (int j = 0; j < 2 * n; j++) (w[c, j], w[p, j]) = (w[p, j], w[c, j]);
            Complex d = w[c, c];
            for (int j = 0; j < 2 * n; j++) w[c, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                Complex f = w[r, c];
                for (int j = 0; j < 2 * n; j++) w[r, j] -= f * w[c, j];
            }
        }
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = w[i, n + j];
        return inv;
    }

    /// <summary>The positive (= negative) sequence Zbus of the chain with the load as the constant impedance at its prefault voltage.</summary>
    private static Complex[,] ChainZ1(PowerFlowResult p)
    {
        Complex y4 = Complex.Conjugate(new Complex(0.6, 0.2)) / Math.Pow(V(p, 4).Magnitude, 2);
        var y = new Complex[4, 4];
        void Series(int a, int b, Complex z) { Complex ys = 1 / z; y[a, a] += ys; y[b, b] += ys; y[a, b] -= ys; y[b, a] -= ys; }
        y[0, 0] += 1 / new Complex(0, 0.2);
        y[3, 3] += y4;
        Series(0, 1, new Complex(0, 0.1)); Series(1, 2, Zline); Series(2, 3, new Complex(0, 0.15));
        return Invert(y);
    }

    // ---- the Core network side --------------------------------------------------------------------------------------

    [Fact]
    public void Port_impedances_are_the_hand_built_zbus_entries_and_a_floating_zero_sequence_port_is_flagged()
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 2, 3, 4 });
        Complex[,] z1 = ChainZ1(pre);
        int[] ix = { 1, 2, 3 };
        for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
            {
                Near(z1[ix[p], ix[q]], n.Impedances.Positive[p, q], 1e-12, $"Z1[{p},{q}]");
                Near(z1[ix[p], ix[q]], n.Impedances.Negative[p, q], 1e-12, $"Z2[{p},{q}]");
            }
        // Zero sequence: buses 2 and 3 are joined by 3 x the line and each shunts to earth; bus 4 (delta side) floats.
        var y0 = new Complex[2, 2];
        Complex yl = 1 / (3.0 * Zline), s2 = 1 / new Complex(0, 0.1), s3 = 1 / new Complex(0, 0.15);
        y0[0, 0] = s2 + yl; y0[1, 1] = s3 + yl; y0[0, 1] = -yl; y0[1, 0] = -yl;
        Complex[,] z0 = Invert(y0);
        for (int p = 0; p < 2; p++) for (int q = 0; q < 2; q++) Near(z0[p, q], n.Impedances.Zero[p, q], 1e-12, $"Z0[{p},{q}]");
        // The floating port carries no zero-sequence transfer; its self entry is the documented 1 pu placeholder.
        for (int k = 0; k < 2; k++) { Assert.Equal(Complex.Zero, n.Impedances.Zero[2, k]); Assert.Equal(Complex.Zero, n.Impedances.Zero[k, 2]); }
        Assert.Equal(Complex.One, n.Impedances.Zero[2, 2]);
        Assert.Equal(new[] { true, true, false }, n.ZeroSequenceGrounded.ToArray());
        Near(V(pre, 2), n.Prefault[0], 1e-15, "prefault V at port 2");
        Assert.Equal(132.0, n.BaseKv[0]);
    }

    [Fact]
    public void A_single_port_is_the_driving_point_that_the_unbalanced_study_reports()
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        UnbalancedFaultResult u = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.SingleLineToGround);
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 3 });
        Near(u.Z1Pu, n.Impedances.Positive[0, 0], 1e-13, "Z1");
        Near(u.Z2Pu, n.Impedances.Negative[0, 0], 1e-13, "Z2");
        Near(u.Z0Pu, n.Impedances.Zero[0, 0], 1e-13, "Z0");
        // ...and so a single-port single-line-to-ground solve through SimultaneousFault gives Unbalanced's phase-a current.
        PortFaultResult r = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.LineToGroundA })[0];
        Assert.Equal(u.PhaseAPu, r.PhaseCurrents[0].Magnitude, 9);
        Assert.Equal(0.0, r.PhaseCurrents[1].Magnitude, 9);
    }

    [Fact]
    public void On_case14_a_single_port_matches_the_unbalanced_driving_point_and_cross_country_ports_satisfy_their_boundary_conditions()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = Prefault(m);
        UnbalancedFaultResult u = FaultAnalysis.Unbalanced(m, pre, 4, FaultType.LineToLine);
        SequencePortNetwork one = FaultAnalysis.SequencePorts(m, pre, new[] { 4 });
        Near(u.Z1Pu, one.Impedances.Positive[0, 0], 1e-12, "Z1");
        Near(u.Z2Pu, one.Impedances.Negative[0, 0], 1e-12, "Z2");

        // Transfer impedance from the public per-bus sequence voltages of two single faults: Z1[p,k] = (V0_p - V1_p) / I1.
        UnbalancedFaultResult at9 = FaultAnalysis.Unbalanced(m, pre, 9, FaultType.LineToLine);
        SequencePortNetwork two = FaultAnalysis.SequencePorts(m, pre, new[] { 4, 9 });
        int i4 = u.BusIds.ToList().IndexOf(4);
        Complex z1_49 = (V(pre, 4) - u.V1[i4]) / u.I1;   // not used: that is the 4,4 entry
        _ = z1_49;
        int i9 = at9.BusIds.ToList().IndexOf(4);
        Near((V(pre, 4) - at9.V1[i9]) / at9.I1, two.Impedances.Positive[0, 1], 1e-9, "Z1[4,9] from the fault at 9");
        Near(-at9.V2[i9] / at9.I2, two.Impedances.Negative[0, 1], 1e-9, "Z2[4,9] from the fault at 9");

        // Mixed faults at the two buses: the boundary conditions of each port hold in the solution (no earth: ll and 3ph only).
        PortFaultResult[] r = SimultaneousFault.Solve(two.Impedances, two.Prefault, new[] { PortFault.LineToLineBC, PortFault.ThreePhase });
        Assert.True(r[0].PhaseCurrents[0].Magnitude < 1e-9);                                   // Ia = 0
        Assert.True((r[0].PhaseCurrents[1] + r[0].PhaseCurrents[2]).Magnitude < 1e-9);        // Ib + Ic = 0
        Assert.True((r[0].PhaseVoltages[1] - r[0].PhaseVoltages[2]).Magnitude < 1e-9);        // Vb = Vc
        foreach (Complex v in r[1].PhaseVoltages) Assert.True(v.Magnitude < 1e-9);             // Va = Vb = Vc = 0
    }

    [Fact]
    public void A_three_phase_fault_at_a_port_with_no_earth_path_carries_no_zero_sequence_current_beside_an_unbalanced_fault()
    {
        // Bus 4 sits behind a delta winding. Beside a line-to-line fault at bus 3 the bolted three-phase fault at bus 4 draws positive and
        // negative sequence current (the line-to-line fault unbalances the network) and exactly no zero-sequence current.
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 3, 4 });
        PortFaultResult[] r = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.LineToLineBC, PortFault.ThreePhase });
        Assert.True(r[1].I0.Magnitude < 1e-12);
        Assert.True(r[1].I2.Magnitude > 1e-3);
        foreach (Complex v in r[1].PhaseVoltages) Assert.True(v.Magnitude < 1e-9);
        // Alone, the same fault is the balanced one: no negative or zero sequence.
        SequencePortNetwork one = FaultAnalysis.SequencePorts(m, pre, new[] { 4 });
        PortFaultResult a = SimultaneousFault.Solve(one.Impedances, one.Prefault, new[] { PortFault.ThreePhase })[0];
        Assert.True(a.I0.Magnitude < 1e-12 && a.I2.Magnitude < 1e-12);
        Assert.Equal(Complex.Abs(V(pre, 4) / ChainZ1(pre)[3, 3]), a.I1.Magnitude, 9);
    }

    [Fact]
    public void Two_simultaneous_three_phase_faults_carry_the_currents_that_the_hand_built_zbus_gives()
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        Complex[,] z = ChainZ1(pre);
        // Ports 2 and 3, both bolted three-phase: V = V0 - Z I = 0, so I = Z^-1 V0 on the two-port block.
        var zp = new Complex[2, 2] { { z[1, 1], z[1, 2] }, { z[2, 1], z[2, 2] } };
        Complex[,] zi = Invert(zp);
        Complex v2 = V(pre, 2), v3 = V(pre, 3);
        Complex i2 = zi[0, 0] * v2 + zi[0, 1] * v3, i3 = zi[1, 0] * v2 + zi[1, 1] * v3;
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 2, 3 });
        PortFaultResult[] r = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.ThreePhase, PortFault.ThreePhase });
        Near(i2, r[0].I1, 1e-9, "positive-sequence current at bus 2");
        Near(i3, r[1].I1, 1e-9, "positive-sequence current at bus 3");
        Assert.Equal(i2.Magnitude, r[0].PhaseCurrents[0].Magnitude, 9);
        Assert.True(r[0].I2.Magnitude < 1e-9 && r[0].I0.Magnitude < 1e-9);
    }

    [Fact]
    public void A_single_line_to_ground_at_one_bus_with_a_line_to_line_at_another_satisfies_both_boundary_conditions_and_couples_the_buses()
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 2, 3 });
        PortFaultResult[] r = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.LineToGroundA, PortFault.LineToLineBC });
        Assert.True(r[0].PhaseVoltages[0].Magnitude < 1e-9);                                   // Va = 0 at bus 2
        Assert.True(r[0].PhaseCurrents[1].Magnitude < 1e-9 && r[0].PhaseCurrents[2].Magnitude < 1e-9);
        Assert.True(r[1].PhaseCurrents[0].Magnitude < 1e-9);                                   // Ia = 0 at bus 3
        Assert.True((r[1].PhaseCurrents[1] + r[1].PhaseCurrents[2]).Magnitude < 1e-9);
        Assert.True((r[1].PhaseVoltages[1] - r[1].PhaseVoltages[2]).Magnitude < 1e-9);
        // Coupled: the line-to-line port alone would carry a different current than with the other fault present.
        SequencePortNetwork one = FaultAnalysis.SequencePorts(m, pre, new[] { 3 });
        PortFaultResult alone = SimultaneousFault.Solve(one.Impedances, one.Prefault, new[] { PortFault.LineToLineBC })[0];
        PortFaultResult clamped = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.ThreePhase, PortFault.LineToLineBC })[1];
        Assert.True(Math.Abs(alone.PhaseCurrents[1].Magnitude - clamped.PhaseCurrents[1].Magnitude) > 1e-3);   // a bolted fault at bus 2 changes what bus 3 carries
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_break_in_the_line_has_the_hand_built_impedances_and_the_textbook_currents(int open)
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SeriesBreakNetwork b = FaultAnalysis.SeriesBreak(m, pre, 1);
        Complex y4 = Complex.Conjugate(new Complex(0.6, 0.2)) / Math.Pow(V(pre, 4).Magnitude, 2);
        // Positive and negative: source side j0.2 + j0.1 to earth, load side j0.15 + 1/y4 to earth, joined by the line.
        Complex zs1 = Zline + new Complex(0, 0.3) + new Complex(0, 0.15) + 1 / y4;
        Complex zs0 = 3.0 * Zline + new Complex(0, 0.1) + new Complex(0, 0.15);
        Complex ipre = (V(pre, 2) - V(pre, 3)) / Zline;
        Near(zs1, b.Zs1, 1e-12, "Zs1");
        Near(zs1, b.Zs2, 1e-12, "Zs2");
        Near(zs0, b.Zs0, 1e-12, "Zs0");
        Near(ipre, b.IPrePositive, 1e-12, "prefault current");
        Assert.Equal(1, b.BranchIndex);
        Assert.Equal(132.0, b.BaseKv);

        SeriesFaultResult r = SeriesFault.OpenConductors(b.IPrePositive, b.Zs1, b.Zs2, b.Zs0, open);
        Complex e1 = zs1 * ipre;
        if (open == 1)
        {
            Complex par = zs1 * zs0 / (zs1 + zs0);
            Near(e1 / (zs1 + par), r.I1, 1e-12, "I1 one open");
            Assert.True(r.Ia.Magnitude < 1e-12);                       // the open conductor carries nothing
        }
        else
        {
            Near(e1 / (zs0 + 2.0 * zs1), r.I1, 1e-12, "I1 two open");
            Assert.True(r.Ib.Magnitude < 1e-12 && r.Ic.Magnitude < 1e-12);
            Near(r.I1, r.Ia / 3.0, 1e-12, "Ia = 3 I1");
        }
    }

    [Fact]
    public void A_break_that_has_no_defined_zero_sequence_impedance_or_no_source_is_refused_by_name()
    {
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        // T1 is delta / grounded wye: its zero-sequence model is a shunt, not a series element.
        var ex = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.SeriesBreak(m, pre, 0));
        Assert.Contains("no plain series zero-sequence impedance", ex.Message, StringComparison.Ordinal);
        // Tap and phase shift.
        GridModel tapped = m.CopyWith(branches: new[] { m.Branches[0], m.Branches[1] with { TapRatio = 1.05 }, m.Branches[2] });
        Assert.Contains("tap ratio or phase shift", Assert.Throws<InvalidOperationException>(() => FaultAnalysis.SeriesBreak(tapped, Prefault(tapped), 1)).Message, StringComparison.Ordinal);
        // Out of range.
        Assert.Throws<ArgumentOutOfRangeException>(() => FaultAnalysis.SeriesBreak(m, pre, 7));
        // A radial line to a delta-wye load transformer: break the line past the only grounded transformer and the zero sequence is not earthed on the far side.
        GridModel noGround = m.CopyWith(branches: new[] { m.Branches[0], m.Branches[1],
            m.Branches[2] with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.Delta } });
        Assert.Contains("reaches no earthed neutral", Assert.Throws<InvalidOperationException>(() => FaultAnalysis.SeriesBreak(noGround, Prefault(noGround), 1)).Message, StringComparison.Ordinal);
        // Duplicate or unknown ports.
        Assert.Throws<ArgumentException>(() => FaultAnalysis.SequencePorts(m, pre, new[] { 2, 2 }));
        Assert.Throws<ArgumentException>(() => FaultAnalysis.SequencePorts(m, pre, new[] { 2, 99 }));
    }

    [Fact]
    public void The_new_core_entry_points_leave_the_existing_studies_untouched()
    {
        // Same model, same prefault: the unbalanced study before and after the new entry points have run is bit for bit the same.
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        UnbalancedFaultResult a = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.DoubleLineToGround);
        _ = FaultAnalysis.SequencePorts(m, pre, new[] { 2, 3 });
        _ = FaultAnalysis.SeriesBreak(m, pre, 1);
        UnbalancedFaultResult b = FaultAnalysis.Unbalanced(m, pre, 3, FaultType.DoubleLineToGround);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PhaseAPu), BitConverter.DoubleToInt64Bits(b.PhaseAPu));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.EarthCurrentPu), BitConverter.DoubleToInt64Bits(b.EarthCurrentPu));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Z0Pu.Imaginary), BitConverter.DoubleToInt64Bits(b.Z0Pu.Imaginary));
    }

    // ---- the verbs --------------------------------------------------------------------------------------------------

    private readonly string _dir = TestTemp.Under("gridsim-faults-" + Guid.NewGuid().ToString("N"));

    public SeriesAndSimultaneousFaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The chain as a JSON v2 case manifest (so the CLI sees the windings).</summary>
    private string ChainCase()
    {
        File.WriteAllText(Path.Combine(_dir, "chain.network.json"), """
            {
              "schema": "gridsim-network/2",
              "buses": [
                { "id": 1, "name": "G", "baseKv": 20, "type": "slack", "vm": 1.0 },
                { "id": 2, "name": "A", "baseKv": 132, "type": "pq" },
                { "id": 3, "name": "B", "baseKv": 132, "type": "pq" },
                { "id": 4, "name": "L", "baseKv": 20, "type": "pq" }
              ],
              "generators": [ { "name": "G1", "bus": 1, "type": "", "capacityMva": 100, "outputMw": 0, "voltageSetpoint": 1.0, "role": "slack", "subtransientXdPu": 0.2 } ],
              "demands": [ { "name": "load", "bus": 4, "pMw": 60, "qMvar": 20 } ],
              "lines": [ { "name": "L23", "from": 2, "to": 3, "rPu": 0.01, "xPu": 0.2, "ratingMva": 100 } ],
              "transformers": [
                { "name": "T1", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "ratingMva": 100, "windings": { "from": "Delta", "to": "WyeGrounded" } },
                { "name": "T2", "from": 3, "to": 4, "rPu": 0, "xPu": 0.15, "ratingMva": 100, "windings": { "from": "WyeGrounded", "to": "Delta" } }
              ]
            }
            """);
        string manifest = Path.Combine(_dir, "chain.case.json");
        File.WriteAllText(manifest, """{ "schema": "gridsim-case/2", "name": "chain4", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["chain.network.json"] }""");
        return manifest;
    }

    private static JsonElement Read(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    private static double Num(JsonElement e, string name) => e.GetProperty(name).GetDouble();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Fault_series_reports_the_core_numbers_through_the_shared_writer(int open)
    {
        string c = ChainCase();
        string o = Path.Combine(_dir, "series.json");
        CliProcess.Result r = CliProcess.RunBuilt("fault-series", c, "--branch", "0", "--open", open.ToString(), "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Series fault", r.StdOut, StringComparison.Ordinal);
        JsonElement doc = Read(o);
        Assert.Equal("fault-series", doc.GetProperty("verb").GetString());
        JsonElement s = doc.GetProperty("summary");

        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SeriesBreakNetwork b = FaultAnalysis.SeriesBreak(m, pre, 1);
        Assert.Equal(b.Zs1.Real, Num(s, "zs1_re_pu"), 6);
        Assert.Equal(b.Zs1.Imaginary, Num(s, "zs1_im_pu"), 6);
        Assert.Equal(b.Zs0.Real, Num(s, "zs0_re_pu"), 6);
        Assert.Equal(b.Zs0.Imaginary, Num(s, "zs0_im_pu"), 6);
        Assert.Equal(b.IPrePositive.Magnitude, Num(s, "i_pre_pu"), 6);   // the CLI solves its own prefault flow: equal to the power-flow tolerance
        SeriesFaultResult core = SeriesFault.OpenConductors(b.IPrePositive, b.Zs1, b.Zs2, b.Zs0, open);
        JsonElement phases = doc.GetProperty("tables").GetProperty("phases");
        Complex[] ci = { core.Ia, core.Ib, core.Ic };
        for (int p = 0; p < 3; p++)
        {
            Assert.Equal(ci[p].Magnitude, Num(phases[p], "i_mag_pu"), 6);
            Assert.Equal(ci[p].Magnitude * 100.0 / (Math.Sqrt(3.0) * 132.0), Num(phases[p], "i_mag_ka"), 6);
        }
        Assert.Equal(open == 1 ? "a" : "b", phases[open == 1 ? 0 : 1].GetProperty("phase").GetString());
        Assert.True(Num(phases[open == 1 ? 0 : 1], "i_mag_pu") < 1e-9);
    }

    [Fact]
    public void Fault_series_is_deterministic_and_its_refusals_and_usage_errors_have_the_documented_exit_codes()
    {
        string c = ChainCase();
        // The result records its own file name, so two runs are compared at the same path: identical bytes.
        string a = Path.Combine(_dir, "out.json");
        Assert.Equal(0, CliProcess.RunBuilt("fault-series", c, "--branch", "0", "--open", "1", "--json", a).ExitCode);
        byte[] first = File.ReadAllBytes(a);
        File.Delete(a);
        Assert.Equal(0, CliProcess.RunBuilt("fault-series", c, "--branch", "0", "--open", "1", "--json", a).ExitCode);
        Assert.Equal(first, File.ReadAllBytes(a));

        CliProcess.Result noOpen = CliProcess.RunBuilt("fault-series", c, "--branch", "0");
        Assert.Equal(2, noOpen.ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-series", c, "--branch", "0", "--open", "3").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-series", c, "--branch", "9", "--open", "1").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-series", c, "--branch", "0", "--open", "1", "--bus", "2").ExitCode);
        CliProcess.Result delta = CliProcess.RunBuilt("fault-series", c, "--branch", "1", "--open", "1");
        Assert.Equal(1, delta.ExitCode);
        Assert.Contains("no plain series zero-sequence impedance", delta.StdErr, StringComparison.Ordinal);
        // A case with no winding connection has no earthed neutral: every break is refused, by name.
        CliProcess.Result case9 = CliProcess.RunBuilt("fault-series", "case9", "--branch", "3", "--open", "2");
        Assert.Equal(1, case9.ExitCode);
        Assert.Contains("earthed neutral", case9.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, CliProcess.RunBuilt("fault-series", Path.Combine(_dir, "missing.json"), "--branch", "0", "--open", "1").ExitCode);
    }

    [Fact]
    public void Fault_simultaneous_matches_the_core_solution_and_a_single_entry_matches_fault_type()
    {
        string c = ChainCase();
        string o = Path.Combine(_dir, "sim.json");
        CliProcess.Result r = CliProcess.RunBuilt("fault-simultaneous", c, "--at", "2:slg,3:ll", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement ports = Read(o).GetProperty("tables").GetProperty("ports");
        GridModel m = Chain();
        PowerFlowResult pre = Prefault(m);
        SequencePortNetwork n = FaultAnalysis.SequencePorts(m, pre, new[] { 2, 3 });
        PortFaultResult[] core = SimultaneousFault.Solve(n.Impedances, n.Prefault, new[] { PortFault.LineToGroundA, PortFault.LineToLineBC });
        for (int p = 0; p < 2; p++)
        {
            for (int ph = 0; ph < 3; ph++)
                Assert.Equal(core[p].PhaseCurrents[ph].Magnitude, Num(ports[p], new[] { "phase_a_pu", "phase_b_pu", "phase_c_pu" }[ph]), 6);
            Assert.Equal((3.0 * core[p].I0).Magnitude, Num(ports[p], "earth_current_pu"), 6);
        }

        // One entry is the ordinary unbalanced fault: the same phase currents as `fault --type` (single line to ground, bus 3).
        string one = Path.Combine(_dir, "one.json"), f = Path.Combine(_dir, "f.json");
        Assert.Equal(0, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "3:slg", "--json", one).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("fault", c, "--bus", "3", "--type", "slg", "--json", f).ExitCode);
        Assert.Equal(Num(Read(f).GetProperty("summary"), "phase_a_pu"), Num(Read(one).GetProperty("tables").GetProperty("ports")[0], "phase_a_pu"), 8);
    }

    [Fact]
    public void Fault_simultaneous_on_case14_agrees_with_fault_for_one_entry_and_runs_two_buses()
    {
        string one = Path.Combine(_dir, "c14-one.json"), f = Path.Combine(_dir, "c14-f.json"), two = Path.Combine(_dir, "c14-two.json");
        Assert.Equal(0, CliProcess.RunBuilt("fault-simultaneous", "case14", "--at", "4:ll", "--json", one).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("fault", "case14", "--bus", "4", "--type", "ll", "--json", f).ExitCode);
        JsonElement pf = Read(one).GetProperty("tables").GetProperty("ports")[0];
        JsonElement ff = Read(f).GetProperty("summary");
        Assert.Equal(Num(ff, "phase_b_pu"), Num(pf, "phase_b_pu"), 8);
        Assert.Equal(Num(ff, "phase_c_pu"), Num(pf, "phase_c_pu"), 8);
        CliProcess.Result r = CliProcess.RunBuilt("fault-simultaneous", "case14", "--at", "4:ll,9:3ph", "--json", two);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement ports = Read(two).GetProperty("tables").GetProperty("ports");
        Assert.Equal(2, ports.GetArrayLength());
        Assert.True(Num(ports[0], "va_pu") > 0.5);                       // the line-to-line bus keeps phase a
        Assert.True(Num(ports[1], "va_pu") < 1e-9);                      // the bolted three-phase bus is at zero
        Assert.True(Num(ports[1], "i0_pu") < 1e-9 && Num(ports[1], "earth_current_pu") < 1e-9);   // case14 has no earthed neutral: no zero-sequence current
        Assert.True(Num(ports[1], "i2_pu") > 1e-3);                      // the line-to-line fault at bus 4 unbalances the network
    }

    [Fact]
    public void Fault_simultaneous_is_deterministic_and_has_the_documented_usage_and_verdict_exit_codes()
    {
        string c = ChainCase();
        string a = Path.Combine(_dir, "out.csv");
        Assert.Equal(0, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "2:dlg,3:3ph", "--csv", a).ExitCode);
        byte[] first = File.ReadAllBytes(a);
        File.Delete(a);
        Assert.Equal(0, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "2:dlg,3:3ph", "--csv", a).ExitCode);
        Assert.Equal(first, File.ReadAllBytes(a));

        Assert.Equal(2, CliProcess.RunBuilt("fault-simultaneous", c).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "2:xyz").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "2:3ph,2:ll").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "99:3ph").ExitCode);
        // Bus 4 sits behind a delta winding: it reaches no earthed neutral, so an earth fault there is exit 3 and nothing is invented.
        CliProcess.Result noEarth = CliProcess.RunBuilt("fault-simultaneous", c, "--at", "4:slg");
        Assert.Equal(3, noEarth.ExitCode);
        Assert.Contains("no earthed neutral", noEarth.StdErr, StringComparison.Ordinal);
        Assert.Equal(3, CliProcess.RunBuilt("fault-simultaneous", "case9", "--at", "4:slg").ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("fault-simultaneous", c, "--at", "4:ll").ExitCode);
    }

    [Fact]
    public void The_two_verbs_have_help_sections_and_the_global_flag_gate_knows_their_flags()
    {
        foreach (string verb in new[] { "fault-series", "fault-simultaneous" })
        {
            CliProcess.Result h = CliProcess.RunBuilt(verb, "--help");
            Assert.Equal(0, h.ExitCode);
            Assert.Contains("gridsim " + verb, h.StdOut, StringComparison.Ordinal);
        }
        Assert.Equal(2, CliProcess.RunBuilt("fault-series", "case9", "--no-such-flag", "1").ExitCode);
    }
}
