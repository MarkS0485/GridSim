using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Dynamics.CoSim;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP27: <c>gridsim cosim &lt;study.json&gt; [--out &lt;file.json&gt;] [--boundary-csv &lt;file.csv&gt;]</c>. RMS-EMT
/// co-simulation through the Core interface (<see cref="RmsEmtInterface"/>, unchanged). The study is a strict
/// <c>gridsim-cosim/1</c> document: an inline <c>gridsim-emt3/1</c> study (the detailed part), the boundary bus of that
/// study, the exchange form (<c>thevenin</c> or <c>norton</c>), the exchange interval in EMT steps, and the RMS side. The
/// RMS side is the Core <see cref="PhasorSide"/>: a source phasor behind a lossless line (surge impedance and travel time)
/// or behind a series impedance, with optional scheduled source changes. It is not the RMS transient kernel, which has no
/// entry to exchange a boundary quantity part-way through a run. The interface is added after every element of the EMT
/// study. Prints the interface description and the outputs; <c>--out</c> writes the EMT traces and the exchange log as
/// JSON (<c>gridsim-cosim/1</c>), <c>--boundary-csv</c> the exchange log alone (time, the voltage and current the EMT side
/// measured, the voltage the RMS side computed, the equivalent it handed over). Exit 0 completed; 3 the EMT run ended with
/// a named status other than completed; 1 a missing or invalid file, or a refused network or interface; 2 usage.
/// </summary>
internal static class CoSimCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim cosim <study.json> [--out <file.json>] [--boundary-csv <file.csv>]";

    /// <summary>The parsed RMS side and interface settings.</summary>
    internal sealed record CoSimSpec(Emt3Study Emt, string BoundaryBus, CoSimForm Form, int ExchangeEverySteps, Complex Source,
        double? SurgeOhm, double? TravelS, Complex? Series, IReadOnlyList<(double AtS, Complex Phasor)> Changes);

    /// <summary>Parse a <c>gridsim-cosim/1</c> document strictly.</summary>
    internal static CoSimSpec Parse(JsonElement root)
    {
        static Emt3Command.InputException Bad(string m) => new(m);
        static void Keys(JsonElement e, string where, params string[] allowed)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Bad($"{where} must be an object.");
            foreach (JsonProperty p in e.EnumerateObject())
                if (Array.IndexOf(allowed, p.Name) < 0) throw Bad($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
        }
        static JsonElement Req(JsonElement e, string key, string where) =>
            e.TryGetProperty(key, out JsonElement v) ? v : throw Bad($"{where}: \"{key}\" is required.");
        static double Num(JsonElement e, string key, string where)
        {
            JsonElement v = Req(e, key, where);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) throw Bad($"{where}: \"{key}\" must be a finite number.");
            return d;
        }
        static string Str(JsonElement e, string key, string where)
        {
            JsonElement v = Req(e, key, where);
            return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Bad($"{where}: \"{key}\" must be a string.");
        }

        Keys(root, "document", "schema", "emt3", "boundary_bus", "form", "exchange_every_steps", "rms");
        string schema = Str(root, "schema", "document");
        if (schema != "gridsim-cosim/1") throw Bad($"schema must be \"gridsim-cosim/1\" but is \"{schema}\".");
        Emt3Study emt = Emt3Command.Parse(Req(root, "emt3", "document"));
        string bus = Str(root, "boundary_bus", "document");
        CoSimForm form = Str(root, "form", "document") switch
        {
            "thevenin" => CoSimForm.Thevenin,
            "norton" => CoSimForm.Norton,
            var f => throw Bad($"form must be \"thevenin\" or \"norton\" but is \"{f}\"."),
        };
        double every = Num(root, "exchange_every_steps", "document");
        if (every != Math.Floor(every) || every < 1 || every > int.MaxValue) throw Bad("exchange_every_steps must be a whole number of 1 or more.");
        JsonElement rms = Req(root, "rms", "document");
        Keys(rms, "rms", "source_re_v", "source_im_v", "line", "series", "source_changes");
        var source = new Complex(Num(rms, "source_re_v", "rms"), Num(rms, "source_im_v", "rms"));
        bool hasLine = rms.TryGetProperty("line", out JsonElement line), hasSeries = rms.TryGetProperty("series", out JsonElement series);
        if (hasLine == hasSeries) throw Bad("rms: give exactly one of \"line\" and \"series\".");
        double? zc = null, tau = null;
        Complex? zs = null;
        if (hasLine)
        {
            Keys(line, "rms.line", "surge_ohm", "travel_s");
            zc = Num(line, "surge_ohm", "rms.line");
            tau = Num(line, "travel_s", "rms.line");
        }
        else
        {
            Keys(series, "rms.series", "r_ohm", "x_ohm");
            zs = new Complex(Num(series, "r_ohm", "rms.series"), Num(series, "x_ohm", "rms.series"));
        }
        var changes = new List<(double, Complex)>();
        if (rms.TryGetProperty("source_changes", out JsonElement ch))
        {
            if (ch.ValueKind != JsonValueKind.Array) throw Bad("rms: \"source_changes\" must be an array.");
            int i = 0;
            foreach (JsonElement c in ch.EnumerateArray())
            {
                string w = $"rms.source_changes[{i++}]";
                Keys(c, w, "at_s", "re_v", "im_v");
                changes.Add((Num(c, "at_s", w), new Complex(Num(c, "re_v", w), Num(c, "im_v", w))));
            }
        }
        return new CoSimSpec(emt, bus, form, (int)every, source, zc, tau, zs, changes);
    }

    /// <summary>The RMS side of a parsed study, with its scheduled changes.</summary>
    internal static PhasorSide RmsSide(CoSimSpec s)
    {
        PhasorSide side = s.Series is { } z ? new PhasorSide(s.Source, z) : new PhasorSide(s.Source, s.Emt.FrequencyHz, s.SurgeOhm!.Value, s.TravelS!.Value);
        foreach ((double at, Complex p) in s.Changes) side.ScheduleSourceChange(at, p);
        return side;
    }

    public static int Run(List<string> a)
    {
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, "cosim", Usage, ["--out", "--boundary-csv"], []);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 1)
        {
            Console.Error.WriteLine(args.Positional.Count > 1 ? $"error: cosim takes one study file. {Usage}" : Usage);
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), "cosim", "co-simulation result", out string? output)) return CliExit.Usage;
        if (!Wp27CliSupport.PrepareOutput(args.Get("--boundary-csv"), "cosim", "co-simulation boundary log", out string? csv)) return CliExit.Usage;
        if (output is not null && csv is not null && string.Equals(output, csv, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"error: cosim: --out and --boundary-csv name the same file. {Usage}");
            return CliExit.Usage;
        }

        string file = args.Positional[0];
        CoSimSpec spec;
        try
        {
            if (!File.Exists(file)) { Console.Error.WriteLine($"error: study file not found: {file}"); return CliExit.InputError; }
            using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(file)), new JsonDocumentOptions { AllowTrailingCommas = false });
            spec = Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or Emt3Command.InputException or LineConstantsCommand.InputException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: cosim: {file}: {ex.Message}");
            return CliExit.InputError;
        }

        Emt3Result res;
        RmsEmtInterface? itf = null;
        try
        {
            PhasorSide side = RmsSide(spec);
            var options = new CoSimOptions(spec.Form, spec.ExchangeEverySteps, spec.Emt.FrequencyHz);
            res = Emt3Runner.Run(spec.Emt, (net, nodes) => itf = RmsEmtInterface.Add(net, "rms", "", nodes(spec.BoundaryBus), side, options, spec.Emt.StepS));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: cosim: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }
        RmsEmtInterface i = itf!;
        NodalResult n = res.Run;
        Console.WriteLine($"COSIM - {n.Method}, step {R(n.StepSeconds)} s, {n.Stats.Steps.ToString(Inv)} step(s), boundary bus {spec.BoundaryBus}, {i.Exchanges.Count.ToString(Inv)} exchange(s)");
        Console.WriteLine($"  {i.Description}");
        for (int k = 0; k < n.ProbeNames.Count; k++)
        {
            double max = 0.0;
            foreach (double x in n.Series[k]) max = Math.Max(max, Math.Abs(x));
            Console.WriteLine($"  {n.ProbeNames[k],-16} max |x| = {max.ToString("E6", Inv)}");
        }
        if (i.Exchanges.LastOrDefault(e => e.MeasurementValid) is { } last)
            Console.WriteLine($"  last valid exchange at {R(last.TimeSeconds)} s: |V| EMT {R(last.EmtVoltage.Magnitude)} V, RMS side {R(last.RmsVoltage.Magnitude)} V; |I| {R(last.EmtCurrent.Magnitude)} A (peak)");
        int exit = CliExit.Ok;
        if (n.Status != NodalStatus.Completed)
        {
            Console.Error.WriteLine($"cosim: the run did not complete: {n.StatusMessage}");
            exit = CliExit.Verdict;
        }
        else Console.WriteLine("  status: completed");

        try
        {
            if (csv is not null)
            {
                // Through the output policy, so the run journal records the log and breaking-change validate compares it;
                // CreateNew keeps the rule that an existing file is never replaced.
                using var fs = new FileStream(CliFiles.Output(csv, "co-simulation boundary log"), FileMode.CreateNew, FileAccess.Write);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
                sw.WriteLine(BoundaryHeader);
                foreach (ExchangeRecord e in i.Exchanges) sw.WriteLine(BoundaryRow(e));
                Console.WriteLine($"  wrote {csv}");
            }
            if (output is not null)
            {
                Wp27CliSupport.WriteJson(output, w =>
                {
                    w.WriteString("schema", "gridsim-cosim/1");
                    w.WriteString("status", n.Status.ToString());
                    w.WriteString("statusMessage", n.StatusMessage);
                    w.WriteString("boundaryBus", spec.BoundaryBus);
                    w.WriteString("form", spec.Form == CoSimForm.Thevenin ? "thevenin" : "norton");
                    Wp27CliSupport.Number(w, "timeStepS", n.StepSeconds);
                    w.WriteNumber("exchangeEverySteps", spec.ExchangeEverySteps);
                    w.WriteNumber("steps", n.Stats.Steps);
                    w.WriteString("interface", i.Description);
                    Wp27CliSupport.Series(w, "timeS", n.Times);
                    w.WriteStartObject("traces");
                    for (int k = 0; k < n.ProbeNames.Count; k++)
                    {
                        Wp27CliSupport.Series(w, n.ProbeNames[k], n.Series[k]);
                    }
                    w.WriteEndObject();
                    w.WriteStartArray("exchanges");
                    foreach (ExchangeRecord e in i.Exchanges)
                    {
                        w.WriteStartObject();
                        Wp27CliSupport.Number(w, "timeS", e.TimeSeconds);
                        w.WriteBoolean("valid", e.MeasurementValid);
                        Wp27CliSupport.Number(w, "emtVRe", e.EmtVoltage.Real); Wp27CliSupport.Number(w, "emtVIm", e.EmtVoltage.Imaginary);
                        Wp27CliSupport.Number(w, "emtIRe", e.EmtCurrent.Real); Wp27CliSupport.Number(w, "emtIIm", e.EmtCurrent.Imaginary);
                        Wp27CliSupport.Number(w, "rmsVRe", e.RmsVoltage.Real); Wp27CliSupport.Number(w, "rmsVIm", e.RmsVoltage.Imaginary);
                        Wp27CliSupport.Number(w, "emfRe", e.Equivalent.Emf.Real); Wp27CliSupport.Number(w, "emfIm", e.Equivalent.Emf.Imaginary);
                        Wp27CliSupport.Number(w, "zRe", e.Equivalent.Impedance.Real); Wp27CliSupport.Number(w, "zIm", e.Equivalent.Impedance.Imaginary);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
                Console.WriteLine($"  wrote {output}");
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("error: cosim: cannot write the result: " + ex.Message);
            return CliExit.InputError;
        }
        return exit;
    }

    /// <summary>The boundary CSV header (peak phasors, volts and amperes; the impedance in ohms).</summary>
    internal const string BoundaryHeader = "time_s,valid,emt_v_re,emt_v_im,emt_i_re,emt_i_im,rms_v_re,rms_v_im,emf_re,emf_im,z_re,z_im";

    /// <summary>One boundary CSV row: every number in its shortest round-trip form, so the file holds the exact bits.</summary>
    internal static string BoundaryRow(ExchangeRecord e) => string.Join(",",
        R(e.TimeSeconds), e.MeasurementValid ? "1" : "0",
        R(e.EmtVoltage.Real), R(e.EmtVoltage.Imaginary), R(e.EmtCurrent.Real), R(e.EmtCurrent.Imaginary),
        R(e.RmsVoltage.Real), R(e.RmsVoltage.Imaginary), R(e.Equivalent.Emf.Real), R(e.Equivalent.Emf.Imaginary),
        R(e.Equivalent.Impedance.Real), R(e.Equivalent.Impedance.Imaginary));

    private static string R(double v) => v.ToString("R", Inv);
}
