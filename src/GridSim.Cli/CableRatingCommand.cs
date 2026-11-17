using GridSim.Core.Analysis.CableRating;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8 (cable thermal sizing): <c>gridsim cable-rating &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. The steady-state
/// IEC 60287 rating of one single-core cable laid direct in the ground (<see cref="Iec60287CableRating"/>), with the
/// nameplate rating kept as separate evidence and an explicit rating selection.
/// </summary>
internal static class CableRatingCommand
{
    private const string Verb = "cable-rating";

    private sealed record Request(int SchemaVersion, string Label, CableConstruction Cable, CableSystem System, BuriedInstallation Installation,
        ScreenLossInput ScreenLoss, RatingSelection Selection, NameplateRating? Nameplate = null);

    public static int Run(List<string> arguments) => SpecialistStudyCli.Run(Verb, arguments, inv =>
    {
        Request q = SpecialistStudyCli.Parse<Request>(inv.Bytes, r => r.SchemaVersion);
        if (string.IsNullOrWhiteSpace(q.Label)) throw new InvalidDataException("label must not be empty.");
        var request = new CableRatingRequest(q.Label, q.Cable, q.System, q.Installation, q.ScreenLoss, q.Nameplate, q.Selection);
        CableRatingResult result = Iec60287CableRating.Calculate(request);
        object json = new
        {
            schemaVersion = 1,
            study = Verb,
            provenance = new { studySha256 = SpecialistStudyCli.Hash(inv.Bytes), engine = GridSim.Core.BuildInfo.InformationalVersion },
            units = new { current = "A", resistance = "ohm/m", thermalResistance = "K.m/W", loss = "W/m", temperature = "C", diameter = "mm", depth = "m" },
            result,
        };
        return (json, result.Status == "EVALUATED" ? CliExit.Ok : CliExit.Verdict);
    });
}
