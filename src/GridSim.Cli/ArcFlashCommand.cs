using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.ArcFlash;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8 (industrial arc flash): <c>gridsim arc-flash &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. One piece of
/// equipment to IEEE 1584-2018 (<see cref="Ieee1584ArcFlash"/>). The bolted fault current is either declared or read from
/// the incumbent IEC 60909 solver for a case bus (outputs only; the <c>iec60909</c> verb and its duties are untouched).
/// </summary>
internal static class ArcFlashCommand
{
    private const string Verb = "arc-flash";

    private sealed record EquipmentInput(double SystemVoltageKv, ElectrodeConfiguration Configuration, double GapMm, double WorkingDistanceMm,
        double FrequencyHz, int Phases, ArcFlashEnclosure? Enclosure = null);
    private sealed record CaseInput(string Sha256, string? Path = null, string? BuiltIn = null);
    private sealed record Iec60909Input(CaseInput Case, int Bus, ShortCircuitObjective Objective);
    private sealed record BoltedFaultJson(double? Ka = null, string? Source = null, Iec60909Input? Iec60909 = null);
    private sealed record Request(int SchemaVersion, string Label, EquipmentInput Equipment, BoltedFaultJson BoltedFault, ClearingTimeInput ClearingTime);

    /// <summary>Largest relative difference accepted between the IEC 60909 bus base voltage and the equipment voltage.</summary>
    public const double VoltageMatchTolerance = 0.005;

    public static int Run(List<string> arguments, Func<string, GridModel> load) => SpecialistStudyCli.Run(Verb, arguments, inv =>
    {
        Request q = SpecialistStudyCli.Parse<Request>(inv.Bytes, r => r.SchemaVersion);
        if (string.IsNullOrWhiteSpace(q.Label)) throw new InvalidDataException("label must not be empty.");
        BoltedFaultInput fault;
        object? caseProvenance = null;
        if (q.BoltedFault.Iec60909 is Iec60909Input sc)
        {
            if (q.BoltedFault.Ka is not null || q.BoltedFault.Source is not null)
                throw new InvalidDataException("boltedFault: give either ka and source, or iec60909, not both.");
            (GridModel model, string identity, string kind) = LoadCase(sc.Case, inv.InputPath, load);
            fault = Ieee1584ArcFlash.FromIec60909(model, sc.Bus, sc.Objective);
            double busKv = fault.Iec60909!.BusBaseKv;
            if (!(busKv > 0) || Math.Abs(busKv - q.Equipment.SystemVoltageKv) > VoltageMatchTolerance * q.Equipment.SystemVoltageKv)
                throw new InvalidDataException($"The IEC 60909 fault bus {sc.Bus} has base voltage {busKv:R} kV, which does not match the equipment " +
                    $"voltage {q.Equipment.SystemVoltageKv:R} kV (tolerance {VoltageMatchTolerance:P1}); the fault duty would be for another voltage level.");
            caseProvenance = new { kind, caseSha256 = identity };
        }
        else
        {
            if (q.BoltedFault.Ka is not double ka || string.IsNullOrWhiteSpace(q.BoltedFault.Source))
                throw new InvalidDataException("boltedFault: give ka and source (where the current came from), or iec60909.");
            fault = new BoltedFaultInput(ka, q.BoltedFault.Source);
        }
        var request = new ArcFlashRequest(q.Label, q.Equipment.SystemVoltageKv, q.Equipment.Configuration, q.Equipment.GapMm,
            q.Equipment.WorkingDistanceMm, q.Equipment.Enclosure, fault, q.ClearingTime, q.Equipment.FrequencyHz, q.Equipment.Phases);
        ArcFlashResult result = Ieee1584ArcFlash.Calculate(request);
        object json = new
        {
            schemaVersion = 1,
            study = Verb,
            provenance = new { studySha256 = SpecialistStudyCli.Hash(inv.Bytes), faultCase = caseProvenance, engine = GridSim.Core.BuildInfo.InformationalVersion },
            units = new { current = "kA", time = "ms", distance = "mm", energy = "J/cm2 and cal/cm2 (1 cal = 4.184 J)" },
            result,
            publishedExample = "IEEE 1584-2018 Annex D.1 and D.2 values (as transcribed in the arcflash library test suite) are checked by the GridSim test suite, not at run time.",
        };
        return (json, result.Status == "EVALUATED" ? CliExit.Ok : CliExit.Verdict);
    });

    // v1 accepts a built-in case or a single case file (MATPOWER .m, PSS/E .raw, UCTE .uct) bound to its exact bytes.
    private static (GridModel Model, string Identity, string Kind) LoadCase(CaseInput c, string studyPath, Func<string, GridModel> load)
    {
        if ((c.Path is null) == (c.BuiltIn is null)) throw new InvalidDataException("boltedFault.iec60909.case must give exactly one of path or builtIn.");
        if (c.Path is string relative)
        {
            string path = System.IO.Path.GetFullPath(relative, System.IO.Path.GetDirectoryName(studyPath)!);
            if (Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("boltedFault.iec60909.case.path: v1 takes a single case file (.m, .raw or .uct); a native JSON case is not supported.");
            byte[] before = File.ReadAllBytes(path);
            string identity = SpecialistStudyCli.Hash(before);
            Require(c.Sha256, identity);
            GridModel model = load(path);
            if (SpecialistStudyCli.Hash(File.ReadAllBytes(path)) != identity) throw new IOException("The case file changed during loading.");
            return (model, identity, "file sha256");
        }
        string name = c.BuiltIn!;
        if (!BuiltInCases.Names.Contains(name.ToLowerInvariant())) throw new UnknownCaseException($"'{name}' is not a built-in case name.");
        GridModel builtIn = load(name);
        string id = SpecialistStudyCli.Hash(Encoding.UTF8.GetBytes(GridSim.Core.BuildInfo.InformationalVersion + "\n" +
            JsonSerializer.Serialize(builtIn, SpecialistStudyCli.ModelJson)));
        Require(c.Sha256, id);
        return (builtIn, id, "built-in: sha256 of build identity and serialised model");
    }

    private static void Require(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Case SHA256 mismatch. Resolved identity: " + actual);
    }
}
