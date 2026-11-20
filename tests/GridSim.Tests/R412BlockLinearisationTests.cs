using GridSim.Core.Dynamics.SmallSignal.Full;
using GridSim.Core.Dynamics.UserModels;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-12: analytic linearisation of R4-08 declarative models. Oracles are hand-derived matrices (the state equations written
/// out in the test), the Jacobians of the combinational blocks worked by hand, and the model's own nonlinear interpreter driven
/// by a small step (an independent check of the whole linearisation, never a finite difference inside the code under test).
/// </summary>
public sealed class R412BlockLinearisationTests
{
    private static int Idx(LinearBlockModel m, string name) => m.StateNames.ToList().IndexOf(name);

    [Fact]
    public void Sexs_lead_lag_into_lag_matches_the_hand_derived_state_space()
    {
        // SEXS: shape = (1+s Ta)/(1+s Tb) of verr; exc = K/(1+s Te) of shape.  c = Ta/Tb.
        const double K = 80.0, Te = 0.07, Ta = 0.3, Tb = 1.2;
        UserModel m = R412Fixtures.Sexs(K, Te, Ta, Tb);
        double efd0 = 2.0;
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { efd0 });
        double c = Ta / Tb;
        int sh = Idx(lin, "shape.xL"), ex = Idx(lin, "exc");
        Assert.True(sh >= 0 && ex >= 0 && lin.StateCount == 2);
        Assert.Equal(-1.0 / Tb, lin.A[sh, sh], 14);
        Assert.Equal(0.0, lin.A[sh, ex], 14);
        Assert.Equal(K / Te, lin.A[ex, sh], 10);
        Assert.Equal(-1.0 / Te, lin.A[ex, ex], 12);
        Assert.Equal((1.0 - c) / Tb, lin.B[sh, 0], 14);
        Assert.Equal(K * c / Te, lin.B[ex, 0], 10);
        Assert.Equal(0.0, lin.B[ex, 1], 14);                 // speed input unused
        Assert.Equal(1.0, lin.C[0, ex], 14);
        Assert.Equal(0.0, lin.D[0, 0], 14);
        Assert.Equal(efd0, lin.Y0[0], 12);
        Assert.Equal(efd0 / K, lin.U0[0], 12);                // the steady regulator error
    }

    [Fact]
    public void Lead_lag_with_equal_time_constants_is_an_exact_pass_through_with_no_state()
    {
        UserModel m = R412Fixtures.Sexs(50.0, 0.05, 1.0, 1.0);
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { 2.0 });
        Assert.Equal(new[] { "exc" }, lin.StateNames);
        Assert.Equal(-1.0 / 0.05, lin.A[0, 0], 12);
        Assert.Equal(50.0 / 0.05, lin.B[0, 0], 9);
    }

    [Fact]
    public void Ieeet1_with_saturation_matches_the_hand_derived_matrix()
    {
        var p = new Dictionary<string, double> { ["Aex"] = 0.1, ["Bex"] = 0.5 };
        UserModel m = UserModelLibrary.Load("IEEET1", p);
        const double Ka = 400.0, Ta = 0.04, Ke = 1.0, Te = 0.8, Kf = 0.03, Tf = 1.0, Aex = 0.1, Bex = 0.5;
        double efd0 = 2.0;
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { efd0 });
        int xl = Idx(lin, "fbw.xL"), reg = Idx(lin, "reg"), exc = Idx(lin, "exc");
        Assert.Equal(3, lin.StateCount);
        double kfT = Kf / Tf;
        double fp = Ke + Aex * Math.Exp(Bex * efd0) * (1.0 + Bex * efd0);   // d/dE of (Ke + Se(E)) E
        Assert.Equal(-1.0 / Tf, lin.A[xl, xl], 12);
        Assert.Equal(1.0 / Tf, lin.A[xl, exc], 12);
        Assert.Equal(Ka * kfT / Ta, lin.A[reg, xl], 8);
        Assert.Equal(-1.0 / Ta, lin.A[reg, reg], 10);
        Assert.Equal(-Ka * kfT / Ta, lin.A[reg, exc], 8);
        Assert.Equal(1.0 / Te, lin.A[exc, reg], 12);
        Assert.Equal(-fp / Te, lin.A[exc, exc], 10);
        Assert.Equal(Ka / Ta, lin.B[reg, 0], 8);
        Assert.Equal(1.0, lin.C[0, exc], 14);
    }

    [Fact]
    public void Ieeet1_linearisation_reproduces_the_nonlinear_interpreter_small_step_response()
    {
        // Independent check of the whole chain: a small step on the regulator error, simulated by the nonlinear interpreter
        // (zero-order-hold steps) and by the linear model (RK4), must agree to the step's second-order error.
        var p = new Dictionary<string, double> { ["Aex"] = 0.1, ["Bex"] = 0.5 };
        UserModel m = UserModelLibrary.Load("IEEET1", p);
        double efd0 = 2.0;
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { efd0 });
        const double eps = 1e-4, dt = 1e-4, tEnd = 0.6;
        ModelInstance inst = m.NewInstance();
        inst.Initialize(efd0);
        double verr0 = inst.EquilibriumError;
        int steps = (int)Math.Round(tEnd / dt);
        for (int i = 0; i < steps; i++) inst.Step(verr0 + eps, 0.0, dt);
        double nonlinear = inst.Output(0) - efd0;

        int n = lin.StateCount;
        var x = new double[n];
        double[] Deriv(double[] xs)
        {
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = lin.B[i, 0] * eps;
                for (int j = 0; j < n; j++) s += lin.A[i, j] * xs[j];
                d[i] = s;
            }
            return d;
        }
        for (int i = 0; i < steps; i++)
        {
            double[] k1 = Deriv(x);
            double[] k2 = Deriv(x.Select((v, j) => v + 0.5 * dt * k1[j]).ToArray());
            double[] k3 = Deriv(x.Select((v, j) => v + 0.5 * dt * k2[j]).ToArray());
            double[] k4 = Deriv(x.Select((v, j) => v + dt * k3[j]).ToArray());
            for (int j = 0; j < n; j++) x[j] += dt / 6.0 * (k1[j] + 2 * k2[j] + 2 * k3[j] + k4[j]);
        }
        double linear = 0.0;
        for (int j = 0; j < n; j++) linear += lin.C[0, j] * x[j];
        Assert.True(Math.Abs(nonlinear) > 1e-3, "the step must move the output");
        Assert.True(Math.Abs(nonlinear - linear) <= 2e-3 * Math.Abs(nonlinear), $"nonlinear {nonlinear:R} vs linear {linear:R}");
    }

    private const string AlgebraicDoc = """
    {
      "format": "gridsim-model/1", "id": "ALG", "role": "generic", "source": "test fixture",
      "ports": { "inputs": [ { "name": "u1", "unit": "pu" }, { "name": "u2", "unit": "pu" } ], "outputs": [ { "name": "y", "unit": "pu", "from": "sw" } ] },
      "blocks": [
        { "id": "lim", "type": "limit", "inputs": ["u1"], "params": { "min": -1.0, "max": 1.0 } },
        { "id": "db",  "type": "deadband", "inputs": ["lim"], "params": { "lower": -0.1, "upper": 0.1 } },
        { "id": "sw",  "type": "switch", "inputs": ["u2", "db", "u1"], "params": { "threshold": 0.0 } }
      ],
      "initialisation": { "inputs": [], "steps": [] }
    }
    """;

    [Theory]
    [InlineData(0.5, 1.0, 0.4, 1.0, 0.0)]    // inside the limit, outside the dead band, selector picks the dead-band branch
    [InlineData(0.05, 1.0, 0.0, 0.0, 0.0)]   // inside the dead band: gain 0
    [InlineData(0.5, -1.0, 0.5, 1.0, 0.0)]   // selector picks u1 directly
    [InlineData(2.0, 1.0, 0.9, 0.0, 0.0)]    // limit active: gain 0
    public void Combinational_blocks_have_the_hand_worked_jacobian(double u1, double u2, double y, double dydu1, double dydu2)
    {
        UserModel m = UserModel.FromDocument(R412Fixtures.Doc(AlgebraicDoc));
        LinearBlockModel lin = BlockLinearisation.Linearise(m, ReadOnlySpan<double>.Empty, new[] { u1, u2 });
        Assert.Equal(0, lin.StateCount);
        Assert.Equal(y, lin.Y0[0], 14);
        Assert.Equal(dydu1, lin.D[0, 0], 14);
        Assert.Equal(dydu2, lin.D[0, 1], 14);
    }

    private const string DelayDoc = """
    {
      "format": "gridsim-model/1", "id": "DLY", "role": "generic", "source": "test fixture",
      "ports": { "inputs": [ { "name": "u", "unit": "pu" } ], "outputs": [ { "name": "y", "unit": "pu", "from": "d" } ] },
      "blocks": [ { "id": "d", "type": "delay", "inputs": ["u"], "params": { "steps": 3 } } ],
      "initialisation": { "inputs": [ { "name": "y0", "unit": "pu" } ], "steps": [ { "action": "initialise", "block": "d", "values": ["y0"] } ] }
    }
    """;

    [Fact]
    public void A_fixed_delay_is_refused_as_not_linearisable_naming_the_block()
    {
        UserModel m = UserModel.FromDocument(R412Fixtures.Doc(DelayDoc));
        var ex = Assert.Throws<NotLinearisableException>(() => BlockLinearisation.Linearise(m, new[] { 0.2 }, new[] { 0.2 }));
        Assert.Equal("fixed-delay", ex.Code);
        Assert.Contains("block d", ex.Message);
    }

    [Fact]
    public void A_state_that_is_not_at_equilibrium_at_the_operating_inputs_is_refused()
    {
        // lag y' = (K u - y)/T initialised at y0 = 1 with an operating input giving K u = 3: not an equilibrium
        const string doc = """
        {
          "format": "gridsim-model/1", "id": "LG", "role": "generic", "source": "test fixture",
          "ports": { "inputs": [ { "name": "u", "unit": "pu" } ], "outputs": [ { "name": "y", "unit": "pu", "from": "b" } ] },
          "blocks": [ { "id": "b", "type": "lag", "inputs": ["u"], "params": { "gain": 1.0, "time": 0.5 } } ],
          "initialisation": { "inputs": [ { "name": "y0", "unit": "pu" } ], "steps": [ { "action": "initialise", "block": "b", "values": ["y0"] } ] }
        }
        """;
        UserModel m = UserModel.FromDocument(R412Fixtures.Doc(doc));
        var ex = Assert.Throws<NotLinearisableException>(() => BlockLinearisation.Linearise(m, new[] { 1.0 }, new[] { 3.0 }));
        Assert.Equal("not-at-equilibrium", ex.Code);
        // and at the matching input it linearises to -1/T and K/T
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { 1.0 }, new[] { 1.0 });
        Assert.Equal(-2.0, lin.A[0, 0], 14);
        Assert.Equal(2.0, lin.B[0, 0], 14);
    }

    [Fact]
    public void A_limit_active_at_the_operating_point_freezes_the_state_and_says_so()
    {
        // SEXS with the field limit at the operating field: the exciter state is frozen (no dynamics) and a note records it
        UserModel m = UserModelLibrary.Load("SEXS", new Dictionary<string, double> { ["Emax"] = 2.0, ["Ta"] = 1.0, ["Tb"] = 1.0 });
        LinearBlockModel lin = BlockLinearisation.Linearise(m, new[] { 2.0 });
        Assert.Equal(0, lin.StateCount);
        Assert.Contains(lin.Notes, s => s.Contains("limit", StringComparison.Ordinal));
        Assert.Equal(0.0, lin.D[0, 0], 14);
    }

    [Fact]
    public void Linearisation_is_repeatable_to_the_bit()
    {
        UserModel m = UserModelLibrary.Load("IEEET1", new Dictionary<string, double> { ["Aex"] = 0.1, ["Bex"] = 0.5 });
        LinearBlockModel a = BlockLinearisation.Linearise(m, new[] { 2.0 });
        LinearBlockModel b = BlockLinearisation.Linearise(m, new[] { 2.0 });
        for (int i = 0; i < a.StateCount; i++)
            for (int j = 0; j < a.StateCount; j++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.A[i, j]), BitConverter.DoubleToInt64Bits(b.A[i, j]));
    }

    [Fact]
    public void Every_shipped_exciter_governor_and_stabiliser_either_linearises_or_is_refused_with_a_reason()
    {
        // The library is the catalogue a study draws from; none may fail with an unexplained exception.
        foreach (string id in UserModelLibrary.Ids)
        {
            UserModel m = UserModelLibrary.Load(id);
            double[] init = m.Role switch { "exciter" => new[] { 2.0 }, "governor" => new[] { 0.5 }, "stabiliser" => new[] { 0.0 }, _ => Array.Empty<double>() };
            if (m.Role == "generic") continue;
            try
            {
                LinearBlockModel lin = BlockLinearisation.Linearise(m, init);
                Assert.Equal(m.Plan.Document.Outputs.Count, lin.OutputCount);
                foreach (double v in lin.A) Assert.True(double.IsFinite(v), id);
            }
            catch (NotLinearisableException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message), id);
            }
            catch (ArgumentException)
            {
                // an initial value outside the model's own limits is refused by the model itself (R252): a reported outcome
            }
        }
    }
}
