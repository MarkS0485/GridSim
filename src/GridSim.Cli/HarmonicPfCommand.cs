using System.Globalization;
using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP27: <c>gridsim harmonic-pf &lt;case&gt; --spectra &lt;file.json&gt; [--orders 5,7] [--norton-g 0] [--norton-b 0]
/// [--out &lt;file.json&gt;]</c>. The coupled harmonic power flow (<see cref="CoupledHarmonicPowerFlow"/>, unchanged) on the
/// study model the <c>harmonics</c> verb uses (<see cref="HarmonicModelOptions.Study"/>: machine source reactance, loads as
/// damping shunts, balanced sequences), with the sources of a <c>gridsim-harmonics/1</c> file. Each source bus carries a
/// Norton coupling admittance <c>g + j b h</c> (pu, <c>--norton-g</c>, <c>--norton-b</c>; both 0 by default, which is the
/// plain penetration solve). The THD denominator is the solved fundamental (Newton, 100 iterations, reactive limits on).
/// Prints and writes, per bus and order, |V_h| and its angle, the voltage THD, the iteration count and whether the
/// fixed-point loop converged. Exit 0 converged; 3 not converged, or the fundamental did not converge; 1 an unreadable
/// case or source file, or a singular Y(h); 2 usage.
/// </summary>
internal static class HarmonicPfCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim harmonic-pf <case> --spectra <file.json> [--orders 5,7,11] [--norton-g 0] [--norton-b 0] [--out <file.json>]";

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, "harmonic-pf", Usage, ["--spectra", "--orders", "--norton-g", "--norton-b", "--out"], []);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 1 || args.Get("--spectra") is null)
        {
            Console.Error.WriteLine(args.Positional.Count > 1 ? $"error: harmonic-pf takes one case. {Usage}" : Usage);
            return CliExit.Usage;
        }
        IReadOnlyList<int>? orders = null;
        if (args.Get("--orders") is { } ot)
        {
            var list = new List<int>();
            foreach (string part in ot.Split(','))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, Inv, out int h) || h < 2 || list.Contains(h))
                {
                    Console.Error.WriteLine($"error: harmonic-pf: --orders expected distinct whole numbers of 2 or more, separated by commas, but got '{ot}'. {Usage}");
                    return CliExit.Usage;
                }
                list.Add(h);
            }
            orders = list;
        }
        if (!Number(args, "--norton-g", out double g) || !Number(args, "--norton-b", out double b)) return CliExit.Usage;
        if (g < 0)
        {
            Console.Error.WriteLine($"error: harmonic-pf: --norton-g is a conductance and may not be negative. {Usage}");
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), "harmonic-pf", "harmonic power flow result", out string? output)) return CliExit.Usage;

        GridModel model;
        HarmonicSourceSet set;
        try
        {
            model = load(args.Positional[0]);
            set = HarmonicSourceSet.Load(args.Get("--spectra")!);
            set.Validate(model);
        }
        // CLI-06: JsonException too (a spectra file that is not valid JSON, or not an object), as the sibling verbs catch it.
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException
            or ForwardInferenceException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("error: harmonic-pf: " + ex.Message);
            return CliExit.InputError;
        }
        if (set.Sources.Count == 0)
        {
            Console.Error.WriteLine("error: harmonic-pf: the source file has no source.");
            return CliExit.InputError;
        }

        HarmonicModelOptions opt = orders is null ? HarmonicModelOptions.Study : HarmonicModelOptions.Study with { Orders = orders };
        var coupling = new NortonCouplingModel(g, b);
        CoupledHarmonicResult r;
        PowerFlowResult pf;
        HarmonicIndicesResult thd;
        try
        {
            // A component at an order the study does not analyse would be dropped and the THD understated: refused, as the
            // harmonics verb refuses it.
            HarmonicPenetration.RefuseComponentsOutsideOrders(set.Sources, opt);
            r = CoupledHarmonicPowerFlow.Solve(model, set.Sources, coupling, opt);
            pf = SolverSelector.CreateNewton(model).Solve(model, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
            if (!pf.Converged)
            {
                Console.WriteLine($"Harmonic power flow - {model.Name}: the fundamental power flow did NOT CONVERGE ({pf.Status}); no THD is reported.   [EXPERIMENTAL]");
                return CliExit.Verdict;
            }
            thd = HarmonicIndices.FromPenetration(r.Result, HarmonicIndices.SolvedFundamentalVm(r.Result, pf));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SingularHarmonicNetworkException)
        {
            Console.Error.WriteLine("error: harmonic-pf: " + ex.Message);
            return CliExit.InputError;
        }
        double[] v1 = HarmonicIndices.SolvedFundamentalVm(r.Result, pf);

        HarmonicPenetrationResult res = r.Result;
        Console.WriteLine($"Harmonic power flow - {model.Name} ({model.BusCount.ToString(Inv)} buses, {set.Sources.Count.ToString(Inv)} source(s), orders {string.Join(",", res.Orders)})   [EXPERIMENTAL]");
        Console.WriteLine($"  Norton coupling at each source bus: g {R(g)} pu, b {R(b)} pu x order (coupling model: {r.CouplingProvenance})");
        Console.WriteLine($"  fixed-point loop: {(r.Converged ? "converged" : "NOT CONVERGED")}, most iterations at one order {r.Iterations.ToString(Inv)}");
        for (int i = 0; i < res.BusIds.Count; i++)
            Console.WriteLine($"  bus {res.BusIds[i].ToString(Inv),-6} THD {thd.ThdVoltagePct[i].ToString("F4", Inv)} %");
        Console.WriteLine($"  worst bus {thd.WorstBusId.ToString(Inv)}: THD {thd.WorstThdPct.ToString("F4", Inv)} %");

        if (output is not null)
        {
            try
            {
                Wp27CliSupport.WriteJson(output, w =>
                {
                    w.WriteString("schema", "gridsim-harmonic-pf/1");
                    w.WriteBoolean("experimental", true);
                    w.WriteString("case", model.Name);
                    w.WriteStartArray("orders");
                    foreach (int h in res.Orders) w.WriteNumberValue(h);
                    w.WriteEndArray();
                    Wp27CliSupport.Number(w, "nortonG", g);
                    Wp27CliSupport.Number(w, "nortonB", b);
                    w.WriteString("couplingProvenance", r.CouplingProvenance.ToString());
                    w.WriteNumber("iterations", r.Iterations);
                    w.WriteBoolean("converged", r.Converged);
                    w.WriteNumber("worstBus", thd.WorstBusId);
                    Wp27CliSupport.Number(w, "worstThdPct", thd.WorstThdPct);
                    w.WriteStartArray("buses");
                    for (int i = 0; i < res.BusIds.Count; i++)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("bus", res.BusIds[i]);
                        Wp27CliSupport.Number(w, "fundamentalVmPu", v1[i]);
                        Wp27CliSupport.Number(w, "thdPct", thd.ThdVoltagePct[i]);
                        w.WriteStartArray("harmonics");
                        for (int oi = 0; oi < res.Orders.Count; oi++)
                        {
                            Complex v = res.Voltage[oi][i];
                            w.WriteStartObject();
                            w.WriteNumber("order", res.Orders[oi]);
                            Wp27CliSupport.Number(w, "re", v.Real);
                            Wp27CliSupport.Number(w, "im", v.Imaginary);
                            Wp27CliSupport.Number(w, "vmPu", v.Magnitude);
                            Wp27CliSupport.Number(w, "vaDeg", GridSim.Core.Numerics.DetMath.Phase(v) * (180.0 / Math.PI));
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("error: harmonic-pf: cannot write the result: " + ex.Message);
                return CliExit.InputError;
            }
            Console.WriteLine($"  wrote {output}");
        }
        return r.Converged ? CliExit.Ok : CliExit.Verdict;
    }

    private static bool Number(Wp27CliSupport.Args args, string flag, out double value)
    {
        value = 0.0;
        if (args.Get(flag) is not { } t) return true;
        if (double.TryParse(t, NumberStyles.Float, Inv, out value) && double.IsFinite(value)) return true;
        Console.Error.WriteLine($"error: harmonic-pf: {flag} expected a finite number but got '{t}'. {Usage}");
        return false;
    }

    private static string R(double v) => v.ToString("R", Inv);
}
