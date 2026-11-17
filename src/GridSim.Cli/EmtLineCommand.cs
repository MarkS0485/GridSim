using GridSim.Core.Analysis.EmtLineStudy;
using GridSim.Core.Dynamics.Emt;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8 (EMT): <c>gridsim emt-line &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. A single constant-parameter lossy
/// line impulse study (<see cref="EmtLineStudyRunner"/>) on the incumbent EMT engine, with analytic comparison and a
/// timestep and section refinement grid. The existing <c>emt</c> verb is untouched.
/// </summary>
internal static class EmtLineCommand
{
    private const string Verb = "emt-line";

    private sealed record LineInput(double LengthM, double ROhmPerM, double LHenryPerM, double CFaradPerM, double GSiemensPerM);
    private sealed record SourceInput(LineSourceKind Kind, double AmplitudeV, bool Matched, double? ResistanceOhm = null,
        double? AlphaPerS = null, double? BetaPerS = null);
    private sealed record TerminationInput(LineTerminationKind Kind, double? Value = null);
    private sealed record IntegrationInput(double DtSeconds, double DurationSeconds);
    private sealed record RefinementInput(int[] TimestepDivisors, int[] SegmentCounts);
    private sealed record OutputInput(int SampleEvery);
    private sealed record Request(int SchemaVersion, string Label, LineInput Line, int Segments, SourceInput Source,
        TerminationInput Termination, IntegrationInput Integration, RefinementInput Refinement, OutputInput Output);

    public static int Run(List<string> arguments) => SpecialistStudyCli.Run(Verb, arguments, inv =>
    {
        Request q = SpecialistStudyCli.Parse<Request>(inv.Bytes, r => r.SchemaVersion);
        if (q.Source.Matched == (q.Source.ResistanceOhm is not null))
            throw new InvalidDataException("source: give either matched: true or matched: false with resistanceOhm.");
        if (string.IsNullOrWhiteSpace(q.Label)) throw new InvalidDataException("label must not be empty.");
        var request = new EmtLineStudyRequest(
            new LossyLineParameters(q.Line.LengthM, q.Line.ROhmPerM, q.Line.LHenryPerM, q.Line.CFaradPerM, q.Line.GSiemensPerM),
            q.Segments,
            new LineSource(q.Source.Kind, q.Source.AmplitudeV, q.Source.ResistanceOhm, q.Source.AlphaPerS, q.Source.BetaPerS),
            new LineTermination(q.Termination.Kind, q.Termination.Value),
            q.Integration.DtSeconds, q.Integration.DurationSeconds, q.Output.SampleEvery,
            q.Refinement.TimestepDivisors, q.Refinement.SegmentCounts);
        EmtLineStudyResult result = EmtLineStudyRunner.Run(request);
        object json = new
        {
            schemaVersion = 1,
            study = Verb,
            label = q.Label,
            provenance = new { studySha256 = SpecialistStudyCli.Hash(inv.Bytes), engine = GridSim.Core.BuildInfo.InformationalVersion },
            stepOrder = EmtLineStudyRunner.StepOrder,
            effectiveOptions = request,
            result,
            comparator = "unverified: no PSCAD or EMTP run; analytic evidence only",
        };
        return (json, CliExit.Ok);
    });
}
