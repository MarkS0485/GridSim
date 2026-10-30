using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Synchrophasor;

/// <summary>
/// N358 (CF6): the opt-in quarantine path for a LIVE C37.118 stream (<c>pmu-ingest --live ... --live-quarantine
/// &lt;dir&gt;</c>). A live device streams the present, and the forward-inference horizon
/// (<see cref="ForwardInferenceGuard"/>, 169 h) refuses every present-time frame: without this path a live run is
/// REJECTED whole, exit 2 (CF5's N340 behaviour, which stays the default). With it, the present-time DATA frames are
/// CAPTURED, not processed:
/// <list type="bullet">
/// <item>They are written, as received (byte for byte, with the CONFIG frames and every frame that carried no trusted
/// time), to a capture file named <c>pmu-live-&lt;received UTC&gt;.quarantine.c37</c> in the quarantine folder, with a
/// sidecar <c>&lt;capture&gt;.quarantine.json</c> (schema <see cref="SidecarSchema"/>) that marks it non-evidential.</item>
/// <item>They are NOT decoded into the pipeline, NOT aligned, NOT mapped, and NOT written as measurement sets: nothing the
/// horizon rule protects ever sees them. Only DATA frames already older than the horizon (none, for a device streaming
/// the present) continue through the recorded pipeline, under the same horizon check as before.</item>
/// <item>The provenance is <see cref="Tag"/> = <see cref="ProvenanceTag.CuratedPlaceholder"/>, the lowest input trust
/// rank (<see cref="ProvenanceTrust.Rank"/> 1), and NEVER <see cref="ProvenanceTag.Recorded"/>: a present-time live
/// sample is real telemetry, but it is not an archived record and has passed no horizon, so it stands in for the
/// archived record that does not exist yet (the CuratedPlaceholder meaning: "a placeholder standing in for real data not
/// yet sourced"). Trust only moves down (technical ch. 8): nothing computed from it can be tagged higher, and since
/// N359 a CuratedPlaceholder cannot seed a replay. The data becomes Recorded only by being re-sourced from an archive
/// (the PMU's or PDC's historian) once it is older than the horizon, never by relabelling this file.</item>
/// <item>A quarantine capture is refused by <c>pmu-ingest &lt;capture&gt;</c> (<see cref="RefuseQuarantinedCapture"/>):
/// once it is older than the horizon it would otherwise pass the time check and be read as a recorded capture,
/// laundering the tag.</item>
/// <item>Like the interposer's quarantine store, the folder may not be inside the GDA public evidence lake (detected by
/// its <c>DataSchema.json</c> master-contract file at or above the folder).</item>
/// </list>
/// </summary>
public static class PmuLiveQuarantine
{
    /// <summary>The sidecar's schema id.</summary>
    public const string SidecarSchema = "gridsim-pmu-quarantine/1";

    /// <summary>The capture file's suffix (the file is a clean C37.118 byte stream, deliberately not named .bin).</summary>
    public const string CaptureSuffix = ".quarantine.c37";

    /// <summary>The sidecar's suffix, appended to the capture file's full name.</summary>
    public const string SidecarSuffix = ".quarantine.json";

    /// <summary>The provenance of every quarantined frame (see the class remarks for why, and why never Recorded).</summary>
    public const ProvenanceTag Tag = ProvenanceTag.CuratedPlaceholder;

    /// <summary>The GDA public lake's master-contract file; a quarantine folder at or below it is refused.</summary>
    public const string EvidenceLakeMarker = "DataSchema.json";

    // One cached options instance (CA1869): indented, so the sidecar reads as a document. Phase 0.1: the same options,
    // bound to the source-generated PmuLiveQuarantineJsonContext (end of file), so the sidecar is written without
    // runtime reflection (NativeAOT).
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly PmuLiveQuarantineJsonContext SidecarJson = new(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The note written into every sidecar.</summary>
    public const string Note =
        "QUARANTINE CAPTURE of a live C37.118 stream: present-time frames newer than the historical horizon when received. " +
        "Non-evidential raw telemetry; never an estimator, replay or measurement-set input; never Recorded. It becomes " +
        "Recorded only by re-sourcing the same instants from an archive once they are older than the horizon.";

    /// <summary>
    /// True when <paramref name="stampUtc"/> is newer than the horizon relative to <paramref name="utcNow"/>: exactly the
    /// condition under which <see cref="ForwardInferenceGuard.RejectFuture"/> throws (the same normalisation and the same
    /// comparison), as a predicate so a caller can partition frames instead of refusing the whole capture.
    /// </summary>
    public static bool IsNewerThanHorizon(DateTime stampUtc, DateTime utcNow)
        => UtcTime.Normalise(stampUtc) > ForwardInferenceGuard.Horizon(utcNow);

    /// <summary>
    /// Refuse (<see cref="InvalidDataException"/>) a quarantine folder inside the GDA public evidence lake: a
    /// <see cref="EvidenceLakeMarker"/> in the folder or any folder above it. The same firewall the interposer's
    /// quarantine store enforces (GridSim.Gda.Interpose.QuarantineLiveStore), repeated here because Core does not reference Gda.
    /// </summary>
    public static void RefuseIfInsideEvidenceLake(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        for (DirectoryInfo? d = new DirectoryInfo(Path.GetFullPath(directory)); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, EvidenceLakeMarker)))
                throw new InvalidDataException(
                    $"the quarantine folder '{directory}' is inside the GDA public evidence lake ({EvidenceLakeMarker} found in " +
                    $"'{d.FullName}'); live telemetry is excluded from the lake by rule, so the quarantine is refused there.");
        }
    }

    /// <summary>
    /// Refuse (<see cref="InvalidDataException"/>) reading a quarantine capture as a recorded capture: a file named with
    /// <see cref="CaptureSuffix"/>, or any file with a <see cref="SidecarSuffix"/> sidecar beside it. Every other path
    /// passes untouched (the recorded pipeline is unchanged for every existing capture).
    /// </summary>
    public static void RefuseQuarantinedCapture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        bool named = path.EndsWith(CaptureSuffix, StringComparison.OrdinalIgnoreCase);
        bool sidecar = File.Exists(path + SidecarSuffix);
        if (named || sidecar)
            throw new InvalidDataException(
                $"'{path}' is a quarantined live capture ({(sidecar ? $"sidecar {Path.GetFileName(path + SidecarSuffix)}" : $"named *{CaptureSuffix}")}), " +
                $"tagged {Tag}, never Recorded: it is not a recorded archive and is not read as one, whatever its age. " +
                "Re-source the same instants from the device's or PDC's archive once they are older than the horizon.");
    }

    /// <summary>
    /// Write one quarantine capture and its sidecar. <paramref name="frames"/> are the frames to keep, in received order,
    /// each a complete C37.118 frame (they are concatenated byte for byte, so the file is a clean capture).
    /// The capture is written first, then the sidecar (which carries the capture's SHA-256), each with
    /// <see cref="FileMode.CreateNew"/>: an existing file is never overwritten (a "-dupN" name is used instead, up to 999).
    /// Fails loud on an empty frame list, a frame that is not a frame (no SYNC byte 0xAA or a size field that disagrees
    /// with its length), or a folder inside the evidence lake. Returns the two paths.
    /// </summary>
    /// <param name="directory">The quarantine folder (created if missing).</param>
    /// <param name="frames">The frames to quarantine, in received order.</param>
    /// <param name="source">Where the frames came from (the --live address), written to the sidecar.</param>
    /// <param name="receivedUtc">When the receive ended (the real clock), used in the file name and the sidecar.</param>
    /// <param name="horizonUtc">The horizon the frames were judged against.</param>
    /// <param name="freshDataFrames">How many of the frames are DATA frames newer than the horizon.</param>
    /// <param name="newestFrameUtc">The newest quarantined DATA frame's time, or null when none decoded.</param>
    /// <param name="oldestFrameUtc">The oldest quarantined DATA frame's time, or null when none decoded.</param>
    public static (string CapturePath, string SidecarPath) Write(
        string directory, IReadOnlyList<byte[]> frames, string source, DateTime receivedUtc, DateTime horizonUtc,
        int freshDataFrames, DateTime? oldestFrameUtc, DateTime? newestFrameUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (frames.Count == 0)
            throw new ArgumentException("a quarantine capture needs at least one frame.", nameof(frames));
        if (freshDataFrames < 0 || freshDataFrames > frames.Count)
            throw new ArgumentOutOfRangeException(nameof(freshDataFrames), freshDataFrames, $"must be between 0 and the {frames.Count} frame(s) given.");
        long total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] f = frames[i] ?? throw new ArgumentException($"frame {i} is null.", nameof(frames));
            // Each frame must be a whole frame: SYNC 0xAA and a 16-bit FRAMESIZE equal to its length (C37.118.2 common
            // header). Anything else would make the file an unclean capture.
            if (f.Length < 16 || f[0] != 0xAA || ((f[2] << 8) | f[3]) != f.Length)
                throw new ArgumentException($"frame {i} ({f.Length} bytes) is not one whole C37.118 frame (SYNC 0xAA and a matching FRAMESIZE).", nameof(frames));
            total += f.Length;
        }

        RefuseIfInsideEvidenceLake(directory);
        Directory.CreateDirectory(directory);
        DateTime received = UtcTime.Normalise(receivedUtc);
        string stem = "pmu-live-" + received.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

        // The capture: CreateNew, so an existing quarantine file is never replaced; on a clash try -dup1, -dup2, ...
        string capturePath = "";
        FileStream? fs = null;
        for (int dup = 0; dup < 1000 && fs is null; dup++)
        {
            capturePath = Path.Combine(directory, stem + (dup == 0 ? "" : "-dup" + dup.ToString(CultureInfo.InvariantCulture)) + CaptureSuffix);
            try { fs = new FileStream(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(capturePath)) { fs = null; }
        }
        if (fs is null)
            throw new IOException($"no free quarantine capture name for '{stem}' in '{directory}' after 1000 tries.");
        byte[] sha;
        using (fs)
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            foreach (byte[] f in frames)
            {
                fs.Write(f, 0, f.Length);
                hash.AppendData(f);
            }
            sha = hash.GetHashAndReset();
        }

        string sidecarPath = capturePath + SidecarSuffix;
        // Phase 0.1: a named type (QuarantineSidecar, below) in place of the anonymous object, with the same members
        // in the same order and of the same types, so the sidecar bytes are unchanged.
        var doc = new QuarantineSidecar
        {
            schema = SidecarSchema,
            capture = Path.GetFileName(capturePath),
            sha256 = Convert.ToHexString(sha).ToLowerInvariant(),
            bytes = total,
            frames = frames.Count,
            freshDataFrames = freshDataFrames,
            source = source,
            receivedUtc = received.ToString("o", CultureInfo.InvariantCulture),
            horizonUtc = UtcTime.Normalise(horizonUtc).ToString("o", CultureInfo.InvariantCulture),
            horizonHours = ForwardInferenceGuard.HorizonHours,
            oldestFrameUtc = oldestFrameUtc is DateTime o ? UtcTime.Normalise(o).ToString("o", CultureInfo.InvariantCulture) : null,
            newestFrameUtc = newestFrameUtc is DateTime n ? UtcTime.Normalise(n).ToString("o", CultureInfo.InvariantCulture) : null,
            sourceClass = "telemetry",   // the interposer quarantine's vocabulary: NEVER "settled" / "metered"
            evidential = false,
            recorded = false,
            provenance = new QuarantineProvenance { tag = Tag.ToString(), note = "lowest input trust; never Recorded; see the note" },
            note = Note,
        };
        using (var sfs = new FileStream(sidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(sfs, doc, SidecarJson.QuarantineSidecar);
        return (capturePath, sidecarPath);
    }

    // Phase 0.1 (source-generated JSON): the sidecar document as named types. The property names ARE the JSON member
    // names (no naming policy applies to these options), so they are lower camelCase on purpose, exactly as the
    // anonymous object spelt them, and declared in the order the anonymous object wrote them.
    internal sealed class QuarantineSidecar
    {
        public required string schema { get; init; }
        public required string capture { get; init; }
        public required string sha256 { get; init; }
        public required long bytes { get; init; }
        public required int frames { get; init; }
        public required int freshDataFrames { get; init; }
        public required string source { get; init; }
        public required string receivedUtc { get; init; }
        public required string horizonUtc { get; init; }
        public required double horizonHours { get; init; }
        public required string? oldestFrameUtc { get; init; }
        public required string? newestFrameUtc { get; init; }
        public required string sourceClass { get; init; }
        public required bool evidential { get; init; }
        public required bool recorded { get; init; }
        public required QuarantineProvenance provenance { get; init; }
        public required string note { get; init; }
    }

    /// <summary>The sidecar provenance block: { tag, note }.</summary>
    internal sealed class QuarantineProvenance
    {
        public required string tag { get; init; }
        public required string note { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the live-quarantine sidecar and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PmuLiveQuarantine.QuarantineSidecar))]
internal sealed partial class PmuLiveQuarantineJsonContext : JsonSerializerContext;
