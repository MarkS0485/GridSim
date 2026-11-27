using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Machines;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 emt3 wiring (gridsim-emt3/1 optional keys): output naming, machines, converters, HVDC and partition. The oracles are the
/// Core tests' own (R417EmtDepthBTests, R419HvdcTests, R419MmcTests, R419PartitionTests): the same model and the same numbers,
/// reached through a study document and the built CLI, plus bit-identity with the Core call and determinism over two runs.
/// </summary>
public sealed class R4Emt3WiringTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-r4emt3w-" + Guid.NewGuid().ToString("N"));

    public R4Emt3WiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static JsonElement Read(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    /// <summary>Run the built CLI on a document and return the process result and the result document (when written).</summary>
    private (CliProcess.Result Proc, JsonElement Doc, string JsonPath) Run(string tag, string doc)
    {
        string f = Write(tag + ".study.json", doc);
        string o = Path.Combine(_dir, tag + ".out.json");
        CliProcess.Result r = CliProcess.RunBuilt("emt3", f, "--json", o);
        return (r, File.Exists(o) ? Read(o) : default, o);
    }

    /// <summary>The trace column <paramref name="col"/> of a result document.</summary>
    private static double[] Col(JsonElement doc, string col)
    {
        JsonElement t = doc.GetProperty("tables").GetProperty("traces");
        var v = new double[t.GetArrayLength()];
        for (int i = 0; i < v.Length; i++) v[i] = t[i].GetProperty(col).GetDouble();
        return v;
    }

    private static string Trace(JsonElement doc) => doc.GetProperty("tables").GetProperty("traces").GetRawText();

    private const string Rlc = """
        {"schema":"gridsim-emt3/1","time_step_s":1e-4,"duration_s":0.01,
         "buses":["S"],
         "sources":[{"name":"G","bus":"S","v_ll_rms":1000,"rs_ohm":1.0}],
         "shunts":[{"name":"R","bus":"S","r_ohm":100}],
         "outputs":[{"name":"OUTNAME","kind":"voltage","target":"S","phase":"a"}]}
        """;

    // ------------------------------------------------------------------------------------------------------------
    // Step 0: output names are trace columns and summary keys
    // ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Va")]
    [InlineData("v-a")]
    [InlineData("1a")]
    [InlineData("v a")]
    [InlineData("")]
    [InlineData("time_s")]
    public void An_output_name_the_result_writer_would_reject_is_refused_as_bad_input_before_the_run(string name)
    {
        (CliProcess.Result r, _, _) = Run("badname", Rlc.Replace("OUTNAME", name));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("output name", r.StdErr);
        Assert.DoesNotContain("Unhandled", r.StdErr);
    }

    [Fact]
    public void A_snake_case_output_name_is_accepted_and_is_the_trace_column_and_the_summary_key()
    {
        (CliProcess.Result r, JsonElement doc, _) = Run("goodname", Rlc.Replace("OUTNAME", "v_a_2"));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(JsonValueKind.Number, doc.GetProperty("tables").GetProperty("traces")[3].GetProperty("v_a_2").ValueKind);
        Assert.True(doc.GetProperty("summary").GetProperty("max_abs_v_a_2").GetDouble() > 0);
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------------------
    // Step 1: machines (R417EmtDepthBTests: synchronous phasor steady state, sudden short circuit, induction direct start)
    // ------------------------------------------------------------------------------------------------------------

    private const double Pi = Math.PI, Ra = 0.003;
    private static readonly double Ib = (2.0 / 3.0) * 100e6 / (13800.0 * Math.Sqrt(2.0 / 3.0));

    private static SubtransientMachine.Parameters Genr() => new(
        InertiaSeconds: 3.5, DampingPu: 0.0, Xd: 1.8, Xdp: 0.3, Xdpp: 0.2, Xq: 1.7, Xqp: 0.5, Xqpp: 0.2,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.5, Tqopp: 0.05, ExternalReactance: 0.1, InfiniteBusVoltagePu: 1.0, Saturation: null, LeakageReactance: 0.15);

    /// <summary>The R417 generator (100 MVA, 13.8 kV, 50 Hz) as a machines[] entry; <paramref name="extra"/> follows the parameters.</summary>
    private static string GenJson(string extra = "") =>
        "{\"name\":\"G\",\"kind\":\"synchronous\",\"bus\":\"T\",\"rating_mva\":100,\"rated_kv_ll\":13.8,"
        + "\"parameters\":{\"inertia_s\":3.5,\"damping_pu\":0,\"xd_pu\":1.8,\"xdp_pu\":0.3,\"xdpp_pu\":0.2,\"xq_pu\":1.7,\"xqp_pu\":0.5,\"xqpp_pu\":0.2,"
        + "\"tdop_s\":6.0,\"tdopp_s\":0.04,\"tqop_s\":0.5,\"tqopp_s\":0.05,\"xl_pu\":0.15,\"ra_pu\":0.003}" + extra + "}";

    private static string IndJson() =>
        "{\"name\":\"M\",\"kind\":\"induction\",\"bus\":\"T\",\"rating_mva\":10,\"rated_kv_ll\":6.6,"
        + "\"parameters\":{\"ra_pu\":0.02,\"xa_pu\":0.10,\"xm_pu\":3.5,\"r1_pu\":0.02,\"x1_pu\":0.12,\"inertia_s\":0.4},"
        + "\"load_torque\":{\"k_pu\":0.8,\"speed_exponent\":2}}";

    private static string Doc(string dt, string duration, string rest, string every = "1") =>
        "{\"schema\":\"gridsim-emt3/1\",\"time_step_s\":" + dt + ",\"duration_s\":" + duration + ",\"sample_every\":" + every + ",\"frequency_hz\":50," + rest + "}";

    private const string IaOut = "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"a\"}]";

    private (CliProcess.Result R, string Doc) Refusal(string tag, string rest)
    {
        string doc = Doc("5e-5", "0.001", rest);
        (CliProcess.Result r, _, _) = Run(tag, doc);
        return (r, doc);
    }

    [Fact]
    public void Machine_entries_are_strict_and_refusals_name_the_place_or_come_from_the_core()
    {
        string gen = GenJson();
        string Mk(string machine, string outs = IaOut) => "\"buses\":[\"T\"],\"machines\":[" + machine + "]," + outs;
        (CliProcess.Result ok, _, _) = Run("m-ok", Doc("5e-5", "0.001", Mk(gen)));
        Assert.True(ok.ExitCode == 0, ok.ToString());

        void Refused(string tag, string rest, string expect)
        {
            (CliProcess.Result r, _) = Refusal(tag, rest);
            Assert.True(r.ExitCode == 1, tag + ": " + r);
            Assert.Contains(expect, r.StdErr);
        }
        Refused("m-unknown-key", Mk(gen.Replace("\"kind\"", "\"colour\":1,\"kind\"")), "unknown key 'colour'");
        Refused("m-unknown-par", Mk(gen.Replace("\"xd_pu\":1.8", "\"xd_pu\":1.8,\"xz_pu\":1")), "unknown key 'xz_pu'");
        Refused("m-bad-kind", Mk(gen.Replace("synchronous", "dc")), "kind must be \"synchronous\" or \"induction\"");
        Refused("m-no-params", Mk("{\"name\":\"G\",\"kind\":\"synchronous\",\"bus\":\"T\",\"rating_mva\":100,\"rated_kv_ll\":13.8}"), "\"parameters\" is required");
        Refused("m-bad-neutral", Mk(gen.Replace("\"kind\"", "\"neutral\":\"floating\",\"kind\"")), "neutral must be");
        Refused("m-no-bus", Mk(gen.Replace("\"bus\":\"T\"", "\"bus\":\"Q\"")), "no bus 'Q'");
        Refused("m-ind-key", Mk(IndJson().Replace("\"kind\"", "\"neutral\":\"grounded\",\"kind\"")), "is not used by an induction machine");
        Refused("m-sync-key", Mk(gen.Replace("\"kind\"", "\"start_speed_pu\":0.5,\"kind\"")), "is not used by a synchronous machine");
        Refused("m-bad-mode", Mk(GenJson(",\"initial\":{\"mode\":\"hot\"}")), "mode must be");
        Refused("m-noload-pq", Mk(GenJson(",\"initial\":{\"mode\":\"no-load\",\"p_mw\":5}")), "no p_mw or q_mvar");
        // the Core's own refusal text is passed through
        Refused("m-core", Mk(gen.Replace("\"xdp_pu\":0.3", "\"xdp_pu\":1.8")), "X'd must be below Xd");
        // machine outputs: no phase, only on a machine, only the kinds the machine has
        Refused("m-speed-phase", Mk(gen, "\"outputs\":[{\"name\":\"w\",\"kind\":\"speed\",\"target\":\"G\",\"phase\":\"a\"}]"), "takes no phase");
        Refused("m-speed-nomachine", Mk(gen, "\"outputs\":[{\"name\":\"w\",\"kind\":\"speed\",\"target\":\"T\"}]"), "is not a machine");
        Refused("m-angle-induction", Mk(IndJson(), "\"outputs\":[{\"name\":\"d\",\"kind\":\"rotor-angle\",\"target\":\"M\"}]"), "not defined for the induction machine");
        // a phase is still required for a voltage output, as before
        Refused("m-volt-nophase", Mk(gen, "\"outputs\":[{\"name\":\"v\",\"kind\":\"voltage\",\"target\":\"T\"}]"), "\"phase\" is required");
    }

    [Fact]
    public void Synchronous_machine_phasor_start_on_a_stiff_bus_holds_the_phasor_steady_state_through_the_document()
    {
        // R417 Synchronous_machine_on_an_infinite_bus_holds_the_phasor_steady_state: the same machine and the same checks, here with the ideal
        // source directly on the terminals (no line, so the start is exactly the steady state). Angles are in the source convention.
        const double angDeg = 20.0, pMw = 85.0, qMvar = 30.0;
        Complex v = Complex.FromPolarCoordinates(1.0, (angDeg - 90.0) * Pi / 180.0);
        Complex iOut = Complex.Conjugate(new Complex(pMw, qMvar) / 100.0 / v);
        SubtransientMachine.Parameters rp = Genr();
        Complex e = v + new Complex(Ra, rp.Xq) * iOut;
        double theta0 = e.Phase - Pi / 2, delta = e.Phase - v.Phase;
        Complex idq = iOut * Complex.Exp(new Complex(0, -theta0));
        double ef = e.Magnitude + (rp.Xd - rp.Xq) * idq.Real, ifdExp = ef / (rp.Xd - 0.15);
        double teExp = (v * Complex.Conjugate(iOut)).Real + Ra * iOut.Magnitude * iOut.Magnitude;

        string machine = GenJson(",\"initial\":{\"mode\":\"phasor\",\"v_pu\":1.0,\"angle_deg\":" + N(angDeg) + ",\"p_mw\":" + N(pMw) + ",\"q_mvar\":" + N(qMvar) + "}");
        string rest = "\"buses\":[\"T\"],"
            + "\"sources\":[{\"name\":\"E\",\"bus\":\"T\",\"v_ll_rms\":13800,\"angle_deg\":" + N(angDeg) + "}],"
            + "\"machines\":[" + machine + "],"
            + "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"a\"},{\"name\":\"w\",\"kind\":\"speed\",\"target\":\"G\"},"
            + "{\"name\":\"delta\",\"kind\":\"rotor-angle\",\"target\":\"G\"},{\"name\":\"te\",\"kind\":\"torque\",\"target\":\"G\"},{\"name\":\"ifd\",\"kind\":\"field-current\",\"target\":\"G\"}]";
        string doc = Doc("5e-5", "1.0", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("sync-phasor", doc);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal("COMPARATOR-UNVERIFIED", res.GetProperty("summary").GetProperty("comparator_status").GetString());
        JsonElement init = res.GetProperty("tables").GetProperty("machines")[0];
        // the initial state is the Core's phasor solution (the same equations as the R417 test)
        Assert.Equal(delta, init.GetProperty("initial_load_angle_rad").GetDouble(), 9);
        Assert.Equal(ifdExp, init.GetProperty("initial_field_current_pu").GetDouble(), 9);
        Assert.Equal(teExp, init.GetProperty("initial_mechanical_torque_pu").GetDouble(), 9);
        Assert.Equal(idq.Real, init.GetProperty("initial_id_pu").GetDouble(), 9);
        Assert.Equal(idq.Imaginary, init.GetProperty("initial_iq_pu").GetDouble(), 9);

        double[] t = Col(res, "time_s"), ia = Col(res, "ia"), w = Col(res, "w"), dl = Col(res, "delta"), te = Col(res, "te"), ifd = Col(res, "ifd");
        int spc = 400, n = t.Length, start = n - spc * 2, count = spc * 2;
        double wg = 2 * Pi * 50;
        Complex acc = 0;
        for (int i = start; i < start + count; i++) acc += ia[i] * Complex.Exp(new Complex(0, -wg * t[i]));
        Complex c = acc * (2.0 / count), iaExp = Ib * iOut;
        double Mean(double[] x) { double sum = 0; for (int i = start; i < start + count; i++) sum += x[i]; return sum / count; }
        Assert.True((c - iaExp).Magnitude / iaExp.Magnitude < 0.01, $"phase-a current {c} against {iaExp}");
        Assert.True(Math.Abs(w[n - 1] - 1.0) < 2e-4);
        Assert.True(Math.Abs(dl[n - 1] - dl[0]) < 0.01);
        Assert.True(Math.Abs(Mean(te) - teExp) / teExp < 0.01);
        Assert.True(Math.Abs(Mean(ifd) - ifdExp) / ifdExp < 0.005);

        // determinism: a second process run gives the same trace table byte for byte
        (CliProcess.Result r2, JsonElement res2, _) = Run("sync-phasor-2", doc);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    [Fact]
    public void Synchronous_machine_sudden_short_circuit_through_the_document_follows_the_classical_envelope_and_is_bit_identical_to_the_core_run()
    {
        // R417 Sudden_three_phase_short_circuit_follows_the_classical_envelope_and_dc_time_constant: the same machine, the same fault instant
        // (omega tf = pi/2, so angle_deg 90 puts the terminal voltage at its zero crossing, as the Core's cosine phasor does), the same checks.
        SubtransientMachine.Parameters rp = Genr();
        double dt = 50e-6, w0 = 2 * Pi * 50.0;
        string rest = "\"buses\":[\"T\"],"
            + "\"machines\":[" + GenJson(",\"initial\":{\"mode\":\"no-load\",\"v_pu\":1.0,\"angle_deg\":90}") + "],"
            + "\"faults\":[{\"name\":\"F\",\"bus\":\"T\",\"phases\":\"abc\",\"r_ohm\":1e-5,\"at_s\":0.005}],"
            + "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"a\"},{\"name\":\"ib\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"b\"},"
            + "{\"name\":\"ic\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"c\"}]";
        string docText = Doc("5e-5", "2.2", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("sync-sc", docText);
        Assert.True(r.ExitCode == 0, r.ToString());
        double[] ia = Col(res, "ia"), ib = Col(res, "ib"), ic = Col(res, "ic"), t = Col(res, "time_s");

        // classical formula, computed here from the data (as the Core test)
        double td1 = rp.Tdop * rp.Xdp / rp.Xd, td2 = rp.Tdopp * rp.Xdpp / rp.Xdp, ta = rp.Xdpp / (w0 * Ra), tf = 0.005;
        double Env(double tau) => (1 / rp.Xdpp - 1 / rp.Xdp) * Math.Exp(-tau / td2) + (1 / rp.Xdp - 1 / rp.Xd) * Math.Exp(-tau / td1) + 1 / rp.Xd;
        Complex a = Complex.FromPolarCoordinates(1, 2 * Pi / 3);
        int spc = 400;
        (double Ac, double Dc) Window(double tauCentre)
        {
            int c0 = (int)Math.Round((tf + tauCentre) / dt) - spc / 2;
            Complex acc = 0, dcc = 0;
            for (int i = c0; i < c0 + spc; i++)
            {
                Complex s = 2.0 / 3.0 * (ia[i] + a * ib[i] + a * a * ic[i]);
                acc += s * Complex.Exp(new Complex(0, -w0 * t[i]));
                dcc += s;
            }
            return (acc.Magnitude / spc / Ib, dcc.Magnitude / spc / Ib);
        }
        double worstEnv = 0;
        foreach (double tau in new[] { 0.05, 0.1, 0.2, 0.4, 0.8, 1.5 })
        {
            double exp = Env(tau);
            worstEnv = Math.Max(worstEnv, Math.Abs(Window(tau).Ac - exp) / exp);
        }
        Assert.True(worstEnv < 0.06, $"envelope deviates by {worstEnv}");
        double d1 = Window(0.05).Dc, dc1 = Window(0.3).Dc;
        double taSim = 0.25 / Math.Log(d1 / dc1), d0 = d1 * Math.Exp(0.05 / taSim);
        Assert.True(Math.Abs(taSim - ta) / ta < 0.08);
        Assert.True(Math.Abs(d0 - 1 / rp.Xdpp) / (1 / rp.Xdpp) < 0.06);
        double worst = 0, i0 = Env(0);
        for (int i = (int)Math.Round(tf / dt) + 1; i < (int)Math.Round((tf + 0.06) / dt); i++)
        {
            double tau = t[i] - tf;
            worst = Math.Max(worst, Math.Abs(ia[i] / Ib - (Env(tau) * Math.Sin(w0 * t[i]) - i0 * Math.Exp(-tau / ta) * Math.Sin(w0 * tf))));
        }
        Assert.True(worst / i0 < 0.10);

        // the Core's own construction (R417 RunShortCircuit), run over a prefix: the document path reproduces it bit for bit
        var net = new NodalNetwork();
        var mach = new SynchronousMachineEmt("G", net.Node("T0"), net.Node("T1"), net.Node("T2"), NodalNetwork.Ground, new SynchronousMachineEmtData(rp, 100.0, 13.8, 50.0, Ra));
        mach.InitialiseNoLoad(1.0);
        net.Add(mach);
        for (int k = 0; k < 3; k++) net.Add(new NodalSwitch($"F{k}", net.Node($"T{k}"), NodalNetwork.Ground, 1e-5, 1e9, false));
        var events = Enumerable.Range(0, 3).Select(k => new NodalEvent(tf, $"F{k}", true)).ToList();
        var probes = Enumerable.Range(0, 3).Select(k => new NodalProbe($"i{k}", NodalProbeKind.ElementCurrent, "G", k)).ToList();
        NodalResult core = NodalSolver.Run(net, dt, 0.3, probes, events, new NodalOptions { EventRule = NodalEventRule.Trapezoidal });
        double[][] cli = [ia, ib, ic];
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < core.Times.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[k][i]), BitConverter.DoubleToInt64Bits(cli[k][i]));

        // determinism
        (CliProcess.Result r2, JsonElement res2, _) = Run("sync-sc-2", docText);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    [Fact]
    public void A_mechanical_torque_step_accelerates_the_rotor_at_the_swing_equation_rate()
    {
        // 2H dw/dt = Tm - Te: right after a +0.1 pu torque step with the terminals on a stiff bus the acceleration is 0.1 / 2H (Te has barely moved).
        string machine = GenJson(",\"initial\":{\"mode\":\"phasor\",\"v_pu\":1.0,\"angle_deg\":0,\"p_mw\":50,\"q_mvar\":10},\"torque_steps\":[{\"at_s\":0.05,\"delta_pu\":0.1}]");
        string rest = "\"buses\":[\"T\"],\"sources\":[{\"name\":\"E\",\"bus\":\"T\",\"v_ll_rms\":13800,\"angle_deg\":0}],"
            + "\"machines\":[" + machine + "],\"outputs\":[{\"name\":\"w\",\"kind\":\"speed\",\"target\":\"G\"}]";
        (CliProcess.Result r, JsonElement res, _) = Run("sync-step", Doc("5e-5", "0.1", rest, "20"));
        Assert.True(r.ExitCode == 0, r.ToString());
        double[] t = Col(res, "time_s"), w = Col(res, "w");
        int i0 = Array.FindIndex(t, x => x >= 0.0499), i1 = Array.FindIndex(t, x => x >= 0.0699);
        Assert.True(Math.Abs(w[i0] - 1.0) < 1e-4, "no acceleration before the step");
        double accel = (w[i1] - w[i0]) / (t[i1] - t[i0]), exp = 0.1 / (2 * 3.5);
        Assert.InRange(accel / exp, 0.9, 1.1);
    }

    private static double AirGapTorque(InductionMachine m, double s)
    {
        Complex zr = new(m.R1Pu / s, m.X1Pu), zm = new(0, m.XmPu);
        Complex zp = zm * zr / (zm + zr);
        Complex zin = new Complex(m.RaPu, m.XaPu) + zp;
        return zp.Real / (zin.Magnitude * zin.Magnitude);
    }

    [Fact]
    public void Induction_machine_direct_start_through_the_document_ends_at_the_equivalent_circuit_slip_and_repeats()
    {
        // R417 Induction_machine_direct_start_ends_at_the_equivalent_circuit_torque_balance_slip: the same motor, inertia and load k w^2.
        var m = new InductionMachine(1, "M1", InductionPowerCode.Mechanical, 8.0, 10.0, 6.6, 0.02, 0.10, 3.5, 0.02, 0.12);
        const double kLoad = 0.8;
        double lo = 1e-6, hi = 0.1;
        for (int it = 0; it < 200; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (AirGapTorque(m, mid) < kLoad * (1 - mid) * (1 - mid)) lo = mid; else hi = mid;
        }
        double sExp = 0.5 * (lo + hi);
        Complex zin = new Complex(m.RaPu, m.XaPu) + new Complex(0, m.XmPu) * new Complex(m.R1Pu / sExp, m.X1Pu) / (new Complex(0, m.XmPu) + new Complex(m.R1Pu / sExp, m.X1Pu));
        double iPkExp = 1.0 / zin.Magnitude, ib = (2.0 / 3.0) * 10e6 / (6600.0 * Math.Sqrt(2.0 / 3.0));

        string rest = "\"buses\":[\"T\"],\"sources\":[{\"name\":\"E\",\"bus\":\"T\",\"v_ll_rms\":6600}],"
            + "\"machines\":[" + IndJson() + "],"
            + "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"M\",\"phase\":\"a\"},{\"name\":\"w\",\"kind\":\"speed\",\"target\":\"M\"},{\"name\":\"te\",\"kind\":\"torque\",\"target\":\"M\"}]";
        string docText = Doc("1e-4", "3.0", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("ind-start", docText);
        Assert.True(r.ExitCode == 0, r.ToString());
        double[] t = Col(res, "time_s"), ia = Col(res, "ia"), w = Col(res, "w"), te = Col(res, "te");
        int n = t.Length, spc = 200, start = n - 4 * spc, count = 4 * spc;
        double Mean(double[] x) { double sum = 0; for (int i = start; i < start + count; i++) sum += x[i]; return sum / count; }
        double speed = Mean(w), torque = Mean(te), sSim = 1 - speed, accel = (w[n - 1] - w[n - 1 - spc]) / (spc * 1e-4);
        Complex acc = 0;
        for (int i = start; i < start + count; i++) acc += ia[i] * Complex.Exp(new Complex(0, -2 * Pi * 50 * t[i]));
        double iPk = (acc * (2.0 / count)).Magnitude / ib;
        Assert.True(Math.Abs(sSim - sExp) < 3e-4, $"slip {sSim} against {sExp}");
        Assert.True(Math.Abs(torque - kLoad * speed * speed) < 5e-3);
        Assert.True(Math.Abs(accel) < 5e-3);
        Assert.True(Math.Abs(iPk - iPkExp) / iPkExp < 0.01);
        Assert.True(ia.Take(400).Max(Math.Abs) / ib > 3.0, "locked-rotor inrush");

        // the document run is bit-identical to the typed Core runner on the same study, and repeats exactly
        var study = new Emt3Study(1e-4, 3.0, 1, 50, IntegrationRule.Trapezoidal, NodalEventRule.Trapezoidal, new NewtonPolicy(),
            ["T"], [new Emt3Source("E", "T", 6600, 50, 0, 0, 0)], [], [], [], [], [], [], [], [],
            [new Emt3Output("ia", "current", "M", "a", "from"), new Emt3Output("w", "speed", "M", "", "from"), new Emt3Output("te", "torque", "M", "", "from")],
            [new Emt3Machine("M", "induction", "T", true, 10, 6.6, 50, null, new Emt3InductionData(0.02, 0.10, 3.5, 0.02, 0.12, 0.4), null, [], [], 0.0, new Emt3LoadTorque(0.8, 2.0))]);
        NodalResult core = Emt3Runner.Run(study).Run;
        Assert.Equal(core.Times.Length, n);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[0][i]), BitConverter.DoubleToInt64Bits(ia[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[1][i]), BitConverter.DoubleToInt64Bits(w[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[2][i]), BitConverter.DoubleToInt64Bits(te[i]));
        }
        (CliProcess.Result r2, JsonElement res2, _) = Run("ind-start-2", docText);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Step 2: DC buses and converters (R417EmtDepthBTests: average and switched VSC, current control with a DC link)
    // ------------------------------------------------------------------------------------------------------------

    private const double Vdc = 1000.0, Mi = 0.8, F1 = 50.0, Mf = 39.0, RLoad = 5.0, LLoad = 10e-3, RonVsc = 1e-3;

    private static string ConvDoc(string kind, string dt, string duration)
    {
        string dcBus = "{\"name\":\"D\",\"capacitance_f\":1e-3,\"initial_v\":1000,\"supply_v\":1000,\"supply_ohm\":0}";
        string conv = "{\"name\":\"C\",\"kind\":\"" + kind + "\",\"ac_bus\":\"A\",\"dc_bus\":\"D\",\"rs_ohm\":1e-3,"
            + "\"modulation\":{\"index\":0.8,\"frequency_hz\":50}"
            + (kind == "switched" ? ",\"pwm\":{\"carrier_hz\":1950,\"carrier_phase\":0}" : "") + "}";
        // the load is a wye of series R-L to a grounded star point (the study has no floating star); the grounded star point carries only zero sequence
        string rest = "\"buses\":[\"A\",\"NN\"],\"dc_buses\":[" + dcBus + "],\"converters\":[" + conv + "],"
            + "\"branches\":[{\"name\":\"LD\",\"from\":\"A\",\"to\":\"NN\",\"r1_ohm\":5,\"l1_h\":0.01}],"
            + "\"switches\":[{\"name\":\"GND\",\"from\":\"NN\",\"phases\":\"abc\",\"closed\":true}],"
            + "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"LD\",\"phase\":\"a\"},{\"name\":\"va\",\"kind\":\"voltage\",\"target\":\"A\",\"phase\":\"a\"},"
            + "{\"name\":\"vp\",\"kind\":\"dc-voltage-positive\",\"target\":\"D\"},{\"name\":\"vn\",\"kind\":\"dc-voltage-negative\",\"target\":\"D\"}]";
        return Doc(dt, duration, rest);
    }

    [Fact]
    public void Converter_and_dc_bus_entries_are_strict_and_refusals_name_the_place_or_come_from_the_core()
    {
        string ok = ConvDoc("average", "1e-4", "0.001");
        (CliProcess.Result r0, JsonElement d0, _) = Run("c-ok", ok);
        Assert.True(r0.ExitCode == 0, r0.ToString());
        Assert.Equal("COMPARATOR-UNVERIFIED", d0.GetProperty("summary").GetProperty("comparator_status").GetString());
        Assert.Equal("AverageValue", d0.GetProperty("tables").GetProperty("converters")[0].GetProperty("model_class").GetString());

        void Refused(string tag, string doc, string expect)
        {
            (CliProcess.Result r, _, _) = Run(tag, doc);
            Assert.True(r.ExitCode == 1, tag + ": " + r);
            Assert.Contains(expect, r.StdErr);
        }
        Refused("c-unknown", ok.Replace("\"kind\":\"average\"", "\"kind\":\"average\",\"gain\":1"), "unknown key 'gain'");
        Refused("c-kind", ok.Replace("\"kind\":\"average\"", "\"kind\":\"matrix\""), "kind must be \"average\" or \"switched\"");
        Refused("c-pwm-avg", ok.Replace("\"modulation\"", "\"pwm\":{\"carrier_hz\":1},\"modulation\""), "used by a switched converter only");
        Refused("c-no-pwm", ConvDoc("switched", "1e-4", "0.001").Replace(",\"pwm\":{\"carrier_hz\":1950,\"carrier_phase\":0}", ""), "\"pwm\" is required");
        Refused("c-both", ok.Replace("\"modulation\"", "\"control\":{\"bandwidth_rad_s\":1},\"modulation\""), "exactly one of");
        Refused("c-neither", ok.Replace(",\"modulation\":{\"index\":0.8,\"frequency_hz\":50}", ""), "exactly one of");
        Refused("c-ctl-nofilter", ok.Replace("\"modulation\":{\"index\":0.8,\"frequency_hz\":50}", "\"control\":{\"bandwidth_rad_s\":600}"), "needs a filter");
        Refused("c-no-dc", ok.Replace("\"dc_bus\":\"D\"", "\"dc_bus\":\"X\""), "no DC bus 'X'");
        Refused("c-no-ac", ok.Replace("\"ac_bus\":\"A\"", "\"ac_bus\":\"Z\""), "no bus 'Z'");
        Refused("c-dc-unknown-key", ok.Replace("\"supply_ohm\":0", "\"supply_ohm\":0,\"x\":1"), "unknown key 'x'");
        // the Core's own refusal text for the link and the converter
        Refused("c-core-cap", ok.Replace("\"capacitance_f\":1e-3", "\"capacitance_f\":0"), "DC link capacitance must be finite and positive");
        Refused("c-core-rs", ok.Replace("\"rs_ohm\":1e-3", "\"rs_ohm\":0"), "series resistance must be finite and positive");
        // output kinds
        string sw = ConvDoc("switched", "1e-4", "0.001");
        Refused("c-sw-current", sw.Replace("\"target\":\"LD\",\"phase\":\"a\"}", "\"target\":\"C\",\"phase\":\"a\"}"), "no single current");
        Refused("c-sw-dccurrent", sw.Replace("\"kind\":\"dc-voltage-positive\",\"target\":\"D\"", "\"kind\":\"dc-current\",\"target\":\"C\""), "defined for an average converter");
        Refused("c-dcv-nonbus", ok.Replace("\"kind\":\"dc-voltage-positive\",\"target\":\"D\"", "\"kind\":\"dc-voltage-positive\",\"target\":\"C\""), "is not a DC bus");
        Refused("c-dcv-phase", ok.Replace("\"kind\":\"dc-voltage-positive\",\"target\":\"D\"", "\"kind\":\"dc-voltage-positive\",\"target\":\"D\",\"phase\":\"a\""), "takes no phase");
    }

    private static Complex Coef(double[] t, double[] x, int start, int count, double f)
    {
        double w = 2 * Pi * f, re = 0, im = 0;
        for (int i = start; i < start + count; i++) { re += x[i] * Math.Cos(w * t[i]); im -= x[i] * Math.Sin(w * t[i]); }
        return new Complex(re, im) * (2.0 / count);
    }

    [Fact]
    public void Average_and_switched_converters_through_the_document_match_the_closed_form_current_and_each_other_and_repeat()
    {
        // R417 Average_value_converter_agrees_with_the_switched_model_fundamental_and_with_the_closed_form_current: the same converter data (Vdc 1000 V,
        // m 0.8, Mf 39, Ron 1 mohm, R 5 ohm, L 10 mH, dt 2.5 us, 8 cycles), the same closed form and bounds. The load star point is grounded here.
        const double dt = 2.5e-6;
        string swDoc = ConvDoc("switched", "2.5e-6", "0.16"), avDoc = ConvDoc("average", "2.5e-6", "0.16");
        (CliProcess.Result rs, JsonElement sw, _) = Run("conv-sw", swDoc);
        (CliProcess.Result ra, JsonElement av, _) = Run("conv-av", avDoc);
        Assert.True(rs.ExitCode == 0, rs.ToString());
        Assert.True(ra.ExitCode == 0, ra.ToString());
        Assert.Equal("Switched", sw.GetProperty("tables").GetProperty("converters")[0].GetProperty("model_class").GetString());
        Assert.Contains("sine PWM", sw.GetProperty("tables").GetProperty("converters")[0].GetProperty("description").GetString());
        Assert.Contains("quantised UP", sw.GetProperty("summary").GetProperty("switching_timing").GetString());
        double[] t = Col(sw, "time_s"), ias = Col(sw, "ia"), vas = Col(sw, "va"), iaa = Col(av, "ia"), vaa = Col(av, "va");
        int spc = (int)Math.Round(1.0 / (F1 * dt)), start = t.Length - 1 - 2 * spc + 1, count = 2 * spc;
        Complex z = new(RLoad + RonVsc, 2 * Pi * F1 * LLoad);
        Complex iExp = (Mi * Vdc / 2) / z * new Complex(0, -1);
        Complex cs = Coef(t, ias, start, count, F1), ca = Coef(t, iaa, start, count, F1);
        Assert.True((cs - iExp).Magnitude / iExp.Magnitude < 0.01, "switched fundamental current vs closed form");
        Assert.True((ca - iExp).Magnitude / iExp.Magnitude < 1e-3, "average-value current vs closed form");
        Assert.True((ca - cs).Magnitude / ca.Magnitude < 0.01, "average-value and switched fundamentals agree");
        double ripAv = Coef(t, vaa, start, count, Mf * F1).Magnitude, ripSw = Coef(t, vas, start, count, Mf * F1).Magnitude;
        Assert.True(ripAv < 1e-6 * ripSw, $"carrier-frequency pole voltage: switched {ripSw}, average {ripAv}");
        // the DC bus is held by its ideal supply
        Assert.True(Math.Abs(Col(sw, "vp")[t.Length - 1] - Vdc / 2) < 1e-6);
        Assert.True(Math.Abs(Col(sw, "vn")[t.Length - 1] + Vdc / 2) < 1e-6);

        // determinism: the switched run (valve events) repeats byte for byte
        (CliProcess.Result rs2, JsonElement sw2, _) = Run("conv-sw-2", swDoc);
        Assert.Equal(0, rs2.ExitCode);
        Assert.Equal(Trace(sw), Trace(sw2));
    }

    [Fact]
    public void Current_controlled_converter_with_a_dc_link_through_the_document_follows_the_sampled_loop_response_and_the_power_balance_link_voltage()
    {
        // R417 Current_controlled_vsc_follows_the_first_order_response_and_the_dc_link_settles_at_the_power_balance_voltage: the same grid (400 V peak,
        // Rf 0.05 ohm, Lf 2 mH), DC link (2 mF, 1070 V start, 1100 V supply behind 0.5 ohm), controller (alpha 2 pi 100, sampled at 2 kHz) and checks.
        const double vg = 400.0, rF = 0.05, lF = 2e-3, vSup = 1100.0, rSup = 0.5, iRef = 100.0, tStep = 0.02, fc = 2000.0, dt = 5e-6;
        double w = 2 * Pi * 50.0, alpha = 2 * Pi * 100.0;
        int every = (int)Math.Round(0.5 / fc / dt);
        string rest = "\"buses\":[\"G\"],"
            + "\"sources\":[{\"name\":\"E\",\"bus\":\"G\",\"v_ll_rms\":" + N(vg * Math.Sqrt(1.5)) + ",\"angle_deg\":90}],"
            + "\"dc_buses\":[{\"name\":\"D\",\"capacitance_f\":2e-3,\"initial_v\":1070,\"supply_v\":1100,\"supply_ohm\":0.5}],"
            + "\"converters\":[{\"name\":\"C\",\"kind\":\"switched\",\"ac_bus\":\"G\",\"dc_bus\":\"D\",\"rs_ohm\":1e-3,\"pwm\":{\"carrier_hz\":2000,\"carrier_phase\":0},"
            + "\"filter\":{\"r_ohm\":0.05,\"l_h\":2e-3},"
            + "\"control\":{\"bandwidth_rad_s\":" + N(alpha) + ",\"control_every\":" + every + ",\"frequency_hz\":50,\"phase_deg\":90,\"steps\":[{\"at_s\":0.02,\"id_a\":100,\"iq_a\":0}]}}],"
            + "\"outputs\":[{\"name\":\"vp\",\"kind\":\"dc-voltage-positive\",\"target\":\"D\"},{\"name\":\"vn\",\"kind\":\"dc-voltage-negative\",\"target\":\"D\"},"
            + "{\"name\":\"fa\",\"kind\":\"current\",\"target\":\"C\",\"phase\":\"a\"},{\"name\":\"fb\",\"kind\":\"current\",\"target\":\"C\",\"phase\":\"b\"},"
            + "{\"name\":\"fc\",\"kind\":\"current\",\"target\":\"C\",\"phase\":\"c\"}]";
        string docText = Doc("5e-6", "0.3", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("conv-ctl", docText);
        Assert.True(r.ExitCode == 0, r.ToString());
        double[] t = Col(res, "time_s"), a = Col(res, "fa"), b = Col(res, "fb"), c = Col(res, "fc"), vp = Col(res, "vp"), vn = Col(res, "vn");
        int n = t.Length;
        var id = new double[n]; var iq = new double[n];
        for (int i = 0; i < n; i++)
        {
            double th = w * t[i];
            id[i] = 2.0 / 3.0 * (a[i] * Math.Cos(th) + b[i] * Math.Cos(th - 2 * Pi / 3) + c[i] * Math.Cos(th + 2 * Pi / 3));
            iq[i] = -2.0 / 3.0 * (a[i] * Math.Sin(th) + b[i] * Math.Sin(th - 2 * Pi / 3) + c[i] * Math.Sin(th + 2 * Pi / 3));
        }
        double Mean(double[] x, int s0, int cnt) { double sum = 0; for (int i = s0; i < s0 + cnt; i++) sum += x[i]; return sum / cnt; }
        int win = (int)Math.Round(1.0 / fc / dt);
        double Avg(double[] x, double tt) { int c0 = (int)Math.Round(tt / dt) - win / 2; return Mean(x, c0, win); }
        // oracle: the sampled loop written out (as the Core test): Kp = alpha L, Ki = alpha R, integrator before output, ZOH plant
        double ts = every * dt, kp = alpha * lF, ki = alpha * rF, aD = Math.Exp(-rF * ts / lF);
        double IAt(double tau)
        {
            double iK = 0, integ = 0; int k = 0;
            while ((k + 1) * ts <= tau) { double e = iRef - iK; integ += ki * ts * e; double u = kp * e + integ; iK = aD * iK + (u / rF) * (1 - aD); k++; }
            double e2 = iRef - iK; double u2 = kp * e2 + integ + ki * ts * e2;
            double x = tau - k * ts;
            return iK * Math.Exp(-rF * x / lF) + (u2 / rF) * (1 - Math.Exp(-rF * x / lF));
        }
        double worst = 0;
        foreach (double dtau in new[] { 0.5e-3, 1e-3, 2e-3, 3e-3, 5e-3, 8e-3, 15e-3 })
        {
            double exp = 0; for (int q = 0; q < 20; q++) exp += IAt(dtau + (q - 9.5) * (1.0 / fc / 20)) / 20;
            worst = Math.Max(worst, Math.Abs(Avg(id, tStep + dtau) - exp) / iRef);
        }
        Assert.True(worst < 0.06, $"step response deviates by {worst} of the reference");
        double idEnd = Avg(id, 0.28), iqEnd = Avg(iq, 0.28);
        int s1 = n - 1 - 2 * 4000;
        double vdc = Mean(vp, s1, 8000) - Mean(vn, s1, 8000);
        double pAc = 1.5 * vg * idEnd, pLoss = 1.5 * rF * idEnd * idEnd + 1.5 * 1e-3 * idEnd * idEnd * 2;
        double vExp = (vSup + Math.Sqrt(vSup * vSup - 4 * rSup * (pAc + pLoss))) / 2;
        Assert.True(Math.Abs(idEnd - iRef) / iRef < 0.01);
        Assert.True(Math.Abs(iqEnd) < 0.02 * iRef);
        Assert.True(Math.Abs(vdc - vExp) / vExp < 0.01, $"DC link {vdc} V against {vExp} V");

        (CliProcess.Result r2, JsonElement res2, _) = Run("conv-ctl-2", docText);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Step 3: HVDC (R419HvdcTests: VSC link, LCC bridge group; R419MmcTests: MMC stations)
    // ------------------------------------------------------------------------------------------------------------

    private static double WindowMean(double[] t, double[] x, double t0, double t1)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < t.Length; i++) { if (t[i] < t0 - 1e-12 || t[i] >= t1 - 1e-12) continue; s += x[i]; n++; }
        return s / n;
    }

    private static Complex WindowPhasor(double[] t, double[] x, double t0, double t1, double f)
    {
        double w = 2 * Pi * f, re = 0, im = 0; int n = 0;
        for (int i = 0; i < t.Length; i++) { if (t[i] < t0 - 1e-12 || t[i] >= t1 - 1e-12) continue; re += x[i] * Math.Cos(w * t[i]); im -= x[i] * Math.Sin(w * t[i]); n++; }
        return new Complex(2.0 * re / n, 2.0 * im / n);
    }

    [Fact]
    public void Hvdc_entries_are_strict_and_refusals_name_the_place_or_come_from_the_core()
    {
        string lcc = "{\"name\":\"G\",\"kind\":\"lcc\",\"v_ll_rms\":10000,\"commutation_l_h\":5e-3,\"pulses\":6,\"alpha_deg\":20,\"load\":{\"r_ohm\":30,\"l_h\":0.2}}";
        const string outs = "\"outputs\":[{\"name\":\"id\",\"kind\":\"dc-current\",\"target\":\"G\"}]";
        string Mk(string entry, string o = outs) => "\"buses\":[],\"hvdc\":[" + entry + "]," + o;
        (CliProcess.Result ok, JsonElement d0, _) = Run("h-ok", Doc("1e-5", "0.002", Mk(lcc)));
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Equal("Switched", d0.GetProperty("tables").GetProperty("converters")[0].GetProperty("model_class").GetString());

        void Refused(string tag, string rest, string expect)
        {
            (CliProcess.Result r, _, _) = Run(tag, Doc("1e-5", "0.002", rest));
            Assert.True(r.ExitCode == 1, tag + ": " + r);
            Assert.Contains(expect, r.StdErr);
        }
        Refused("h-kind", Mk(lcc.Replace("\"lcc\"", "\"hybrid\"")), "kind must be \"vsc\", \"lcc\" or \"dc-line\"");
        Refused("h-unknown", Mk(lcc.Replace("\"pulses\":6", "\"pulses\":6,\"gain\":1")), "unknown key 'gain'");
        Refused("h-other-kind-key", Mk(lcc.Replace("\"pulses\":6", "\"pulses\":6,\"dc_cap_f\":1")), "unknown key 'dc_cap_f'");
        Refused("h-no-load", Mk(lcc.Replace(",\"load\":{\"r_ohm\":30,\"l_h\":0.2}", "")), "\"load\" is required");
        Refused("h-core-pulses", Mk(lcc.Replace("\"pulses\":6", "\"pulses\":8")), "a bridge group has 6 or 12 pulses");
        string vsc = "{\"name\":\"S\",\"kind\":\"vsc\",\"grid_v_ll_rms\":9797.96,\"filter_l_h\":5e-3,\"dc_nominal_v\":20000,\"dc_cap_f\":1e-3,\"mode\":\"power\",\"set_point\":1e6,\"dc_supply_v\":20000,\"dc_supply_ohm\":0.001}";
        (CliProcess.Result okv, _, _) = Run("h-vsc-ok", Doc("5e-5", "0.002", Mk(vsc, "\"outputs\":[{\"name\":\"idc\",\"kind\":\"dc-current\",\"target\":\"S\"}]")));
        Assert.True(okv.ExitCode == 0, okv.ToString());
        Refused("h-ideal-supply-current", Mk(vsc.Replace(",\"dc_supply_ohm\":0.001", ""), "\"outputs\":[{\"name\":\"idc\",\"kind\":\"dc-current\",\"target\":\"S\"}]"), "is not measured");
        Refused("h-vsc-mode", Mk(vsc.Replace("\"power\"", "\"torque\"")), "mode must be");
        Refused("h-vsc-mmc-missing", Mk(vsc.Replace("\"mode\"", "\"converter\":\"mmc-average\",\"mode\"")), "\"mmc\" is required");
        Refused("h-vsc-mmc-on-average", Mk(vsc.Replace("\"mode\"", "\"mmc\":{},\"mode\"")), "used by an MMC converter only");
        Refused("h-vsc-conv", Mk(vsc.Replace("\"mode\"", "\"converter\":\"neural\",\"mode\"")), "converter must be");
        Refused("h-core-dcv", Mk(vsc.Replace("\"mode\":\"power\",\"set_point\":1e6", "\"mode\":\"dc-voltage\",\"set_point\":-5")), "DC voltage set point must be positive");
        Refused("h-dcline-stations", Mk("{\"name\":\"L\",\"kind\":\"dc-line\",\"from\":\"S\",\"to\":\"Z\",\"r_ohm\":2,\"surge_ohm\":300,\"delay_s\":5e-4}"), "joins two VSC stations");
        Refused("h-lcc-current", Mk(lcc, "\"outputs\":[{\"name\":\"i\",\"kind\":\"current\",\"target\":\"G\",\"phase\":\"a\"}]"), "use dc-current");
        Refused("h-dcv-wrong", Mk(lcc, "\"outputs\":[{\"name\":\"i\",\"kind\":\"dc-voltage-positive\",\"target\":\"X\"}]"), "no element 'X'");
    }

    [Theory]
    [InlineData(6, 20.0)]
    [InlineData(12, 20.0)]
    [InlineData(12, 35.0)]
    public void Lcc_bridge_group_through_the_document_matches_the_closed_form_dc_voltage_and_current(int pulses, double alphaDeg)
    {
        // R419 Lcc_bridge_group_dc_voltage_and_current_match_the_closed_forms: the same bridge (10 kV, 50 Hz, Lc 5 mH, Rs 1 mohm), the same R-L load
        // and the same bound. Closed form Vd = N [Vd0 cos(alpha) - (3 w Lc / pi) Id - 2 (Ron + Rs) Id], Vd0 = 3 sqrt(2) V_LL / pi per six-pulse bridge.
        string entry = "{\"name\":\"G\",\"kind\":\"lcc\",\"v_ll_rms\":10000,\"frequency_hz\":50,\"commutation_l_h\":5e-3,\"source_r_ohm\":1e-3,\"pulses\":" + pulses
            + ",\"alpha_deg\":" + N(alphaDeg) + ",\"load\":{\"r_ohm\":30,\"l_h\":0.2}}";
        string rest = "\"buses\":[],\"hvdc\":[" + entry + "],"
            + "\"outputs\":[{\"name\":\"id\",\"kind\":\"dc-current\",\"target\":\"G\"},{\"name\":\"vp\",\"kind\":\"dc-voltage-positive\",\"target\":\"G\"},"
            + "{\"name\":\"vn\",\"kind\":\"dc-voltage-negative\",\"target\":\"G\"}]";
        string docText = Doc("1e-5", "0.2", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("lcc-" + pulses + "-" + alphaDeg, docText);
        Assert.True(r.ExitCode == 0, r.ToString());
        double[] t = Col(res, "time_s"), id = Col(res, "id"), vp = Col(res, "vp"), vn = Col(res, "vn");
        double nGrp = pulses / 6, w = 2 * Pi * 50, alpha = alphaDeg * Pi / 180, rLoad = 30.0;
        double vd0 = nGrp * 3 * Math.Sqrt(2) / Pi * 10000.0, rc = nGrp * 3 * w * 5e-3 / Pi, rcond = nGrp * 2 * (1e-3 + 1e-3);
        double idClosed = vd0 * Math.Cos(alpha) / (rLoad + rc + rcond), vdClosed = rLoad * idClosed;
        var vd = new double[t.Length];
        for (int i = 0; i < vd.Length; i++) vd[i] = vp[i] - vn[i];
        Assert.InRange(Math.Abs(WindowMean(t, id, 0.1, 0.2) - idClosed) / idClosed, 0.0, 6e-3);
        Assert.InRange(Math.Abs(WindowMean(t, vd, 0.1, 0.2) - vdClosed) / vdClosed, 0.0, 6e-3);
        Assert.Equal("Switched", res.GetProperty("tables").GetProperty("converters")[0].GetProperty("model_class").GetString());
        // a repeat is byte-identical
        (CliProcess.Result r2, JsonElement res2, _) = Run("lcc-again-" + pulses + "-" + alphaDeg, docText);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    private static string StationJson(string name, string mode, double setPoint, double dcV, string extra = "") =>
        "{\"name\":\"" + name + "\",\"kind\":\"vsc\",\"grid_v_ll_rms\":" + N(8000 * Math.Sqrt(1.5)) + ",\"frequency_hz\":50,\"grid_angle_deg\":0,\"grid_r_ohm\":0.05,\"grid_l_h\":1e-3,"
        + "\"filter_r_ohm\":0.05,\"filter_l_h\":5e-3,\"converter_rs_ohm\":0.01,\"dc_nominal_v\":" + N(dcV) + ",\"dc_cap_f\":5e-3,\"converter\":\"average\","
        + "\"mode\":\"" + mode + "\",\"set_point\":" + N(setPoint) + ",\"current_bandwidth_rad_s\":1000,\"outer_bandwidth_rad_s\":60,\"control_every\":2,\"initial_dc_v\":" + N(dcV) + extra + "}";

    [Fact]
    public void Vsc_hvdc_link_through_the_document_starts_from_the_load_flow_state_and_matches_the_dc_closed_form()
    {
        // R419 Vsc_link_dc_steady_state_matches_the_closed_form_and_starts_from_the_load_flow_state: the same two stations (8 kV peak, 1 mH grid, 5 mH reactor,
        // 10 mohm converter), the same DC line (loop 2 ohm, 300 ohm, 0.5 ms), the same load-flow solution of the VscDcLink and the same bounds.
        const double orderMw = 8.0, rdc = 2.0, v2kv = 20.0, rf = 0.05, rs = 0.01;
        var first = new VscConverter(1, VscDcControl.Power, VscAcControl.Voltage, orderMw, 1.0, 0, 0, 0, 0, 0, 1.0, 1000, -1000);
        var second = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.Voltage, v2kv, 1.0, 0, 0, 0, 0, 0, 1.0, 1000, -1000);
        var sol = VscDcLinks.SolveDc(new VscDcLink("L", rdc, first, second));
        double iLf = sol.CurrentKa * 1000.0, vd1Lf = sol.FirstDcKv * 1000.0, vd2Lf = sol.SecondDcKv * 1000.0;
        Assert.Equal(orderMw, vd1Lf * (vd1Lf - vd2Lf) / rdc / 1e6, 9);                    // the load-flow closed form P = Vd1 (Vd1 - Vd2) / R
        double pDc = orderMw * 1e6, pPcc = pDc;
        for (int k = 0; k < 3; k++) { double ihat = 2.0 * pPcc / (3.0 * 8000.0); pPcc = pDc + 1.5 * (rf + rs) * ihat * ihat; }

        string rest = "\"buses\":[],\"hvdc\":["
            + StationJson("S1", "power", -pPcc, vd1Lf) + "," + StationJson("S2", "dc-voltage", vd2Lf, vd2Lf) + ","
            + "{\"name\":\"DC\",\"kind\":\"dc-line\",\"from\":\"S1\",\"to\":\"S2\",\"r_ohm\":" + N(rdc) + ",\"surge_ohm\":300,\"delay_s\":5e-4,\"initial_current_a\":" + N(iLf) + "}],"
            + "\"outputs\":[{\"name\":\"v1p\",\"kind\":\"dc-voltage-positive\",\"target\":\"S1\"},{\"name\":\"v1n\",\"kind\":\"dc-voltage-negative\",\"target\":\"S1\"},"
            + "{\"name\":\"v2p\",\"kind\":\"dc-voltage-positive\",\"target\":\"S2\"},{\"name\":\"v2n\",\"kind\":\"dc-voltage-negative\",\"target\":\"S2\"},"
            + "{\"name\":\"iline\",\"kind\":\"dc-current\",\"target\":\"DC\",\"end\":\"from\"},{\"name\":\"iline2\",\"kind\":\"dc-current\",\"target\":\"DC\",\"end\":\"to\"}]";
        string docText = Doc("5e-5", "1.2", rest);
        (CliProcess.Result r, JsonElement res, _) = Run("vsc-link", docText);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(2, res.GetProperty("tables").GetProperty("converters").GetArrayLength());
        double[] t = Col(res, "time_s"), il = Col(res, "iline"), il2 = Col(res, "iline2");
        double[] v1 = Col(res, "v1p").Zip(Col(res, "v1n"), (a, b) => a - b).ToArray(), v2 = Col(res, "v2p").Zip(Col(res, "v2n"), (a, b) => a - b).ToArray();
        // t = dt: the state starts from the load-flow solution
        Assert.InRange(Math.Abs(v1[1] - vd1Lf) / vd1Lf, 0.0, 2e-3);
        Assert.InRange(Math.Abs(v2[1] - vd2Lf) / vd2Lf, 0.0, 2e-3);
        Assert.InRange(Math.Abs(il[1] - iLf) / iLf, 0.0, 2e-3);
        // steady state over the last 0.2 s
        double mv1 = WindowMean(t, v1, 1.0, 1.2), mv2 = WindowMean(t, v2, 1.0, 1.2), mi = WindowMean(t, il, 1.0, 1.2), mi2 = WindowMean(t, il2, 1.0, 1.2);
        double pDc1 = mv1 * mi, pClosed = mv1 * (mv1 - mv2) / rdc;
        Assert.InRange(Math.Abs(mv2 - vd2Lf) / vd2Lf, 0.0, 2e-3);
        Assert.InRange(Math.Abs(mi - iLf) / iLf, 0.0, 5e-3);
        Assert.InRange(Math.Abs(mv1 - vd1Lf) / vd1Lf, 0.0, 3e-3);
        Assert.InRange(Math.Abs(pDc1 - pClosed) / pClosed, 0.0, 3e-3);
        Assert.InRange(Math.Abs(pDc1 / 1e6 - orderMw) / orderMw, 0.0, 6e-3);
        Assert.InRange(Math.Abs(mi + mi2) / mi, 0.0, 1e-3);
        (CliProcess.Result r2, JsonElement res2, _) = Run("vsc-link-2", docText);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(Trace(res), Trace(res2));
    }

    private static string MmcStation(string converter) =>
        "{\"name\":\"M\",\"kind\":\"vsc\",\"grid_v_ll_rms\":" + N(8000 * Math.Sqrt(1.5)) + ",\"frequency_hz\":50,\"grid_angle_deg\":0,\"grid_r_ohm\":0.05,\"grid_l_h\":1e-3,"
        + "\"filter_r_ohm\":0.02,\"filter_l_h\":5e-3,\"dc_nominal_v\":20000,\"dc_cap_f\":1e-3,\"converter\":\"" + converter + "\","
        + "\"mmc\":{\"submodules_per_arm\":20,\"submodule_cap_f\":0.02,\"arm_resistance_ohm\":0.1,\"arm_inductance_h\":6e-3},"
        + "\"mode\":\"power\",\"set_point\":4e6,\"current_bandwidth_rad_s\":1000,\"outer_bandwidth_rad_s\":60,\"control_every\":2,\"dc_supply_v\":20000,\"dc_supply_ohm\":0.001}";

    [Fact]
    public void Mmc_arm_average_and_sub_module_stations_through_the_document_agree_and_balance_the_dc_power()
    {
        // R419 Sub_module_level_model_agrees_with_the_arm_average_model: the same station (20 sub-modules per arm, 20 kV stiff DC side, 4 MW injected), run to
        // 0.5 s and compared over 0.4 to 0.5 s with the same bounds (AC current amplitude 1.5 per cent, DC current 2 per cent). The DC supply current is
        // the oracle's 3 x the mean upper-arm current; the power balance Vdc Idc = P + losses is added.
        (JsonElement avg, JsonElement det) = (default, default);
        foreach (string kind in new[] { "mmc-average", "mmc-detailed" })
        {
            string rest = "\"buses\":[],\"hvdc\":[" + MmcStation(kind) + "],"
                + "\"outputs\":[{\"name\":\"ia\",\"kind\":\"current\",\"target\":\"M\",\"phase\":\"a\"},{\"name\":\"idc\",\"kind\":\"dc-current\",\"target\":\"M\"}]";
            (CliProcess.Result r, JsonElement res, _) = Run("mmc-" + kind, Doc("5e-5", "0.5", rest));
            Assert.True(r.ExitCode == 0, r.ToString());
            // the Core classes both MMC models as average-value (no valve-level switching ripple); the description says which stack
            Assert.Equal("AverageValue", res.GetProperty("tables").GetProperty("converters")[0].GetProperty("model_class").GetString());
            if (kind == "mmc-average") avg = res; else det = res;
        }
        double[] tA = Col(avg, "time_s"), tD = Col(det, "time_s");
        double iA = WindowPhasor(tA, Col(avg, "ia"), 0.4, 0.5, 50).Magnitude, iD = WindowPhasor(tD, Col(det, "ia"), 0.4, 0.5, 50).Magnitude;
        double idcA = Math.Abs(WindowMean(tA, Col(avg, "idc"), 0.4, 0.5)), idcD = Math.Abs(WindowMean(tD, Col(det, "idc"), 0.4, 0.5));
        Assert.InRange(Math.Abs(iA - iD) / iA, 0.0, 0.015);
        Assert.InRange(Math.Abs(idcA - idcD) / idcA, 0.0, 0.02);
        // power balance: the 20 kV supply delivers the 4 MW order plus the converter, reactor and grid losses, within 2 per cent
        Assert.InRange(Math.Abs(idcA * 20000.0 - 4e6) / 4e6, 0.0, 0.02);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The example documents and the schema file
    // ------------------------------------------------------------------------------------------------------------

    private static JsonElement Schema()
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(CliProcess.Root(), "schemas", "emt3", "emt3.schema.json")));
        return d.RootElement.Clone();
    }

    [Fact]
    public void The_schema_file_lists_every_top_level_key_of_every_example_document_and_each_example_runs()
    {
        JsonElement schema = Schema();
        Assert.Equal("gridsim-emt3/1", schema.GetProperty("$id").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        JsonElement props = schema.GetProperty("properties");
        string[] files = Directory.GetFiles(Path.Combine(CliProcess.Root(), "docs", "examples", "emt3"), "*.json");
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            JsonElement doc = Read(f);
            foreach (JsonProperty p in doc.EnumerateObject()) Assert.True(props.TryGetProperty(p.Name, out _), $"{Path.GetFileName(f)}: key '{p.Name}' is not in the schema");
            string o = Path.Combine(_dir, Path.GetFileName(f) + ".out.json");
            CliProcess.Result r = CliProcess.RunBuilt("emt3", f, "--json", o);
            Assert.True(r.ExitCode == 0, f + " " + r);
            Assert.Equal("Completed", Read(o).GetProperty("summary").GetProperty("status").GetString());
        }
    }
}
