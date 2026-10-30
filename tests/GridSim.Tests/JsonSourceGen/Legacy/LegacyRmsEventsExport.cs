using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.RmsEventsExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyRmsEventsExport)
// and the namespace changed.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyRmsEventsExport
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

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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
        var doc = new
        {
            schema = Schema,
            @case = caseLabel,
            nominalHz,
            stepSeconds,
            durationSeconds,
            samples = run.Machines[0].TimeSeconds.Count,
            // CF6-F5: the CSVs hold every everySteps-th step (k = 0, N, 2N, ...), samplesWritten rows per trace.
            everySteps,
            samplesWritten = (run.Machines[0].TimeSeconds.Count + everySteps - 1) / everySteps,
            // Time order, stable for equal times (the order the study applies them in).
            events = events.Select((e, i) => (e, i)).OrderBy(x => x.e.AtSeconds).Select(x => new
            {
                index = x.i,
                kind = x.e.GetType().Name,
                atSeconds = x.e.AtSeconds,
                description = x.e.Describe(),
            }).ToArray(),
            verdict = new
            {
                stayedInSynchronism = run.StayedInSynchronism,
                text = run.StayedInSynchronism ? "STABLE (the connected machines stay in synchronism)" : "UNSTABLE (loss of synchronism)",
            },
            converterIterationFailures = run.ConverterIterationFailures,
            machines = machineBuses.Select((bus, i) => new { trace = i, bus, islandReference = i >= connected }).ToArray(),
            converters = (run.Converters ?? Array.Empty<RmsConverterTrace>()).Select((c, i) => new { index = i, name = c.Name, bus = c.Bus }).ToArray(),
            files = new
            {
                machines = MachinesCsv,
                converters = run.Converters is { Count: > 0 } ? ConvertersCsv : null,
            },
            provenance = new { tag = Provenance.ToString(), note = "a model trajectory of the RMS event study, not a record" },
            indicative,
            indicativeNote,
        };
        return JsonSerializer.Serialize(doc, Json) + "\n";
    }
}
