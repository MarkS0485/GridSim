using System.Numerics;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// PSS/E sequence data, device item H: zero-sequence mutual coupling between whole lines (a group of coupled lines is
/// stamped from the inverse of its impedance matrix) and machine zero-sequence impedances (a grounded neutral, a shunt to
/// earth at the machine's bus). The oracle is the zero-sequence nodal network of the fixture written out here by hand.
/// The fixture is ukgds-style-v30.raw / .seq (see <see cref="PsseRev30AndSeqTests"/>).
/// </summary>
public class PsseSeqMutualTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static PsseParseResult Ukgds30() => PsseRawParser.ParseFile(Fixture("ukgds-style-v30.raw"), new PsseReadOptions { Revision = 30, FrequencyHz = 50 });

    private const string MutualHeader = "@!I\tJ\tICKT1\tK\tL\tICKT2\tRM\tXM\tBIJ1\tBIJ2\tBKL1\tBKL2\t\r\n";

    /// <summary>The fixture's SEQ text with one mutual coupling record <paramref name="record"/> (tab separated).</summary>
    private static string SeqWith(string record)
    {
        string s = File.ReadAllText(Fixture("ukgds-style-v30.seq"));
        Assert.Contains(MutualHeader, s, StringComparison.Ordinal);
        return s.Replace(MutualHeader, MutualHeader + record + "\r\n", StringComparison.Ordinal);
    }

    /// <summary>Solve the small complex system a x = b by Gaussian elimination (the test's own oracle).</summary>
    private static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var x = (Complex[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            for (int r = c + 1; r < n; r++)
            {
                Complex f = m[r, c] / m[c, c];
                for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            Complex s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }

    [Fact]
    public void A_mutual_coupling_between_whole_lines_gives_the_hand_built_zero_sequence_driving_point_impedance()
    {
        // Lines 3-4 (0.15 + j0.24) and 4-5 (0.12 + j0.18) coupled by Zm = 0.02 + j0.1 in the record's directions 3->4, 4->5.
        // (The fixture's line 3-5 is out of service.)
        PsseSeqResult r = PsseSeqReader.Apply(SeqWith("3\t4\t1\t4\t5\t1\t0.02\t0.1\t0\t1\t0\t1"), Ukgds30());
        ZeroSequenceMutual mu = Assert.Single(r.Model.ZeroSequenceMutuals);
        Assert.Equal((0.02, 0.1), (mu.RPu, mu.XPu));
        Assert.Contains(r.Notes, n => n.StartsWith("1 zero-sequence mutual coupling(s) between whole lines", StringComparison.Ordinal));

        // By hand, the zero-sequence network of buses 3, 4, 5: the earth paths at 3 (the 33/11 kV unit, (0.045 + 3 x
        // 5.25) + j0.88) and 4 (0.055 + j(0.95 + 3 x 1.5)), and the coupled pair: with Z = [[Za, Zm], [Zm, Zc]] and
        // Y = Z^-1, lines a (3->4) and c (4->5) inject I = A Y A^T V.
        Complex za = new(0.15, 0.24), zc = new(0.12, 0.18), zm = new(0.02, 0.1);
        Complex det = za * zc - zm * zm;
        Complex yaa = zc / det, ycc = za / det, yac = -zm / det;
        var y = new Complex[3, 3];   // rows / columns: bus 3, 4, 5
        y[0, 0] += Complex.One / new Complex(0.045 + 3.0 * 5.25, 0.88);
        y[1, 1] += Complex.One / new Complex(0.055, 0.95 + 3.0 * 1.5);
        // Incidence: a = e3 - e4, c = e4 - e5.
        int[][] inc = { new[] { 0, 1 }, new[] { 1, 2 } };
        Complex[,] yy = { { yaa, yac }, { yac, ycc } };
        for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
            {
                int fp = inc[p][0], tp = inc[p][1], fq = inc[q][0], tq = inc[q][1];
                y[fp, fq] += yy[p, q]; y[fp, tq] -= yy[p, q]; y[tp, fq] -= yy[p, q]; y[tp, tq] += yy[p, q];
            }
        // The driving point at bus 5 (its earth return runs through line 4-5 and then splits between bus 4 and line 3-4, so
        // the coupling acts).
        Complex expected = Solve(y, new[] { Complex.Zero, Complex.Zero, Complex.One })[2];
        (Complex z0, _, bool grounded) = Iec60909.ZeroSequenceThevenin(r.Model, 5);
        Assert.True(grounded);
        Assert.True(Complex.Abs(z0 - expected) <= 1e-12 * Complex.Abs(expected), $"Z0 {z0} vs hand-built {expected}");

        // Control: without the coupling the driving point differs.
        (Complex z0Plain, _, _) = Iec60909.ZeroSequenceThevenin(PsseSeqReader.Apply(File.ReadAllText(Fixture("ukgds-style-v30.seq")), Ukgds30()).Model, 5);
        Assert.True(Complex.Abs(z0Plain - expected) > 1e-6, "the coupling changes the driving point");
    }

    [Fact]
    public void Record_directions_set_the_sign_and_both_reversed_is_the_same_coupling()
    {
        GridModel fwd = PsseSeqReader.Apply(SeqWith("3\t4\t1\t4\t5\t1\t0.02\t0.1\t0\t1\t0\t1"), Ukgds30()).Model;
        GridModel both = PsseSeqReader.Apply(SeqWith("4\t3\t1\t5\t4\t1\t0.02\t0.1\t0\t1\t0\t1"), Ukgds30()).Model;
        GridModel one = PsseSeqReader.Apply(SeqWith("3\t4\t1\t5\t4\t1\t0.02\t0.1\t0\t1\t0\t1"), Ukgds30()).Model;
        Assert.Equal(fwd.ZeroSequenceMutuals, both.ZeroSequenceMutuals);
        Assert.Equal((-0.02, -0.1), (one.ZeroSequenceMutuals[0].RPu, one.ZeroSequenceMutuals[0].XPu));
    }

    [Fact]
    public void Zero_sequence_currents_of_coupled_lines_meet_kirchhoffs_law_at_an_unearthed_bus()
    {
        // An earth fault at bus 3: bus 5 hangs off bus 4 on line 4-5 with no earth path of its own, so line 4-5 carries no
        // zero-sequence current; coupled to line 3-4, it still sees the induced voltage V4 - V5 = Zm I(3-4).
        GridModel m = PsseSeqReader.Apply(SeqWith("3\t4\t1\t4\t5\t1\t0.02\t0.1\t0\t1\t0\t1"), Ukgds30()).Model;
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        UnbalancedFaultResult f = FaultAnalysis.Unbalanced(m, pf, 3, FaultType.SingleLineToGround);
        Assert.True(f.FaultBusGrounded);
        Complex at5 = Complex.Zero;
        foreach (BranchSequenceCurrents b in f.Branches)
        {
            if (b.From == 5) at5 += b.I0From;
            if (b.To == 5) at5 += b.I0To;
        }
        Assert.True(Complex.Abs(at5) <= 1e-12 * Complex.Abs(f.I0), $"KCL at bus 5: {at5} vs fault I0 {f.I0}");
        BranchSequenceCurrents l34 = f.Branches.Single(b => b.From == 3 && b.To == 4);
        Complex induced = new Complex(0.02, 0.1) * l34.I0From;
        Assert.True(Complex.Abs(induced) > 1e-6, "line 3-4 carries zero-sequence current");
        int i4 = f.BusIds.ToList().IndexOf(4), i5 = f.BusIds.ToList().IndexOf(5);
        Complex drop = f.V0[i4] - f.V0[i5];
        Assert.True(Complex.Abs(drop - induced) <= 1e-12 * Complex.Abs(induced), $"V4 - V5 {drop} vs Zm I34 {induced}");
    }

    [Fact]
    public void A_machine_zero_sequence_impedance_earths_its_bus_and_the_writers_refuse_the_data()
    {
        GridModel m = PsseSeqReader.Apply(File.ReadAllText(Fixture("ukgds-style-v30.seq")), Ukgds30()).Model;
        Generator g = m.AllGenerators[0];
        Assert.Equal(new MachineZeroSequence(0, 0.01), g.ZeroSequenceZPu);
        // Bus 1 with the 132/33 kV unit taken out of the zero sequence (both windings delta): only the machine earths it.
        GridModel onlyMachine = m.CopyWith(branches: m.Branches.Select(b => b.FromWinding == WindingConnection.WyeGrounded && b.From == 1
            ? b with { FromWinding = WindingConnection.Delta } : b).ToArray());
        (Complex z0, _, bool grounded) = Iec60909.ZeroSequenceThevenin(onlyMachine, 1);
        Assert.True(grounded);
        Complex zg = new Complex(0, 0.01) * (100.0 / g.MBase);
        Assert.True(Complex.Abs(z0 - zg) <= 1e-12 * Complex.Abs(zg), $"Z0 {z0} vs {zg}");
        Assert.Contains("machine zero-sequence impedances (1)", Assert.Throws<InvalidDataException>(() => PsseRawWriter.Write(m)).Message);
    }
}
