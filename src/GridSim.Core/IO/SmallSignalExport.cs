using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Dynamics.SmallSignal;

namespace GridSim.Core.IO;

/// <summary>
/// Serialises a small-signal / modal analysis to the <c>gridsim-smallsignal/1</c> document: the real state
/// matrix A (the eigen input), the kind-tagged states (so a downstream check can assert the real/synthetic
/// separation survives the wire format), the synchronising matrix, and the computed eigenvalues, damping
/// ratios, modal frequencies, participation factors and RHP count. The sidecar
/// (<c>crossval_smallsignal.py</c>) recomputes the eigenvalues from <c>aMatrix</c> and diffs.
///
/// <para>N180 (October 2026, additive fields; every existing field and value is unchanged): the document now says
/// whether the participation factors exist. <c>participationAvailable</c> is false when the modal decomposition found
/// the state matrix defective (<see cref="ModalResult.ParticipationUnavailable"/>); the <c>participation</c> rows are
/// then all-zero placeholders, NOT participation, and <c>participationUnavailableReason</c> carries the
/// decomposition's own explanation. Before N180 the zeros were written with no flag, so a reader could not tell them
/// from real participation. A caller that decides an exit code (the CLI) can also pass it, and it is written as
/// <c>exitCode</c>, so the file carries the verdict its run ended with (omitted when not given).</para>
/// </summary>
public static class SmallSignalExport
{
    public const string SchemaId = "gridsim-smallsignal/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated SmallSignalExportJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly SmallSignalExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    /// <param name="exitCode">N180: the exit code the calling run ends with (the CLI: 1 when any eigenvalue is in the
    /// right half-plane, else 0), written as <c>exitCode</c>; null (the default) leaves the field out.</param>
    public static string ToJson(string label, SmallSignalModel model, ModalResult modal, int? exitCode = null)
    {
        int n = model.Order;
        var a = new double[n][];
        for (int i = 0; i < n; i++)
        {
            a[i] = new double[n];
            for (int j = 0; j < n; j++) a[i][j] = model.A[i, j];
        }
        int kn = model.Kext.GetLength(0);
        var kext = new double[kn][];
        for (int i = 0; i < kn; i++)
        {
            kext[i] = new double[kn];
            for (int j = 0; j < kn; j++) kext[i][j] = model.Kext[i, j];
        }

        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new SmallSignalDoc
        {
            schema = SchemaId,
            label = label,
            f0 = model.F0,
            inverterStatesIncluded = model.InverterStatesIncluded,
            states = model.States.Select(s => new StateRow
            {
                index = s.Index,
                kind = s.Kind.ToString(),
                ownerId = s.OwnerId,
                name = s.Name,
                synthetic = s.IsSynthetic,
            }).ToArray(),
            aMatrix = a,
            angleNodeBuses = model.AngleNodeBuses.ToArray(),
            kext = kext,
            eigenvalues = modal.Modes.Select(m => new EigenvalueRow { re = m.Eigenvalue.Real, im = m.Eigenvalue.Imaginary }).ToArray(),
            dampingRatios = modal.Modes.Select(m => m.DampingRatio).ToArray(),
            frequenciesHz = modal.Modes.Select(m => m.FrequencyHz).ToArray(),
            participation = modal.Modes.Select(m => m.ParticipationByState.ToArray()).ToArray(),
            rhpEigenvalueCount = modal.RhpEigenvalueCount,
            minDampingRatio = modal.MinDampingRatio,
            // N180: whether `participation` holds participation factors or all-zero placeholders (defective A).
            participationAvailable = !modal.ParticipationUnavailable,
            participationUnavailableReason = modal.ParticipationUnavailableReason,   // null (omitted) when available
            exitCode = exitCode,                                                    // null (omitted) unless given
        };
        return JsonSerializer.Serialize(doc, Json.SmallSignalDoc);
    }

    /// <param name="exitCode">See <see cref="ToJson"/>.</param>
    public static void WriteJson(string path, string label, SmallSignalModel model, ModalResult modal, int? exitCode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(label, model, modal, exitCode));
    }

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types (the type decides how a number is written). JsonSourceGenEquivalenceTests holds the bytes equal to
    // the old reflection-built document.

    /// <summary>The whole <c>gridsim-smallsignal/1</c> document.</summary>
    internal sealed class SmallSignalDoc
    {
        public required string schema { get; init; }
        public required string label { get; init; }
        public required double f0 { get; init; }
        public required bool inverterStatesIncluded { get; init; }
        public required StateRow[] states { get; init; }
        public required double[][] aMatrix { get; init; }
        public required int[] angleNodeBuses { get; init; }
        public required double[][] kext { get; init; }
        public required EigenvalueRow[] eigenvalues { get; init; }
        public required double[] dampingRatios { get; init; }
        public required double[] frequenciesHz { get; init; }
        public required double[][] participation { get; init; }
        public required int rhpEigenvalueCount { get; init; }
        public required double minDampingRatio { get; init; }
        public required bool participationAvailable { get; init; }
        public required string? participationUnavailableReason { get; init; }
        public required int? exitCode { get; init; }
    }

    /// <summary>One state label: { index, kind, ownerId, name, synthetic }.</summary>
    internal sealed class StateRow
    {
        public required int index { get; init; }
        public required string kind { get; init; }
        public required int ownerId { get; init; }
        public required string name { get; init; }
        public required bool synthetic { get; init; }
    }

    /// <summary>One eigenvalue: { re, im }.</summary>
    internal sealed class EigenvalueRow
    {
        public required double re { get; init; }
        public required double im { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the small-signal export document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SmallSignalExport.SmallSignalDoc))]
internal sealed partial class SmallSignalExportJsonContext : JsonSerializerContext;
