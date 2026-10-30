using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;

namespace GridSim.Core.IO;

/// <summary>
/// N363 (CF6): the export of one RMS scheduled-event study (<c>gridsim stability --rms --events ... --events-out
/// &lt;dir&gt;</c>, <see cref="RmsTransientStability.StudyEvents"/>). Until N363 that run printed its verdict and nothing
/// else: no result file and no trajectory. The export follows the stability / RMS conventions of the other exports:
/// <list type="bullet">
/// <item><c>rms-events-machines.csv</c>: every integrated machine's trajectory at every integration step, one row per
/// (sample, machine): time, trace index, bus, rotor angle (rad, as integrated: no conversion), speed and the four flux
/// states (pu), each double in its shortest round-trip form ("R", invariant culture), so the file holds exactly the bits
/// the integrator produced.</item>
/// <item><c>rms-events-converters.csv</c> (only when the fleet has converter plant): each converter's terminal voltage,
/// active and reactive power and current, and its dip-mode flag, at every step.</item>
/// <item><c>rms-events.json</c> (schema <see cref="Schema"/>): the summary a reader needs to interpret the CSVs: the
/// case, step, duration, nominal frequency, every event in time order, the verdict, the converter-iteration count, the
/// machine list (trace index, bus, island reference or not) and the provenance.</item>
/// </list>
/// Both CSVs start with the caller's header lines, each prefixed "# " (the CLI passes its run header: code version,
/// machine, run description), followed by a "# provenance:" line. <b>Provenance:</b> a model trajectory, not a record,
/// so <see cref="ProvenanceTag.Synthetic"/> (the tag every model-output export carries, as the frequency export does);
/// <paramref name="indicativeNote"/> says in words whether the machine data was synthesised (INDICATIVE) or came from a
/// dynamics file. Pure formatting: nothing here changes or recomputes a number of the run.
/// </summary>
public static class RmsEventsExport
{
    /// <summary>The summary document's schema id.</summary>
    public const string Schema = "gridsim-rms-events-export/1";

    /// <summary>The file names written into the export folder.</summary>
    public const string MachinesCsv = "rms-events-machines.csv";
    public const string ConvertersCsv = "rms-events-converters.csv";
    public const string SummaryJson = "rms-events.json";

    /// <summary>The provenance of every RMS event export: a model output (see the class remarks).</summary>
    public const ProvenanceTag Provenance = ProvenanceTag.Synthetic;

    /// <summary>The machines CSV column header.</summary>
    public const string MachinesHeader = "t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu";

    /// <summary>The converters CSV column header.</summary>
    public const string ConvertersHeader = "t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): the same options (indented, no naming
    // policy), bound to the source-generated RmsEventsExportJsonContext (end of file): no runtime reflection.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly RmsEventsExportJsonContext Json = new(new JsonSerializerOptions { WriteIndented = true });

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Check that the run and the machine labels line up, failing loud (<see cref="ArgumentException"/>) rather than
    /// writing a file whose rows are misattributed: one bus per machine trace, every trace (and converter trace) as long
    /// as the first, at least one machine and one sample, and at most as many island references as traces.
    /// </summary>
    private static void Validate(MultiMachineSubtransient.Result run, IReadOnlyList<int> machineBuses, int islandReferences)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(machineBuses);
        if (run.Machines.Count == 0) throw new ArgumentException("the RMS run has no machine trace to export.", nameof(run));
        if (machineBuses.Count != run.Machines.Count)
            throw new ArgumentException($"{machineBuses.Count} machine buses for {run.Machines.Count} machine traces; the labels do not line up.", nameof(machineBuses));
        if (islandReferences < 0 || islandReferences > run.Machines.Count)
            throw new ArgumentOutOfRangeException(nameof(islandReferences), islandReferences, $"must be between 0 and the {run.Machines.Count} traces.");
        int n = run.Machines[0].TimeSeconds.Count;
        if (n == 0) throw new ArgumentException("the RMS run has no samples to export.", nameof(run));
        for (int i = 0; i < run.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace m = run.Machines[i];
            if (m.TimeSeconds.Count != n || m.DeltaRad.Count != n || m.SpeedPu.Count != n || m.EqpPu.Count != n
                || m.EdpPu.Count != n || m.EqppPu.Count != n || m.EddpPu.Count != n)
                throw new ArgumentException($"machine trace {i} does not have {n} samples in every series.", nameof(run));
        }
        if (run.Converters is { } cv)
            for (int c = 0; c < cv.Count; c++)
            {
                RmsConverterTrace t = cv[c];
                if (t.TerminalVoltagePu.Count != n || t.ActivePowerPu.Count != n || t.ReactivePowerPu.Count != n
                    || t.ActiveCurrentPu.Count != n || t.ReactiveCurrentPu.Count != n || t.InDipMode.Count != n)
                    throw new ArgumentException($"converter trace {c} ({t.Name}) does not have {n} samples in every series.", nameof(run));
            }
    }

    /// <summary>The largest decimation accepted (one row per million steps is already far below any useful trajectory).</summary>
    public const int MaxEverySteps = 1_000_000;

    private static void RequireEvery(int everySteps)
    {
        if (everySteps < 1 || everySteps > MaxEverySteps)
            throw new ArgumentOutOfRangeException(nameof(everySteps), everySteps, $"must be a whole number of steps from 1 to {MaxEverySteps}.");
    }

    /// <summary>The "# provenance:" line both CSVs carry after the caller's header lines.</summary>
    public static string ProvenanceLine(string indicativeNote) =>
        $"provenance: {Provenance} (a model trajectory of the RMS event study, not a record); {indicativeNote}";

    /// <summary>The machines CSV text (see the class remarks). <paramref name="machineBuses"/> is the bus of each machine
    /// trace in trace order; the last <paramref name="islandReferences"/> traces are synthetic island references.</summary>
    /// <param name="everySteps">Main's ruling on CF6-F5: write every N-th integration step (k = 0, N, 2N, ...); 1 (the
    /// default) writes every step, so the default file is exactly the full-resolution one. Must be 1 or more.</param>
    public static string MachinesCsvText(MultiMachineSubtransient.Result run, IReadOnlyList<int> machineBuses, int islandReferences,
        IReadOnlyList<string> headerLines, string indicativeNote, int everySteps = 1)
    {
        Validate(run, machineBuses, islandReferences);
        RequireEvery(everySteps);
        ArgumentNullException.ThrowIfNull(headerLines);
        var sb = new StringBuilder();
        foreach (string h in headerLines) sb.Append("# ").Append(h).Append('\n');
        sb.Append("# ").Append(ProvenanceLine(indicativeNote)).Append('\n');
        if (islandReferences > 0)
            sb.Append("# traces ").Append(run.Machines.Count - islandReferences).Append(" to ").Append(run.Machines.Count - 1)
              .Append(" are synthetic island references (stiff sources holding an island's voltage), not machines of the case\n");
        sb.Append(MachinesHeader).Append('\n');
        int n = run.Machines[0].TimeSeconds.Count;
        for (int k = 0; k < n; k += everySteps)
            for (int i = 0; i < run.Machines.Count; i++)
            {
                MultiMachineSubtransient.MachineTrace m = run.Machines[i];
                sb.Append(R(m.TimeSeconds[k])).Append(',').Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(machineBuses[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(R(m.DeltaRad[k])).Append(',').Append(R(m.SpeedPu[k])).Append(',')
                  .Append(R(m.EqpPu[k])).Append(',').Append(R(m.EdpPu[k])).Append(',')
                  .Append(R(m.EqppPu[k])).Append(',').Append(R(m.EddpPu[k])).Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>The converters CSV text, or null when the run has no converter traces (no file is written then).</summary>
    public static string? ConvertersCsvText(MultiMachineSubtransient.Result run, IReadOnlyList<int> machineBuses, int islandReferences,
        IReadOnlyList<string> headerLines, string indicativeNote, int everySteps = 1)
    {
        Validate(run, machineBuses, islandReferences);
        RequireEvery(everySteps);
        ArgumentNullException.ThrowIfNull(headerLines);
        if (run.Converters is not { Count: > 0 } cv) return null;
        var sb = new StringBuilder();
        foreach (string h in headerLines) sb.Append("# ").Append(h).Append('\n');
        sb.Append("# ").Append(ProvenanceLine(indicativeNote)).Append('\n');
        sb.Append(ConvertersHeader).Append('\n');
        IReadOnlyList<double> t = run.Machines[0].TimeSeconds;
        for (int k = 0; k < t.Count; k += everySteps)
            for (int c = 0; c < cv.Count; c++)
            {
                RmsConverterTrace x = cv[c];
                // The name is quoted only if it holds a comma or a quote (RFC 4180), so a plain name reads as itself.
                string name = x.Name.Contains(',', StringComparison.Ordinal) || x.Name.Contains('"', StringComparison.Ordinal)
                    ? "\"" + x.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : x.Name;
                sb.Append(R(t[k])).Append(',').Append(c.ToString(CultureInfo.InvariantCulture)).Append(',').Append(name).Append(',')
                  .Append(x.Bus.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(R(x.TerminalVoltagePu[k])).Append(',').Append(R(x.ActivePowerPu[k])).Append(',')
                  .Append(R(x.ReactivePowerPu[k])).Append(',').Append(R(x.ActiveCurrentPu[k])).Append(',')
                  .Append(R(x.ReactiveCurrentPu[k])).Append(',').Append(x.InDipMode[k] ? "true" : "false").Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>The summary JSON text (schema <see cref="Schema"/>).</summary>
    public static string SummaryJsonText(MultiMachineSubtransient.Result run, IReadOnlyList<int> machineBuses, int islandReferences,
        IReadOnlyList<RmsEvent> events, string caseLabel, double nominalHz, double stepSeconds, double durationSeconds,
        bool indicative, string indicativeNote, int everySteps = 1)
    {
        Validate(run, machineBuses, islandReferences);
        RequireEvery(everySteps);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseLabel);
        if (events.Count == 0) throw new ArgumentException("an RMS event study has at least one event.", nameof(events));
        if (!double.IsFinite(nominalHz) || !(nominalHz > 0)) throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz, "must be positive and finite.");
        if (!double.IsFinite(stepSeconds) || !(stepSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "must be positive and finite.");
        if (!double.IsFinite(durationSeconds) || !(durationSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "must be positive and finite.");

        int connected = run.Machines.Count - islandReferences;
        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new RmsSummaryDoc
        {
            schema = Schema,
            caseLabel = caseLabel,
            nominalHz = nominalHz,
            stepSeconds = stepSeconds,
            durationSeconds = durationSeconds,
            samples = run.Machines[0].TimeSeconds.Count,
            // CF6-F5: the CSVs hold every everySteps-th step (k = 0, N, 2N, ...), samplesWritten rows per trace.
            everySteps = everySteps,
            samplesWritten = (run.Machines[0].TimeSeconds.Count + everySteps - 1) / everySteps,
            // Time order, stable for equal times (the order the study applies them in).
            events = events.Select((e, i) => (e, i)).OrderBy(x => x.e.AtSeconds).Select(x => new EventRow
            {
                index = x.i,
                kind = x.e.GetType().Name,
                atSeconds = x.e.AtSeconds,
                description = x.e.Describe(),
            }).ToArray(),
            verdict = new VerdictRow
            {
                stayedInSynchronism = run.StayedInSynchronism,
                text = run.StayedInSynchronism ? "STABLE (the connected machines stay in synchronism)" : "UNSTABLE (loss of synchronism)",
            },
            converterIterationFailures = run.ConverterIterationFailures,
            machines = machineBuses.Select((bus, i) => new MachineRow { trace = i, bus = bus, islandReference = i >= connected }).ToArray(),
            converters = (run.Converters ?? Array.Empty<RmsConverterTrace>()).Select((c, i) => new ConverterRow { index = i, name = c.Name, bus = c.Bus }).ToArray(),
            files = new FilesRow
            {
                machines = MachinesCsv,
                converters = run.Converters is { Count: > 0 } ? ConvertersCsv : null,
            },
            provenance = new ProvenanceRow { tag = Provenance.ToString(), note = "a model trajectory of the RMS event study, not a record" },
            indicative = indicative,
            indicativeNote = indicativeNote,
        };
        return JsonSerializer.Serialize(doc, Json.RmsSummaryDoc) + "\n";
    }

    // ---- Phase 0.1: the summary document as named types --------------------------------------------------------------
    // These replace the anonymous objects SummaryJsonText used to build, so the source-generated context can describe
    // them (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types. JsonSourceGenEquivalenceTests holds the bytes equal to the old reflection-built document.

    /// <summary>The whole RMS event-study summary document.</summary>
    internal sealed class RmsSummaryDoc
    {
        public required string schema { get; init; }
        // Named caseLabel in C# because the source generator does not escape the keyword in an @case property; the
        // attribute keeps the JSON name "case" exactly as before.
        [JsonPropertyName("case")]
        public required string caseLabel { get; init; }
        public required double nominalHz { get; init; }
        public required double stepSeconds { get; init; }
        public required double durationSeconds { get; init; }
        public required int samples { get; init; }
        public required int everySteps { get; init; }
        public required int samplesWritten { get; init; }
        public required EventRow[] events { get; init; }
        public required VerdictRow verdict { get; init; }
        public required int converterIterationFailures { get; init; }
        public required MachineRow[] machines { get; init; }
        public required ConverterRow[] converters { get; init; }
        public required FilesRow files { get; init; }
        public required ProvenanceRow provenance { get; init; }
        public required bool indicative { get; init; }
        public required string indicativeNote { get; init; }
    }

    /// <summary>One event, in time order: { index, kind, atSeconds, description }.</summary>
    internal sealed class EventRow
    {
        public required int index { get; init; }
        public required string kind { get; init; }
        public required double atSeconds { get; init; }
        public required string description { get; init; }
    }

    /// <summary>{ stayedInSynchronism, text }.</summary>
    internal sealed class VerdictRow
    {
        public required bool stayedInSynchronism { get; init; }
        public required string text { get; init; }
    }

    /// <summary>One machine trace: { trace, bus, islandReference }.</summary>
    internal sealed class MachineRow
    {
        public required int trace { get; init; }
        public required int bus { get; init; }
        public required bool islandReference { get; init; }
    }

    /// <summary>One converter: { index, name, bus }.</summary>
    internal sealed class ConverterRow
    {
        public required int index { get; init; }
        public required string name { get; init; }
        public required int bus { get; init; }
    }

    /// <summary>{ machines, converters } (converters is null, written as null, when there are none).</summary>
    internal sealed class FilesRow
    {
        public required string machines { get; init; }
        public required string? converters { get; init; }
    }

    /// <summary>{ tag, note }.</summary>
    internal sealed class ProvenanceRow
    {
        public required string tag { get; init; }
        public required string note { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the RMS event-study summary document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RmsEventsExport.RmsSummaryDoc))]
internal sealed partial class RmsEventsExportJsonContext : JsonSerializerContext;
