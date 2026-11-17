using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Core.Analysis.PlanningStudies;

/// <summary>
/// The P7 timestamp convention. An instant is ISO 8601 with seconds, up to seven fractional digits (100 ns ticks) and
/// an explicit UTC offset: <c>Z</c> or <c>+hh:mm</c> / <c>-hh:mm</c>. A local wall-clock time without an offset is
/// refused, because around a clock change it names zero or two instants. Every instant is normalised to UTC.
/// </summary>
public static partial class PlanningTime
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static DateTimeOffset Parse(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
            throw new PlanningManifestException($"{what}: a timestamp is required.");
        if (!Pattern().IsMatch(text))
            throw new PlanningManifestException(
                $"{what}: '{text}' is not an ISO 8601 instant with an explicit UTC offset (for example " +
                "2027-06-01T00:00:00Z or 2027-06-01T01:00:00+01:00). A local time without an offset is ambiguous " +
                "across clock changes and is refused.");
        if (!DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset value))
            throw new PlanningManifestException($"{what}: '{text}' is not a valid calendar instant.");
        return value.ToUniversalTime();
    }

    /// <summary>Canonical UTC text with all seven fractional digits.</summary>
    public static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Identity derivations. All text is UTF-8 with LF line ends; digests are lowercase hex SHA-256.</summary>
public static class PlanningFingerprints
{
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The digest of a consumed-file manifest: one line per file, path TAB sha256 LF, in load order. This is
    /// the same encoding transmission-security uses for a native case, so one <c>case.sha256</c> serves both.</summary>
    public static string Bundle(IEnumerable<PlanningInputFile> files) =>
        Sha256(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n")));

    /// <summary>The master identity: the bundle digest bound to its interpretation options.</summary>
    public static string Master(string bundleSha256, PlanningInterpretation interpretation) =>
        Sha256("gridsim-planning-master/1\nbundle\t" + bundleSha256.ToLowerInvariant() + "\nloader\t" +
            interpretation.Loader + "\ndispatch\t" + (interpretation.Dispatch ? "true" : "false") + "\n");

    /// <summary>The derivation identity of a resolved case: the master plus the applied operations in order with
    /// their replacement values. Two snapshots with the same active changes share it.</summary>
    public static string Derivation(string masterSha256, IEnumerable<PlanningAuditEntry> applied) =>
        Sha256("gridsim-planning-case/1\nmaster\t" + masterSha256 + "\n" + string.Concat(applied.Select(a =>
            a.Project + "/" + a.Operation + "\t" + a.Target + "\t" + a.Field + "\t" + a.Effective + "\n")));

    /// <summary>The request fingerprint of a job: manifest version, job ID, snapshot instant, the study's canonical
    /// parameters and the derivation of the case it runs on.</summary>
    public static string Request(string jobId, PlanningSnapshot snapshot, PlanningStudyDefinition study, string derivationSha256) =>
        Sha256(PlanningManifestVersion.Current + "\njob\t" + jobId + "\ninstant\t" + PlanningTime.Format(snapshot.Instant) +
            "\n" + study.Canonical() + "\nderivation\t" + derivationSha256 + "\n");

    /// <summary>The input fingerprint of a job: the master identity and the resolved case content.</summary>
    public static string Input(string masterSha256, string caseSha256) =>
        Sha256("master\t" + masterSha256 + "\ncase\t" + caseSha256 + "\n");
}
