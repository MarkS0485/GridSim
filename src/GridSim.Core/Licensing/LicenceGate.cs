using System.IO;
using System.Security.Cryptography;

namespace GridSim.Core.Licensing;

/// <summary>The outcome of a licence check against one network model.</summary>
/// <param name="Allowed">True when the model may be loaded/solved under the active entitlement.</param>
/// <param name="Message">On denial, the user-facing reason (tier, limit, contact); empty when allowed.</param>
public readonly record struct GateResult(bool Allowed, string Message)
{
    public static GateResult Ok() => new(true, string.Empty);

    public static GateResult Deny(string message) => new(false, message);
}

/// <summary>
/// The single entry point every front-end (CLI, Web, WPF) shares for licence enforcement. It resolves
/// the active <see cref="Entitlement"/> once, offline, and answers "may this model be used?" against
/// the bus-count metric (the only licensed quantity, LICENSE.md §2).
///
/// Resolution precedence: an explicit token → the <c>GRIDSIM_LICENCE</c> environment variable → the
/// installed <c>%LOCALAPPDATA%\GridSim\licence.gridsim</c> file → the free Personal default. A key is
/// installed there by <c>gridsim --importLicence &lt;file.gridsim&gt;</c>. This mirrors the established
/// <c>--root ?? GDA_ROOT</c> flag-then-env pattern and the <c>%LOCALAPPDATA%\GridSim\</c> file convention
/// (<c>AppSettings</c>). All reads are best-effort: a missing or unreadable key never stops the software
/// starting - it runs at the free tier.
///
/// F27 (Ruling R1, 2026-10-04): a key is bound to the GridSim major version it was issued for (LICENSE.md section 1
/// sells a licence per major version; <see cref="Entitlement.MajorVersion"/>). A key for 1.x is accepted by every 1.x
/// build and refused by any other major version (2.x, and an older line too), exactly like any other rejected key:
/// resolution falls back to the free tier and <see cref="SourceNote"/> says why, naming both versions; installing such
/// a key fails with the same message. Legacy keys with no major version read as 1.x, so they keep working on 1.x.
/// </summary>
public sealed class LicenceGate
{
    /// <summary>Licensing contact, per LICENSE.md.</summary>
    public const string ContactEmail = "gdatooling@proton.me";

    /// <summary>The GridSim licence-file extension. A <c>.gridsim</c> file holds one <c>GSK1 …</c> key.</summary>
    public const string FileExtension = ".gridsim";

    /// <summary>The environment variable that can carry a licence key for a single run.</summary>
    public const string EnvVar = "GRIDSIM_LICENCE";

    /// <summary>The active, verified entitlement (or <see cref="Entitlement.FreeDefault"/>).</summary>
    public Entitlement Entitlement { get; }

    /// <summary>
    /// Non-null when a key was found but rejected (bad signature, wrong build, malformed) and we fell
    /// back to the free tier. Surfaced in <see cref="Describe"/> so a mistyped key is visible, not silent.
    /// </summary>
    public string? SourceNote { get; }

    /// <summary>The major version this gate enforces: the running build's (<see cref="BuildInfo.MajorVersion"/>), or a
    /// test's stand-in for another line.</summary>
    private readonly int _buildMajor;

    public LicenceGate(Entitlement entitlement, string? sourceNote = null)
        : this(entitlement, sourceNote, BuildInfo.MajorVersion)
    {
    }

    /// <summary>Test seam: a gate that behaves as a build of major version <paramref name="buildMajor"/> would.</summary>
    internal LicenceGate(Entitlement entitlement, string? sourceNote, int buildMajor)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        Entitlement = entitlement;
        SourceNote = sourceNote;
        _buildMajor = buildMajor;
    }

    /// <summary>The major version of the running build, which every licence key must have been issued for (F27).</summary>
    public static int BuildMajorVersion => BuildInfo.MajorVersion;

    /// <summary>
    /// F27: does a verified key's major version fit a build of major version <paramref name="buildMajor"/>? True when
    /// they are equal. Otherwise false, with a user-facing <paramref name="reason"/> naming the key's line, the build and
    /// the licence rule, for example: "this key was issued for GridSim 1.x and this build is major version 2 ...".
    /// </summary>
    public static bool FitsMajorVersion(Entitlement entitlement, int buildMajor, out string reason)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        if (entitlement.MajorVersion == buildMajor) { reason = string.Empty; return true; }
        reason = $"this key was issued for GridSim {entitlement.MajorVersion}.x and this build is major version {buildMajor} " +
                 $"(GridSim {buildMajor}.x); a licence covers every release of the major version it was issued for " +
                 $"(LICENSE.md section 1), so GridSim {buildMajor}.x needs a {buildMajor}.x key: {ContactEmail}";
        return false;
    }

    /// <summary>The machine-local licence file: <c>%LOCALAPPDATA%\GridSim\licence.gridsim</c>.</summary>
    public static string LicenceFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "licence" + FileExtension);

    /// <summary>Resolve the active entitlement using the standard precedence and the embedded public key.</summary>
    public static LicenceGate Resolve(string? explicitToken = null)
    {
        using ECDsa? verifier = LicenceKey.CreateEmbeddedVerifier();
        return Resolve(
            explicitToken,
            Environment.GetEnvironmentVariable(EnvVar),
            ReadFileToken(LicenceFilePath),
            verifier);
    }

    /// <summary>
    /// Resolution core with every input injected - used by the public <see cref="Resolve(string?)"/> and by
    /// tests (which supply their own verifier and controlled sources). The first non-empty source wins:
    /// if it verifies we use it; if it is present but rejected we note the reason and fall back to free
    /// rather than silently ignoring a key the user clearly intended to use.
    /// </summary>
    internal static LicenceGate Resolve(string? explicitToken, string? envToken, string? fileToken, ECDsa? verifier)
        => Resolve(explicitToken, envToken, fileToken, verifier, BuildInfo.MajorVersion);

    /// <summary>
    /// The resolution core with the build's major version injected too (F27), so tests can show a 1.x key refused by a
    /// 2.x build. A verified key for another major version is treated like any rejected key: free tier, reason noted.
    /// </summary>
    internal static LicenceGate Resolve(string? explicitToken, string? envToken, string? fileToken, ECDsa? verifier, int buildMajor)
    {
        foreach ((string? token, string source) in new[]
                 {
                     (explicitToken, "explicit key"),
                     (envToken, EnvVar),
                     (fileToken, "licence" + FileExtension),
                 })
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (verifier is null)
                return new LicenceGate(Entitlement.FreeDefault, "no public key embedded in this build; running at the free tier", buildMajor);
            if (!LicenceKey.TryParse(token, verifier, out Entitlement ent, out string reason))
                return new LicenceGate(Entitlement.FreeDefault, $"licence key from {source} rejected: {reason}", buildMajor);
            // F27: a genuine key for another major version is still refused (and, like any rejected key, the next
            // source is not tried: the key the user clearly meant to use is named, not silently skipped).
            if (!FitsMajorVersion(ent, buildMajor, out string majorReason))
                return new LicenceGate(Entitlement.FreeDefault, $"licence key from {source} rejected: {majorReason}", buildMajor);
            return new LicenceGate(ent, null, buildMajor);
        }
        return new LicenceGate(Entitlement.FreeDefault, null, buildMajor);
    }

    /// <summary>
    /// May a model of <paramref name="busCount"/> buses be used? Bundled networks (the shipped IEEE/CIGRE
    /// test cases and the synthetic GB network, LICENSE.md §1) are always allowed and never count. For a
    /// user model: a model above the tier's bus cap is denied. An EXPIRED paid licence falls back to the free
    /// Personal tier (R274 / Q7, Mark 2026-10-02: "always default back to a free tier"): a lapsed customer is never
    /// worse off than an unlicensed user, so a model within the free cap is allowed, and one above it is denied with
    /// a message naming the expiry and the free cap.
    /// </summary>
    public GateResult Check(int busCount, bool bundled)
    {
        if (bundled) return GateResult.Ok();

        // F27, belt and braces: Resolve never hands out a key for another major version, but a gate built directly
        // from such an entitlement must not honour it either. It gets the free tier's rules, like an expired key.
        // (The free default itself is never version-bound.)
        if (!ReferenceEquals(Entitlement, Entitlement.FreeDefault) && !FitsMajorVersion(Entitlement, _buildMajor, out string majorReason))
        {
            Entitlement free = Entitlement.FreeDefault;
            if (busCount > free.BusLimit)
                return GateResult.Deny(
                    $"Your {Entitlement.Tier.DisplayName()} licence does not apply: {majorReason}. The free {free.Tier.DisplayName()} " +
                    $"tier applies meanwhile: this network has {busCount:N0} buses and the free tier allows {free.BusLimit:N0}.");
            return GateResult.Ok();
        }

        // D-DOC-086 (October 2026): a key dated in the future does not apply yet. Its issue date was never checked, so
        // it applied at once; now it gets the free tier's rules until that day, exactly as an expired key does.
        if (Entitlement.IsNotYetValid)
        {
            Entitlement free = Entitlement.FreeDefault;
            if (busCount > free.BusLimit)
                return GateResult.Deny(
                    $"Your {Entitlement.Tier.DisplayName()} licence starts on {Entitlement.Issued:yyyy-MM-dd} (UTC) and does not " +
                    $"apply before then, so the free {free.Tier.DisplayName()} tier applies: this network has {busCount:N0} buses " +
                    $"and the free tier allows {free.BusLimit:N0}. Check the computer's date, or ask: {ContactEmail}");
            return GateResult.Ok();
        }

        if (Entitlement.IsExpired)
        {
            // The free tier's own rules, exactly as an unlicensed install gets them (Entitlement.FreeDefault).
            Entitlement free = Entitlement.FreeDefault;
            if (busCount > free.BusLimit)
                return GateResult.Deny(
                    $"Your {Entitlement.Tier.DisplayName()} licence expired on {Entitlement.Expires:yyyy-MM-dd}, so the " +
                    $"free {free.Tier.DisplayName()} tier applies: this network has {busCount:N0} buses and the free tier " +
                    $"allows {free.BusLimit:N0}. Renew or ask about a new term: {ContactEmail}");
            return GateResult.Ok();
        }

        if (!Entitlement.IsUnlimited && busCount > Entitlement.BusLimit)
            return GateResult.Deny(
                $"This network has {busCount:N0} buses; your {Entitlement.Tier.DisplayName()} licence allows " +
                $"{Entitlement.BusLimit:N0}. A larger or commercial licence removes the limit: {ContactEmail}");

        return GateResult.Ok();
    }

    /// <summary>Check a model and throw <see cref="LicenceException"/> on denial - for callers that hard-block.</summary>
    public void Enforce(int busCount, bool bundled)
    {
        GateResult r = Check(busCount, bundled);
        if (!r.Allowed) throw new LicenceException(r.Message);
    }

    /// <summary>N215: the ASCII field separator of the <see cref="Describe"/> status line (and the web footer).</summary>
    public const string Sep = " | ";

    /// <summary>
    /// GR-248: true when the free tier's rules apply although a key is installed: the key has expired, is dated in the
    /// future, or is for another major version (the same rule <see cref="Describe"/> uses for its limit). Every front
    /// end that shows the limit reads it from here, so a footer can never print the key's own limit while the free cap
    /// is enforced.
    /// </summary>
    public bool FreeRulesApply
    {
        get
        {
            Entitlement e = Entitlement;
            bool foreignMajor = !ReferenceEquals(e, Entitlement.FreeDefault) && !FitsMajorVersion(e, _buildMajor, out _);
            return foreignMajor || e.IsExpired || e.IsNotYetValid;
        }
    }

    /// <summary>One-line status for CLI <c>licence status</c> and the Web/WPF footers.</summary>
    public string Describe()
    {
        Entitlement e = Entitlement;
        string who = !string.IsNullOrEmpty(e.Org) ? e.Org
                   : !string.IsNullOrEmpty(e.Email) ? e.Email
                   : "unregistered";
        // D-DOC-152 (October 2026): the bus limit shown is the one Check actually applies. An expired key, a key dated in
        // the future (D-DOC-086) and a key for another major version (F27) all get the free tier's rules, but the line
        // used to print the key's own limit ("unlimited buses") next to EXPIRED. It now prints the free cap and says why.
        bool freeRulesApply = FreeRulesApply;
        string limit = freeRulesApply
            ? $"{Entitlement.FreeDefault.BusLimit:N0} buses (the free tier applies)"
            : e.IsUnlimited ? "unlimited buses" : $"{e.BusLimit:N0} buses";
        string expiry = e.IsNotYetValid ? $"NOT YET VALID (starts {e.Issued:yyyy-MM-dd})"
                      : e.Expires == DateOnly.MaxValue ? "no expiry"
                      : e.IsExpired ? $"EXPIRED {e.Expires:yyyy-MM-dd}"
                      : $"expires {e.Expires:yyyy-MM-dd}";
        // N215 (October 2026): the fields are separated by an ASCII " | " (Sep). They used to be separated by the non-ASCII
        // middle dot U+00B7, which a legacy console code page prints as "?" or mojibake; the fields and their order are
        // unchanged.
        string id = e.LicenceId is "free" or "" ? string.Empty : $"{Sep}id {e.LicenceId}";
        // F27: a key's line ("for GridSim 1.x"); the free default is not bound to one.
        string line = ReferenceEquals(e, Entitlement.FreeDefault) ? string.Empty : $"{Sep}for GridSim {e.MajorVersion}.x";
        string note = SourceNote is null ? string.Empty : $"  ({SourceNote})";
        return $"{e.Tier.DisplayName()}{Sep}{who}{Sep}{limit}{Sep}{expiry}{line}{id}{note}";
    }

    /// <summary>
    /// Validate a token against the embedded key and write it to <see cref="LicenceFilePath"/>. Throws
    /// <see cref="LicenceException"/> if the key is rejected or no public key is embedded. Returns the
    /// verified entitlement.
    /// </summary>
    public static Entitlement Install(string token) => Install(token, LicenceFilePath);

    /// <summary>
    /// <see cref="Install(string)"/> to an explicit licence file, so a test exercises the refusal paths against a temp
    /// file instead of the user's real installed licence (a regression that wrote before verifying would otherwise
    /// overwrite it). The key is verified completely before anything is written.
    /// </summary>
    public static Entitlement Install(string token, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        using ECDsa? verifier = LicenceKey.CreateEmbeddedVerifier()
            ?? throw new LicenceException("This build has no embedded public key, so it cannot validate a licence.");
        string bare = ExtractToken(token);
        if (!LicenceKey.TryParse(bare, verifier, out Entitlement ent, out string reason))
            throw new LicenceException($"Licence key rejected: {reason}");
        // F27: never install a key this build would then refuse at every start.
        if (!FitsMajorVersion(ent, BuildInfo.MajorVersion, out string majorReason))
            throw new LicenceException($"Licence key rejected: {majorReason}");

        string path = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, bare + Environment.NewLine);
        return ent;
    }

    /// <summary>
    /// Import a licence from a <c>.gridsim</c> file: read it, validate, and install it to
    /// <see cref="LicenceFilePath"/>. This is what <c>gridsim --importLicence &lt;file&gt;</c> runs. Throws
    /// <see cref="LicenceException"/> if the file cannot be read or the key is rejected.
    /// </summary>
    public static Entitlement ImportFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string content;
        try { content = File.ReadAllText(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new LicenceException($"Could not read licence file '{filePath}': {ex.Message}");
        }
        return Install(content);
    }

    private static string? ReadFileToken(string path)
    {
        try { return File.Exists(path) ? ExtractToken(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Pull the bare <c>GSK1 …</c> token out of a <c>.gridsim</c> file's contents: skip blank lines and
    /// <c>#</c> comment lines (so a file may carry a human-readable header), and return the key: the first key line
    /// joined with the key lines that follow it up to the next blank or comment line, so a key wrapped across lines
    /// reads as it does from <c>GRIDSIM_LICENCE</c> (GR-713: only the first line was kept, and a wrapped key failed as
    /// too short). A bare token with no comments passes through unchanged.
    /// </summary>
    internal static string ExtractToken(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var key = new System.Text.StringBuilder();
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            bool keyLine = line.Length > 0 && line[0] != '#';
            if (keyLine) key.Append(line);
            else if (key.Length > 0) break;   // the key ends at the first blank or comment line after it
        }
        return key.Length > 0 ? key.ToString() : content.Trim();
    }
}
