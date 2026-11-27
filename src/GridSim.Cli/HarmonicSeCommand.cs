using System.Globalization;
using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP27: <c>gridsim harmonic-se &lt;case&gt; &lt;measurements.json&gt; [--out &lt;file.json&gt;]</c>. Harmonic state
/// estimation (<see cref="HarmonicStateEstimator"/>, unchanged) on the study model the <c>harmonics</c> verb uses
/// (<see cref="HarmonicModelOptions.Study"/>), from a <c>gridsim-harmonic-meas/1</c> file of bus voltage phasors. Per order it
/// prints and writes the estimated injection at each candidate bus, the estimated voltage at every bus, each measurement's
/// residual and normalised residual, whether the candidates are observable, and the chi-square test at 99 %
/// (<see cref="HarmonicStateEstimateAnalysis"/>). Exit 0 when every order is observable and passes the chi-square test; 3
/// when an order is not observable or fails the test (suspected bad data); 1 an unreadable case or measurement file, or a
/// singular Y(h); 2 usage.
/// </summary>
internal static class HarmonicSeCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim harmonic-se <case> <measurements.json> [--out <file.json>]";

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, "harmonic-se", Usage, ["--out"], []);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 2)
        {
            Console.Error.WriteLine(args.Positional.Count > 2 ? $"error: harmonic-se takes a case and a measurement file. {Usage}" : Usage);
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), "harmonic-se", "harmonic state estimate", out string? output)) return CliExit.Usage;

        GridModel model;
        HarmonicMeasurementSet set;
        try
        {
            model = load(args.Positional[0]);
            set = HarmonicMeasurementSet.Load(args.Positional[1]);
            set.Validate(model);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or ForwardInferenceException)
        {
            Console.Error.WriteLine("error: harmonic-se: " + ex.Message);
            return CliExit.InputError;
        }

        var reports = new List<HarmonicStateEstimateReport>();
        try
        {
            foreach (HarmonicMeasurementOrder o in set.Orders)
                reports.Add(HarmonicStateEstimateAnalysis.Analyse(model, o.Order, o.Voltages, o.CandidateBuses, HarmonicModelOptions.Study));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SingularHarmonicNetworkException)
        {
            Console.Error.WriteLine("error: harmonic-se: " + ex.Message);
            return CliExit.InputError;
        }

        bool ok = true;
        Console.WriteLine($"Harmonic state estimation - {model.Name} ({model.BusCount.ToString(Inv)} buses, {reports.Count.ToString(Inv)} order(s))   [EXPERIMENTAL]");
        foreach (HarmonicStateEstimateReport r in reports)
        {
            if (!r.Observable)
            {
                ok = false;
                Console.WriteLine($"  order {r.Order.ToString(Inv)}: NOT OBSERVABLE - the candidate sources cannot be told apart from these measurements");
                continue;
            }
            ok &= !r.BadData;
            for (int j = 0; j < r.SourceBuses.Count; j++)
                Console.WriteLine($"  order {r.Order.ToString(Inv)}: injection at bus {r.SourceBuses[j].ToString(Inv)} {C(r.Injection[j])} pu (|I| {r.Injection[j].Magnitude.ToString("R", Inv)})");
            string test = double.IsNaN(r.Threshold)
                ? "no redundancy, no chi-square test"
                : $"chi-square {r.ChiSquare.ToString("R", Inv)} against {r.Threshold.ToString("R", Inv)} (99 %, {r.DegreesOfFreedom.ToString(Inv)} degrees of freedom): {(r.BadData ? "BAD DATA SUSPECTED" : "consistent")}";
            Console.WriteLine($"    {test}");
            if (r.LargestNormalisedResidualBus is int wb)
            {
                HarmonicMeasurementResidual worst = r.Residuals.First(x => x.Bus == wb);
                Console.WriteLine($"    largest normalised residual: bus {wb.ToString(Inv)}, {worst.NormalisedResidual.ToString("R", Inv)}");
            }
        }

        if (output is not null)
        {
            try
            {
                Wp27CliSupport.WriteJson(output, w =>
                {
                    w.WriteString("schema", "gridsim-harmonic-se/1");
                    w.WriteBoolean("experimental", true);
                    w.WriteString("case", model.Name);
                    Wp27CliSupport.Number(w, "probability", HarmonicStateEstimateAnalysis.Probability);
                    w.WriteStartArray("orders");
                    foreach (HarmonicStateEstimateReport r in reports)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("order", r.Order);
                        w.WriteBoolean("observable", r.Observable);
                        Wp27CliSupport.Number(w, "chiSquare", r.ChiSquare);
                        w.WriteNumber("degreesOfFreedom", r.DegreesOfFreedom);
                        Wp27CliSupport.Number(w, "threshold", r.Threshold);
                        w.WriteBoolean("badData", r.BadData);
                        if (r.LargestNormalisedResidualBus is int lb) w.WriteNumber("largestNormalisedResidualBus", lb);
                        else w.WriteNull("largestNormalisedResidualBus");
                        w.WriteStartArray("injections");
                        for (int j = 0; j < r.Injection.Length; j++)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("bus", r.SourceBuses[j]);
                            Wp27CliSupport.Number(w, "re", r.Injection[j].Real);
                            Wp27CliSupport.Number(w, "im", r.Injection[j].Imaginary);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteStartArray("voltages");
                        for (int i = 0; i < r.Voltage.Length; i++)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("bus", r.BusIds[i]);
                            Wp27CliSupport.Number(w, "re", r.Voltage[i].Real);
                            Wp27CliSupport.Number(w, "im", r.Voltage[i].Imaginary);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteStartArray("residuals");
                        foreach (HarmonicMeasurementResidual x in r.Residuals)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("bus", x.Bus);
                            Wp27CliSupport.Number(w, "measuredRe", x.Measured.Real);
                            Wp27CliSupport.Number(w, "measuredIm", x.Measured.Imaginary);
                            Wp27CliSupport.Number(w, "estimatedRe", x.Estimated.Real);
                            Wp27CliSupport.Number(w, "estimatedIm", x.Estimated.Imaginary);
                            Wp27CliSupport.Number(w, "residualRe", x.Residual.Real);
                            Wp27CliSupport.Number(w, "residualIm", x.Residual.Imaginary);
                            Wp27CliSupport.Number(w, "sigma", x.Sigma);
                            Wp27CliSupport.Number(w, "residualSigma", x.ResidualSigma);
                            Wp27CliSupport.Number(w, "normalisedResidual", x.NormalisedResidual);
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
                Console.Error.WriteLine("error: harmonic-se: cannot write the result: " + ex.Message);
                return CliExit.InputError;
            }
            Console.WriteLine($"  wrote {output}");
        }
        return ok ? CliExit.Ok : CliExit.Verdict;
    }

    private static string C(Complex z) =>
        z.Real.ToString("R", Inv) + (z.Imaginary < 0 ? " - j" : " + j") + Math.Abs(z.Imaginary).ToString("R", Inv);
}
