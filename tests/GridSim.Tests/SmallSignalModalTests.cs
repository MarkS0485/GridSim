using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The small-signal / modal path. Load-bearing invariants: the SMIB analytic frequency and damping, the
/// DEFLATED-COI participation (the default undamped case is defective and must be deflated), the
/// collapse-to-lossless-symmetric-ContinuumWave equality, the WSCC-9 electromechanical mode count/band,
/// the GFM faithful-reduction graceful limit at synInertia=0, and the real/synthetic separation.
/// </summary>
public class SmallSignalModalTests
{
    private const double F0 = 50.0;
    private static double Ws => 2.0 * Math.PI * F0;
    private static readonly double[] Wscc9Xdp = { 0.0608, 0.1198, 0.1813 };

    [Fact]
    public void SMIB_oscillatory_mode_matches_the_analytic_small_signal_frequency()
    {
        const double h = 3.5, ps = 1.732;
        var hArr = new[] { h, 1e8 };
        var k = new double[,] { { 0, ps }, { ps, 0 } };
        SmallSignalModel m = SmallSignalAssembler.BuildSwingFromCoupling(hArr, k, new[] { 1, 2 }, F0);
        ModalResult modal = ModalAnalysis.Analyze(m);

        Mode osc = modal.Modes.First(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0);
        double analytic = Math.Sqrt(Ws * ps / (2.0 * h));
        Assert.Equal(analytic, osc.Eigenvalue.Imaginary, 6);   // Im(s) = sqrt(ws Ps / 2H)
        Assert.Equal(0.0, osc.Eigenvalue.Real, 6);             // undamped
    }

    [Fact]
    public void SMIB_damped_mode_matches_the_analytic_real_part()
    {
        const double h = 3.5, ps = 1.732, d = 0.5;
        var hArr = new[] { h, 1e8 };
        var k = new double[,] { { 0, ps }, { ps, 0 } };
        SmallSignalModel m = SmallSignalAssembler.BuildSwingFromCoupling(hArr, k, new[] { 1, 2 }, F0, new[] { d, 0.0 });
        ModalResult modal = ModalAnalysis.Analyze(m);

        Mode osc = modal.Modes.First(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0);
        Assert.Equal(-d / (4.0 * h), osc.Eigenvalue.Real, 6);  // Re(s) = -D/(4H)
        double wn = Math.Sqrt(Ws * ps / (2.0 * h));
        Assert.Equal(d / (4.0 * h * wn), osc.DampingRatio, 5); // zeta = D/(4H wn)
    }

    [Fact]
    public void Undamped_swing_has_exactly_the_defective_COI_pair_and_participation_is_deflated()
    {
        const double h = 3.5, ps = 1.732;
        var hArr = new[] { h, 1e8 };
        var k = new double[,] { { 0, ps }, { ps, 0 } };
        SmallSignalModel m = SmallSignalAssembler.BuildSwingFromCoupling(hArr, k, new[] { 1, 2 }, F0);
        ModalResult modal = ModalAnalysis.Analyze(m);

        // Exactly two eigenvalues at the origin - the rigid-body COI defective pair.
        int zeros = modal.Modes.Count(x => x.Eigenvalue.Magnitude < 1e-6);
        Assert.Equal(2, zeros);

        // Participation is computed on the DEFLATED electromechanical subspace: the finite machine (bus 1)
        // dominates, the infinite bus (bus 2) barely participates.
        Mode osc = modal.Modes.First(x => x.IsOscillatory);
        double finite = osc.ParticipationByState[0] + osc.ParticipationByState[2]; // delta_g1 + omega_g1
        double infinite = osc.ParticipationByState[1] + osc.ParticipationByState[3];
        Assert.True(finite > 0.9, $"finite machine should dominate: {finite}");
        Assert.True(infinite < 0.05, $"infinite bus barely participates: {infinite}");

        // The rigid COI modes carry no participation (defined only on the deflated subspace).
        foreach (Mode z in modal.Modes.Where(x => x.Eigenvalue.Magnitude < 1e-6))
            Assert.True(z.ParticipationByState.Sum() < 1e-12);
    }

    [Fact]
    public void Collapses_to_a_lossless_symmetric_ContinuumWave_to_1e_minus_9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);

        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, Wscc9Xdp);
        // Zero the transfer conductances (G) => symmetric, unclamped coupling on BOTH sides.
        double[,] kLossless = SmallSignalAssembler.LosslessSynchronisingCoupling(model, pf, Wscc9Xdp);
        SmallSignalModel ssm = SmallSignalAssembler.BuildSwingFromCoupling(p.H, kLossless, p.GeneratorBuses, F0);
        ModalResult modal = ModalAnalysis.Analyze(ssm);

        double[] mine = modal.Modes
            .Where(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0)
            .Select(x => x.FrequencyHz).OrderBy(x => x).ToArray();

        ContinuumWaveModel.WaveModes wave = ContinuumWaveModel.Analyze(p.H, kLossless, F0);
        double[] seed = wave.InterAreaModesHz.OrderBy(x => x).ToArray();

        Assert.Equal(seed.Length, mine.Length);
        for (int i = 0; i < mine.Length; i++)
            Assert.Equal(seed[i], mine[i], 9);   // machine-eps agreement in the lossless symmetric limit
    }

    [Fact]
    public void Wscc9_has_two_electromechanical_modes_in_band()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf, Wscc9Xdp);
        ModalResult modal = ModalAnalysis.Analyze(ssm);

        double[] freqs = modal.Modes
            .Where(x => x.IsOscillatory && x.Eigenvalue.Imaginary > 0)
            .Select(x => x.FrequencyHz).OrderBy(x => x).ToArray();

        Assert.Equal(2, freqs.Length);                       // 2 electromechanical modes for 3 machines
        foreach (double f in freqs) Assert.InRange(f, 0.8, 2.2);
        Assert.Equal(0, modal.RhpEigenvalueCount);           // stable (marginal) - no false RHP from the COI pair
    }

    [Fact]
    public void Gfm_faithful_reduction_is_graceful_at_zero_synthetic_inertia()
    {
        // Pure-droop GFM (synInertia = 0, the code default) must NOT divide by zero.
        AssetClass cls = Gfm(synInertia: 0.0, damping: 5.0, actuateTau: 0.05);
        InverterLinearBlock block = LinearInverterModel.ForGfm(cls, bus: 5, ratedMw: 100, f0: F0);
        Assert.Equal(1, block.Order);
        Assert.Equal(-1.0 / 0.05, block.Aself[0, 0], 9);     // pole = -1/tau
        Assert.Equal(0.0, block.SyntheticInertiaMwsPerHz);   // no synthetic inertia
        Assert.All(new[] { block.Aself[0, 0], block.FreqInputRow[0], block.RocofInputRow[0] },
            v => Assert.False(double.IsNaN(v) || double.IsInfinity(v)));

        // A zero actuator tau floors to 1e-3 (mirrors the code's Math.Max guard) - no 1/0.
        AssetClass cls0 = Gfm(synInertia: 5.0, damping: 5.0, actuateTau: 0.0);
        InverterLinearBlock b0 = LinearInverterModel.ForGfm(cls0, bus: 5, ratedMw: 100, f0: F0);
        Assert.Equal(-1000.0, b0.Aself[0, 0], 6);            // -1/max(1e-3, 0)
        Assert.False(double.IsNaN(b0.RocofInputRow[0]) || double.IsInfinity(b0.RocofInputRow[0]));
    }

    [Fact]
    public void Enabling_inverters_leaves_the_machine_block_byte_identical_and_adds_control_modes()
    {
        (GridModel model, double[] xdp) = BoundCase9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);

        SmallSignalModel swing = SmallSignalAssembler.FromSolvedCase(model, pf, xdp);
        SmallSignalModel withInv = SmallSignalAssembler.FromSolvedCaseWithInverters(model, pf, xdp);

        int twoG = 2 * swing.MachineCount;
        // The machine sub-block is byte-identical: no synthetic term is folded into a machine H/E.
        for (int i = 0; i < twoG; i++)
            for (int j = 0; j < twoG; j++)
                Assert.Equal(swing.A[i, j], withInv.A[i, j]);

        Assert.True(withInv.Order > swing.Order);
        Assert.True(withInv.InverterStatesIncluded);

        // The GFM (actuateTau = 0.05) contributes a real actuator pole near -1/tau = -20.
        ModalResult modal = ModalAnalysis.Analyze(withInv);
        Assert.Contains(modal.Modes, m => !m.IsOscillatory && Math.Abs(m.Eigenvalue.Real - (-20.0)) < 2.0);
    }

    [Fact]
    public void Real_and_synthetic_states_are_structurally_separated()
    {
        (GridModel model, double[] xdp) = BoundCase9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        SmallSignalModel withInv = SmallSignalAssembler.FromSolvedCaseWithInverters(model, pf, xdp);

        int twoG = 2 * withInv.MachineCount;
        // Machine states are REAL; every added inverter state is SYNTHETIC (control).
        for (int i = 0; i < withInv.Order; i++)
        {
            StateLabel s = withInv.States[i];
            if (i < twoG) Assert.False(s.IsSynthetic, $"machine state {i} must be real");
            else Assert.True(s.IsSynthetic, $"inverter state {i} must be synthetic");
        }
    }

    [Fact]
    public void With_inverters_fails_loud_when_nothing_is_bound()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.Throws<InvalidOperationException>(() =>
            SmallSignalAssembler.FromSolvedCaseWithInverters(model, pf, Wscc9Xdp));
    }

    // --- helpers ---

    private static AssetClass Gfm(double synInertia, double damping, double actuateTau)
    {
        var pars = new Dictionary<string, double>
        {
            [AssetParams.SynInertiaMwsPerHz] = synInertia,
            [AssetParams.DampingMwPerHz] = damping,
            [AssetParams.ActuateTauS] = actuateTau,
        };
        return new AssetClass("gfm-test", AssetKind.GridFormingInverter, "gfm", pars,
            new Dictionary<string, AssetClassProvenance>());
    }

    private static (GridModel Model, double[] Xdp) BoundCase9()
    {
        GridModel baseModel = IeeeCases.Case9();
        // Bind the first machine to a grid-forming class via its inline dynamics class.
        var gens = baseModel.Generators.ToList();
        gens[0] = gens[0] with { DynamicsClass = "gfm-test" };
        var lib = new AssetClassLibrary(new[] { Gfm(synInertia: 0.0, damping: 5.0, actuateTau: 0.05) });
        var bindings = new AssetBindingSet(Array.Empty<AssetBinding>());
        var model = new GridModel
        {
            BaseMva = baseModel.BaseMva,
            Buses = baseModel.Buses,
            Generators = gens,
            Branches = baseModel.Branches,
            Oltcs = baseModel.Oltcs,
            Boundaries = baseModel.Boundaries,
            FrequencyHz = baseModel.FrequencyHz,
            Name = baseModel.Name,
            AssetDynamics = new AssetDynamicsModel(lib, bindings),
        };
        return (model, Wscc9Xdp);
    }
}
