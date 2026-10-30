using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.SmallSignalExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacySmallSignalExport)
// and the namespace changed.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacySmallSignalExport
{
    public const string SchemaId = "gridsim-smallsignal/1";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

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

        var doc = new
        {
            schema = SchemaId,
            label,
            f0 = model.F0,
            inverterStatesIncluded = model.InverterStatesIncluded,
            states = model.States.Select(s => new
            {
                index = s.Index,
                kind = s.Kind.ToString(),
                ownerId = s.OwnerId,
                name = s.Name,
                synthetic = s.IsSynthetic,
            }).ToArray(),
            aMatrix = a,
            angleNodeBuses = model.AngleNodeBuses.ToArray(),
            kext,
            eigenvalues = modal.Modes.Select(m => new { re = m.Eigenvalue.Real, im = m.Eigenvalue.Imaginary }).ToArray(),
            dampingRatios = modal.Modes.Select(m => m.DampingRatio).ToArray(),
            frequenciesHz = modal.Modes.Select(m => m.FrequencyHz).ToArray(),
            participation = modal.Modes.Select(m => m.ParticipationByState.ToArray()).ToArray(),
            rhpEigenvalueCount = modal.RhpEigenvalueCount,
            minDampingRatio = modal.MinDampingRatio,
            // N180: whether `participation` holds participation factors or all-zero placeholders (defective A).
            participationAvailable = !modal.ParticipationUnavailable,
            participationUnavailableReason = modal.ParticipationUnavailableReason,   // null (omitted) when available
            exitCode,                                                                // null (omitted) unless given
        };
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <param name="exitCode">See <see cref="ToJson"/>.</param>
    public static void WriteJson(string path, string label, SmallSignalModel model, ModalResult modal, int? exitCode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(label, model, modal, exitCode));
    }
}
