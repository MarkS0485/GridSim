using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.Journal;

/// <summary>One breaking change since RC-4, with the scope that says which runs it can explain and the effect it claims
/// on each quantity it moves.</summary>
public sealed record BreakingChange(string Id, string Version, string Kind, string Changelog, string Title,
    IReadOnlyList<string> Verbs, IReadOnlyList<string> Options, IReadOnlyList<string> InputFormats, string Condition, string Magnitude)
{
    /// <summary>The claimed effects. A field the change can move but no claim covers is not explained by it.</summary>
    public IReadOnlyList<BreakingChangeClaim> Claims { get; init; } = Array.Empty<BreakingChangeClaim>();
}

/// <summary>
/// What a breaking change claims about one class of field (the fields whose name matches <see cref="Field"/>, a pattern
/// with <c>*</c> and <c>|</c> between alternatives, ignoring case). One of:
/// <list type="bullet">
/// <item><c>bound</c>: the change moves each value by at most <see cref="MaxAbs"/> and/or <see cref="MaxRel"/>;</item>
/// <item><c>transform</c>: the change is exact, new = f x old with f in [<see cref="FactorMin"/>, <see cref="FactorMax"/>]
/// (equal for a single factor, such as -1 for a sign flip);</item>
/// <item><c>qualitative</c>: no bound is meaningful; the field makes the transition <see cref="From"/> to <see cref="To"/>
/// (patterns, such as a status or the exit code).</item>
/// </list>
/// </summary>
public sealed record BreakingChangeClaim(string Field, string Kind, double? MaxAbs, double? MaxRel, double? FactorMin, double? FactorMax,
    string? From, string? To)
{
    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The claim as text, for the report.</summary>
    public string Describe(string id) => Kind switch
    {
        "bound" => id + " " + string.Join(" ", new[] { MaxAbs is double a ? "abs<=" + R(a) : null, MaxRel is double r ? "rel<=" + R(r) : null }
            .Where(x => x is not null)),
        "transform" => id + (FactorMin == FactorMax ? " new=" + R(FactorMin!.Value) + "*old" : " new/old in [" + R(FactorMin!.Value) + "," + R(FactorMax!.Value) + "]"),
        _ => id + " " + From + "->" + To,
    };
}

/// <summary>The claim-check verdicts of a changed field (and of a changed entry: its worst field).</summary>
public static class ClaimVerdict
{
    public const string Explained = "EXPLAINED";
    public const string ExceedsClaim = "EXCEEDS-CLAIM";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Unexplained = "UNEXPLAINED";
    /// <summary>The field moved under a breaking change that has no bound (<see cref="UnboundedChanges"/>): expected, but
    /// no number can check it, so the job needs a person's review.</summary>
    public const string Unbounded = "UNBOUNDED";
    /// <summary>An UNBOUNDED job whose every moved field was acknowledged with <c>--accept Bn</c>, now or on an earlier run,
    /// and has moved no further since.</summary>
    public const string Reviewed = "REVIEWED";
}

/// <summary>The claim check of one changed field.</summary>
public static class ClaimCheck
{
    /// <summary>A field name against a claim pattern: <c>*</c> matches any run of characters, <c>|</c> separates
    /// alternatives, case is ignored.</summary>
    public static bool Matches(string pattern, string text)
    {
        foreach (string alt in pattern.Split('|'))
        {
            string rx = "^" + Regex.Escape(alt).Replace(@"\*", ".*", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    // A transform holds when every changed value's new/old ratio lies in [lo, hi], allowing 4 ulps of rounding in the
    // ratio (the recorded and the re-run value are each rounded once, and so is their quotient).
    private static bool InRange(double v, double lo, double hi) =>
        v >= lo - 4 * Math.Abs(lo) * MachineEpsilon && v <= hi + 4 * Math.Abs(hi) * MachineEpsilon;

    // 2^-52, the spacing of doubles at 1.
    private const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>
    /// Judge one changed field against the claims that cover it (from the breaking changes that can explain the run).
    /// All bounds: they add, and the field must lie within the sum. One transform: every new/old ratio must lie in its
    /// range. One qualitative claim: the field's one transition must match. A transform or transition together with any
    /// other claim does not compose: AMBIGUOUS. No covering claim: UNEXPLAINED.
    /// </summary>
    public static void JudgeField(FieldDifference f, IReadOnlyList<(string Id, BreakingChangeClaim Claim)> claims)
    {
        var inv = CultureInfo.InvariantCulture;
        f.Observed = f.Kind == "number"
            ? $"abs {ReplayReport.Number(f.MaxAbsDiff)} rel {ReplayReport.Number(f.MaxRelDiff)}"
              + (f.MinRatio is double lo ? $" new/old [{lo.ToString("R", inv)},{f.MaxRatio!.Value.ToString("R", inv)}]" : "")
              + (f.FromZero ? " (from 0)" : "")
            : f.Transition ?? f.Kind + " x" + f.Count.ToString(inv);
        if (claims.Count == 0) { f.Verdict = ClaimVerdict.Unexplained; return; }
        f.ExplainedBy = string.Join(";", claims.Select(c => c.Id).Distinct());
        f.Claimed = string.Join("; ", claims.Select(c => c.Claim.Describe(c.Id)));
        if (claims.All(c => c.Claim.Kind == "bound"))
        {
            double? abs = claims.Any(c => c.Claim.MaxAbs is not null) ? claims.Sum(c => c.Claim.MaxAbs ?? 0) : null;
            double? rel = claims.Any(c => c.Claim.MaxRel is not null) ? claims.Sum(c => c.Claim.MaxRel ?? 0) : null;
            if (claims.Count > 1)
                f.Claimed = string.Join(" ", new[] { abs is double a ? "abs<=" + a.ToString("R", inv) : null, rel is double r ? "rel<=" + r.ToString("R", inv) : null }
                    .Where(x => x is not null)) + " (sum of " + f.Claimed + ")";
            bool within = f.Kind == "number"
                && (abs is not double am || (f.MaxAbsDiff is double oa && oa <= am))
                && (rel is not double rm || (f.MaxRelDiff is double orl && orl <= rm));
            f.Verdict = within ? ClaimVerdict.Explained : ClaimVerdict.ExceedsClaim;
            return;
        }
        if (claims.Count > 1) { f.Verdict = ClaimVerdict.Ambiguous; return; }
        BreakingChangeClaim c = claims[0].Claim;
        if (c.Kind == "transform")
        {
            bool holds = f.Kind == "number" && !f.FromZero && f.MinRatio is double rlo && f.MaxRatio is double rhi
                && InRange(rlo, c.FactorMin!.Value, c.FactorMax!.Value) && InRange(rhi, c.FactorMin!.Value, c.FactorMax!.Value);
            f.Verdict = holds ? ClaimVerdict.Explained : ClaimVerdict.ExceedsClaim;
            return;
        }
        int k = f.Transition?.IndexOf("->", StringComparison.Ordinal) ?? -1;
        bool matches = k >= 0 && Matches(c.From!, f.Transition![..k]) && Matches(c.To!, f.Transition[(k + 2)..]);
        f.Verdict = matches ? ClaimVerdict.Explained : ClaimVerdict.ExceedsClaim;
    }
}

/// <summary>
/// The breaking-change map (<c>gridsim-breaking-changes/1</c>), embedded in the build: every BREAKING CHANGE since RC-4
/// with an ID (B1, B2, ...), the version it landed in and its scope. <c>breaking-change validate</c> uses it to say which
/// breaking change explains a result that moved.
/// </summary>
public sealed class BreakingChangeMap
{
    /// <summary>The resource name of the embedded map.</summary>
    public const string ResourceName = "GridSim.Core.breaking-changes.json";

    private BreakingChangeMap(IReadOnlyList<BreakingChange> entries, string sha256)
    {
        Entries = entries;
        Sha256 = sha256;
    }

    /// <summary>The entries in ID order (which is version order).</summary>
    public IReadOnlyList<BreakingChange> Entries { get; }

    /// <summary>SHA-256 of the map's bytes, recorded in every validate report.</summary>
    public string Sha256 { get; }

    private static readonly Lazy<BreakingChangeMap> Embedded = new(() => Parse(EmbeddedBytes()));

    /// <summary>The map compiled into this build.</summary>
    public static BreakingChangeMap Default => Embedded.Value;

    /// <summary>The embedded map's bytes.</summary>
    public static byte[] EmbeddedBytes()
    {
        using Stream s = typeof(BreakingChangeMap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("the breaking-change map is not embedded in this build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Read a map. Refuses a duplicate or out-of-order ID, or versions that do not ascend.</summary>
    public static BreakingChangeMap Parse(byte[] bytes)
    {
        using JsonDocument d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes));
        JsonElement r = d.RootElement;
        if (r.GetProperty("schema").GetString() != "gridsim-breaking-changes/1")
            throw new InvalidDataException("not a gridsim-breaking-changes/1 document.");
        var list = new List<BreakingChange>();
        static IReadOnlyList<string> Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();
        static double? D(JsonElement o, string n) => o.TryGetProperty(n, out JsonElement v) ? v.GetDouble() : null;
        static string? S(JsonElement o, string n) => o.TryGetProperty(n, out JsonElement v) ? v.GetString() : null;
        foreach (JsonElement e in r.GetProperty("entries").EnumerateArray())
        {
            JsonElement scope = e.GetProperty("scope");
            var claims = new List<BreakingChangeClaim>();
            if (e.TryGetProperty("claims", out JsonElement ce))
                foreach (JsonElement c in ce.EnumerateArray())
                {
                    double? factor = D(c, "factor");
                    var claim = new BreakingChangeClaim(c.GetProperty("field").GetString()!, c.GetProperty("kind").GetString()!,
                        D(c, "maxAbs"), D(c, "maxRel"), factor ?? D(c, "factorMin"), factor ?? D(c, "factorMax"), S(c, "from"), S(c, "to"));
                    bool ok = claim.Kind switch
                    {
                        "bound" => claim.MaxAbs is not null || claim.MaxRel is not null,
                        "transform" => claim.FactorMin is double lo && claim.FactorMax is double hi && lo <= hi,
                        "qualitative" => claim.From is not null && claim.To is not null,
                        _ => false,
                    };
                    if (!ok) throw new InvalidDataException($"{e.GetProperty("id").GetString()}: the claim on '{claim.Field}' is not a complete bound, transform or qualitative claim.");
                    claims.Add(claim);
                }
            list.Add(new BreakingChange(
                e.GetProperty("id").GetString()!, e.GetProperty("version").GetString()!, e.GetProperty("kind").GetString()!,
                e.GetProperty("changelog").GetString()!, e.GetProperty("title").GetString()!,
                Strings(scope.GetProperty("verbs")), Strings(scope.GetProperty("options")), Strings(scope.GetProperty("inputFormats")),
                scope.GetProperty("condition").GetString()!, e.GetProperty("magnitude").GetString()!) { Claims = claims });
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id != "B" + (i + 1).ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException($"breaking-change IDs must run B1, B2, ... in order; entry {i + 1} is {list[i].Id}.");
            if (i > 0 && VersionOrder.Compare(list[i - 1].Version, list[i].Version) > 0)
                throw new InvalidDataException($"breaking-change versions must ascend; {list[i].Id} ({list[i].Version}) comes after {list[i - 1].Version}.");
        }
        return new BreakingChangeMap(list, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// The IDs that can explain a changed re-run: recorded version &lt; Bk.version &lt;= build version, and the verb,
    /// a flag or an input format in Bk's scope. A <c>*</c> verb match is written <c>B2 (broad scope)</c>, a weak
    /// explanation. Empty when nothing explains it.
    /// </summary>
    public IReadOnlyList<string> Explain(string recordedVersion, string buildVersion, string verb, IReadOnlyList<string> argv,
        IReadOnlyList<string> inputFiles)
    {
        var ids = new List<string>();
        foreach (BreakingChange b in Entries)
        {
            if (!(VersionOrder.Compare(recordedVersion, b.Version) < 0 && VersionOrder.Compare(b.Version, buildVersion) <= 0)) continue;
            bool exact = b.Verbs.Contains(verb)
                || argv.Any(a => b.Options.Any(o => a == o || a.StartsWith(o + "=", StringComparison.Ordinal)))
                || inputFiles.Any(f => b.InputFormats.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
            if (exact) ids.Add(b.Id);
            else if (b.Verbs.Contains("*")) ids.Add(b.Id + " (broad scope)");
        }
        return ids;
    }

    /// <summary>
    /// RC5 (2.1.122): the IDs among <paramref name="candidates"/> (as <see cref="Explain"/> gives them) whose qualitative
    /// claim on the field <c>(exit code)</c> covers the transition <paramref name="recordedExit"/> to
    /// <paramref name="replayExit"/>, in ID order. Breaking changes with no bound (<see cref="UnboundedChanges.IsUnbounded"/>)
    /// are left out, as they are in the claim check. <c>breaking-change validate</c> asks this before it reports a re-run
    /// that newly exits 1 or 2 as CANNOT-REPLAY: a breaking change that claims such a refusal explains it instead.
    /// </summary>
    public IReadOnlyList<string> ExitClaims(IReadOnlyList<string> candidates, int recordedExit, int replayExit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ids = candidates.Select(c => c.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);
        string from = recordedExit.ToString(CultureInfo.InvariantCulture), to = replayExit.ToString(CultureInfo.InvariantCulture);
        return Entries.Where(b => ids.Contains(b.Id) && !UnboundedChanges.IsUnbounded(b)
                && b.Claims.Any(c => c.Kind == "qualitative" && ClaimCheck.Matches(c.Field, "(exit code)")
                    && ClaimCheck.Matches(c.From!, from) && ClaimCheck.Matches(c.To!, to)))
            .Select(b => b.Id).ToList();
    }

    /// <summary>
    /// RC5 (2.1.122): fill a re-run that a breaking change claims as a refusal (<see cref="ExitClaims"/> not empty): CHANGED,
    /// with the single field <c>(exit code)</c> and its transition. The recorded outputs are not compared, since the claim
    /// is that the run is now refused and writes no result. The claim check (<see cref="Judge"/>) then explains it.
    /// </summary>
    public static void MarkClaimedRefusal(ReplayOutcome o, int recordedExit, int replayExit, IReadOnlyList<string> claimedBy, string firstErrorLine)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(claimedBy);
        ArgumentNullException.ThrowIfNull(firstErrorLine);
        o.Fields.Clear();
        o.Fields.Add(new FieldDifference { File = "(run)", Field = "(exit code)", Kind = "exit", MinAgreeingBits = 0, Count = 1,
            Transition = string.Create(CultureInfo.InvariantCulture, $"{recordedExit}->{replayExit}") });
        o.Status = ReplayStatus.Changed;
        o.Reason = string.Create(CultureInfo.InvariantCulture, $"exit {recordedExit} -> {replayExit}, a refusal claimed by {string.Join(", ", claimedBy)}; outputs not compared")
            + (firstErrorLine.Length > 0 ? ": " + firstErrorLine : "");
    }

    /// <summary>
    /// The claim check of a CHANGED outcome. <paramref name="candidates"/> are the IDs <see cref="Explain"/> gives. Each
    /// changed field is judged against the claims of those IDs that cover it (<see cref="ClaimCheck.JudgeField"/>). The
    /// outcome takes the worst verdict of its fields (UNEXPLAINED, then EXCEEDS-CLAIM, then AMBIGUOUS, then EXPLAINED)
    /// and lists the IDs its fields rest on.
    /// </summary>
    public void Judge(ReplayOutcome o, IReadOnlyList<string> candidates)
    {
        var ids = candidates.Select(c => c.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);
        var entries = Entries.Where(b => ids.Contains(b.Id)).ToList();
        foreach (FieldDifference f in o.Fields)
            ClaimCheck.JudgeField(f, entries.SelectMany(b => b.Claims.Where(c => ClaimCheck.Matches(c.Field, f.Field)).Select(c => (b.Id, c))).ToList());
        string[] order = [ClaimVerdict.Unexplained, ClaimVerdict.ExceedsClaim, ClaimVerdict.Ambiguous, ClaimVerdict.Explained];
        o.Verdict = o.Fields.Count == 0 ? ClaimVerdict.Unexplained : order.First(v => o.Fields.Any(f => f.Verdict == v));
        o.ExplainedBy = o.Fields.SelectMany(f => f.ExplainedBy.Split(';', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal)
            .OrderBy(x => int.Parse(x[1..], CultureInfo.InvariantCulture)).ToList();
    }
}

/// <summary>Release numbers compared part by part as numbers: 2.1.090 &lt; 2.1.103. A leading <c>v</c> and a
/// trailing <c>-RC-n</c> (or any <c>-</c> or <c>+</c> suffix) are ignored.</summary>
public static class VersionOrder
{
    /// <summary>Parse a release number, or null when it is not one.</summary>
    public static long[]? Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        string v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        int cut = v.IndexOfAny(['-', '+']);
        if (cut >= 0) v = v[..cut];
        string[] parts = v.Split('.');
        var nums = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        return nums;
    }

    /// <summary>Compare two release numbers; one that does not parse sorts first.</summary>
    public static int Compare(string a, string b)
    {
        long[]? x = Parse(a), y = Parse(b);
        if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }
}
