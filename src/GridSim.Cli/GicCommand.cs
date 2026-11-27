using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis.Gic;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP28: <c>gridsim gic &lt;case&gt; --gic &lt;file.json&gt; [--field N,E] [--direction-sweep STEP] [--out &lt;file.json&gt;]</c>.
/// Geomagnetically induced currents for a uniform geoelectric field (Core <see cref="GicSolver"/>), with the DC data from a
/// <c>gridsim-gic/1</c> file. <c>--field</c> gives the northward and eastward field in V/km; <c>--direction-sweep</c> the
/// step in degrees of a sweep at 1 V/km that reports each transformer's largest effective GIC. At least one is required.
/// Writes its own JSON with <c>--out</c> (never an existing file); not on the shared --json/--csv writer. Exit 0 solved;
/// 1 an unreadable case or gic file, or a network that cannot be solved; 2 usage.
/// </summary>
internal static class GicCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim gic <case> --gic <file.json> [--field N,E] [--direction-sweep <degrees>] [--out <file.json>]";

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        Wp27CliSupport.Args? args = Wp27CliSupport.Parse(a, "gic", Usage, ["--gic", "--field", "--direction-sweep", "--out"], []);
        if (args is null) return CliExit.Usage;
        if (args.Positional.Count != 1 || args.Get("--gic") is null || (args.Get("--field") is null && args.Get("--direction-sweep") is null))
        {
            Console.Error.WriteLine(args.Positional.Count > 1 ? $"error: gic takes one case. {Usage}" : $"error: gic needs a case, --gic and --field or --direction-sweep. {Usage}");
            return CliExit.Usage;
        }
        double en = 0, ee = 0;
        bool hasField = args.Get("--field") is not null;
        if (hasField)
        {
            string[] parts = args.Get("--field")!.Split(',');
            if (parts.Length != 2 || !double.TryParse(parts[0].Trim(), NumberStyles.Float, Inv, out en) || !double.TryParse(parts[1].Trim(), NumberStyles.Float, Inv, out ee)
                || !double.IsFinite(en) || !double.IsFinite(ee))
            {
                Console.Error.WriteLine($"error: gic: --field expected two finite numbers N,E in V/km but got '{args.Get("--field")}'. {Usage}");
                return CliExit.Usage;
            }
        }
        double step = 0;
        bool sweep = args.Get("--direction-sweep") is not null;
        if (sweep && (!double.TryParse(args.Get("--direction-sweep"), NumberStyles.Float, Inv, out step) || !(step > 0) || step > 180))
        {
            Console.Error.WriteLine($"error: gic: --direction-sweep expected a step above 0 and at most 180 degrees but got '{args.Get("--direction-sweep")}'. {Usage}");
            return CliExit.Usage;
        }
        if (!Wp27CliSupport.PrepareOutput(args.Get("--out"), "gic", "GIC result", out string? output)) return CliExit.Usage;

        GridModel model;
        GicNetwork net;
        GicResult? r = null;
        IReadOnlyList<GicSweepEntry>? sw = null;
        try
        {
            model = load(args.Positional[0]);
            GicFile file = GicFile.Load(Path.GetFullPath(args.Get("--gic")!));
            net = GicNetwork.Build(model, file);
            if (hasField) r = GicSolver.Solve(net, en, ee);
            if (sweep) sw = GicSolver.Sweep(net, step);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: gic: " + ex.Message);
            return CliExit.InputError;
        }

        Console.WriteLine($"GIC - {model.Name}: {net.Substations.Count.ToString(Inv)} substation(s), {net.Lines.Count.ToString(Inv)} line(s), {net.Units.Count.ToString(Inv)} transformer(s); gic file sha256 {net.FileSha256}");
        if (r is not null)
        {
            Console.WriteLine($"  field {R(en)} V/km north, {R(ee)} V/km east");
            foreach (GicSubstationResult s in r.Substations)
                Console.WriteLine($"  substation {s.Name,-16} neutral {s.NeutralA.ToString("F3", Inv),12} A (3 phases)");
            foreach (GicTransformerResult t in r.Transformers)
                Console.WriteLine($"  transformer {t.Branch,-14} I_H {t.IhA.ToString("F3", Inv),10} A  I_X {t.IxA.ToString("F3", Inv),10} A  I_eff {t.EffectiveA.ToString("F3", Inv),10} A/phase"
                    + (t.QMvar is double q ? $"  Q {q.ToString("F3", Inv)} Mvar" : ""));
            foreach (GicLineResult l in r.Lines)
                Console.WriteLine($"  line {l.Branch,-14} {l.SourceV.ToString("F3", Inv),12} V  {l.CurrentA.ToString("F3", Inv),12} A (3 phases)");
        }
        if (sw is not null)
        {
            Console.WriteLine($"  direction sweep at 1 V/km, step {R(step)} degrees:");
            foreach (GicSweepEntry e in sw)
                Console.WriteLine($"  transformer {e.Branch,-14} max I_eff {e.MaxEffectiveA.ToString("F3", Inv),10} A/phase at {R(e.DirectionDeg)} degrees");
        }

        if (output is not null)
        {
            try
            {
                Wp27CliSupport.WriteJson(output, w =>
                {
                    w.WriteString("schema", "gridsim-gic-result/1");
                    w.WriteString("case", model.Name);
                    w.WriteString("gicFileSha256", net.FileSha256);
                    if (r is not null) WriteResult(w, r);
                    if (sw is not null)
                    {
                        Wp27CliSupport.Number(w, "sweepStepDeg", step);
                        w.WriteStartArray("sweep");
                        foreach (GicSweepEntry e in sw)
                        {
                            w.WriteStartObject();
                            w.WriteString("branch", e.Branch);
                            Wp27CliSupport.Number(w, "maxEffectiveA", e.MaxEffectiveA);
                            Wp27CliSupport.Number(w, "directionDeg", e.DirectionDeg);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                });
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("error: gic: cannot write the result: " + ex.Message);
                return CliExit.InputError;
            }
            Console.WriteLine($"  wrote {output}");
        }
        return CliExit.Ok;
    }

    private static void WriteResult(Utf8JsonWriter w, GicResult r)
    {
        Wp27CliSupport.Number(w, "fieldNorthVPerKm", r.FieldNorthVPerKm);
        Wp27CliSupport.Number(w, "fieldEastVPerKm", r.FieldEastVPerKm);
        w.WriteStartArray("substations");
        foreach (GicSubstationResult s in r.Substations)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            Wp27CliSupport.Number(w, "neutralA", s.NeutralA);
            Wp27CliSupport.Number(w, "neutralV", s.NeutralV);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("transformers");
        foreach (GicTransformerResult t in r.Transformers)
        {
            w.WriteStartObject();
            w.WriteString("branch", t.Branch);
            w.WriteString("type", t.Type.ToString());
            w.WriteNumber("hvBus", t.HvBus);
            w.WriteNumber("lvBus", t.LvBus);
            Wp27CliSupport.Number(w, "ratio", t.Ratio);
            Wp27CliSupport.Number(w, "ihA", t.IhA);
            Wp27CliSupport.Number(w, "ixA", t.IxA);
            Wp27CliSupport.Number(w, "neutralA", t.NeutralA);
            Wp27CliSupport.Number(w, "effectiveA", t.EffectiveA);
            Wp27CliSupport.Number(w, "voltagePu", t.VoltagePu);
            if (t.QMvar is double q) Wp27CliSupport.Number(w, "qMvar", q); else w.WriteNull("qMvar");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("lines");
        foreach (GicLineResult l in r.Lines)
        {
            w.WriteStartObject();
            w.WriteString("branch", l.Branch);
            w.WriteNumber("fromBus", l.FromBus);
            w.WriteNumber("toBus", l.ToBus);
            Wp27CliSupport.Number(w, "northKm", l.NorthKm);
            Wp27CliSupport.Number(w, "eastKm", l.EastKm);
            Wp27CliSupport.Number(w, "sourceV", l.SourceV);
            Wp27CliSupport.Number(w, "ohmsPerPhase", l.OhmsPerPhase);
            Wp27CliSupport.Number(w, "currentA", l.CurrentA);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string R(double v) => v.ToString("R", Inv);
}
