using GridSim.Core.Dynamics.UserModels;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-08 worked case 2: every block of the closed catalogue against a closed form or an exact identity. The oracles are
/// independent of the interpreter: the analytic step responses (with System.Math.Exp; the interpreter's own exponential is
/// the deterministic DetMath one, so agreement is to rounding, 1e-12), exact arithmetic identities, and hand-worked values.
/// </summary>
public sealed class R408UserModelBlockTests
{
    /// <summary>A generic one-block document: inputs u (and v, w when given), one block b, output y. <paramref name="init"/> is
    /// the initialise values array text, or null for a combinational block.</summary>
    internal static ModelDocument Single(string type, string paramsJson, string[]? inputs = null, string? init = null,
        string extra = "", string[]? ports = null, string outUnit = "pu")
    {
        ports ??= new[] { "u" };
        inputs ??= new[] { "u" };
        string portJson = string.Join(", ", ports.Select(p => $"{{ \"name\": \"{p}\", \"unit\": \"pu\" }}"));
        string inJson = string.Join(", ", inputs.Select(i => "\"" + i + "\""));
        string initSteps = init is null ? "" : $"{{ \"action\": \"initialise\", \"block\": \"b\", \"values\": {init} }}";
        string json = $$"""
        {
          "format": "gridsim-model/1", "id": "T", "role": "generic", "source": "test",
          "ports": { "inputs": [ {{portJson}} ], "outputs": [ { "name": "y", "unit": "{{outUnit}}", "from": "b" } ] },
          "blocks": [ { "id": "b", "type": "{{type}}", "inputs": [ {{inJson}} ], "params": { {{paramsJson}} } {{extra}} } ],
          "initialisation": { "inputs": [ { "name": "u0", "unit": "pu" } ], "steps": [ {{initSteps}} ] }
        }
        """;
        return ModelDocument.Parse(json);
    }

    internal static ModelInstance Make(ModelDocument d, IReadOnlyDictionary<string, double>? p = null)
        => ModelInstance.Create(ModelCompiler.Compile(d), p);

    private static void Near(double expected, double actual, double tol = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= tol * Math.Max(1.0, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

    [Fact]
    public void Lag_unit_step_is_K_times_one_minus_exp_minus_t_over_T_at_every_sample()
    {
        ModelInstance m = Make(Single("lag", "\"gain\": 2.0, \"time\": 0.5", init: "[0.0]"));
        m.Initialize(0.0);
        const double dt = 0.01;
        for (int n = 1; n <= 300; n++)
            Near(2.0 * (1.0 - Math.Exp(-n * dt / 0.5)), m.Step(1.0, dt));
    }

    [Fact]
    public void Lead_lag_unit_step_is_one_minus_one_minus_Ta_over_Tb_times_exp_minus_t_over_Tb()
    {
        ModelInstance m = Make(Single("leadlag", "\"lead\": 0.3, \"lag\": 0.6", init: "[0.0]"));
        m.Initialize(0.0);
        const double dt = 0.02;
        for (int n = 1; n <= 200; n++)
            Near(1.0 - (1.0 - 0.3 / 0.6) * Math.Exp(-n * dt / 0.6), m.Step(1.0, dt));
    }

    [Fact]
    public void Washout_unit_step_decays_as_exp_minus_t_over_T()
    {
        ModelInstance m = Make(Single("washout", "\"time\": 0.4", init: "[0.0]"));
        m.Initialize(0.0);
        const double dt = 0.01;
        for (int n = 1; n <= 250; n++)
            Near(Math.Exp(-n * dt / 0.4), m.Step(1.0, dt));
    }

    [Fact]
    public void Integrator_ramp_is_k_u_t_and_the_windup_limited_state_is_held_at_the_limit_then_leaves_at_once()
    {
        ModelInstance ramp = Make(Single("integrator", "\"gain\": 3.0", init: "[0.0]", outUnit: "s"));
        ramp.Initialize(0.0);
        for (int n = 1; n <= 100; n++) Near(3.0 * 0.5 * n * 0.1, ramp.Step(0.5, 0.1), 1e-11);

        ModelInstance w = Make(Single("integrator", "\"gain\": 1.0, \"min\": -1.0, \"max\": 1.0", init: "[0.0]", outUnit: "s"));
        w.Initialize(0.0);
        double x = 0.0;
        for (int n = 0; n < 200; n++) x = w.Step(1.0, 0.1);
        Assert.Equal(1.0, x);                                   // held at the limit, not wound up to 20
        Near(0.9, w.Step(-1.0, 0.1), 1e-15);                    // leaves immediately: no windup to unwind
    }

    [Fact]
    public void Pi_controller_with_constant_input_is_kp_u_plus_ki_u_t()
    {
        ModelInstance m = Make(Single("pi", "\"kp\": 2.0, \"ki\": 1.5", init: "[0.0, 0.0]"));
        m.Initialize(0.0);
        for (int n = 1; n <= 50; n++) Near(2.0 * 0.1 + 1.5 * 0.1 * n * 0.05, m.Step(0.1, 0.05), 1e-11);
    }

    [Fact]
    public void Limit_clips_at_the_bounds_exactly()
    {
        ModelInstance m = Make(Single("limit", "\"min\": -0.5, \"max\": 0.75"));
        Assert.Equal(0.75, m.Step(9.0, 0.01));
        Assert.Equal(-0.5, m.Step(-9.0, 0.01));
        Assert.Equal(0.25, m.Step(0.25, 0.01));
    }

    [Fact]
    public void Rate_limiter_follows_at_the_rise_and_fall_rates()
    {
        ModelInstance m = Make(Single("ratelimit", "\"rise\": 2.0, \"fall\": -1.0", init: "[0.0]"));
        m.Initialize(0.0);
        double y = 0;
        for (int n = 0; n < 5; n++) y = m.Step(10.0, 0.1);
        Near(1.0, y);                                           // 2 per second for 0.5 s
        for (int n = 0; n < 3; n++) y = m.Step(-10.0, 0.1);
        Near(0.7, y);                                           // 1 per second for 0.3 s
        Near(0.7 - 0.05, m.Step(0.0, 0.05));                    // input 0 is below: still falling
        Near(0.0, m.Step(0.0, 5.0));                            // reaches the input and stops there
    }

    [Fact]
    public void Dead_band_is_zero_inside_and_the_excess_outside()
    {
        ModelInstance m = Make(Single("deadband", "\"lower\": -0.1, \"upper\": 0.2"));
        Near(0.3, m.Step(0.5, 0.01));
        Assert.Equal(0.0, m.Step(0.1, 0.01));
        Assert.Equal(0.0, m.Step(0.2, 0.01));
        Near(-0.2, m.Step(-0.3, 0.01));
    }

    [Fact]
    public void Table_interpolates_linearly_and_holds_the_ends()
    {
        ModelInstance m = Make(Single("table1d", "", extra: ", \"table\": { \"x\": [0, 1, 2], \"y\": [0, 10, 12], \"xUnit\": \"pu\", \"yUnit\": \"pu\" }"));
        Near(5.0, m.Step(0.5, 0.01));
        Near(11.0, m.Step(1.5, 0.01));
        Assert.Equal(0.0, m.Step(-1.0, 0.01));
        Assert.Equal(12.0, m.Step(5.0, 0.01));
        Assert.Equal(10.0, m.Step(1.0, 0.01));
    }

    [Fact]
    public void Exponential_saturation_is_a_times_exp_b_u()
    {
        ModelInstance m = Make(Single("expsat", "\"a\": 0.1, \"b\": 2.0"));
        Near(0.1 * Math.Exp(3.0), m.Step(1.5, 0.01));
        Near(0.1, m.Step(0.0, 0.01));
    }

    [Fact]
    public void Fixed_delay_returns_the_initial_value_for_n_steps_then_the_input_n_steps_ago()
    {
        ModelInstance m = Make(Single("delay", "\"steps\": 3", init: "[7.0]"));
        m.Initialize(0.0);
        Assert.Equal(7.0, m.Step(1.0, 0.01));
        Assert.Equal(7.0, m.Step(2.0, 0.01));
        Assert.Equal(7.0, m.Step(3.0, 0.01));
        Assert.Equal(1.0, m.Step(4.0, 0.01));
        Assert.Equal(2.0, m.Step(5.0, 0.01));
    }

    [Fact]
    public void Sum_product_min_max_switch_are_exact_left_to_right_operations()
    {
        string[] ports = { "u", "v", "w" };
        var sum = Make(Single("sum", "", new[] { "u", "v", "w" }, extra: ", \"signs\": \"+-+\"", ports: ports));
        sum.Initialize(0.0);
        Span<double> inputs = stackalloc double[] { 1.5, 0.25, 4.0 };
        sum.Step(inputs, 0.01);
        Assert.Equal(1.5 - 0.25 + 4.0, sum.Output(0));
        var prod = Make(Single("product", "", new[] { "u", "v", "w" }, ports: ports));
        prod.Step(inputs, 0.01);
        Assert.Equal(1.5 * 0.25 * 4.0, prod.Output(0));
        var min = Make(Single("min", "", new[] { "u", "v", "w" }, ports: ports));
        min.Step(inputs, 0.01);
        Assert.Equal(0.25, min.Output(0));
        var max = Make(Single("max", "", new[] { "u", "v", "w" }, ports: ports));
        max.Step(inputs, 0.01);
        Assert.Equal(4.0, max.Output(0));
        var sw = Make(Single("switch", "\"threshold\": 1.0", new[] { "u", "v", "w" }, ports: ports));
        sw.Step(inputs, 0.01);
        Assert.Equal(0.25, sw.Output(0));                       // u = 1.5 >= 1.0 selects v
        Span<double> low = stackalloc double[] { 0.5, 0.25, 4.0 };
        sw.Step(low, 0.01);
        Assert.Equal(4.0, sw.Output(0));                        // below the threshold selects w
    }

    [Fact]
    public void Gain_and_divide_are_k_times_u_and_u_over_d()
    {
        Assert.Equal(0.3 * 7.0, Make(Single("gain", "\"k\": 0.3")).Step(7.0, 0.01));
        Assert.Equal(7.0 / 0.3, Make(Single("div", "\"d\": 0.3")).Step(7.0, 0.01));
    }

    [Fact]
    public void Hold_keeps_its_initial_value_and_const_gives_its_value()
    {
        ModelInstance h = Make(Single("hold", "", inputs: Array.Empty<string>(), init: "[0.42]"));
        h.Initialize(0.0);
        Assert.Equal(0.42, h.Step(9.0, 0.01));
        Assert.Equal(0.42, h.Step(-9.0, 0.01));
        Assert.Equal(3.5, Make(Single("const", "\"value\": 3.5", inputs: Array.Empty<string>())).Step(0.0, 0.01));
    }

    [Fact]
    public void Satpole_without_saturation_is_the_exact_first_order_pole_with_Ke()
    {
        // Te dE/dt = u - Ke E with Se = 0: E(t) = (u/Ke)(1 - exp(-Ke t/Te)) from E(0) = 0.
        ModelInstance m = Make(Single("satpole", "\"te\": 0.8, \"ke\": 1.0, \"a\": 0.0, \"b\": 0.0", init: "[0.0]"));
        m.Initialize(0.0);
        for (int n = 1; n <= 100; n++) Near(2.0 * (1.0 - Math.Exp(-n * 0.01 / 0.8)), m.Step(2.0, 0.01));
    }
}
