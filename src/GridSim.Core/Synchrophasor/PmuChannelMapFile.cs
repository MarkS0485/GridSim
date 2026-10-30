using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridSim.Core.Synchrophasor;

/// <summary>
/// A loaded PMU channel map (<c>gridsim-pmu-channels/1</c>): which phasor of which PMU is the voltage at which bus or
/// the current in which branch, the per-unit bases and sigmas, and the named angle-reference channel (R295).
/// </summary>
/// <param name="Case">Name of the network case the channels describe (informational, copied to each measurement set).</param>
/// <param name="BaseMva">System base MVA of the measurement sets built from it.</param>
/// <param name="Reference">The voltage channel every angle is re-referenced against.</param>
/// <param name="Maps">One entry per PMU, in file order.</param>
public sealed record PmuChannelMapSet(string Case, double BaseMva, PmuAngleReference Reference, IReadOnlyList<PmuChannelMap> Maps);

/// <summary>
/// D-DOC-107 (October 2026): the file format that lets <c>pmu-ingest</c> build <see cref="Estimation.MeasurementSet"/>s.
/// Before, <c>pmu-ingest</c> only decoded and summarised a capture: it built no measurement set, so nothing it read ever
/// met <see cref="PmuMeasurementSetMapper"/> or its horizon guard (the mapper had no production caller, D-DOC-203). With
/// a channel map, every aligned instant becomes a <c>gridsim-measurements/1</c> set through that mapper, and so through
/// the guard.
///
/// <para><b>Format</b> (camelCase JSON):</para>
/// <code>
/// { "schema": "gridsim-pmu-channels/1", "case": "case9", "baseMva": 100,
///   "reference": { "pmu": 101, "phasor": 0, "anchorDeg": 0 },
///   "pmus": [ { "pmu": 101,
///               "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": 9526.3, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.1 } ],
///               "currents": [ { "phasor": 1, "from": 1, "to": 4, "circuit": null, "baseAmps": 2405.6,
///                               "iMagSigmaPu": 0.005, "iAngSigmaDeg": 0.2 } ] } ] }
/// </code>
/// <para>Every field except <c>case</c>, <c>circuit</c> and <c>anchorDeg</c> is required. <see cref="Load"/> refuses,
/// naming every problem at once: a wrong schema, a missing or non-positive base or sigma (or one that is not finite), a
/// negative phasor index, a PMU listed twice, a phasor mapped twice in one PMU, no channel at all, and a reference that
/// is not one of the mapped voltage channels. <see cref="ValidateAgainst"/> then checks the map against the capture's
/// CONFIG frame: every PMU must be in it, every phasor index inside that PMU's phasor count, and (when the CONFIG frame
/// carries PHUNIT types) a voltage channel must be a voltage phasor and a current channel a current phasor.</para>
/// </summary>
public static class PmuChannelMapFile
{
    /// <summary>The wire-format identifier.</summary>
    public const string SchemaId = "gridsim-pmu-channels/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated PmuChannelMapFileJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly PmuChannelMapFileJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    });

    // ---- wire DTOs (nullable so a missing field is seen, not defaulted to 0) ------------------------------------------

    // Phase 0.1: the wire DTOs below are internal rather than private only so the source-generated JSON context, a
    // separate class, can reach them; nothing else changes.
    internal sealed class FileDto
    {
        public string? Schema { get; set; }
        public string? Case { get; set; }
        public double? BaseMva { get; set; }
        public ReferenceDto? Reference { get; set; }
        public List<PmuDto?>? Pmus { get; set; }
    }

    internal sealed class ReferenceDto
    {
        public int? Pmu { get; set; }
        public int? Phasor { get; set; }
        public double? AnchorDeg { get; set; }
    }

    internal sealed class PmuDto
    {
        public int? Pmu { get; set; }
        public List<VoltageDto?>? Voltages { get; set; }
        public List<CurrentDto?>? Currents { get; set; }
    }

    internal sealed class VoltageDto
    {
        public int? Phasor { get; set; }
        public int? Bus { get; set; }
        public double? BaseVolts { get; set; }
        public double? VmSigmaPu { get; set; }
        public double? VaSigmaDeg { get; set; }
    }

    internal sealed class CurrentDto
    {
        public int? Phasor { get; set; }
        public int? From { get; set; }
        public int? To { get; set; }
        public string? Circuit { get; set; }
        public double? BaseAmps { get; set; }
        public double? IMagSigmaPu { get; set; }
        public double? IAngSigmaDeg { get; set; }
    }

    /// <summary>Load and check a channel map. Throws <see cref="InvalidDataException"/> naming every problem, or the
    /// file system's own exception when the file cannot be read.</summary>
    public static PmuChannelMapSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileDto? dto;
        try { dto = JsonSerializer.Deserialize(File.ReadAllText(path), Json.FileDto); }
        catch (JsonException ex) { throw new InvalidDataException($"PMU channel map '{path}' is not valid JSON: {ex.Message}", ex); }
        if (dto is null) throw new InvalidDataException($"PMU channel map '{path}' is empty (JSON null).");

        var problems = new List<string>();
        if (!string.Equals(dto.Schema, SchemaId, StringComparison.Ordinal))
            problems.Add($"schema is '{dto.Schema ?? "(missing)"}', expected '{SchemaId}'");
        double baseMva = Positive(dto.BaseMva, "baseMva", problems);

        var maps = new List<PmuChannelMap>();
        var seenPmus = new HashSet<int>();
        if (dto.Pmus is null || dto.Pmus.Count == 0) problems.Add("pmus is missing or empty");
        else
        {
            for (int k = 0; k < dto.Pmus.Count; k++)
            {
                PmuDto? p = dto.Pmus[k];
                string where = $"pmus[{k}]";
                if (p is null) { problems.Add($"{where} is null"); continue; }
                if (p.Pmu is not int pmuId) { problems.Add($"{where}.pmu is missing"); continue; }
                where = $"pmus[{k}] (PMU {pmuId})";
                if (!seenPmus.Add(pmuId)) problems.Add($"{where} lists PMU {pmuId} a second time");
                var usedPhasors = new HashSet<int>();
                var volts = new List<PmuVoltageChannel>();
                foreach ((VoltageDto? v, int i) in (p.Voltages ?? new List<VoltageDto?>()).Select((v, i) => (v, i)))
                {
                    string w = $"{where}.voltages[{i}]";
                    if (v is null) { problems.Add($"{w} is null"); continue; }
                    int ph = Index(v.Phasor, $"{w}.phasor", problems);
                    if (ph >= 0 && !usedPhasors.Add(ph)) problems.Add($"{w}: phasor {ph} is mapped twice in this PMU");
                    if (v.Bus is not int bus) { problems.Add($"{w}.bus is missing"); continue; }
                    volts.Add(new PmuVoltageChannel(ph, bus, Positive(v.BaseVolts, $"{w}.baseVolts", problems),
                        Positive(v.VmSigmaPu, $"{w}.vmSigmaPu", problems), Positive(v.VaSigmaDeg, $"{w}.vaSigmaDeg", problems)));
                }
                var amps = new List<PmuCurrentChannel>();
                foreach ((CurrentDto? c, int i) in (p.Currents ?? new List<CurrentDto?>()).Select((c, i) => (c, i)))
                {
                    string w = $"{where}.currents[{i}]";
                    if (c is null) { problems.Add($"{w} is null"); continue; }
                    int ph = Index(c.Phasor, $"{w}.phasor", problems);
                    if (ph >= 0 && !usedPhasors.Add(ph)) problems.Add($"{w}: phasor {ph} is mapped twice in this PMU");
                    if (c.From is not int from || c.To is not int to) { problems.Add($"{w}.from / .to is missing"); continue; }
                    amps.Add(new PmuCurrentChannel(ph, from, to, c.Circuit, Positive(c.BaseAmps, $"{w}.baseAmps", problems),
                        Positive(c.IMagSigmaPu, $"{w}.iMagSigmaPu", problems), Positive(c.IAngSigmaDeg, $"{w}.iAngSigmaDeg", problems)));
                }
                if (volts.Count == 0 && amps.Count == 0) problems.Add($"{where} maps no channel");
                maps.Add(new PmuChannelMap(pmuId, volts, amps));
            }
        }

        PmuAngleReference? reference = null;
        if (dto.Reference is null) problems.Add("reference is missing (every PMU angle must be re-referenced to a named voltage channel, R295)");
        else if (dto.Reference.Pmu is not int rp || dto.Reference.Phasor is not int rph) problems.Add("reference.pmu / reference.phasor is missing");
        else
        {
            double anchor = dto.Reference.AnchorDeg ?? 0.0;
            if (!double.IsFinite(anchor)) problems.Add("reference.anchorDeg is not finite");
            if (!maps.Any(m => m.PmuId == rp && m.Voltages.Any(v => v.PhasorIndex == rph)))
                problems.Add($"reference (PMU {rp}, phasor {rph}) is not one of the mapped voltage channels");
            reference = new PmuAngleReference(rp, rph, anchor);
        }

        if (problems.Count > 0)
            throw new InvalidDataException($"PMU channel map '{path}' is not usable: " + string.Join("; ", problems) + ".");
        return new PmuChannelMapSet(dto.Case ?? "pmu", baseMva, reference!, maps);
    }

    /// <summary>
    /// Check a loaded map against the capture's CONFIG frame (see the type remarks). Throws
    /// <see cref="InvalidDataException"/> naming every mismatch. This is what keeps a mistyped phasor index from
    /// reaching the mapper as a raw <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    public static void ValidateAgainst(PmuChannelMapSet map, C37118ConfigFrame config)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);
        var problems = new List<string>();
        foreach (PmuChannelMap m in map.Maps)
        {
            PmuConfig? found = config.Pmus.FirstOrDefault(p => p.IdCode == m.PmuId);
            if (found is null) { problems.Add($"PMU {m.PmuId} is not in the capture's CONFIG frame"); continue; }
            PmuConfig pc = found;   // non-null from here on (the local function below captures it)
            void Check(int phasor, bool wantCurrent, string what)
            {
                if (phasor < 0 || phasor >= pc.PhasorCount)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"PMU {m.PmuId} ('{pc.StationName}') has {pc.PhasorCount} phasor(s); {what} phasor {phasor} does not exist"));
                    return;
                }
                if (pc.PhasorUnits is { } units && phasor < units.Count && pc.PhasorIsCurrent(phasor) != wantCurrent)
                    problems.Add($"PMU {m.PmuId} ('{pc.StationName}') phasor {phasor} is a {(wantCurrent ? "voltage" : "current")} " +
                        $"channel in the CONFIG frame (PHUNIT), but the map uses it as a {what}");
            }
            foreach (PmuVoltageChannel v in m.Voltages) Check(v.PhasorIndex, wantCurrent: false, "voltage");
            foreach (PmuCurrentChannel c in m.Currents) Check(c.PhasorIndex, wantCurrent: true, "current");
        }
        if (problems.Count > 0)
            throw new InvalidDataException("The PMU channel map does not fit the capture: " + string.Join("; ", problems) + ".");
    }

    // A required, finite, strictly positive number.
    private static double Positive(double? value, string field, List<string> problems)
    {
        if (value is not double v) { problems.Add($"{field} is missing"); return double.NaN; }
        if (!double.IsFinite(v) || v <= 0) problems.Add(string.Create(CultureInfo.InvariantCulture, $"{field} is {v}; it must be a finite number above 0"));
        return v;
    }

    // A required, non-negative phasor index (returns -1 when unusable so later checks can skip it).
    private static int Index(int? value, string field, List<string> problems)
    {
        if (value is not int v) { problems.Add($"{field} is missing"); return -1; }
        if (v < 0) { problems.Add($"{field} is {v}; it must be 0 or more"); return -1; }
        return v;
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a PMU channel-map file and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PmuChannelMapFile.FileDto))]
internal sealed partial class PmuChannelMapFileJsonContext : JsonSerializerContext;
