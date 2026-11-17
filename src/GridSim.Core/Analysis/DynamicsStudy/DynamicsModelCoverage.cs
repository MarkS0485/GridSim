using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>The disposition of one DYR record in the model coverage report.</summary>
public static class DyrRecordStatus
{
    /// <summary>Mapped onto a GridSim model with every value carried (exactly, or by an exact derivation it lists).</summary>
    public const string Supported = "SUPPORTED";
    /// <summary>Mapped, but a value GridSim's model has no place for was left out (unmodelled dynamics allowed).</summary>
    public const string Approximated = "APPROXIMATED";
    /// <summary>Not used: an unmapped model, a value the controller cannot honour, or a controller with no machine.</summary>
    public const string Skipped = "SKIPPED";
    /// <summary>Names a machine the RAW case does not hold. Always refused (the reader's rule): no simulation.</summary>
    public const string Unmatched = "UNMATCHED";
    /// <summary>For a machine the RAW case holds out of service: not integrated, not a gap.</summary>
    public const string OutOfService = "OUT OF SERVICE";
    /// <summary>A model the reader maps but this study (v1, synchronous machine chains) does not carry.</summary>
    public const string OutOfScope = "OUT OF SCOPE";
}

/// <summary>One DYR record in the coverage report.</summary>
/// <param name="Line">The 1-based line the record starts on.</param>
/// <param name="Bus">IBUS.</param>
/// <param name="Model">The model name as read (upper case).</param>
/// <param name="Id">The machine ID.</param>
/// <param name="Role">machine, exciter, pss, governor, a renewable role, or unknown.</param>
/// <param name="GeneratorIndex">Index into <c>model.Generators</c> of the in-service unit it names, or null.</param>
/// <param name="Status">One of <see cref="DyrRecordStatus"/>.</param>
/// <param name="Semantics">How the values map ("exact", "exact with derivations", "values left out", "not used").</param>
/// <param name="Derived">Each derived GridSim parameter and its derivation, as the reader tagged it.</param>
/// <param name="Reasons">Why a record is not SUPPORTED (the reader's own words where it gave them).</param>
/// <param name="AffectedTraces">The machine trace channels this record's unit drives (its rotor angle, speed and
/// terminal-bus voltage); a coupled network makes every trace indirectly dependent on every unit.</param>
public sealed record DyrRecordCoverage(int Line, int Bus, string Model, string Id, string Role, int? GeneratorIndex,
    string Status, string Semantics, IReadOnlyList<string> Derived, IReadOnlyList<string> Reasons,
    IReadOnlyList<string> AffectedTraces);

/// <summary>One in-service unit of the RAW case in the coverage report.</summary>
/// <param name="Bus">Bus number.</param>
/// <param name="Id">Machine ID.</param>
/// <param name="GeneratorIndex">Index into <c>model.Generators</c>.</param>
/// <param name="Status">BOUND (a supported machine model and controllers), APPROXIMATED (bound with a left-out value or a
/// provenance tag other than Recorded / DerivedFromRecorded), or UNBOUND (no machine model: the study does not
/// synthesise machine data, so nothing can be simulated).</param>
/// <param name="Machine">The machine record's model and line, or null.</param>
/// <param name="Exciter">The exciter model bound, or null (field held).</param>
/// <param name="Stabiliser">The stabiliser model bound, or null.</param>
/// <param name="Governor">The governor model bound, or null (mechanical power held).</param>
/// <param name="Provenance">The provenance tags behind the unit's numbers.</param>
/// <param name="Notes">What the loader filled in from the case rather than the file.</param>
public sealed record DynamicsUnitCoverage(int Bus, string Id, int GeneratorIndex, string Status, string? Machine,
    string? Exciter, string? Stabiliser, string? Governor, IReadOnlyList<string> Provenance, IReadOnlyList<string> Notes);

/// <summary>
/// RC3 P6: the model coverage report. It precedes any verdict: every DYR record with its native identity and status,
/// every in-service unit with what it is bound to, and whether a like-for-like verdict is possible.
/// </summary>
/// <param name="Status">COMPLETE (every record supported or out of service, every in-service unit bound) or BLOCKED.</param>
/// <param name="LikeForLike">True only when COMPLETE.</param>
/// <param name="Simulable">True when the rows can be simulated: no unmatched record, every unit bound, and either
/// COMPLETE or the LeaveOut policy.</param>
/// <param name="BlockingReasons">Why the report is BLOCKED, in a fixed order.</param>
/// <param name="Records">Every DYR record, in file order.</param>
/// <param name="Units">Every in-service RAW unit, in model order.</param>
/// <param name="ReaderNotes">The DYR reader's own notes on the records it was given.</param>
public sealed record DynamicsModelCoverageReport(string Status, bool LikeForLike, bool Simulable,
    IReadOnlyList<string> BlockingReasons, IReadOnlyList<DyrRecordCoverage> Records,
    IReadOnlyList<DynamicsUnitCoverage> Units, IReadOnlyList<string> ReaderNotes)
{
    /// <summary>Record counts by status, in <see cref="DyrRecordStatus"/> order.</summary>
    public IReadOnlyDictionary<string, int> RecordCounts => new SortedDictionary<string, int>(
        Records.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
}

/// <summary>The coverage report together with the machine data it bound (null when nothing could be bound).</summary>
public sealed record DynamicsModelBinding(DynamicsModelCoverageReport Report, MachineDynamicsSet? Set);

/// <summary>
/// RC3 P6: builds the model coverage report of a DYR file against a RAW case, using the incumbent reader
/// (<see cref="PsseDyrReader"/>) and loader (<see cref="MachineDynamicsLoader"/>) unchanged. The reader's strict and
/// permissive paths stay what they are; this class classifies records before and after calling the permissive path:
/// <list type="number">
/// <item>Every record is read (<see cref="PsseDyrReader.ReadRecords"/>; a malformed file is an input error).</item>
/// <item>Records the reader does not map, whose PSS/E order is not public, of a renewable chain (out of v1 scope), naming
/// a machine the RAW case does not hold, or for an out-of-service unit, are classified here and not passed on.</item>
/// <item>The remaining records are rewritten one per original line (so the reader's line numbers are the file's) and read
/// with unmodelled dynamics allowed. A record whose line appears in a written template is used; with a left-out issue on
/// that line it is APPROXIMATED, otherwise SUPPORTED. A record not written is SKIPPED with the reader's reason.</item>
/// <item>The document is bound by the loader; a unit with no machine template is UNBOUND.</item>
/// </list>
/// When no record is APPROXIMATED or SKIPPED the permissive read gives exactly the document the strict read gives (the
/// option only matters when there are issues; pinned by a test).
/// </summary>
public static partial class DynamicsModelCoverage
{
    private static readonly System.Buffers.SearchValues<char> QuoteNeeded = System.Buffers.SearchValues.Create(" \t,/'");

    private const string LeftOutPrefix = "left out (unmodelled dynamics allowed): ";

    [GeneratedRegex(@"\(line (\d+)\)")]
    private static partial Regex LineTag();

    [GeneratedRegex(@", line (\d+): ([A-Z0-9_]+) record\.$")]
    private static partial Regex TemplateLine();

    /// <summary>Build the coverage report and the bound machine data.</summary>
    public static DynamicsModelBinding Build(PsseParseResult raw, string dyrText, string dyrName, string schemasDir,
        DynamicsUnmodelledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(dyrText);
        ArgumentNullException.ThrowIfNull(dyrName);
        ArgumentNullException.ThrowIfNull(schemasDir);
        GridModel model = raw.Model;
        IReadOnlyList<PsseDyrRecord> records = PsseDyrReader.ReadRecords(dyrText);

        // Native identity of every unit: (bus, id) -> (in service, index into model.Generators or -1).
        var units = new Dictionary<(int, string), (bool InService, int GenIndex)>();
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            Generator g = model.AllGenerators[k];
            int gi = -1;
            for (int j = 0; j < model.Generators.Count; j++) if (ReferenceEquals(model.Generators[j], g)) { gi = j; break; }
            units[(raw.MachineKeys[k].Bus, raw.MachineKeys[k].Id)] = (g.InService, gi);
        }

        var pre = new Dictionary<int, (string Status, string Role, string Reason)>();   // by record index
        var passOn = new List<PsseDyrRecord>();
        for (int i = 0; i < records.Count; i++)
        {
            PsseDyrRecord r = records[i];
            string refusedKey = r.Model == "USRMDL" && r.Values.Count > 0 ? r.Values[0].Trim().Trim('\'').ToUpperInvariant() : r.Model;
            string? wecc = PsseDyrRenewables.ModelOf(r);
            if (PsseDyrRenewables.RefusedOrder.TryGetValue(refusedKey, out string? modelled))
            {
                pre[i] = (DyrRecordStatus.Skipped, "unknown", $"{refusedKey}: GridSim models {modelled}, but the order of the PSS/E record is not in any public source, so it is not read with a guessed order");
                continue;
            }
            if (wecc is not null)
            {
                pre[i] = (DyrRecordStatus.OutOfScope, PsseDyrRenewables.Roles[wecc], $"{wecc} is a renewable converter chain; dynamics-study v1 carries synchronous machine chains only (stability --rms --dyn-data reads it)");
                continue;
            }
            if (!PsseDyrReader.MappedModels.TryGetValue(r.Model, out string? role))
            {
                string name = r.Model == "USRMDL" && r.Values.Count > 0 ? $"USRMDL {r.Values[0].Trim()}" : r.Model;
                pre[i] = (DyrRecordStatus.Skipped, "unknown", $"{name} is a model the DYR reader does not map");
                continue;
            }
            if (!units.TryGetValue((r.Bus, r.Id), out var unit))
            {
                pre[i] = (DyrRecordStatus.Unmatched, role, $"{r.Model} names machine '{r.Id}' at bus {r.Bus}, which the RAW case does not hold");
                continue;
            }
            if (!unit.InService)
            {
                pre[i] = (DyrRecordStatus.OutOfService, role, $"machine '{r.Id}' at bus {r.Bus} is out of service in the RAW case; it is not integrated");
                continue;
            }
            passOn.Add(r);
        }

        // The permissive read of the records passed on, one per original line.
        PsseDyrResult? read = null;
        MachineDynamicsSet? set = null;
        bool anyMachine = passOn.Any(r => PsseDyrReader.MappedModels[r.Model] == "machine");
        var usedLines = new HashSet<int>();
        var derivedByLine = new Dictionary<int, List<string>>();
        var issuesByLine = new Dictionary<int, List<string>>();
        var readerNotes = new List<string>();
        if (anyMachine)
        {
            read = PsseDyrReader.Read(Rewrite(passOn), raw, Path.GetFileNameWithoutExtension(dyrName),
                new PsseDyrOptions { AllowUnmodelled = true, SourceName = dyrName });
            foreach (string note in read.Notes)
            {
                readerNotes.Add(note);
                if (!note.StartsWith(LeftOutPrefix, StringComparison.Ordinal)) continue;
                string issue = note[LeftOutPrefix.Length..].TrimEnd('.');
                foreach (Match mt in LineTag().Matches(issue))
                {
                    int line = int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!issuesByLine.TryGetValue(line, out var list)) issuesByLine[line] = list = new List<string>();
                    if (!list.Contains(issue)) list.Add(issue);
                }
            }
            using (JsonDocument doc = JsonDocument.Parse(read.Json))
            {
                foreach (JsonElement t in doc.RootElement.GetProperty("templates").EnumerateArray())
                {
                    JsonElement prov = t.GetProperty("provenance");
                    string note = prov.GetProperty("_default").GetProperty("note").GetString() ?? "";
                    Match mt = TemplateLine().Match(note);
                    if (!mt.Success) continue;
                    int line = int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture);
                    usedLines.Add(line);
                    foreach (JsonProperty p in prov.EnumerateObject())
                    {
                        if (p.Name == "_default") continue;
                        if (p.Value.GetProperty("tag").GetString() != "DerivedFromRecorded") continue;
                        string text = p.Value.GetProperty("note").GetString() ?? "";
                        int colon = text.IndexOf(": ", StringComparison.Ordinal);
                        if (!derivedByLine.TryGetValue(line, out var list)) derivedByLine[line] = list = new List<string>();
                        list.Add($"{p.Name}: {(colon >= 0 ? text[(colon + 2)..] : text)}");
                    }
                }
            }
            if (read.Machines > 0) set = BindDocument(read.Json, dyrName, schemasDir, model);
        }

        // Every in-service unit.
        var unitRows = new List<DynamicsUnitCoverage>();
        var traceLabels = new Dictionary<(int, string), IReadOnlyList<string>>();
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            Generator g = model.AllGenerators[k];
            if (!g.InService) continue;
            PsseMachineKey key = raw.MachineKeys[k];
            int gi = units[(key.Bus, key.Id)].GenIndex;
            traceLabels[(key.Bus, key.Id)] = TraceLabels(key.Bus, key.Id);
            MachineDynamicsEntry? e = set?.For(gi);
            PsseDyrRecord? mrec = passOn.FirstOrDefault(r => r.Bus == key.Bus && r.Id == key.Id && PsseDyrReader.MappedModels[r.Model] == "machine" && usedLines.Contains(r.Line));
            string? Slot(string role) => passOn.FirstOrDefault(r => r.Bus == key.Bus && r.Id == key.Id
                && PsseDyrReader.MappedModels[r.Model] == role && usedLines.Contains(r.Line))?.Model;
            if (e is null)
            {
                unitRows.Add(new DynamicsUnitCoverage(key.Bus, key.Id, gi, "UNBOUND", null, null, null, null,
                    Array.Empty<string>(), new[] { "no GENROU or GENSAL record is bound to this unit; the study does not synthesise machine data" }));
                continue;
            }
            bool recorded = e.Provenance.All(t => t is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded);
            // A unit is approximated when a number is not Recorded / DerivedFromRecorded, when one of its records left a value
            // out or was not used, or when one of its records names a model the study does not carry.
            bool approximated = !recorded
                || passOn.Any(r => r.Bus == key.Bus && r.Id == key.Id && (issuesByLine.ContainsKey(r.Line) || !usedLines.Contains(r.Line)))
                || pre.Any(kv => records[kv.Key].Bus == key.Bus && records[kv.Key].Id == key.Id
                    && kv.Value.Status is DyrRecordStatus.Skipped or DyrRecordStatus.OutOfScope);
            unitRows.Add(new DynamicsUnitCoverage(key.Bus, key.Id, gi, approximated ? "APPROXIMATED" : "BOUND",
                mrec is null ? null : $"{mrec.Model} (line {mrec.Line.ToString(CultureInfo.InvariantCulture)})",
                e.Exciter?.Model, e.Exciter?.Pss is null ? null : Slot("pss") ?? "PSS1A", e.Data.Governor?.Model,
                e.Provenance.Select(t => t.ToString()).ToList(), e.Notes.ToList()));
        }

        // Every record.
        var rows = new List<DyrRecordCoverage>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            PsseDyrRecord r = records[i];
            int? gi = units.TryGetValue((r.Bus, r.Id), out var u) && u.GenIndex >= 0 ? u.GenIndex : null;
            IReadOnlyList<string> traces = traceLabels.TryGetValue((r.Bus, r.Id), out var tl) ? tl : Array.Empty<string>();
            if (pre.TryGetValue(i, out var p))
            {
                rows.Add(new DyrRecordCoverage(r.Line, r.Bus, r.Model, r.Id, p.Role, gi, p.Status, "not used",
                    Array.Empty<string>(), new[] { p.Reason }, p.Status == DyrRecordStatus.OutOfService ? Array.Empty<string>() : traces));
                continue;
            }
            string role = PsseDyrReader.MappedModels[r.Model];
            IReadOnlyList<string> derived = derivedByLine.TryGetValue(r.Line, out var dl) ? dl : Array.Empty<string>();
            IReadOnlyList<string> issues = issuesByLine.TryGetValue(r.Line, out var il) ? il : Array.Empty<string>();
            if (usedLines.Contains(r.Line))
            {
                bool approx = issues.Count > 0;
                rows.Add(new DyrRecordCoverage(r.Line, r.Bus, r.Model, r.Id, role, gi,
                    approx ? DyrRecordStatus.Approximated : DyrRecordStatus.Supported,
                    approx ? "values left out" : derived.Count > 0 ? "exact with derivations" : "exact",
                    derived, issues, traces));
            }
            else
            {
                IReadOnlyList<string> why = issues.Count > 0 ? issues
                    : !anyMachine ? new[] { "no machine record of the case is mapped, so no controller can be bound" }
                    : new[] { "the reader wrote no template for this record" };
                rows.Add(new DyrRecordCoverage(r.Line, r.Bus, r.Model, r.Id, role, gi, DyrRecordStatus.Skipped, "not used",
                    Array.Empty<string>(), why, traces));
            }
        }

        // The verdict of the report, in a fixed order.
        var blocking = new List<string>();
        foreach (DyrRecordCoverage r in rows)
            if (r.Status is DyrRecordStatus.Unmatched or DyrRecordStatus.Skipped or DyrRecordStatus.Approximated or DyrRecordStatus.OutOfScope)
                blocking.Add($"DYR line {r.Line.ToString(CultureInfo.InvariantCulture)} {r.Model} for machine '{r.Id}' at bus {r.Bus.ToString(CultureInfo.InvariantCulture)}: {r.Status}");
        foreach (DynamicsUnitCoverage un in unitRows)
            if (un.Status != "BOUND")
                blocking.Add($"machine '{un.Id}' at bus {un.Bus.ToString(CultureInfo.InvariantCulture)}: {un.Status}");
        bool complete = blocking.Count == 0;
        bool simulable = rows.All(r => r.Status != DyrRecordStatus.Unmatched) && unitRows.All(un => un.Status != "UNBOUND")
            && (complete || policy == DynamicsUnmodelledPolicy.LeaveOut);
        var report = new DynamicsModelCoverageReport(complete ? "COMPLETE" : "BLOCKED", complete, simulable, blocking, rows,
            unitRows, readerNotes);
        return new DynamicsModelBinding(report, simulable ? set : null);
    }

    /// <summary>The trace channel labels of one unit.</summary>
    public static IReadOnlyList<string> TraceLabels(int bus, string id)
    {
        string b = bus.ToString(CultureInfo.InvariantCulture);
        return new[] { $"rotorAngle {b}/'{id}'", $"speed {b}/'{id}'", $"frequency {b}/'{id}'", $"busVoltage {b}" };
    }

    /// <summary>The records as DYR text, each on its original line (blank lines between), comma separated so an empty
    /// field stays a field. Quotes round the model name and ID; a value holding a blank, comma, slash or quote is quoted.</summary>
    internal static string Rewrite(IReadOnlyList<PsseDyrRecord> records)
    {
        var sb = new StringBuilder();
        int line = 1;
        foreach (PsseDyrRecord r in records)
        {
            while (line < r.Line) { sb.Append('\n'); line++; }
            sb.Append(r.Bus.ToString(CultureInfo.InvariantCulture)).Append(",'").Append(r.Model).Append("','").Append(r.Id).Append('\'');
            foreach (string v in r.Values)
            {
                sb.Append(',');
                if (v.AsSpan().IndexOfAny(QuoteNeeded) >= 0) sb.Append('"').Append(v).Append('"');
                else sb.Append(v);
            }
            sb.Append(" /\n");
            line++;
        }
        return sb.ToString();
    }

    /// <summary>Bind a gridsim-dynamics/2 document with the incumbent loader, from a temporary copy named
    /// <c>&lt;dyr&gt;.dynamics.json</c> that is removed after loading (the CLI's own route for a DYR file).</summary>
    public static MachineDynamicsSet BindDocument(string json, string dyrName, string schemasDir, GridModel model)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-dynamics-study-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, Path.GetFileName(dyrName) + ".dynamics.json");
            File.WriteAllText(path, json);
            return MachineDynamicsLoader.Load(path, schemasDir, model);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
