using System.Globalization;
using System.Text;
using GridSim.Core.Analysis.Reconfiguration;

namespace GridSim.Core.Analysis.Opf;

/// <summary>
/// The result files of the optimisation verbs (<c>acopf-opt</c>, <c>acscopf</c>, <c>reconfig</c>): one long table of
/// rows (record, element, field, value), written as CSV (header <c>record,element,field,value</c>) or as JSON
/// (<c>{"format": "gridsim-optimisation-result/1", "verb": ..., "rows": [[record, element, field, value], ...]}</c>).
/// Numbers are invariant-culture round-trip ("R"), rows are in a fixed order, so the same result gives the same bytes
/// under any culture.
/// </summary>
public static class OptimisationReport
{
    /// <summary>The JSON format name.</summary>
    public const string Format = "gridsim-optimisation-result/1";

    /// <summary>One row: what it is, which element, the field and the value (a number, a string or a bool).</summary>
    public readonly record struct Row(string Record, string Element, string Field, object Value);

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The rows of an <c>acopf-opt</c> run (the rounding result carries the continuous optimum).</summary>
    public static IReadOnlyList<Row> Rows(OpfRoundingResult r)
    {
        var rows = new List<Row>();
        AcOpfExtendedResult c = r.Continuous;
        rows.Add(new("summary", "", "objective_kind", c.Objective.ToString().ToLowerInvariant()));
        rows.Add(new("summary", "", "rounding", r.Status.ToString()));
        rows.Add(new("summary", "", "rounding_message", r.Message));
        if (r.Mode == OpfRoundingMode.Neighbours)
        {
            rows.Add(new("summary", "", "rounding_mode", "neighbours"));
            rows.Add(new("summary", "", "rounding_combinations", r.Combinations));
            rows.Add(new("summary", "", "rounding_combination", r.Combination));
        }
        AddOpf(rows, "continuous", c);
        foreach (OpfRoundedControl rc in r.Controls)
        {
            rows.Add(new("rounding", $"{rc.Kind} {rc.Element}", "step", rc.Step));
            rows.Add(new("rounding", $"{rc.Kind} {rc.Element}", "rounded", rc.Rounded));
            rows.Add(new("rounding", $"{rc.Kind} {rc.Element}", "other_neighbour", rc.OtherNeighbour));
        }
        if (r.FlippedControl >= 0) rows.Add(new("rounding", "", "flipped_control", r.FlippedControl));
        if (r.Final is not null && r.Status is OpfRoundingStatus.Rounded or OpfRoundingStatus.Flipped or OpfRoundingStatus.BestNeighbour) AddOpf(rows, "discrete", r.Final);
        return rows;
    }

    /// <summary>The rows of an <c>acscopf</c> run.</summary>
    public static IReadOnlyList<Row> Rows(AcScopfResult r)
    {
        var rows = new List<Row>
        {
            new("summary", "", "status", r.Status.ToString()),
            new("summary", "", "mode", r.Mode.ToString().ToLowerInvariant()),
            new("summary", "", "message", r.Message),
            new("summary", "", "rounds", r.Rounds.Count),
        };
        foreach (int k in r.Secured) rows.Add(new("outage", k.ToString(CultureInfo.InvariantCulture), "state", "secured"));
        foreach (int k in r.ScreenedOk) rows.Add(new("outage", k.ToString(CultureInfo.InvariantCulture), "state", "screened-ok"));
        foreach (int k in r.Islanding) rows.Add(new("outage", k.ToString(CultureInfo.InvariantCulture), "state", "islanding"));
        if (r.OutageList is { } list)   // general outages: the ids are list positions, so each is named
            for (int i = 0; i < list.Count; i++) rows.Add(new("outage", i.ToString(CultureInfo.InvariantCulture), "label", list[i].Label));
        foreach (AcScopfRound rd in r.Rounds)
        {
            string e = rd.Round.ToString(CultureInfo.InvariantCulture);
            rows.Add(new("round", e, "secured", string.Join(" ", rd.Secured.Select(k => k.ToString(CultureInfo.InvariantCulture)))));
            rows.Add(new("round", e, "iterations", rd.Iterations));
            rows.Add(new("round", e, "objective", rd.Objective));
            rows.Add(new("round", e, "added", string.Join(" ", rd.Added.Select(k => k.ToString(CultureInfo.InvariantCulture)))));
        }
        if (r.Optimal && r.Last is not null)
        {
            AddOpf(rows, "base", r.Last);
            foreach (AcOpfBlockResult b in r.Last.Outages)
            {
                string blk = $"outage {b.Outage.ToString(CultureInfo.InvariantCulture)}";
                rows.Add(new(blk, "", "total_generation_mw", b.TotalGenerationMw));
                foreach (AcOpfGenResult g in b.Generators)
                {
                    rows.Add(new(blk, $"unit {g.Index.ToString(CultureInfo.InvariantCulture)}", "pg_mw", g.PgMw));
                    rows.Add(new(blk, $"unit {g.Index.ToString(CultureInfo.InvariantCulture)}", "qg_mvar", g.QgMvar));
                }
                foreach (AcOpfBinding bd in b.Binding) rows.Add(new(blk, $"{bd.Kind} {bd.Element}", "shadow_price", bd.ShadowPrice));
            }
            foreach (AcOpfBinding bd in r.Last.CouplingBinding) rows.Add(new("coupling", $"{bd.Kind} {bd.Element}", "shadow_price", bd.ShadowPrice));
        }
        return rows;
    }

    /// <summary>The rows of a <c>reconfig</c> run.</summary>
    public static IReadOnlyList<Row> Rows(ReconfigResult r)
    {
        var rows = new List<Row>
        {
            new("summary", "", "status", r.Status.ToString()),
            new("summary", "", "message", r.Message),
            new("summary", "", "base_loss_mw", r.BaseLossMw),
            new("summary", "", "loss_mw", r.LossMw),
            new("summary", "", "vmin_pu", r.VminPu),
            new("summary", "", "open", string.Join(" ", r.OpenBranches.Select(k => k.ToString(CultureInfo.InvariantCulture)))),
        };
        for (int i = 0; i < r.Swaps.Count; i++)
        {
            string e = (i + 1).ToString(CultureInfo.InvariantCulture);
            rows.Add(new("swap", e, "closed", r.Swaps[i].Closed));
            rows.Add(new("swap", e, "opened", r.Swaps[i].Opened));
            rows.Add(new("swap", e, "loss_mw", r.Swaps[i].LossMw));
        }
        return rows;
    }

    private static void AddOpf(List<Row> rows, string tag, AcOpfExtendedResult r)
    {
        AcOpfIpmResult b = r.Base;
        rows.Add(new(tag, "", "status", b.Status.ToString()));
        rows.Add(new(tag, "", "iterations", b.Iterations));
        rows.Add(new(tag, "", "objective", b.ObjectivePerHour));
        rows.Add(new(tag, "", "total_generation_mw", b.TotalGenerationMw));
        rows.Add(new(tag, "", "total_load_mw", b.TotalLoadMw));
        rows.Add(new(tag, "", "losses_mw", b.LossesMw));
        if (!b.Converged) return;
        foreach (AcOpfBusResult bus in b.Buses)
        {
            string e = $"bus {bus.BusId.ToString(CultureInfo.InvariantCulture)}";
            rows.Add(new(tag, e, "vm_pu", bus.VmPu));
            rows.Add(new(tag, e, "va_deg", bus.VaDeg));
        }
        foreach (AcOpfGenResult g in b.Generators)
        {
            string e = $"unit {g.Index.ToString(CultureInfo.InvariantCulture)}";
            rows.Add(new(tag, e, "pg_mw", g.PgMw));
            rows.Add(new(tag, e, "qg_mvar", g.QgMvar));
        }
        foreach (OpfControlValue cv in r.Controls)
        {
            string e = $"{cv.Kind} {cv.Element}";
            rows.Add(new(tag, e, "value", cv.Value));
            rows.Add(new(tag, e, "case_value", cv.CaseValue));
        }
        foreach (AcOpfBinding bd in b.Binding) rows.Add(new(tag, $"{bd.Kind} {bd.Element}", "shadow_price", bd.ShadowPrice));
    }

    private static string Cell(object v) => v switch
    {
        double d => Num(d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => Quoted(GridSim.Core.IO.CsvFormulaGuard.Neutralise(s)),
        _ => throw new ArgumentException($"unsupported cell type {v.GetType().Name}"),
    };

    // A string cell after the formula-injection guard (CWE-1236): quoted (with doubled quotes) when it holds a comma, a
    // quote or a line break.
    private static string Quoted(string s) =>
        s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal) ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    /// <summary>The rows as CSV (LF line ends).</summary>
    public static string Csv(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder("record,element,field,value\n");
        foreach (Row r in rows) sb.Append(Cell(r.Record)).Append(',').Append(Cell(r.Element)).Append(',').Append(Cell(r.Field)).Append(',').Append(Cell(r.Value)).Append('\n');
        return sb.ToString();
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string JsonValue(object v) => v switch
    {
        double d => double.IsFinite(d) ? Num(d) : JsonString(Num(d)),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => JsonString(s),
        _ => throw new ArgumentException($"unsupported cell type {v.GetType().Name}"),
    };

    /// <summary>The rows as JSON (a non-finite number is written as its string, "NaN" or "Infinity").</summary>
    public static string Json(string verb, IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": ").Append(JsonString(Format)).Append(",\n  \"verb\": ").Append(JsonString(verb)).Append(",\n  \"rows\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            sb.Append(i == 0 ? "\n    [" : ",\n    [").Append(JsonString(r.Record)).Append(", ").Append(JsonString(r.Element)).Append(", ")
              .Append(JsonString(r.Field)).Append(", ").Append(JsonValue(r.Value)).Append(']');
        }
        sb.Append(rows.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
        return sb.ToString();
    }
}
