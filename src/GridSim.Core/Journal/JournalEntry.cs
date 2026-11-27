using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GridSim.Core.Journal;

// ---------------------------------------------------------------------------------------------------------------------
// The run journal (gridsim-journal/1, technical chapter 42): one JSON line per command-line run, written when the run
// ends, so that a later build can re-run the same jobs and say bit for bit what changed. The journal is a convenience
// log, not evidence: it has no hash chain (the audit log, technical chapter 31, is the evidence trail).
//
// This file holds the entry and its one serialised form. Keys are written in a fixed order, numbers and times in the
// invariant culture, so the same entry always gives the same line.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>A command-line token the journal records: an input file, a case folder, an output path or a bundled case.</summary>
public sealed class JournalToken
{
    /// <summary>The token's index in <see cref="JournalEntry.Argv"/>.</summary>
    public int Index { get; set; }

    /// <summary>For a comma list (bench --cases a,b), the piece's position in the list; null for a whole token.</summary>
    public int? Part { get; set; }

    /// <summary>input, case-folder, output or builtin.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The token (or the comma-list piece, or the value of a --flag=value token) as written.</summary>
    public string Raw { get; set; } = "";

    /// <summary>The absolute path, resolved against the run's working folder.</summary>
    public string Abs { get; set; } = "";

    /// <summary>The path relative to the journal's folder.</summary>
    public string Rel { get; set; } = "";

    /// <summary>SHA-256 of an input file's bytes (null for other kinds). For a bundled case, the SHA-256 of the build's
    /// bundled-data manifest (provenance only).</summary>
    public string? Sha256 { get; set; }

    /// <summary>The input file's length in bytes.</summary>
    public long? Bytes { get; set; }

    /// <summary>True when the input's bytes were copied into the snapshot store.</summary>
    public bool? Snapshot { get; set; }

    /// <summary>Why a snapshot was not taken: "size" (a file over the size limit), "damaged" (a damaged object of that hash
    /// could not be removed from the store), "busy" (another program held the file through every retry) or "write-failed"
    /// (the object could not be written for another reason).</summary>
    public string? SnapshotSkipped { get; set; }
}

/// <summary>A file a case folder or a v2 manifest consumes (the run reads it through the token).</summary>
public sealed class JournalCaseFile
{
    /// <summary>The token index (<see cref="JournalToken.Index"/>) of the folder or manifest.</summary>
    public int Token { get; set; }

    /// <summary>The label the case identity gives the file (buses.json, network/lines.json, ...).</summary>
    public string Label { get; set; } = "";

    /// <summary>The file's path relative to the folder (or the manifest's folder): where a snapshot copy is placed.</summary>
    public string Path { get; set; } = "";

    /// <summary>The absolute path of the file when the run read it.</summary>
    public string Abs { get; set; } = "";

    /// <summary>SHA-256 of the file's bytes.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>True when the file's bytes are in the snapshot store.</summary>
    public bool? Snapshot { get; set; }
}

/// <summary>An output the run wrote, hashed when the run ended.</summary>
public sealed class JournalOutput
{
    /// <summary>The absolute path.</summary>
    public string Abs { get; set; } = "";

    /// <summary>The path relative to the journal's folder.</summary>
    public string Rel { get; set; } = "";

    /// <summary>The path relative to the run's working folder when the output lies inside it (a default output under
    /// <c>results/</c>), else null.</summary>
    public string? CwdRel { get; set; }

    /// <summary>SHA-256 of the file's bytes when the run ended.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The file's length in bytes.</summary>
    public long Bytes { get; set; }
}

/// <summary>One run: what was asked for, what it read, what it wrote and how it ended.</summary>
public sealed class JournalEntry
{
    /// <summary>The entry schema tag.</summary>
    public const string SchemaTag = "gridsim-journal/1";

    public string Schema { get; set; } = SchemaTag;

    /// <summary>The entry's sequence number in its journal, from 1, continuous across segments.</summary>
    public long Seq { get; set; }

    /// <summary>When the run ended (provenance only: nothing is decided by it).</summary>
    public string RecordedUtc { get; set; } = "";

    /// <summary>The release number of the build that ran it.</summary>
    public string Version { get; set; } = "";

    /// <summary>The release number and commit, as <c>gridsim --version</c> prints them.</summary>
    public string InformationalVersion { get; set; } = "";

    /// <summary>The commit the build was made from ("unknown" when the build recorded none).</summary>
    public string Commit { get; set; } = "";

    /// <summary>The build status of the build that ran it (<c>RELEASE</c> or <c>UNSUPPORTED BUILD</c>, see
    /// <see cref="GridSim.Core.Diagnostics.SupplyChain.BuildStatus"/>); null when not known (an entry imported from a
    /// result file, or written by an earlier build), and then not written.</summary>
    public string? BuildStatus { get; set; }

    /// <summary>The canonical verb, or "run" for the no-verb case run.</summary>
    public string Verb { get; set; } = "";

    /// <summary>The command line, the journal's own switches taken out.</summary>
    public List<string> Argv { get; set; } = new();

    /// <summary>The run's working folder.</summary>
    public string Cwd { get; set; } = "";

    public List<JournalToken> Tokens { get; set; } = new();

    public List<JournalCaseFile> CaseFiles { get; set; } = new();

    public List<JournalOutput> Outputs { get; set; } = new();

    public int ExitCode { get; set; }

    /// <summary>The solver backend the run used ("cpu", or the GPU backend's description).</summary>
    public string Backend { get; set; } = "cpu";

    /// <summary>True when a GPU backend was asked for (GPU arithmetic is outside the bit-identity contract).</summary>
    public bool Gpu { get; set; }

    /// <summary>Wall time of the run in milliseconds (provenance only; the validate scheduler uses it for start order).</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Peak working set in bytes (provenance only; the validate scheduler uses it for memory admission).</summary>
    public long PeakWorkingSetBytes { get; set; }

    /// <summary>The replay class the recording build gave the run (safe or never), with its reason for never.</summary>
    public string ReplayClass { get; set; } = "never";

    public string? ReplayReason { get; set; }

    /// <summary>Set on an entry built by <c>breaking-change import</c> from a result file: the inputs were hashed at
    /// import, not when the run happened.</summary>
    public string? Imported { get; set; }
}

/// <summary>The one serialised form of a <see cref="JournalEntry"/>: a single JSON line.</summary>
public static class JournalJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The entry as one line of JSON (no line break), keys in the fixed order of the schema.</summary>
    public static string Line(JournalEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options))
        {
            w.WriteStartObject();
            w.WriteString("schema", e.Schema);
            w.WriteNumber("seq", e.Seq);
            w.WriteString("recordedUtc", e.RecordedUtc);
            w.WriteString("version", e.Version);
            w.WriteString("informationalVersion", e.InformationalVersion);
            w.WriteString("commit", e.Commit);
            if (e.BuildStatus is not null) w.WriteString("buildStatus", e.BuildStatus);
            w.WriteString("verb", e.Verb);
            w.WriteStartArray("argv");
            foreach (string a in e.Argv) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteString("cwd", e.Cwd);
            w.WriteStartArray("tokens");
            foreach (JournalToken t in e.Tokens)
            {
                w.WriteStartObject();
                w.WriteNumber("i", t.Index);
                if (t.Part is int p) w.WriteNumber("part", p);
                w.WriteString("kind", t.Kind);
                w.WriteString("raw", t.Raw);
                w.WriteString("abs", t.Abs);
                w.WriteString("rel", t.Rel);
                if (t.Sha256 is not null) w.WriteString("sha256", t.Sha256);
                if (t.Bytes is long b) w.WriteNumber("bytes", b);
                if (t.Snapshot is bool s) w.WriteBoolean("snapshot", s);
                if (t.SnapshotSkipped is not null) w.WriteString("snapshotSkipped", t.SnapshotSkipped);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("caseFiles");
            foreach (JournalCaseFile c in e.CaseFiles)
            {
                w.WriteStartObject();
                w.WriteNumber("token", c.Token);
                w.WriteString("label", c.Label);
                w.WriteString("path", c.Path);
                w.WriteString("abs", c.Abs);
                w.WriteString("sha256", c.Sha256);
                if (c.Snapshot is bool s) w.WriteBoolean("snapshot", s);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("outputs");
            foreach (JournalOutput o in e.Outputs)
            {
                w.WriteStartObject();
                w.WriteString("abs", o.Abs);
                w.WriteString("rel", o.Rel);
                if (o.CwdRel is not null) w.WriteString("cwdRel", o.CwdRel);
                w.WriteString("sha256", o.Sha256);
                w.WriteNumber("bytes", o.Bytes);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("exitCode", e.ExitCode);
            w.WriteString("backend", e.Backend);
            w.WriteBoolean("gpu", e.Gpu);
            w.WriteNumber("elapsedMs", e.ElapsedMs);
            w.WriteNumber("peakWorkingSetBytes", e.PeakWorkingSetBytes);
            w.WriteStartObject("replay");
            w.WriteString("class", e.ReplayClass);
            if (e.ReplayReason is not null) w.WriteString("reason", e.ReplayReason);
            w.WriteEndObject();
            if (e.Imported is not null) w.WriteString("imported", e.Imported);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Parse one journal line. Returns null with <paramref name="error"/> set when the line is not a readable
    /// <c>gridsim-journal/1</c> entry; an entry of another schema is returned with <paramref name="error"/>
    /// "entry schema unknown" so the caller can report it by its sequence number.
    /// </summary>
    public static JournalEntry? Parse(string line, out string? error)
    {
        error = null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(line);
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "damaged entry"; return null; }
            var e = new JournalEntry
            {
                Schema = Str(r, "schema") ?? "",
                Seq = r.TryGetProperty("seq", out JsonElement seq) && seq.TryGetInt64(out long s) ? s : 0,
            };
            if (e.Schema != JournalEntry.SchemaTag)
            {
                error = "entry schema unknown";
                return e.Seq > 0 ? e : null;
            }
            e.RecordedUtc = Str(r, "recordedUtc") ?? "";
            e.Version = Str(r, "version") ?? "";
            e.InformationalVersion = Str(r, "informationalVersion") ?? "";
            e.Commit = Str(r, "commit") ?? "";
            e.BuildStatus = Str(r, "buildStatus");
            e.Verb = Str(r, "verb") ?? "";
            e.Argv = r.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? "").ToList();
            e.Cwd = Str(r, "cwd") ?? "";
            foreach (JsonElement t in r.GetProperty("tokens").EnumerateArray())
                e.Tokens.Add(new JournalToken
                {
                    Index = t.GetProperty("i").GetInt32(),
                    Part = t.TryGetProperty("part", out JsonElement p) ? p.GetInt32() : null,
                    Kind = Str(t, "kind") ?? "",
                    Raw = Str(t, "raw") ?? "",
                    Abs = Str(t, "abs") ?? "",
                    Rel = Str(t, "rel") ?? "",
                    Sha256 = Str(t, "sha256"),
                    Bytes = t.TryGetProperty("bytes", out JsonElement b) ? b.GetInt64() : null,
                    Snapshot = t.TryGetProperty("snapshot", out JsonElement sn) ? sn.GetBoolean() : null,
                    SnapshotSkipped = Str(t, "snapshotSkipped"),
                });
            foreach (JsonElement c in r.GetProperty("caseFiles").EnumerateArray())
                e.CaseFiles.Add(new JournalCaseFile
                {
                    Token = c.GetProperty("token").GetInt32(),
                    Label = Str(c, "label") ?? "",
                    Path = Str(c, "path") ?? "",
                    Abs = Str(c, "abs") ?? "",
                    Sha256 = Str(c, "sha256") ?? "",
                    Snapshot = c.TryGetProperty("snapshot", out JsonElement sn) ? sn.GetBoolean() : null,
                });
            foreach (JsonElement o in r.GetProperty("outputs").EnumerateArray())
                e.Outputs.Add(new JournalOutput
                {
                    Abs = Str(o, "abs") ?? "",
                    Rel = Str(o, "rel") ?? "",
                    CwdRel = Str(o, "cwdRel"),
                    Sha256 = Str(o, "sha256") ?? "",
                    Bytes = o.GetProperty("bytes").GetInt64(),
                });
            e.ExitCode = r.GetProperty("exitCode").GetInt32();
            e.Backend = Str(r, "backend") ?? "cpu";
            e.Gpu = r.TryGetProperty("gpu", out JsonElement g) && g.ValueKind == JsonValueKind.True;
            e.ElapsedMs = r.TryGetProperty("elapsedMs", out JsonElement el) && el.TryGetInt64(out long ms) ? ms : 0;
            e.PeakWorkingSetBytes = r.TryGetProperty("peakWorkingSetBytes", out JsonElement pk) && pk.TryGetInt64(out long pw) ? pw : 0;
            if (r.TryGetProperty("replay", out JsonElement rp))
            {
                e.ReplayClass = Str(rp, "class") ?? "never";
                e.ReplayReason = Str(rp, "reason");
            }
            e.Imported = Str(r, "imported");
            if (e.Seq <= 0) { error = "damaged entry"; return null; }
            return e;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = "damaged entry";
            return null;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A UTC time in the round-trip form the entry uses.</summary>
    public static string Utc(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
