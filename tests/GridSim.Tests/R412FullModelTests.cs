using System.Numerics;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Dynamics.SmallSignal.Full;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-12 worked cases 1, 2 and 5 and the refusal contracts of the full-model assembler.
/// <para>Case 1: the classical SMIB against its closed form, and the delegated classical path against the incumbent
/// <c>modal</c> bit for bit. Case 2: Heffron-Phillips constants K1 to K6 derived in this test from the SMIB dq equations
/// (deMello and Concordia, 1969) and checked against central differences of those same equations; the fourth-order K-constant
/// model's matrix and eigenvalues are the oracle for the assembled model with an AVR. Case 5: the PSS added to the K-constant
/// model by explicit state-space and compared in direction and magnitude.</para>
/// </summary>
public sealed class R412FullModelTests
{
    private const double Ws = 2.0 * Math.PI * R412Fixtures.F0;

    // ---- case 1 ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(90.0, 1.0, 0.4, 0.0)]
    [InlineData(60.0, 1.02, 0.6, 1.5)]
    public void Classical_SMIB_matches_the_closed_form_natural_frequency_and_damping(double pgMw, double vt, double xe, double damping)
    {
        const double h = 3.5, xdp = 0.3, eb = 1.0;
        (GridModel model, PowerFlowResult pf) = R412Fixtures.Smib(pgMw, vt, xe, eb, h);
        var specs = new List<FullMachineSpec>
        {
            new(0, FullMachineModel.Classical, Xdp: R412Fixtures.XInf, InfiniteBus: true),
            new(1, FullMachineModel.Classical, Xdp: xdp, DampingSystemPu: damping),
        };
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.False(fm.DelegatedToClassical);          // the infinite bus takes the dq path
        Assert.Equal(2, fm.Order);

        // independent circuit solution; the closed form K = E E_inf cos(delta) / X
        (Complex v2, Complex i2, Complex v1) = R412Fixtures.SmibCircuit(pgMw / 100.0, vt, xe, eb);
        Complex ePrime = v2 + new Complex(0.0, xdp) * i2;
        Complex eInf = v1 - new Complex(0.0, R412Fixtures.XInf) * i2;
        double xTotal = xdp + xe + R412Fixtures.XInf;
        double delta = ePrime.Phase - eInf.Phase;
        Assert.Equal(pgMw / 100.0, ePrime.Magnitude * eInf.Magnitude * Math.Sin(delta) / xTotal, 9);   // the oracle's own power balance
        double k = ePrime.Magnitude * eInf.Magnitude * Math.Cos(delta) / xTotal;
        double wn = Math.Sqrt(k * Ws / (2.0 * h));

        FullModalResult r = FullModalAnalysis.Analyze(fm);
        Mode osc = r.Modes.Select(m => m.Mode).First(m => m.IsOscillatory && m.Eigenvalue.Imaginary > 0);
        double sigma = -damping / (4.0 * h);
        double wd = Math.Sqrt(wn * wn - sigma * sigma);
        Assert.Equal(wd, osc.Eigenvalue.Imaginary, 7);
        Assert.Equal(sigma, osc.Eigenvalue.Real, 8);
        Assert.Equal(FullModalStatus.Complete, r.Status);
        Assert.True(r.Modes.All(m => m.ResidualPassed));
    }

    [Fact]
    public void Without_flux_or_controllers_the_matrix_and_modes_are_the_incumbent_modal_bit_for_bit()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        double[] xdp = model.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu[g.Bus]).ToArray();
        var specs = model.Generators.Select((g, i) => new FullMachineSpec(i, FullMachineModel.Classical, Xdp: xdp[i])).ToList();
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.True(fm.DelegatedToClassical);

        SmallSignalModel inc = SmallSignalAssembler.FromSolvedCase(model, pf, xdp, null);
        Assert.Equal(inc.Order, fm.Order);
        for (int i = 0; i < inc.Order; i++)
            for (int j = 0; j < inc.Order; j++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(inc.A[i, j]), BitConverter.DoubleToInt64Bits(fm.A[i, j]));

        ModalResult mr = ModalAnalysis.Analyze(inc);
        FullModalResult r = FullModalAnalysis.Analyze(fm);
        Assert.Equal(mr.Modes.Count, r.Modes.Count);
        Assert.Equal(mr.RhpEigenvalueCount, r.RhpCount);
        for (int i = 0; i < mr.Modes.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(mr.Modes[i].Eigenvalue.Real), BitConverter.DoubleToInt64Bits(r.Modes[i].Mode.Eigenvalue.Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(mr.Modes[i].Eigenvalue.Imaginary), BitConverter.DoubleToInt64Bits(r.Modes[i].Mode.Eigenvalue.Imaginary));
            Assert.Equal(mr.Modes[i].DampingRatio, r.Modes[i].Mode.DampingRatio);
            for (int k = 0; k < inc.Order; k++)
                Assert.Equal(mr.Modes[i].ParticipationByState[k], r.Modes[i].Mode.ParticipationByState[k]);
        }
        Assert.All(r.Modes, m => Assert.True(m.ResidualPassed));
    }

    [Fact]
    public void The_dq_network_elimination_reproduces_the_classical_swing_eigenvalues_on_a_three_machine_network()
    {
        // Independent cross-check of the Xq-behind-reactance reduction and its closed-form E_Q/id loop: with Xd = X'd = Xq a
        // flux-decay machine IS the classical machine plus a decoupled E'q pole at -1/T'd0 (block triangular), so the damped
        // classical eigenvalues must all be found in the full model together with three poles at -1/T'd0.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        double[] xdp = model.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu[g.Bus]).ToArray();
        const double d = 2.0, tdop = 8.0;
        var specs = model.Generators.Select((g, i) => new FullMachineSpec(i, FullMachineModel.FluxDecay, Xdp: xdp[i], Xd: xdp[i], Xq: xdp[i], Tdop: tdop, DampingSystemPu: d)).ToList();
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.False(fm.DelegatedToClassical);
        Assert.Equal(9, fm.Order);

        SmallSignalModel inc = SmallSignalAssembler.FromSolvedCase(model, pf, xdp, new[] { d, d, d });
        Complex[] ref6 = GeneralEigen.Eigenvalues(inc.A).ToArray();
        Complex[] full = GeneralEigen.Eigenvalues(fm.A).ToArray();
        foreach (Complex e in ref6)
            Assert.True(full.Min(f => Complex.Abs(f - e)) < 1e-8, $"classical eigenvalue {e} missing from the dq-path model");
        Assert.Equal(3, full.Count(f => Complex.Abs(f - new Complex(-1.0 / tdop, 0.0)) < 1e-9));
    }

    // ---- case 2: Heffron-Phillips ---------------------------------------------------------------------------------------------

    private sealed record Hp(double K1, double K2, double K3, double K4, double K5, double K6, double Delta0, double Eqp0, double Efd0, double Vt0,
        double Id0, double Iq0, double Eb, double XeTot);

    private const double Hh = 3.5, Xd = 1.6, Xq = 1.55, Xdp = 0.32, Tdop = 6.0, XeLine = 0.45, Pm = 0.9, Vt = 1.0, Damp = 2.0;

    /// <summary>The operating point from the circuit and K1..K6 from the SMIB dq equations, no GridSim solver involved.</summary>
    private static Hp HeffronPhillips()
    {
        (Complex v2, Complex i2, Complex v1) = R412Fixtures.SmibCircuit(Pm, Vt, XeLine, 1.0);
        Complex eInf = v1 - new Complex(0.0, R412Fixtures.XInf) * i2;
        Complex rot = Complex.Conjugate(eInf) / eInf.Magnitude;          // reference angle at the infinite bus
        double eb = eInf.Magnitude, xe = XeLine + R412Fixtures.XInf;
        Complex v = v2 * rot, i = i2 * rot;
        Complex eQ = v + new Complex(0.0, Xq) * i;
        double delta = eQ.Phase;
        Complex z = i * Complex.Exp(new Complex(0.0, -delta));
        double iq = z.Real, id = -z.Imaginary;
        double vd = Xq * iq, vq = eQ.Magnitude - Xq * id;
        double vt = Math.Sqrt(vd * vd + vq * vq);
        Assert.Equal(v2.Magnitude, vt, 10);
        double eqp = vq + Xdp * id;
        double efd = eqp + (Xd - Xdp) * id;

        // the SMIB algebra as written by H-P: iq = Eb sin(d)/XqT, id = (E'q - Eb cos d)/XdpT
        double xqT = Xq + xe, xdpT = Xdp + xe, xdT = Xd + xe;
        (double te, double vtm, double idm, double iqm) Alg(double dl, double ep)
        {
            double iqx = eb * Math.Sin(dl) / xqT, idx = (ep - eb * Math.Cos(dl)) / xdpT;
            double ted = ep * iqx + (Xq - Xdp) * idx * iqx;
            double vdx = eb * Math.Sin(dl) - xe * iqx, vqx = eb * Math.Cos(dl) + xe * idx;
            return (ted, Math.Sqrt(vdx * vdx + vqx * vqx), idx, iqx);
        }
        var op = Alg(delta, eqp);
        Assert.Equal(Pm, op.te, 9);                    // the SMIB algebra reproduces the circuit's operating point
        Assert.Equal(id, op.idm, 9);
        Assert.Equal(iq, op.iqm, 9);
        Assert.Equal(vt, op.vtm, 9);

        double eq0 = eqp + (Xq - Xdp) * id;
        double k1 = eq0 * eb * Math.Cos(delta) / xqT + (Xq - Xdp) / xdpT * iq * eb * Math.Sin(delta);
        double k2 = iq * xqT / xdpT;
        double k3 = xdpT / xdT;
        double k4 = (Xd - Xdp) * eb * Math.Sin(delta) / xdpT;
        double k5 = vd / vt * eb * Math.Cos(delta) * Xq / xqT - vq / vt * eb * Math.Sin(delta) * Xdp / xdpT;
        double k6 = vq / vt * xe / xdpT;

        // the constants are the partial derivatives of the algebra: central differences (independent of the formulas above)
        const double e = 1e-6;
        Assert.Equal((Alg(delta + e, eqp).te - Alg(delta - e, eqp).te) / (2 * e), k1, 6);
        Assert.Equal((Alg(delta, eqp + e).te - Alg(delta, eqp - e).te) / (2 * e), k2, 6);
        Assert.Equal((Alg(delta + e, eqp).vtm - Alg(delta - e, eqp).vtm) / (2 * e), k5, 6);
        Assert.Equal((Alg(delta, eqp + e).vtm - Alg(delta, eqp - e).vtm) / (2 * e), k6, 6);
        // K3 and K4 from the field equation T'd0 dE'q/dt = Efd - E'q - (Xd - X'd) id
        double F(double dl, double ep) => -ep - (Xd - Xdp) * Alg(dl, ep).idm;
        Assert.Equal(-(F(delta, eqp + e) - F(delta, eqp - e)) / (2 * e), 1.0 / k3, 6);
        Assert.Equal(-(F(delta + e, eqp) - F(delta - e, eqp)) / (2 * e), k4, 6);
        return new Hp(k1, k2, k3, k4, k5, k6, delta, eqp, efd, vt, id, iq, eb, xe);
    }

    private static (GridModel Model, PowerFlowResult Pf, List<FullMachineSpec> Specs) HpCase(UserModel? exciter, UserModel? stabiliser)
    {
        (GridModel model, PowerFlowResult pf) = R412Fixtures.Smib(Pm * 100.0, Vt, XeLine, 1.0, Hh);
        var specs = new List<FullMachineSpec>
        {
            new(0, FullMachineModel.Classical, Xdp: R412Fixtures.XInf, InfiniteBus: true),
            new(1, FullMachineModel.FluxDecay, Xdp: Xdp, Xd: Xd, Xq: Xq, Tdop: Tdop, DampingSystemPu: Damp, Exciter: exciter, Stabiliser: stabiliser),
        };
        return (model, pf, specs);
    }

    private static double[,] KModel(Hp k, double ka, double ta)
    {
        var a = new double[4, 4];
        a[0, 1] = Ws;
        a[1, 0] = -k.K1 / (2 * Hh); a[1, 1] = -Damp / (2 * Hh); a[1, 2] = -k.K2 / (2 * Hh);
        a[2, 0] = -k.K4 / Tdop; a[2, 2] = -1.0 / (k.K3 * Tdop); a[2, 3] = 1.0 / Tdop;
        a[3, 0] = -ka * k.K5 / ta; a[3, 2] = -ka * k.K6 / ta; a[3, 3] = -1.0 / ta;
        return a;
    }

    private static void AssertSameSpectrum(double[,] x, double[,] y, double tol)
    {
        Complex[] ex = GeneralEigen.Eigenvalues(x).ToArray(), ey = GeneralEigen.Eigenvalues(y).ToArray();
        Assert.Equal(ex.Length, ey.Length);
        foreach (Complex e in ex) Assert.True(ey.Min(f => Complex.Abs(f - e)) < tol * (1 + Complex.Abs(e)), $"{e} has no partner");
        foreach (Complex e in ey) Assert.True(ex.Min(f => Complex.Abs(f - e)) < tol * (1 + Complex.Abs(e)), $"{e} has no partner");
    }

    [Fact]
    public void Heffron_Phillips_with_an_AVR_the_assembled_matrix_and_eigenvalues_equal_the_K_constant_model()
    {
        const double ka = 50.0, ta = 0.05;
        Hp k = HeffronPhillips();
        (GridModel model, PowerFlowResult pf, List<FullMachineSpec> specs) = HpCase(R412Fixtures.Sexs(ka, ta), null);
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.Equal(4, fm.Order);
        Assert.True(fm.AllControllersLinearised);
        Assert.Equal(k.Efd0, fm.Machines[0].EfdPu, 7);
        Assert.Equal(k.Eqp0, fm.Machines[0].EqpPu, 7);

        // matrix entry by entry (states delta, omega, E'q, Efd in both)
        double[,] km = KModel(k, ka, ta);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True(Math.Abs(km[i, j] - fm.A[i, j]) <= 1e-6 * (1.0 + Math.Abs(km[i, j])), $"A[{i},{j}] K-model {km[i, j]:R} vs assembled {fm.A[i, j]:R}");
        AssertSameSpectrum(km, fm.A, 1e-6);

        FullModalResult r = FullModalAnalysis.Analyze(fm);
        Assert.Equal(FullModalStatus.Complete, r.Status);
        Assert.Equal(4, r.Modes.Count);
        // participation of the exciter state is reported in its own category
        FullMode swing = r.Modes.First(m => m.Mode.IsOscillatory && m.Mode.Eigenvalue.Imaginary > 0 && m.Mode.FrequencyHz < 4.0);
        Assert.True(swing.ParticipationByCategory[(int)FullStateCategory.MachineAngle] > 0.0,
            $"swing {swing.Mode.Eigenvalue} avail {swing.ParticipationAvailable} reason {r.ParticipationUnavailableReason} method {r.Method} cats {string.Join(",", swing.ParticipationByCategory)} modes {string.Join(" | ", r.Modes.Select(m => m.Mode.Eigenvalue.ToString()))}");
        Assert.Equal(1.0, swing.ParticipationByCategory.Sum(), 9);
    }

    [Fact]
    public void A_PSS_changes_the_swing_damping_by_the_amount_the_extended_K_constant_model_predicts()
    {
        const double ka = 50.0, ta = 0.05, ks = 20.0, tw = 10.0, t1 = 0.3, t2 = 0.03, t3 = 0.3, t4 = 0.03;
        Hp k = HeffronPhillips();
        UserModel pss = UserModelLibrary.Load("PSS1A", new Dictionary<string, double> { ["Ks"] = ks, ["Tw"] = tw, ["T1"] = t1, ["T2"] = t2, ["T3"] = t3, ["T4"] = t4 });
        (GridModel model, PowerFlowResult pf, List<FullMachineSpec> specs) = HpCase(R412Fixtures.Sexs(ka, ta), pss);
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.Equal(7, fm.Order);

        // extended K-constant model: states delta, omega, E'q, Efd, xw, x1, x2 with the PSS1A written out stage by stage
        double c1 = t1 / t2, c2 = t3 / t4;
        var a = new double[7, 7];
        double[,] k4 = KModel(k, ka, ta);
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) a[i, j] = k4[i, j];
        // y1 = ks*w - xw ; y2 = c1*y1 + x1 ; y3 = c2*y2 + x2 = vs
        double[] y1 = { 0, ks, 0, 0, -1, 0, 0 };
        double[] y2 = y1.Select((v, i) => c1 * v + (i == 5 ? 1.0 : 0.0)).ToArray();
        double[] y3 = y2.Select((v, i) => c2 * v + (i == 6 ? 1.0 : 0.0)).ToArray();
        for (int j = 0; j < 7; j++)
        {
            a[3, j] += ka * y3[j] / ta;                                  // vs joins the regulator error
            a[4, j] = (j == 1 ? ks / tw : 0.0) + (j == 4 ? -1.0 / tw : 0.0);
            a[5, j] = ((1.0 - c1) * y1[j] - (j == 5 ? 1.0 : 0.0)) / t2;
            a[6, j] = ((1.0 - c2) * y2[j] - (j == 6 ? 1.0 : 0.0)) / t4;
        }
        for (int i = 0; i < 7; i++)
            for (int j = 0; j < 7; j++)
                Assert.True(Math.Abs(a[i, j] - fm.A[i, j]) <= 1e-6 * (1.0 + Math.Abs(a[i, j])), $"A[{i},{j}] extended K-model {a[i, j]:R} vs assembled {fm.A[i, j]:R}");
        AssertSameSpectrum(a, fm.A, 1e-6);

        // damping of the swing mode with and without the stabiliser, both models
        double Zeta(double[,] m, double wGuess)
        {
            Complex e = GeneralEigen.Eigenvalues(m).Where(x => x.Imaginary > 0).OrderBy(x => Math.Abs(x.Imaginary - wGuess)).First();
            return -e.Real / Complex.Abs(e);
        }
        double w0 = Math.Sqrt(k.K1 * Ws / (2 * Hh));
        double zFullPss = Zeta(fm.A, w0), zKPss = Zeta(a, w0);
        (GridModel m0, PowerFlowResult p0, List<FullMachineSpec> s0) = HpCase(R412Fixtures.Sexs(ka, ta), null);
        FullSmallSignalModel noPss = FullModelAssembler.Assemble(m0, p0, s0);
        double zFull = Zeta(noPss.A, w0), zK = Zeta(k4, w0);
        Assert.Equal(zKPss - zK, zFullPss - zFull, 6);                  // the same change in damping
        Assert.True(zKPss > zK, $"the stabiliser must add damping in the extended K-model (zeta {zK:F5} -> {zKPss:F5})");
        Assert.True(zFullPss > zFull);
    }

    // ---- refusals and reporting --------------------------------------------------------------------------------------------------

    private const string GovernorWithDelay = """
    {
      "format": "gridsim-model/1", "id": "GOVDLY", "role": "governor", "source": "test fixture",
      "ports": { "inputs": [ { "name": "speed", "unit": "pu" } ], "outputs": [ { "name": "pm", "unit": "pu", "from": "pmech" } ] },
      "blocks": [
        { "id": "pref", "type": "hold" },
        { "id": "dly", "type": "delay", "inputs": ["speed"], "params": { "steps": 2 } },
        { "id": "pmech", "type": "sum", "inputs": ["pref", "dly"], "signs": "++" }
      ],
      "initialisation": { "inputs": [ { "name": "pm0", "unit": "pu" } ], "steps": [
        { "action": "initialise", "block": "pref", "values": ["pm0"] },
        { "action": "initialise", "block": "dly", "values": [0.0] } ] }
    }
    """;

    [Fact]
    public void A_controller_that_cannot_be_linearised_is_reported_and_the_result_is_INCOMPLETE()
    {
        UserModel gov = UserModel.FromDocument(R412Fixtures.Doc(GovernorWithDelay));
        (GridModel model, PowerFlowResult pf) = R412Fixtures.Smib(Pm * 100.0, Vt, XeLine, 1.0, Hh);
        var specs = new List<FullMachineSpec>
        {
            new(0, FullMachineModel.Classical, Xdp: R412Fixtures.XInf, InfiniteBus: true),
            new(1, FullMachineModel.FluxDecay, Xdp: Xdp, Xd: Xd, Xq: Xq, Tdop: Tdop, DampingSystemPu: Damp,
                Exciter: R412Fixtures.Sexs(50.0, 0.05), Governor: gov, CodedControllers: new[] { "Esst4b" }),
        };
        FullSmallSignalModel fm = FullModelAssembler.Assemble(model, pf, specs);
        Assert.Equal(2, fm.NotLinearised.Count);
        Assert.Contains(fm.NotLinearised, n => n.Controller.Contains("GOVDLY", StringComparison.Ordinal) && n.Reason.Contains("fixed-delay", StringComparison.Ordinal));
        Assert.Contains(fm.NotLinearised, n => n.Controller == "Esst4b");
        Assert.Equal(4, fm.Order);                       // the delay is not approximated into extra states
        FullModalResult r = FullModalAnalysis.Analyze(fm);
        Assert.Equal(FullModalStatus.Incomplete, r.Status);
        Assert.Equal(2, r.IncompleteReasons.Count(s => s.StartsWith("not linearised", StringComparison.Ordinal)));
        Assert.Equal(2, r.NotLinearised.Count);
    }

    [Fact]
    public void Inconsistent_specs_are_refused_with_the_generator_named()
    {
        (GridModel model, PowerFlowResult pf) = R412Fixtures.Smib(90.0, 1.0, 0.4);
        Assert.Throws<ArgumentException>(() => FullModelAssembler.Assemble(model, pf, new[] { new FullMachineSpec(7) }));   // no such generator
        var exciterOnClassical = new FullMachineSpec(1, FullMachineModel.Classical, Exciter: R412Fixtures.Sexs(50.0, 0.05));
        var ex = Assert.Throws<ArgumentException>(() => FullModelAssembler.Assemble(model, pf, new[] { exciterOnClassical }));
        Assert.Contains("flux-decay", ex.Message, StringComparison.Ordinal);
        var badData = new FullMachineSpec(1, FullMachineModel.FluxDecay, Xdp: 0.3, Xd: 0.2, Xq: 1.0, Tdop: 5.0);
        Assert.Throws<ArgumentException>(() => FullModelAssembler.Assemble(model, pf, new[] { badData }));                  // Xd < X'd
        var pssNoExciter = new FullMachineSpec(1, FullMachineModel.FluxDecay, Xdp: 0.3, Xd: 1.6, Xq: 1.5, Tdop: 5.0, Stabiliser: UserModelLibrary.Load("PSS1A"));
        Assert.Throws<ArgumentException>(() => FullModelAssembler.Assemble(model, pf, new[] { pssNoExciter }));
    }

    [Fact]
    public void Assembly_and_analysis_are_repeatable_to_the_bit()
    {
        (GridModel model, PowerFlowResult pf, List<FullMachineSpec> specs) = HpCase(R412Fixtures.Sexs(50.0, 0.05), null);
        FullModalResult a = FullModalAnalysis.Analyze(FullModelAssembler.Assemble(model, pf, specs));
        FullModalResult b = FullModalAnalysis.Analyze(FullModelAssembler.Assemble(model, pf, specs));
        Assert.Equal(a.EigenvalueDigest, b.EigenvalueDigest);
        Assert.NotEqual("", a.EigenvalueDigest);
    }

    [Fact]
    public void The_new_namespace_uses_no_banned_maths_random_or_reflection()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "GridSim.slnx"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string full = Path.Combine(dir!, "src", "GridSim.Core", "Dynamics", "SmallSignal", "Full");
        Assert.True(Directory.Exists(full));
        var banned = new System.Text.RegularExpressions.Regex(
            @"Math\.(Sin|Cos|Exp|Log|Pow|Atan2|Tan|Sinh|Cosh|Tanh)\(|Hypot\(|System\.Random|new Random\(|Activator\.|System\.Reflection|Parallel\.|Task\.Run|Complex\.(Sqrt|Exp|Pow|Log|Sin|Cos)\(",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (string f in Directory.GetFiles(full, "*.cs"))
        {
            string text = File.ReadAllText(f);
            System.Text.RegularExpressions.Match m = banned.Match(text);
            Assert.False(m.Success, $"{Path.GetFileName(f)} uses {m.Value}");
        }
    }
}
