using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Estimation;
using Parquet.Serialization;

namespace GridSim.Gda.Interpose;

/// <summary>One GDA-shaped telemetry row. Column names follow GDA's unified-table contract (snake_case, unit
/// encoded in the column name) via <see cref="JsonPropertyNameAttribute"/>, which Parquet.Net honours for
/// column naming. Only the value column matching the measurement kind is populated; the rest stay null.</summary>
public sealed class QuarantineRow
{
    [JsonPropertyName("timestamp_utc")] public DateTime TimestampUtc { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("source_class")] public string SourceClass { get; set; } = "telemetry";
    [JsonPropertyName("cadence_min")] public double CadenceMin { get; set; }
    [JsonPropertyName("vm_pu")] public double? VmPu { get; set; }
    [JsonPropertyName("va_deg")] public double? VaDeg { get; set; }
    [JsonPropertyName("p_mw")] public double? PMw { get; set; }
    [JsonPropertyName("q_mvar")] public double? QMvar { get; set; }
    [JsonPropertyName("i_pu")] public double? IPu { get; set; }
    [JsonPropertyName("i_ang_deg")] public double? IAngDeg { get; set; }
    [JsonPropertyName("bus")] public int? Bus { get; set; }
    [JsonPropertyName("from_bus")] public int? FromBus { get; set; }
    [JsonPropertyName("to_bus")] public int? ToBus { get; set; }
    [JsonPropertyName("sigma")] public double Sigma { get; set; }
    [JsonPropertyName("provenance")] public string Provenance { get; set; } = "";
    [JsonPropertyName("min_confidence")] public double MinConfidence { get; set; }
    [JsonPropertyName("year")] public int Year { get; set; }
    [JsonPropertyName("week")] public int Week { get; set; }
}

/// <summary>
/// Writes interposer telemetry to a <b>GDA-shaped, but quarantined</b> Parquet store: the columns and hive
/// partitioning (<c>&lt;root&gt;/Parquet/&lt;source&gt;/year=YYYY/week=WW/*.parquet</c>, with
/// <c>timestamp_utc</c> / <c>source</c> / <c>source_class</c> / unit-named value columns / <c>sigma</c> /
/// <c>provenance</c>) match GDA's unified-table contract so GDA tooling (<c>lake_access.read_window</c>) can
/// read it - <b>but it is deliberately firewalled from GDA's public evidence lake</b>. Live SCADA is on GDA's
/// explicit exclude-by-rule list (README "does NOT use SCADA data"; manual Table 1.3), so this store:
/// <list type="bullet">
/// <item>tags every row <c>source_class = "telemetry"</c> and drops a machine-readable <c>_QUARANTINE.json</c>
/// sidecar marking the partition non-evidential;</item>
/// <item>refuses, in code, to write anywhere inside the GDA lake (detected by a <c>DataSchema.json</c>
/// master-contract file at or above the target) - the firewall is enforced, not merely documented.</item>
/// </list>
/// It must never be merged into GDA's MERGE-only unified tables.
/// </summary>
public sealed class QuarantineLiveStore
{
    private const string EvidenceLakeMarker = "DataSchema.json"; // the GDA lake's master-contract file

    private static readonly JsonSerializerOptions SidecarJson = new() { WriteIndented = true };

    private readonly string _sourceDir;
    private readonly string _sourceId;
    private int _seq;
    private bool _sidecarWritten;

    /// <summary>Open (creating) a quarantine store rooted at <paramref name="root"/> for source id
    /// <paramref name="sourceId"/> (default "rtu_live").</summary>
    /// <exception cref="InvalidOperationException">If the target resolves inside the GDA public lake.</exception>
    public QuarantineLiveStore(string root, string sourceId = "rtu_live")
    {
        _sourceId = sourceId;
        string full = Path.GetFullPath(root);
        RefuseIfInsideEvidenceLake(full);
        _sourceDir = Path.Combine(full, "Parquet", sourceId);
        Directory.CreateDirectory(_sourceDir);
        // GR-828: the non-evidential marker is written when the store opens, BEFORE any partition exists. It used to be
        // written after the first Parquet file, so a crash between the two left telemetry in the tree with no marker.
        EnsureSidecar();
    }

    /// <summary>Number of Parquet partitions written this session.</summary>
    public int PartitionsWritten { get; private set; }

    /// <summary>The file the last <see cref="WriteWindow"/> created (null before the first). It differs from the
    /// planned name only when that name was already taken and a "-dupN" name was used instead.</summary>
    public string? LastPartitionPath { get; private set; }

    /// <summary>Persist one measurement window as a GDA-shaped Parquet partition. No-op for an empty set.</summary>
    public void WriteWindow(MeasurementSet set, string protocol, double cadenceMin)
        => WriteWindowAsync(set, protocol, cadenceMin).GetAwaiter().GetResult();

    /// <inheritdoc cref="WriteWindow"/>
    public async Task WriteWindowAsync(MeasurementSet set, string protocol, double cadenceMin)
    {
        if (set.Measurements.Count == 0) return;
        // GR-828: a window with no taken time is refused. It used to be stamped with the current clock, so the row's
        // timestamp_utc (and its partition) recorded when it was written, not when it was measured. The project-wide
        // rule (UtcTime.Normalise) reads an Unspecified stamp as UTC; ToUniversalTime used to shift it by the machine's
        // own zone.
        if (set.TakenUtc is not DateTime taken)
            throw new ArgumentException(
                "a quarantine window must carry its TakenUtc (the window instant); the store will not stamp telemetry with the write time.",
                nameof(set));
        DateTime ts = GridSim.Core.Diagnostics.UtcTime.Normalise(taken);
        int year = ISOWeek.GetYear(ts);
        int week = ISOWeek.GetWeekOfYear(ts);

        var rows = new List<QuarantineRow>(set.Measurements.Count);
        foreach (Measurement m in set.Measurements)
        {
            var row = new QuarantineRow
            {
                TimestampUtc = ts,
                Source = _sourceId,
                SourceClass = "telemetry",   // GDA controlled vocabulary; NEVER "settled"/"metered"
                CadenceMin = cadenceMin,
                Sigma = m.Sigma,
                Provenance = $"{protocol}:{m.Kind}@{m.Describe()}",
                MinConfidence = 0.0,         // live telemetry carries no settlement confidence
                Year = year,
                Week = week,
            };
            if (m.IsBranch) { row.FromBus = m.From; row.ToBus = m.To; }
            else row.Bus = m.Bus;

            switch (m.Kind)
            {
                case MeasurementKind.VmPu: row.VmPu = m.Value; break;
                case MeasurementKind.VaDeg: row.VaDeg = m.Value; break;
                case MeasurementKind.PInjMw:
                case MeasurementKind.PFlowMw: row.PMw = m.Value; break;
                case MeasurementKind.QInjMvar:
                case MeasurementKind.QFlowMvar: row.QMvar = m.Value; break;
                case MeasurementKind.IMagPu: row.IPu = m.Value; break;
                case MeasurementKind.IAngDeg: row.IAngDeg = m.Value; break;
            }
            rows.Add(row);
        }

        // GR-829: the partition and file names are formatted in the invariant culture; the date pattern otherwise
        // follows the current culture's calendar (a Thai Buddhist or Hijri calendar renames the files).
        string partDir = Path.Combine(_sourceDir, FormattableString.Invariant($"year={year}"), FormattableString.Invariant($"week={week:D2}"));
        Directory.CreateDirectory(partDir);
        string file = Path.Combine(partDir, FormattableString.Invariant($"part-{ts:yyyyMMddTHHmmssfffZ}-{_seq++:D6}.parquet"));

        // Never overwrite an earlier partition: a second window with the same instant and sequence (a restarted
        // listener writing into the same store) is kept under a "-dupN" name instead of replacing the first.
        await using (Stream fs = GridSim.Core.IO.NeverOverwrite.CreateNew(file, out string written))
        {
            await ParquetSerializer.SerializeAsync(rows, fs);
            LastPartitionPath = written;
        }

        PartitionsWritten++;
    }

    /// <summary>Write the firewall marker once - a machine-readable statement that this store is
    /// non-evidential live SCADA, outside GDA's public-data-only guarantees, and must not be merged.</summary>
    private void EnsureSidecar()
    {
        if (_sidecarWritten) return;
        var marker = new
        {
            store = _sourceId,
            source_class = "telemetry",
            evidential = false,
            reason = "live SCADA/RTU telemetry - GDA explicit exclude-by-rule (README 'does NOT use SCADA data'; manual Table 1.3)",
            never_merge_into = "GDA unified (MERGE-only) tables",
            horizon_hours = GridSim.Core.Diagnostics.ForwardInferenceGuard.HorizonHours,
            written_by = "gridsim interpose",
        };
        File.WriteAllText(Path.Combine(_sourceDir, "_QUARANTINE.json"), JsonSerializer.Serialize(marker, SidecarJson));
        _sidecarWritten = true;
    }

    /// <summary>Throw if <paramref name="target"/> is at or below a directory containing the GDA lake's
    /// master-contract file - writing telemetry there would contaminate the public evidence estate.</summary>
    /// <remarks>GR-825: every existing directory on the way up that is a symbolic link or junction is resolved to its
    /// final target and that target's own chain is checked too, so a link outside the lake that points into it is
    /// refused. Before, only the path as written was walked, and such a link passed. A link that cannot be resolved (a
    /// loop, or a chain deeper than <see cref="MaxLinkDepth"/>) is refused rather than trusted.</remarks>
    private static void RefuseIfInsideEvidenceLake(string target, int depth = 0)
    {
        if (depth > MaxLinkDepth)
            throw new InvalidOperationException(
                $"refusing to open the quarantine store: resolving links under '{target}' went deeper than {MaxLinkDepth} levels " +
                "(a link loop?), so whether it lies inside the GDA public lake cannot be decided.");
        for (DirectoryInfo? d = new(target); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, EvidenceLakeMarker)))
                throw new InvalidOperationException(
                    $"refusing to write live telemetry into the GDA public lake (found {EvidenceLakeMarker} at " +
                    $"'{d.FullName}'). The interposer's quarantine store must live OUTSIDE the evidence estate.");
            if (d.Exists && d.LinkTarget is not null)
            {
                FileSystemInfo? real;
                try { real = d.ResolveLinkTarget(returnFinalTarget: true); }
                catch (IOException ex)
                {
                    throw new InvalidOperationException(
                        $"refusing to open the quarantine store: the link '{d.FullName}' cannot be resolved ({ex.Message}), so " +
                        "whether it points into the GDA public lake cannot be decided.", ex);
                }
                if (real is not null) RefuseIfInsideEvidenceLake(Path.GetFullPath(real.FullName), depth + 1);
            }
        }
    }

    // GR-825: the deepest chain of links the firewall follows before refusing.
    private const int MaxLinkDepth = 32;
}
