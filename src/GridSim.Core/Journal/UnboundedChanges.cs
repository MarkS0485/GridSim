using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.Journal;

/// <summary>
/// Breaking changes with no bound, in <c>breaking-change validate</c>. Such a change (B3: a PSS/E DYR CSVGN1 record read
/// with <c>--dyr-extended --allow-unmodelled</c> now follows its control; B27: the same for EXDC2, IEEEX1, ESAC8B, ESST3A
/// and IEEEG3 records) claims only a change of model on every field (one qualitative claim on <c>*</c>), so no number can
/// check what it moves. It is raised only for a job its condition really covers (<see cref="Applies"/>), and then every
/// field of that job that no bounded change explains is UNBOUNDED, reported once for the job with each field's size. A
/// person reviews it and acknowledges it with <c>--accept Bn</c> (<see cref="Review"/>); later runs show the job as REVIEWED while it moves no further.
/// </summary>
public static class UnboundedChanges
{
    /// <summary>A DYR input of a job: its file name and its recorded bytes (null with the reason when they could not be
    /// read).</summary>
    public sealed record DyrInput(string Name, byte[]? Bytes, string? Problem);

    /// <summary>Reads the bytes a re-run will read at a path; null with the reason when they cannot be read.</summary>
    public delegate byte[]? ByteReader(string path, out string? problem);

    /// <summary>
    /// A replay's DYR input, read where the re-run will read it (<paramref name="replayPath"/>) and added to
    /// <paramref name="into"/>. A <c>--dyn-data</c> file that holds JSON is not a DYR and is not added
    /// (<paramref name="skipJson"/>). Returns null when it was read, else the CANNOT-REPLAY reason
    /// <c>input unreadable: &lt;recorded path&gt; (&lt;why&gt;)</c>, so the condition of an unbounded change is always decided
    /// on the DYR itself, never assumed.
    /// </summary>
    public static string? AddDyr(string recordedAbs, string replayPath, bool skipJson, ByteReader read, List<DyrInput> into)
    {
        ArgumentNullException.ThrowIfNull(recordedAbs);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(into);
        byte[]? bytes = read(replayPath, out string? problem);
        if (bytes is null) return $"input unreadable: {recordedAbs} ({problem})";
        if (skipJson && Encoding.UTF8.GetString(bytes).TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('{')) return null;
        into.Add(new DyrInput(Path.GetFileName(recordedAbs), bytes, null));
        return null;
    }

    /// <summary>Whether an unbounded change is raised for a job, and a note to print with it (null for none).</summary>
    public sealed record Applicability(bool Applies, string? Note);

    /// <summary>True for a breaking change whose claims are all a qualitative change of model on every field
    /// (<c>"field": "*"</c>): it has no bound.</summary>
    public static bool IsUnbounded(BreakingChange b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return b.Claims.Count > 0 && b.Claims.All(c => c.Kind == "qualitative" && c.Field == "*");
    }

    // The condition of each unbounded change, beyond the map's scope: the options the job must carry (all of them) and
    // the DYR models its DYR input must hold (any one of them). B3: map entry "a DYR CSVGN1 record read with both
    // --dyr-extended and --allow-unmodelled". B27: the same for EXDC2, IEEEX1, ESAC8B, ESST3A and IEEEG3.
    private static readonly Dictionary<string, (string[] Options, string[] DyrModels)> Conditions = new(StringComparer.Ordinal)
    {
        ["B3"] = (["--dyr-extended", "--allow-unmodelled"], ["CSVGN1"]),
        ["B27"] = (["--dyr-extended", "--allow-unmodelled"], ["EXDC2", "IEEEX1", "ESAC8B", "ESST3A", "IEEEG3"]),
    };

    /// <summary>
    /// Whether the unbounded change <paramref name="b"/> is raised for a job with this command line and these DYR inputs.
    /// For B3: the command line must hold both <c>--dyr-extended</c> and <c>--allow-unmodelled</c>, and a DYR input must
    /// hold a CSVGN1 record (B27: an EXDC2, IEEEX1, ESAC8B, ESST3A or IEEEG3 record). Nothing is assumed (2.1.127): a DYR
    /// input whose bytes are missing raises nothing, and validate refuses to re-run a job whose recorded DYR it cannot read
    /// (CANNOT-REPLAY), so for every re-run job the DYR is inspected. A job with no DYR input (a <c>pf</c> run, one whose
    /// <c>--dyn-data</c> is a JSON file, or a <c>dynamics-study</c> run, which refuses <c>--allow-unmodelled</c> and reads
    /// its DYR without the extended library) is not raised.
    /// <para>For B33 (<see cref="DcModelChange"/>): the job must run the DC model (<see cref="RunsDcModel"/>) and its case
    /// must have a term MATPOWER's makeBdc adds (<paramref name="caseUsesMakeBdcTerms"/>, decided by validate on the case
    /// the re-run loads, with <c>DcPowerFlow.UsesMakeBdcTerms</c>). A case with none is computed by the earlier
    /// instructions bit for bit, so any change there is not B33's. Nothing is assumed: a case that was not read
    /// (<c>null</c>) raises nothing, and validate makes a job whose case it cannot read CANNOT-REPLAY before it is re-run.</para>
    /// </summary>
    public static Applicability Applies(BreakingChange b, string verb, IReadOnlyList<string> argv, IReadOnlyList<DyrInput> dyrInputs,
        bool? caseUsesMakeBdcTerms = null)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(dyrInputs);
        if (b.Id == DcModelChange) return new Applicability(caseUsesMakeBdcTerms == true && RunsDcModel(verb, argv), null);
        if (!Conditions.TryGetValue(b.Id, out var cond)) return new Applicability(true, null);
        foreach (string o in cond.Options)
            if (!argv.Any(a => a == o || a.StartsWith(o + "=", StringComparison.Ordinal))) return new Applicability(false, null);
        // 2.1.127: only a DYR whose recorded bytes hold the record raises the change. A DYR input that could not be read
        // raises nothing: validate makes such a job CANNOT-REPLAY before it is re-run, so no CLI job reaches here with one.
        if (dyrInputs.Any(d => d.Bytes is { } bytes && cond.DyrModels.Any(model => HoldsModel(bytes, model)))) return new Applicability(true, null);
        return new Applicability(false, null);
    }

    /// <summary>B33 (2.1.144): the DC power flow and every DC study use MATPOWER's DC model (makeBdc).</summary>
    public const string DcModelChange = "B33";

    /// <summary>
    /// True when a job of B33's scope runs the DC model, so B33 can move it. The AC modes are left out: every
    /// <c>nminus1</c> run is an AC power flow (the DC corrective redispatch belongs to the <c>n-1-1</c> verb), and
    /// <c>sensitivity</c> is DC only for <c>--kind otdf</c> and <c>--kind psdf</c> (<c>tap</c>, <c>dvdq</c> and
    /// <c>effectiveness</c> are AC sensitivities). Every other verb of the scope runs the DC model.
    /// </summary>
    public static bool RunsDcModel(string verb, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(argv);
        if (verb == "nminus1") return false;
        if (verb != "sensitivity") return true;
        string? kind = null;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "--kind") { kind = i + 1 < argv.Count ? argv[i + 1] : null; break; }
            if (argv[i].StartsWith("--kind=", StringComparison.Ordinal)) { kind = argv[i]["--kind=".Length..]; break; }
        }
        return string.Equals(kind, "otdf", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "psdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the DYR text holds a record of <paramref name="model"/>: the name as a whole word, in or out of
    /// quotes, before the record's '/' terminator on its line (text after '/' is a comment).</summary>
    public static bool HoldsModel(byte[] dyr, string model)
    {
        ArgumentNullException.ThrowIfNull(dyr);
        ArgumentNullException.ThrowIfNull(model);
        var rx = new Regex("(?<![A-Za-z0-9_])" + Regex.Escape(model) + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (string line in Encoding.Latin1.GetString(dyr).Split('\n'))
        {
            int slash = line.IndexOf('/', StringComparison.Ordinal);
            if (rx.IsMatch(slash >= 0 ? line[..slash] : line)) return true;
        }
        return false;
    }

    /// <summary>
    /// The claim check of a CHANGED outcome with the unbounded changes set apart. <paramref name="candidates"/> are the IDs
    /// <see cref="BreakingChangeMap.Explain"/> gives. The bounded ones judge the fields as usual
    /// (<see cref="BreakingChangeMap.Judge"/>). An unbounded one is raised only on an exact (not broad-scope) match whose
    /// condition holds for this job (<see cref="Applies"/>), and then covers every field the bounded ones do not explain
    /// (<see cref="Mark"/>). An unbounded change whose condition does not hold explains nothing.
    /// <paramref name="caseUsesMakeBdcTerms"/> is B33's condition on the job's case (see <see cref="Applies"/>).
    /// </summary>
    public static void Judge(BreakingChangeMap map, ReplayOutcome o, IReadOnlyList<string> candidates, string verb, IReadOnlyList<string> argv, IReadOnlyList<DyrInput> dyrInputs,
        bool? caseUsesMakeBdcTerms = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(candidates);
        var bounded = new List<string>();
        var raised = new List<string>();
        string? note = null;
        foreach (string c in candidates)
        {
            BreakingChange b = map.Entries.First(x => x.Id == c.Split(' ')[0]);
            if (!IsUnbounded(b)) { bounded.Add(c); continue; }
            if (c.Contains(' ', StringComparison.Ordinal)) continue;
            Applicability app = Applies(b, verb, argv, dyrInputs, caseUsesMakeBdcTerms);
            if (!app.Applies) continue;
            raised.Add(b.Id);
            note ??= app.Note;
        }
        map.Judge(o, bounded);
        Mark(o, raised, note);
    }

    /// <summary>
    /// Mark a CHANGED outcome, already judged against its bounded changes, as moved under the unbounded changes
    /// <paramref name="ids"/>: every field not EXPLAINED becomes UNBOUNDED (the change has no bound, so it covers any
    /// movement), and the outcome is UNBOUNDED when any field is. <paramref name="note"/> is added to the reason.
    /// </summary>
    public static void Mark(ReplayOutcome o, IReadOnlyList<string> ids, string? note)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        string joined = string.Join(";", ids);
        bool any = false;
        foreach (FieldDifference f in o.Fields)
        {
            if (f.Verdict == ClaimVerdict.Explained) continue;
            f.Verdict = ClaimVerdict.Unbounded;
            f.ExplainedBy = joined;
            f.Claimed = string.Join("; ", ids.Select(id => id + " no bound (a change of model)"));
            any = true;
        }
        if (any)
        {
            o.Verdict = ClaimVerdict.Unbounded;
            o.ExplainedBy = o.ExplainedBy.Concat(ids).Distinct(StringComparer.Ordinal)
                .OrderBy(x => int.Parse(x[1..], CultureInfo.InvariantCulture)).ToList();
        }
        if (note is not null) o.Reason = o.Reason is null ? note : o.Reason + "; " + note;
    }

    /// <summary>One review line for the report: a job, the unbounded change, and how it stands.</summary>
    public sealed record ReviewRow(long Seq, string Id, string Outcome, string Detail);

    /// <summary>
    /// Apply the acknowledgements. Each UNBOUNDED outcome whose every unbounded change is in <paramref name="acceptNow"/> is
    /// REVIEWED, and its fields and sizes are added to <paramref name="file"/> (per field the larger of what was there and
    /// what moved now). Otherwise it is REVIEWED when <paramref name="file"/> holds, for the same job (sequence number and
    /// recorded time) and each of its unbounded changes, every field that moved, each by at least as much as it moved now
    /// (a number by its largest absolute and relative difference, any other kind by its count and transition); else it
    /// stays UNBOUNDED and the detail says why. Returns one row per UNBOUNDED or REVIEWED outcome and change, in order.
    /// </summary>
    public static IReadOnlyList<ReviewRow> Review(IReadOnlyList<ReplayOutcome> outcomes, AcceptFile file, IReadOnlyCollection<string> acceptNow, string buildVersion)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(acceptNow);
        var rows = new List<ReviewRow>();
        foreach (ReplayOutcome o in outcomes)
        {
            if (o.Status != ReplayStatus.Changed || o.Verdict != ClaimVerdict.Unbounded) continue;
            List<FieldDifference> moved = o.Fields.Where(f => f.Verdict == ClaimVerdict.Unbounded).ToList();
            string[] ids = moved.SelectMany(f => f.ExplainedBy.Split(';')).Distinct(StringComparer.Ordinal)
                .OrderBy(x => int.Parse(x[1..], CultureInfo.InvariantCulture)).ToArray();
            if (ids.All(id => acceptNow.Contains(id)))
            {
                foreach (string id in ids)
                {
                    file.Add(new AcceptRecord(id, o.Seq, o.RecordedUtc, o.Verb, o.RecordedVersion, buildVersion,
                        moved.Where(f => f.ExplainedBy.Split(';').Contains(id))
                            .Select(f => new AcceptedField(f.File, f.Field, f.Kind, f.MaxAbsDiff, f.MaxRelDiff, f.Count, f.Transition)).ToList()));
                    rows.Add(new ReviewRow(o.Seq, id, "accepted now", string.Create(CultureInfo.InvariantCulture, $"{moved.Count} field(s) recorded in the accept file")));
                }
                o.Verdict = ClaimVerdict.Reviewed;
                foreach (FieldDifference f in moved) f.Verdict = ClaimVerdict.Reviewed;
                continue;
            }
            string? why = null;
            foreach (string id in ids)
            {
                AcceptRecord? rec = file.Find(id, o.Seq, o.RecordedUtc);
                if (rec is null) { why ??= $"{id} not yet accepted for this job"; continue; }
                foreach (FieldDifference f in moved.Where(f => f.ExplainedBy.Split(';').Contains(id)))
                {
                    AcceptedField? a = rec.Fields.FirstOrDefault(x => x.File == f.File && x.Field == f.Field);
                    if (a is null) { why ??= $"{f.File} {f.Field} moved, and was not accepted before"; continue; }
                    if (!Within(f, a)) why ??= $"{f.File} {f.Field} moved further than accepted ({f.Observed}; accepted {a.Describe()})";
                }
            }
            if (why is null)
            {
                o.Verdict = ClaimVerdict.Reviewed;
                foreach (FieldDifference f in moved) f.Verdict = ClaimVerdict.Reviewed;
                foreach (string id in ids) rows.Add(new ReviewRow(o.Seq, id, "accepted earlier", "every moved field within its accepted size"));
            }
            else foreach (string id in ids) rows.Add(new ReviewRow(o.Seq, id, "needs review", why));
        }
        return rows;
    }

    // SEC-30: a size that is NaN (a field that moved to or from NaN) is within an accepted NaN; "<=" alone is false for it.
    private static bool Le(double x, double limit) => x <= limit || (double.IsNaN(x) && double.IsNaN(limit));

    private static bool Within(FieldDifference f, AcceptedField a)
    {
        if (f.Kind != a.Kind) return false;
        if (f.Kind == "number")
            return f.MaxAbsDiff is double fa && a.MaxAbs is double aa && Le(fa, aa)
                && f.MaxRelDiff is double fr && a.MaxRel is double ar && Le(fr, ar);
        return f.Count <= a.Count && f.Transition == a.Transition;
    }
}

/// <summary>One field an acknowledgement covers, with the size it was accepted at.</summary>
public sealed record AcceptedField(string File, string Field, string Kind, double? MaxAbs, double? MaxRel, long Count, string? Transition)
{
    /// <summary>The accepted size as text.</summary>
    public string Describe() => Kind == "number"
        ? "abs " + ReplayReport.Number(MaxAbs) + " rel " + ReplayReport.Number(MaxRel)
        : Transition ?? Kind + " x" + Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One acknowledgement: an unbounded change on one job (sequence number and recorded time), the build it was
/// accepted on, and the fields with their accepted sizes.</summary>
public sealed record AcceptRecord(string Id, long Seq, string RecordedUtc, string Verb, string RecordedVersion, string AcceptedOn,
    IReadOnlyList<AcceptedField> Fields);

/// <summary>
/// The accept file (<c>gridsim-breaking-change-accept/1</c>) beside a journal: <c>&lt;journal name&gt;.accept.json</c>.
/// Written by <c>breaking-change validate --accept Bn</c>, read by every validate of that journal. Records are kept in
/// (sequence, ID) order and fields in (file, field) order, so the bytes depend only on what was accepted.
/// </summary>
public sealed class AcceptFile
{
    /// <summary>The schema tag.</summary>
    public const string Schema = "gridsim-breaking-change-accept/1";

    private readonly List<AcceptRecord> _records = new();

    /// <summary>The records, in (sequence, ID) order.</summary>
    public IReadOnlyList<AcceptRecord> Records => _records;

    /// <summary>The accept file of <paramref name="journal"/>: its name without extension plus <c>.accept.json</c>, in the
    /// same folder.</summary>
    public static string PathFor(string journal) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(journal))!, System.IO.Path.GetFileNameWithoutExtension(journal) + ".accept.json");

    /// <summary>The record for one job and change, or null.</summary>
    public AcceptRecord? Find(string id, long seq, string recordedUtc) =>
        _records.FirstOrDefault(r => r.Id == id && r.Seq == seq && r.RecordedUtc == recordedUtc);

    /// <summary>Add a record; one already there for the same job and change is merged (per field, the larger size).</summary>
    public void Add(AcceptRecord rec)
    {
        ArgumentNullException.ThrowIfNull(rec);
        AcceptRecord? old = Find(rec.Id, rec.Seq, rec.RecordedUtc);
        var fields = new Dictionary<(string, string), AcceptedField>();
        foreach (AcceptedField f in (old?.Fields ?? Array.Empty<AcceptedField>()).Concat(rec.Fields))
        {
            if (fields.TryGetValue((f.File, f.Field), out AcceptedField? had) && had.Kind == f.Kind)
                fields[(f.File, f.Field)] = had with
                {
                    MaxAbs = Max(had.MaxAbs, f.MaxAbs),
                    MaxRel = Max(had.MaxRel, f.MaxRel),
                    Count = Math.Max(had.Count, f.Count),
                    Transition = had.Transition == f.Transition ? f.Transition : null,
                };
            else fields[(f.File, f.Field)] = f;
        }
        if (old is not null) _records.Remove(old);
        _records.Add(rec with
        {
            Fields = fields.Values.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Field, StringComparer.Ordinal).ToList(),
        });
        _records.Sort((a, b) => a.Seq != b.Seq ? a.Seq.CompareTo(b.Seq) : string.CompareOrdinal(a.Id, b.Id));
    }

    private static double? Max(double? a, double? b) => a is double x && b is double y ? Math.Max(x, y) : a ?? b;

    // SEC-30: JSON has no NaN or infinity, and WriteNumber throws on them; such a size is written as the text the result
    // files use ("NaN", "Infinity", "-Infinity") and read back from it.
    private static void WriteSize(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, v);
        else w.WriteString(name, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static double ReadSize(JsonElement e)
        => e.ValueKind == JsonValueKind.String
            ? double.Parse(e.GetString()!, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : e.GetDouble();

    /// <summary>The file as JSON text (UTF-8, LF, invariant culture).</summary>
    public string Json()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteStartArray("accepted");
            foreach (AcceptRecord r in _records)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteNumber("seq", r.Seq);
                w.WriteString("recordedUtc", r.RecordedUtc);
                w.WriteString("verb", r.Verb);
                w.WriteString("recordedVersion", r.RecordedVersion);
                w.WriteString("acceptedOn", r.AcceptedOn);
                w.WriteStartArray("fields");
                foreach (AcceptedField f in r.Fields)
                {
                    w.WriteStartObject();
                    w.WriteString("file", f.File);
                    w.WriteString("field", f.Field);
                    w.WriteString("kind", f.Kind);
                    if (f.MaxAbs is double a) WriteSize(w, "maxAbs", a);
                    if (f.MaxRel is double rl) WriteSize(w, "maxRel", rl);
                    w.WriteNumber("count", f.Count);
                    if (f.Transition is not null) w.WriteString("transition", f.Transition);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Read an accept file's text. Refuses (InvalidDataException) a document that is not one.</summary>
    public static AcceptFile Parse(string text)
    {
        var f = new AcceptFile();
        try
        {
            using JsonDocument d = JsonDocument.Parse(text);
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out JsonElement s) || s.GetString() != Schema)
                throw new InvalidDataException($"not a {Schema} document");
            foreach (JsonElement e in r.GetProperty("accepted").EnumerateArray())
            {
                var fields = new List<AcceptedField>();
                foreach (JsonElement x in e.GetProperty("fields").EnumerateArray())
                    fields.Add(new AcceptedField(x.GetProperty("file").GetString()!, x.GetProperty("field").GetString()!, x.GetProperty("kind").GetString()!,
                        x.TryGetProperty("maxAbs", out JsonElement a) ? ReadSize(a) : null,
                        x.TryGetProperty("maxRel", out JsonElement rl) ? ReadSize(rl) : null,
                        x.GetProperty("count").GetInt64(),
                        x.TryGetProperty("transition", out JsonElement t) ? t.GetString() : null));
                f.Add(new AcceptRecord(e.GetProperty("id").GetString()!, e.GetProperty("seq").GetInt64(), e.GetProperty("recordedUtc").GetString()!,
                    e.GetProperty("verb").GetString()!, e.GetProperty("recordedVersion").GetString()!, e.GetProperty("acceptedOn").GetString()!, fields));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        return f;
    }
}
