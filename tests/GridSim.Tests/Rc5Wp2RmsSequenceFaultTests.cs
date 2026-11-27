using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Workflow;
using GridSim.Scripting;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP2: RMS unbalanced and impedance faults (<see cref="RmsSequenceFault"/>, <see cref="RmsBranchSequenceFault"/>), the
/// positive-sequence method of PSS/E and PowerFactory RMS: the equivalent fault shunt at the fault point built from the
/// negative- and zero-sequence Thevenin impedances. Verification sources, stated per test:
/// <list type="number">
/// <item>At t = 0+ the RMS fault quantities equal the existing static operating-point fault study
/// (<see cref="FaultAnalysis.Unbalanced"/> / <see cref="FaultAnalysis.ThreePhase"/>) under the same assumptions
/// (subtransient machine reactances, loads as constant admittance at the prefault voltage, prefault voltages from the same
/// power flow). The two are independent assemblies and factorisations of the same nodal equations.</item>
/// <item>A three-phase fault with Zf = 0 is the bolted fault, bit for bit.</item>
/// <item>A single-machine-infinite-bus case whose electrical power during the fault and critical-clearing ordering are
/// derived by hand (transfer reactance of the star network with the fault shunt; equal-area ordering of the four types).</item>
/// <item>Determinism: dense and sparse power-flow engines, repeated and parallel runs, and the three SIMD modes of the built
/// CLI give the same bits.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp2RmsSequenceFaultTests
{
    private const double Step = 0.005;

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
        => model.Generators.Select(g => new SubtransientInit.MachineData(
            H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    /// <summary>WSCC 9-bus with each unit's X''d = 0.22 (machine base, the RMS data's X'') for the static study, and each
    /// generator step-up transformer YNd (delta on the machine side, earthed star on the network side), so the network has
    /// a zero-sequence earth path.</summary>
    private static (GridModel Model, PowerFlowResult Pf) EarthedCase9()
    {
        GridModel c9 = IeeeCases.Case9();
        var genBuses = new HashSet<int>(c9.Generators.Select(g => g.Bus));
        var branches = c9.Branches.Select(b =>
            genBuses.Contains(b.From) ? b with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.WyeGrounded }
            : genBuses.Contains(b.To) ? b with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.Delta }
            : b).ToList();
        GridModel m = c9.CopyWith(branches: branches, generators: c9.Generators.Select(g => g with { SubtransientXdPu = 0.22 }).ToList());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
            if ((m.Branches[i].From == a && m.Branches[i].To == b) || (m.Branches[i].From == b && m.Branches[i].To == a)) return i;
        throw new InvalidOperationException($"no branch {a}-{b}");
    }

    /// <summary>|a - b| within <paramref name="rel"/> of max(|a|, 1).</summary>
    private static void Close(Complex want, Complex got, double rel, string what)
        => Assert.True((want - got).Magnitude <= rel * Math.Max(1.0, want.Magnitude), $"{what}: want {want}, got {got}");

    private static Complex Prefault(PowerFlowResult pf, int bus)
    {
        int i = pf.BusIds.ToList().IndexOf(bus);
        return Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
    }

    private static RmsFaultStudyResult Run(GridModel m, PowerFlowResult pf, params RmsEvent[] events)
        => RmsTransientStability.StudyEventsWithFaults(m, pf, GenrouData(m), events, stepSeconds: Step, durationSeconds: 0.3);

    // ---- 1. t = 0+ equals the static short-circuit study ------------------------------------------------------------

    /// <summary>
    /// Tolerance 1e-10 relative, derived: the static and RMS values are two LU solves of the same well-conditioned nodal
    /// equations (case9 fault network, admittances 1 to 60 pu, condition below 1e4), each backward stable to a few ulp, so
    /// their difference is bounded near 1e-12; the machine admittance's base conversion differs by one rounding. The RMS
    /// side also rebuilds the prefault voltage from the machine EMFs (an equilibrium to a few ulp).
    /// </summary>
    [Theory]
    [InlineData(7, FaultType.SingleLineToGround, 0.0)]
    [InlineData(7, FaultType.SingleLineToGround, 0.05)]
    [InlineData(7, FaultType.LineToLine, 0.0)]
    [InlineData(7, FaultType.LineToLine, 0.05)]
    [InlineData(7, FaultType.DoubleLineToGround, 0.0)]
    [InlineData(7, FaultType.DoubleLineToGround, 0.05)]
    [InlineData(4, FaultType.SingleLineToGround, 0.02)]
    [InlineData(9, FaultType.DoubleLineToGround, 0.02)]
    public void At_t0_plus_the_rms_fault_equals_the_static_unbalanced_study(int bus, FaultType type, double xf)
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        RmsFaultStudyResult st = Run(m, pf, new RmsSequenceFault(0.1, bus, type, 0.0, xf), new RmsFaultClear(0.2, bus));
        RmsFaultTrace trace = Assert.Single(st.Faults);
        RmsFaultSample s = trace.Samples[0];
        Assert.True(s.Energised);
        Assert.Equal(0.1, s.TimeSeconds, 12);
        UnbalancedFaultResult u = FaultAnalysis.Unbalanced(m, pf, bus, type, xf, 0.22);
        Assert.True(u.FaultBusGrounded);
        Assert.True(s.Grounded || type == FaultType.LineToLine);
        Close(u.I1, s.I1, 1e-10, "I1");
        Close(u.I2, s.I2, 1e-10, "I2");
        Close(u.I0, s.I0, 1e-10, "I0");
        Close(u.Z2Pu, s.Z2Pu, 1e-10, "Z2");
        if (type != FaultType.LineToLine)
        {
            // The zero-sequence network is the static study's own (same stamps, same component, same engine): exact.
            Assert.Equal(u.Z0Pu, s.Z0Pu);
        }
        (double va, double vb, double vc) = u.FaultBusPhaseVoltages();
        (double ra, double rb, double rc) = s.PhaseVoltagesPu;
        Assert.True(Math.Abs(va - ra) <= 1e-10 && Math.Abs(vb - rb) <= 1e-10 && Math.Abs(vc - rc) <= 1e-10,
            $"phase voltages static ({va}, {vb}, {vc}) vs RMS ({ra}, {rb}, {rc})");
        // Only the samples while the fault is on: 0.1 s up to the step before the clearance at 0.2 s.
        Assert.Equal(20, trace.Samples.Count);
    }

    [Fact]
    public void Three_phase_through_zf_equals_the_static_three_phase_study()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        RmsFaultSample s = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.ThreePhase, 0.0, 0.05), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        FaultResult f = FaultAnalysis.ThreePhase(m, pf, 7, 0.05, 0.22);
        Assert.True(Math.Abs(f.FaultCurrentPu - s.I1.Magnitude) <= 1e-10 * f.FaultCurrentPu, $"{f.FaultCurrentPu} vs {s.I1.Magnitude}");
        Assert.Equal(Complex.Zero, s.I2);
        Assert.Equal(Complex.Zero, s.I0);
    }

    /// <summary>A complex fault impedance (R and X): the static study takes Zf = jX only, so the reference is its own
    /// Thevenin impedances in the textbook relation I1 = V / (Z1 + Z2 + Z0 + 3 Zf) (Grainger and Stevenson, ch. 12).</summary>
    [Fact]
    public void A_resistive_fault_impedance_follows_the_sequence_relation()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        var zf = new Complex(0.01, 0.05);
        UnbalancedFaultResult u = FaultAnalysis.Unbalanced(m, pf, 7, FaultType.SingleLineToGround, 0.0, 0.22);
        Complex v = Prefault(pf, 7);
        RmsFaultSample slg = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.SingleLineToGround, zf.Real, zf.Imaginary), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Close(v / (u.Z1Pu + u.Z2Pu + u.Z0Pu + 3.0 * zf), slg.I1, 1e-10, "SLG I1");
        RmsFaultSample ll = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.LineToLine, zf.Real, zf.Imaginary), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Close(v / (u.Z1Pu + u.Z2Pu + zf), ll.I1, 1e-10, "LL I1");
        Close(-ll.I1, ll.I2, 0.0, "LL I2 = -I1");
        RmsFaultSample llg = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.DoubleLineToGround, zf.Real, zf.Imaginary), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Complex zg = u.Z0Pu + 3.0 * zf;
        Complex i1 = v / (u.Z1Pu + u.Z2Pu * zg / (u.Z2Pu + zg));
        Close(i1, llg.I1, 1e-10, "LLG I1");
        Close(-i1 * u.Z2Pu / (u.Z2Pu + zg), llg.I0, 1e-10, "LLG I0");
        // The sequence-network connection at the fault: I1 + I2 + I0 = 0 (phase a carries nothing) for LLG.
        Assert.True((llg.I1 + llg.I2 + llg.I0).Magnitude <= 1e-12 * llg.I1.Magnitude);
    }

    /// <summary>The bolted three-phase fault is the 1e7 pu shunt of <see cref="RmsBusFault"/>, so its current is
    /// V / (Z1 + 1e-7): within 1e-6 relative (derived: the shunt's 1e-7 pu against |Z1| about 0.1 pu, plus the solve with a
    /// 1e7 diagonal) of that relation built from the static Z1.</summary>
    [Fact]
    public void The_bolted_three_phase_fault_current_is_the_bolted_shunt_relation()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        RmsFaultSample s = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.ThreePhase), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Complex z1 = FaultAnalysis.Unbalanced(m, pf, 7, FaultType.LineToLine, 0.0, 0.22).Z1Pu;
        Complex want = Prefault(pf, 7) / (z1 + 1.0 / SubtransientInit.BoltedFaultShuntPu);
        Assert.True((want - s.I1).Magnitude <= 1e-6 * want.Magnitude, $"{want} vs {s.I1}");
    }

    // ---- 2. Zf -> 0 three-phase is RmsBusFault --------------------------------------------------------------------

    [Fact]
    public void Three_phase_with_zero_impedance_reproduces_the_bolted_fault_bit_for_bit()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        IReadOnlyList<SubtransientInit.MachineData> data = GenrouData(m);
        RmsEvent[] bolted = FullModelKernelDigestTests.Events(m);
        RmsEvent[] seq = bolted.Select(e => e is RmsBusFault f ? new RmsSequenceFault(f.AtSeconds, f.BusId, FaultType.ThreePhase) : e).ToArray();
        MultiMachineSubtransient.Result a = RmsTransientStability.StudyEvents(m, pf, data, bolted, stepSeconds: Step, durationSeconds: 1.5);
        MultiMachineSubtransient.Result b = RmsTransientStability.StudyEvents(m, pf, data, seq, stepSeconds: Step, durationSeconds: 1.5);
        RmsFaultStudyResult c = RmsTransientStability.StudyEventsWithFaults(m, pf, data, seq, stepSeconds: Step, durationSeconds: 1.5);
        string da = FullModelKernelDigestTests.Digest(a, Array.Empty<RmsEvent>());
        Assert.Equal(da, FullModelKernelDigestTests.Digest(b, Array.Empty<RmsEvent>()));
        Assert.Equal(da, FullModelKernelDigestTests.Digest(c.Result, Array.Empty<RmsEvent>()));
        // And a list without any sequence fault is untouched by the traced entry point.
        RmsFaultStudyResult d = RmsTransientStability.StudyEventsWithFaults(m, pf, data, bolted, stepSeconds: Step, durationSeconds: 1.5);
        Assert.Equal(da, FullModelKernelDigestTests.Digest(d.Result, Array.Empty<RmsEvent>()));
        Assert.Empty(d.Faults);
    }

    [Fact]
    public void A_shrinking_fault_impedance_converges_on_the_bolted_fault()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        Complex bolted = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.ThreePhase), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0].I1;
        double last = double.PositiveInfinity;
        foreach (double x in new[] { 1e-2, 1e-4, 1e-6 })
        {
            Complex i = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.ThreePhase, 0.0, x), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0].I1;
            double err = (i - bolted).Magnitude / bolted.Magnitude;
            Assert.True(err < last, $"Xf {x}: {err} did not shrink from {last}");
            last = err;
        }
        Assert.True(last < 1e-4, $"Xf 1e-6: {last}");
    }

    // ---- 3. SMIB, hand-derived -------------------------------------------------------------------------------------

    private const double X1 = 0.25, XLine = 0.5, XInf = 0.01, X0Machine = 0.08;

    /// <summary>A 100 MVA machine (X'' = 0.25 pu, earthed neutral with X0 = 0.08 pu) sending 1 pu over a lossless line of
    /// X = 0.5 pu to a stiff source (X'' = 0.01 pu, H = 10,000 s), both terminals at 1 pu; the fault is at the machine
    /// terminal bus 1.</summary>
    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Smib()
    {
        var m = new GridModel
        {
            Name = "smib",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Pv, BaseKv: 132), new Bus(2, BusType.Slack, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, Pg: 100, Vg: 1.0, MBase: 100, H: 3.5, Name: "machine", SubtransientXdPu: X1)
                    { ZeroSequenceZPu = new MachineZeroSequence(0.0, X0Machine) },
                new Generator(2, Pg: 0, Vg: 1.0, MBase: 100, H: 1e4, Name: "infinite", SubtransientXdPu: XInf),
            },
            Branches = new[] { new Branch(1, 2, 0.0, XLine, Name: "line") },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>
        {
            new(H: 3.5, Damping: 0.0, Xd: 1.8, Xdp: 0.30, Xdpp: X1, Xq: 1.7, Xqp: 0.55, Xqpp: X1, Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05),
            new(H: 1e4, Damping: 0.0, Xd: XInf, Xdp: XInf, Xdpp: XInf, Xq: XInf, Xqp: XInf, Xqpp: XInf, Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05),
        };
        return (m, pf, data);
    }

    /// <summary>The hand derivation. Z2 at bus 1 is X1 in parallel with (XLine + XInf) (X2 = X'' for both machines),
    /// Z0 = X0 of the machine (the far end has no earth path); the equivalent fault reactance is Xf (three-phase),
    /// Z2 + Z0 (SLG), Z2 (LL), Z2 Z0 / (Z2 + Z0) (LLG). With E1 = V1 + jX1 I and E2 = V2 - jXInf I from the prefault
    /// voltages and line current I = (V1 - V2) / (j XLine), the star network E1 - X1 - bus 1 (fault shunt) - XLine + XInf -
    /// E2 has the transfer reactance X1 + Xb + X1 Xb / Xeq (Xb = XLine + XInf), so Pe(0+) = |E1||E2| sin(d12) / Xtr.</summary>
    private static double HandPe(Complex v1, Complex v2, double xeq)
    {
        Complex i = (v1 - v2) / new Complex(0.0, XLine);
        Complex e1 = v1 + new Complex(0.0, X1) * i, e2 = v2 - new Complex(0.0, XInf) * i;
        double xb = XLine + XInf;
        double xtr = X1 + xb + X1 * xb / xeq;
        return e1.Magnitude * e2.Magnitude * Math.Sin(e1.Phase - e2.Phase) / xtr;
    }

    private static double Xeq(FaultType type, double xf)
    {
        double x2 = X1 * (XLine + XInf) / (X1 + XLine + XInf);
        return type switch
        {
            FaultType.ThreePhase => xf,
            FaultType.SingleLineToGround => x2 + X0Machine + 3 * xf,
            FaultType.LineToLine => x2 + xf,
            _ => x2 * (X0Machine + 3 * xf) / (x2 + X0Machine + 3 * xf),
        };
    }

    /// <summary>The electrical power at t = 0+ against the hand derivation: from the solved prefault voltages, 1e-10
    /// relative (the engine solves the same two-node network); and from the exact operating point (V1 = 1 at 30 degrees,
    /// sin 30 = P X / V1 V2 = 0.5), 1e-8 (limited by the power flow's 1e-12 pu mismatch).</summary>
    [Theory]
    [InlineData(FaultType.ThreePhase, 0.05)]
    [InlineData(FaultType.SingleLineToGround, 0.0)]
    [InlineData(FaultType.LineToLine, 0.0)]
    [InlineData(FaultType.DoubleLineToGround, 0.0)]
    [InlineData(FaultType.SingleLineToGround, 0.02)]
    public void Smib_electrical_power_during_the_fault_matches_the_hand_derivation(FaultType type, double xf)
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        var events = new RmsEvent[] { new RmsSequenceFault(0.1, 1, type, 0.0, xf), new RmsFaultClear(0.2, 1) };
        RmsTransientStability.ObservedStudy obs = RmsTransientStability.StudyEventsObserved(m, pf, data, events, new[] { 1, 2 },
            stepSeconds: Step, durationSeconds: 0.3);
        RmsFaultStudyResult st = RmsTransientStability.StudyEventsWithFaults(m, pf, data, events, stepSeconds: Step, durationSeconds: 0.3);
        RmsFaultSample s = st.Faults[0].Samples[0];
        int k = obs.Buses.TimeSeconds.ToList().FindIndex(t => Math.Abs(t - s.TimeSeconds) < 1e-12);
        Assert.Equal(20, k);
        Complex v1 = Complex.FromPolarCoordinates(obs.Buses.MagnitudePu[0][k], obs.Buses.AngleRad[0][k]);
        Complex v2 = Complex.FromPolarCoordinates(obs.Buses.MagnitudePu[1][k], obs.Buses.AngleRad[1][k]);
        Assert.True((v1 - s.V1).Magnitude <= 1e-12, $"fault trace V1 {s.V1} vs observed {v1}");
        double pe = (v1 * Complex.Conjugate((v1 - v2) / new Complex(0.0, XLine))).Real;
        double hand = HandPe(Prefault(pf, 1), Prefault(pf, 2), Xeq(type, xf));
        Assert.True(Math.Abs(pe - hand) <= 1e-10 * hand, $"{type}: Pe {pe} vs hand {hand}");
        double exact = HandPe(Complex.FromPolarCoordinates(1.0, Math.PI / 6), Complex.One, Xeq(type, xf));
        Assert.True(Math.Abs(pe - exact) <= 1e-8 * exact, $"{type}: Pe {pe} vs exact {exact}");
        // The machine sends 1 pu before the fault and less during it (the transfer reactance rises).
        Assert.True(pe < 1.0);
    }

    /// <summary>The classical critical clearing time of the SMIB by the equal-area criterion, from the same data (xs is the machine's reactance behind its constant voltage, X'' by default): the machine
    /// is the constant voltage E1 behind X'' = X1 and the infinite bus the constant E2 behind XInf, both taken from the
    /// solved prefault point as in <see cref="HandPe"/>. With the transfer reactances Xtr (fault on, from <see cref="Xeq"/>) and
    /// Xpost = X1 + XLine + XInf (fault cleared, line intact), Pe = Pf sin d during the fault and Pp sin d after it; Pm = Pe(d0)
    /// = Pp sin d0 = 1 pu. The fault can be cleared at d_cl if the accelerating area Pm (d_cl - d0) + Pf (cos d_cl - cos d0)
    /// does not exceed the decelerating area up to d_max = pi - d0p, d0p = asin(Pm / Pp):
    /// Pp (cos d_cl - cos d_max) - Pm (d_max - d_cl). The largest such d_cl is found by bisection, and the time to reach it
    /// by integrating (2 H / w0) d'' = Pm - Pf sin d from rest at d0 with RK4 at 10 microseconds (error far below the 5 ms step).</summary>
    private static double ClassicalCct(GridModel m, PowerFlowResult pf, FaultType type, double xf, double xs = X1)
    {
        Complex v1 = Prefault(pf, 1), v2 = Prefault(pf, 2);
        Complex i = (v1 - v2) / new Complex(0.0, XLine);
        Complex e1 = v1 + new Complex(0.0, xs) * i, e2 = v2 - new Complex(0.0, XInf) * i;
        double d0 = e1.Phase - e2.Phase, ee = e1.Magnitude * e2.Magnitude, xb = XLine + XInf;
        double pp = ee / (xs + xb), pm = pp * Math.Sin(d0);
        double xeq = Xeq(type, xf);
        double pf0 = xeq == 0.0 ? 0.0 : ee / (xs + xb + xs * xb / xeq);
        double dmax = Math.PI - Math.Asin(pm / pp);
        double Surplus(double dcl) => pp * (Math.Cos(dcl) - Math.Cos(dmax)) - pm * (dmax - dcl)
            - (pm * (dcl - d0) + pf0 * (Math.Cos(dcl) - Math.Cos(d0)));
        double lo = d0, hi = dmax;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (Surplus(mid) >= 0.0) lo = mid; else hi = mid;
        }
        double dcr = lo, w0 = 2.0 * Math.PI * m.FrequencyHz, h = 3.5;
        double d = d0, w = 0.0, t = 0.0;
        const double dt = 1e-5;
        double Acc(double dd) => (pm - pf0 * Math.Sin(dd)) * w0 / (2.0 * h);
        while (d < dcr)
        {
            double k1d = w, k1w = Acc(d);
            double k2d = w + 0.5 * dt * k1w, k2w = Acc(d + 0.5 * dt * k1d);
            double k3d = w + 0.5 * dt * k2w, k3w = Acc(d + 0.5 * dt * k2d);
            double k4d = w + dt * k3w, k4w = Acc(d + dt * k3d);
            d += dt / 6.0 * (k1d + 2 * k2d + 2 * k3d + k4d);
            w += dt / 6.0 * (k1w + 2 * k2w + 2 * k3w + k4w);
            t += dt;
        }
        return t;
    }

    /// <summary>The critical clearing times order as the equivalent fault reactances do (equal-area criterion: the larger
    /// Xeq, the more power flows during the fault, the longer it can last): bolted three-phase (Xeq = 0, Pe = 0) &lt; LLG
    /// (Z2 || Z0) &lt; LL (Z2) &lt; SLG (Z2 + Z0). <see cref="ClassicalCct"/> derives, by the equal-area criterion, the
    /// classical clearing time of each type with the machine as a constant voltage behind a reactance. Behind X'' = 0.25 (the
    /// flux held at its prefault value) it gives 0.134, 0.177 and 0.273 s for three-phase, LLG and LL (0.354 s for SLG), an upper bound: the
    /// subtransient flux decays during the fault with its field held, which lowers the power the machine can send. Behind
    /// X'q = 0.55, the larger of the transient reactances (Xd' = 0.30, Xq' = 0.55) that the swing settles toward, it gives 0.082,
    /// 0.099 and 0.134 s (0.159 s for SLG), an estimate of a lower bound (the floor is an argument about where the swing settles, not a proof). Each GridSim figure lies between the two to within the 5 ms search step. The GridSim
    /// figures (steps of 5 ms: 17, 22, 33 and 41, so 0.085, 0.110, 0.165 and 0.205 s) are also pinned as regression values.
    /// The bolted three-phase bracket is also the bolted <see cref="RmsBusFault"/> search's, exactly.</summary>
    [Fact]
    public void Smib_critical_clearing_times_order_three_phase_llg_ll_slg()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Smib();
        CctBracket Cct(FaultType t) => RmsTransientStability.CriticalClearingBracketSequenceFault(m, pf, data, 1, t, 0.0, 0.0,
            Array.Empty<RmsEvent>(), stepSeconds: Step, maxSeconds: 1.0);
        CctBracket b3 = Cct(FaultType.ThreePhase), llg = Cct(FaultType.DoubleLineToGround),
            ll = Cct(FaultType.LineToLine), slg = Cct(FaultType.SingleLineToGround);
        foreach (CctBracket b in new[] { b3, llg, ll, slg })
        {
            Assert.False(b.UnstableAtZero);
            Assert.NotNull(b.UnstableSteps);
        }
        Assert.True(b3.StableSeconds < llg.StableSeconds, $"3ph {b3.StableSeconds} vs LLG {llg.StableSeconds}");
        Assert.True(llg.StableSeconds < ll.StableSeconds, $"LLG {llg.StableSeconds} vs LL {ll.StableSeconds}");
        Assert.True(ll.StableSeconds < slg.StableSeconds, $"LL {ll.StableSeconds} vs SLG {slg.StableSeconds}");
        Assert.Equal(new[] { 17, 22, 33, 41 }, new[] { b3.StableSteps, llg.StableSteps, ll.StableSteps, slg.StableSteps });
        foreach ((FaultType t, CctBracket b) in new[]
                 { (FaultType.ThreePhase, b3), (FaultType.DoubleLineToGround, llg), (FaultType.LineToLine, ll), (FaultType.SingleLineToGround, slg) })
        {
            double classical = ClassicalCct(m, pf, t, 0.0);
            Assert.True(b.StableSeconds <= classical + Step, $"{t}: GridSim {b.StableSeconds} s above the classical {classical} s");
            double lower = ClassicalCct(m, pf, t, 0.0, xs: 0.55);
            Assert.True(b.StableSeconds >= lower - Step, $"{t}: GridSim {b.StableSeconds} s below the classical {lower} s behind X'q");
        }
        CctBracket bolted = RmsTransientStability.CriticalClearingBracketEvents(m, pf, data, 1, Array.Empty<RmsEvent>(),
            stepSeconds: Step, maxSeconds: 1.0);
        Assert.Equal(bolted.StableSteps, b3.StableSteps);
        Assert.Equal(bolted.UnstableSteps, b3.UnstableSteps);
    }

    // ---- Faults along a line ---------------------------------------------------------------------------------------

    /// <summary>A fault 30 % along line 4-5 equals the static study on the split model (the static fault-along-line split,
    /// <see cref="FaultAlongLine.Build"/>, solved by its own power flow): 1e-8 relative, derived from the two power flows'
    /// 1e-12 pu mismatch amplified by the network's condition (below 1e4). The line's charging is set to 0 here: two lumped
    /// pi sections are not the same network as one, so with charging the split case's own power flow is a slightly
    /// different operating point (about 3e-5 pu on case9) from the unsplit line the RMS run starts from, and the comparison
    /// would measure that modelling difference rather than the method. With no charging the split is exact.</summary>
    [Theory]
    [InlineData(FaultType.SingleLineToGround, 0.02)]
    [InlineData(FaultType.LineToLine, 0.0)]
    [InlineData(FaultType.DoubleLineToGround, 0.0)]
    [InlineData(FaultType.ThreePhase, 0.03)]
    public void A_fault_along_a_line_equals_the_static_study_on_the_split_line(FaultType type, double xf)
    {
        (GridModel m9, _) = EarthedCase9();
        int j = BranchBetween(m9, 4, 5);
        GridModel m0 = m9.CopyWith(branches: m9.Branches.Select((b, i) => i == j ? b with { B = 0.0 } : b).ToList());
        var opts = new PowerFlowOptions { Tolerance = 1e-12 };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m0, opts);
        RmsFaultStudyResult st = Run(m0, pf, new RmsBranchSequenceFault(0.1, j, 0.3, type, 0.0, xf), new RmsBranchFaultClear(0.2, j));
        RmsFaultSample s = st.Faults[0].Samples[0];
        Assert.Equal(j, st.Faults[0].BranchIndex);
        FaultNetwork split = FaultAlongLine.Build(m0, j, 0.3);
        PowerFlowResult pfSplit = new NewtonRaphsonPowerFlow().Solve(split.Model, opts);
        Assert.True(pfSplit.Converged);
        if (type == FaultType.ThreePhase)
        {
            FaultResult f = FaultAnalysis.ThreePhase(split.Model, pfSplit, split.FaultBusId, xf, 0.22);
            Assert.True(Math.Abs(f.FaultCurrentPu - s.I1.Magnitude) <= 1e-8 * f.FaultCurrentPu, $"{f.FaultCurrentPu} vs {s.I1.Magnitude}");
            return;
        }
        UnbalancedFaultResult u = FaultAnalysis.Unbalanced(split.Model, pfSplit, split.FaultBusId, type, xf, 0.22);
        Close(u.I1, s.I1, 1e-8, "I1");
        Close(u.I2, s.I2, 1e-8, "I2");
        Close(u.I0, s.I0, 1e-8, "I0");
        Close(u.Z2Pu, s.Z2Pu, 1e-10, "Z2");
        if (type != FaultType.LineToLine) Close(u.Z0Pu, s.Z0Pu, 1e-12, "Z0");
    }

    [Fact]
    public void Tripping_a_faulted_line_removes_the_fault_current_and_recomputes_the_thevenin_values()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        int j = BranchBetween(m, 4, 5), k = BranchBetween(m, 5, 6);
        // Contract change in 2.1.098 (the event list below used an LL fault on line 4-5 while the SLG fault at bus 7 was in
        // place). Derivation: an unbalanced fault joins the sequence networks at its point (SLG: in series; LL: the negative
        // in parallel with the positive). Two such faults join them at two points, so the faulted network is a two-port in
        // each sequence and the two faults' currents depend on each other through the negative and zero sequences. One
        // equivalent shunt per fault, each from its own Thevenin values, cannot represent that, so the second unbalanced fault
        // is now refused. A balanced fault is the same shunt Yf from each phase to earth: in symmetrical components
        // A^-1 (Yf I) A = Yf I, so it is the shunt Yf in each of the positive, negative and zero sequences and stamping it
        // there is exact. The line fault is therefore made three-phase, and the bus fault's Z2 now includes it.
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => Run(m, pf,
            new RmsSequenceFault(0.05, 7, FaultType.SingleLineToGround, 0.0, 0.02),
            new RmsBranchSequenceFault(0.1, j, 0.5, FaultType.LineToLine), new RmsBranchTrip(0.15, j),
            new RmsBranchTrip(0.2, k), new RmsBranchFaultClear(0.25, j), new RmsFaultClear(0.25, 7)));
        Assert.Contains("cross-country", ex.Message, StringComparison.Ordinal);
        RmsFaultStudyResult st = Run(m, pf,
            new RmsSequenceFault(0.05, 7, FaultType.SingleLineToGround, 0.0, 0.02),
            new RmsBranchSequenceFault(0.1, j, 0.5, FaultType.ThreePhase), new RmsBranchTrip(0.15, j),
            new RmsBranchTrip(0.2, k), new RmsBranchFaultClear(0.25, j), new RmsFaultClear(0.25, 7));
        RmsFaultTrace bus = st.Faults.Single(f => f.BusId == 7), line = st.Faults.Single(f => f.BranchIndex == j);
        Assert.All(line.Samples.Where(s => s.TimeSeconds < 0.15 - 1e-9), s => Assert.True(s.Energised && s.I1.Magnitude > 0.5));
        Assert.All(line.Samples.Where(s => s.TimeSeconds > 0.15 - 1e-9), s => Assert.True(!s.Energised && s.I1 == Complex.Zero));
        // The bus fault's Z2 changes when the balanced line fault comes on (it is in the negative sequence too) and with
        // every topology stage after (line 4-5 out, which also removes the line fault, then 5-6 out too).
        Complex Z2At(double t) => bus.Samples.First(s => Math.Abs(s.TimeSeconds - t) < 1e-9).Z2Pu;
        Assert.NotEqual(Z2At(0.05), Z2At(0.1));
        Assert.NotEqual(Z2At(0.1), Z2At(0.15));
        Assert.NotEqual(Z2At(0.15), Z2At(0.2));
        Assert.Equal(Z2At(0.2), Z2At(0.245));
    }

    // ---- Earthing, refusals and inputs -----------------------------------------------------------------------------

    [Fact]
    public void An_unearthed_network_carries_no_earth_fault_current_and_llg_falls_back_to_ll()
    {
        GridModel m = IeeeCases.Case9();   // no winding or machine earthing data: no zero-sequence earth path
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        RmsFaultSample slg = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.SingleLineToGround), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Assert.False(slg.Grounded);
        Assert.Equal(Complex.Zero, slg.I1);
        Assert.Equal(Complex.Zero, slg.I0);
        RmsFaultSample llg = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.DoubleLineToGround), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        RmsFaultSample ll = Run(m, pf, new RmsSequenceFault(0.1, 7, FaultType.LineToLine), new RmsFaultClear(0.2, 7)).Faults[0].Samples[0];
        Assert.Equal(ll.I1, llg.I1);
        Assert.Equal(ll.I2, llg.I2);
        Assert.Equal(Complex.Zero, llg.I0);
    }

    [Fact]
    public void Bad_sequence_fault_inputs_are_refused_by_name()
    {
        (GridModel m, PowerFlowResult pf) = EarthedCase9();
        IReadOnlyList<SubtransientInit.MachineData> data = GenrouData(m);
        void Refused<T>(params RmsEvent[] ev) where T : Exception
            => Assert.ThrowsAny<T>(() => RmsTransientStability.StudyEvents(m, pf, data, ev, stepSeconds: Step, durationSeconds: 0.3));
        int line = BranchBetween(m, 4, 5), tx = BranchBetween(m, 1, 4);
        Refused<ArgumentException>(new RmsSequenceFault(0.1, 99, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsSequenceFault(0.1, 7, FaultType.LineToLine, -0.1, 0.0));
        Refused<ArgumentException>(new RmsSequenceFault(0.1, 7, FaultType.LineToLine, double.NaN, 0.0));
        Refused<ArgumentException>(new RmsSequenceFault(0.1, 7, (FaultType)42));
        Refused<ArgumentException>(new RmsBusFault(0.1, 7), new RmsSequenceFault(0.15, 7, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsSequenceFault(0.1, 7, FaultType.LineToLine), new RmsBusFault(0.15, 7));
        Refused<ArgumentException>(new RmsBranchSequenceFault(0.1, line, 0.0, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsBranchSequenceFault(0.1, line, 1.0, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsBranchSequenceFault(0.1, tx, 0.5, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsBranchSequenceFault(0.1, 99, 0.5, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsBranchTrip(0.05, line), new RmsBranchSequenceFault(0.1, line, 0.5, FaultType.LineToLine));
        Refused<ArgumentException>(new RmsBranchFaultClear(0.1, line));
    }

    [Fact]
    public void The_cli_event_forms_parse_to_the_sequence_events()
    {
        IReadOnlyList<RmsEvent> ev = RmsEventSpec.Parse("sfault:slg:7@0.1,clear:7@0.2,sfault:3ph:5:0.01:0.05@0.3,lfault:llg:1:0.25@0.4,lfault:ll:2:0.5:0:0.1@0.45,lclear:1@0.5");
        Assert.Equal(new RmsSequenceFault(0.1, 7, FaultType.SingleLineToGround), ev[0]);
        Assert.Equal(new RmsSequenceFault(0.3, 5, FaultType.ThreePhase, 0.01, 0.05), ev[2]);
        Assert.Equal(new RmsBranchSequenceFault(0.4, 1, 0.25, FaultType.DoubleLineToGround), ev[3]);
        Assert.Equal(new RmsBranchSequenceFault(0.45, 2, 0.5, FaultType.LineToLine, 0.0, 0.1), ev[4]);
        Assert.Equal(new RmsBranchFaultClear(0.5, 1), ev[5]);
        Assert.Throws<FormatException>(() => RmsEventSpec.Parse("sfault:abc:7@0.1"));
        Assert.Throws<FormatException>(() => RmsEventSpec.Parse("sfault:slg:7:0.1@0.1"));
        Assert.Equal("single line to ground fault at bus 7 at 0.1 s", ev[0].Describe());
        Assert.Equal("three-phase fault at bus 5 through Zf = 0.01 + j0.05 pu at 0.3 s", ev[2].Describe());
    }

    [Fact]
    public void The_script_fault_statement_takes_a_type_and_an_impedance_and_keeps_the_bolted_form()
    {
        Statement? plain = Parser.ParseLineText("FAULT BUS \"7\"");
        Assert.Equal("FAULT BUS \"7\"", plain!.Canonical());
        var typed = Assert.IsType<FaultStatement>(Parser.ParseLineText("FAULT BUS \"7\" slg WITH R 0 PU X 0.05 PU"));
        Assert.Equal("SLG", typed.FaultType);
        Assert.Equal(0.05m, typed.FaultX!.Value.Value);
        Assert.Equal("FAULT BUS \"7\" SLG WITH R 0 PU X 0.05 PU", typed.Canonical());
        Assert.Equal("FAULT BUS \"7\" LL", Parser.ParseLineText("FAULT BUS \"7\" LL")!.Canonical());
        Assert.ThrowsAny<ScriptException>(() => Parser.ParseLineText("FAULT BUS \"7\" SLG WITH R 0 MW X 0.05 PU"));
    }

    // ---- 4. Determinism --------------------------------------------------------------------------------------------

    private static string FaultDigest(RmsFaultStudyResult r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(FullModelKernelDigestTests.Digest(r.Result, Array.Empty<RmsEvent>()));
            void C(Complex z) { w.Write(BitConverter.DoubleToInt64Bits(z.Real)); w.Write(BitConverter.DoubleToInt64Bits(z.Imaginary)); }
            foreach (RmsFaultTrace f in r.Faults)
            {
                w.Write(f.EventIndex);
                foreach (RmsFaultSample s in f.Samples)
                {
                    w.Write(BitConverter.DoubleToInt64Bits(s.TimeSeconds)); w.Write(s.Energised); w.Write(s.Grounded);
                    C(s.V1); C(s.V2); C(s.V0); C(s.I1); C(s.I2); C(s.I0); C(s.Z2Pu); C(s.Z0Pu);
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static RmsEvent[] MixedEvents(GridModel m) => new RmsEvent[]
    {
        new RmsSequenceFault(0.1, 7, FaultType.SingleLineToGround, 0.0, 0.02),
        new RmsFaultClear(0.18, 7),
        new RmsBranchSequenceFault(0.3, BranchBetween(m, 4, 5), 0.3, FaultType.DoubleLineToGround, 0.01, 0.0),
        new RmsBranchTrip(0.38, BranchBetween(m, 4, 5)),
        new RmsBranchFaultClear(0.4, BranchBetween(m, 4, 5)),
        new RmsBranchReclose(0.7, BranchBetween(m, 4, 5)),
    };

    [Fact]
    public void Dense_and_sparse_power_flow_repeated_and_parallel_runs_give_the_same_bits()
    {
        (GridModel m, _) = EarthedCase9();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m), sparse = new SparseNewtonRaphsonPowerFlow().Solve(m);
        IReadOnlyList<SubtransientInit.MachineData> data = GenrouData(m);
        RmsEvent[] ev = MixedEvents(m);
        RmsFaultStudyResult Go(PowerFlowResult pf) => RmsTransientStability.StudyEventsWithFaults(m, pf, data, ev, stepSeconds: Step, durationSeconds: 1.0);
        string want = FaultDigest(Go(dense));
        Assert.Equal(want, FaultDigest(Go(sparse)));
        Assert.Equal(want, FaultDigest(Go(dense)));
        var par = new string[4];
        Parallel.For(0, par.Length, i => par[i] = FaultDigest(Go(i % 2 == 0 ? dense : sparse)));
        Assert.All(par, d => Assert.Equal(want, d));
    }

    /// <summary>The built CLI in the three SIMD modes the release gate runs (default, DOTNET_EnableAVX2=0,
    /// DOTNET_EnableHWIntrinsic=0) writes the same fault-point and machine CSV rows, so the numbers are the same bits at
    /// every vector tier.</summary>
    [Fact]
    public void The_cli_fault_point_export_is_the_same_bits_in_every_simd_mode()
    {
        string root = TestTemp.Under("gridsim-wp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var modes = new (string Name, Dictionary<string, string?> Env)[]
            {
                ("default", new Dictionary<string, string?>()),
                ("noavx2", new Dictionary<string, string?> { ["DOTNET_EnableAVX2"] = "0" }),
                ("nohw", new Dictionary<string, string?> { ["DOTNET_EnableHWIntrinsic"] = "0" }),
            };
            string? wantFaults = null, wantMachines = null;
            foreach ((string name, Dictionary<string, string?> env) in modes)
            {
                string outDir = Path.Combine(root, name);
                CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "stability", "case9", "--rms", "--events",
                    "sfault:ll:7:0:0.05@0.1,clear:7@0.183,lfault:3ph:1:0.3:0:0.02@0.5,lclear:1@0.56", "--events-out", outDir);
                Assert.True(r.ExitCode == 0, r.ToString());
                Assert.Contains("fault points (positive-sequence representation", r.StdOut, StringComparison.Ordinal);
                string Rows(string file) => string.Join("\n", File.ReadAllLines(Path.Combine(outDir, file)).Where(l => !l.StartsWith('#')));
                string faults = Rows(RmsFaultPointsExport.FileName), machines = Rows(RmsEventsExport.MachinesCsv);
                Assert.StartsWith(RmsFaultPointsExport.Header, faults, StringComparison.Ordinal);
                wantFaults ??= faults;
                wantMachines ??= machines;
                Assert.Equal(wantFaults, faults);
                Assert.Equal(wantMachines, machines);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
