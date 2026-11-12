using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>What <see cref="SessionCookieSettings.Judge"/> decides about a sign-in cookie that is being validated.</summary>
public enum CookieVerdict
{
    /// <summary>The sign-in is inside the absolute cap; carry on.</summary>
    Accept = 0,
    /// <summary>The sign-in is older than the absolute cap (or its stamp is unreadable); reject it and sign out.</summary>
    Expired = 1,
    /// <summary>The cookie carries no sign-in stamp (minted by another app, or before stamps existed); stamp it now.</summary>
    NeedsStamp = 2,
}

/// <summary>
/// GR-732: how long a sign-in cookie lives. Two settings, read and checked ONCE at start-up (a malformed value stops the
/// host naming the setting, as every other web setting does):
/// <list type="bullet">
/// <item><c>Auth:SlidingHours</c> (default 8): the idle window. Each use past half the window renews the cookie for another
/// window, so a visitor idle for longer than this must sign in again.</item>
/// <item><c>Auth:AbsoluteDays</c> (default 14): the hard cap from sign-in. A cookie older than this is rejected even if
/// it was renewed every hour, because the sign-in time is stored inside the cookie and a renewal does not move it
/// (the cookie's own IssuedUtc DOES move on renewal, so it is not used for the cap, except to stamp a cookie that has
/// no sign-in time yet).</item>
/// </list>
/// Roles are carried in the sign-in ticket the authority minted, so a role removed at the authority takes effect at the
/// next sign-in: at most the sliding window when idle, or the absolute cap in all.
/// </summary>
public sealed class SessionCookieSettings
{
    /// <summary>Configuration key of the idle window in hours (environment variable <c>Auth__SlidingHours</c>).</summary>
    public const string SlidingHoursSetting = "Auth:SlidingHours";
    /// <summary>Configuration key of the absolute cap in days (<c>Auth__AbsoluteDays</c>).</summary>
    public const string AbsoluteDaysSetting = "Auth:AbsoluteDays";

    /// <summary>Default idle window: 8 hours, one working day.</summary>
    public const int DefaultSlidingHours = 8;
    /// <summary>Default absolute cap: 14 days (the old fixed sliding window).</summary>
    public const int DefaultAbsoluteDays = 14;

    /// <summary>Largest idle window accepted (one year); a bound that catches a stray extra digit.</summary>
    public const int MaxSlidingHours = 24 * 365;
    /// <summary>Largest absolute cap accepted (one year).</summary>
    public const int MaxAbsoluteDays = 365;

    /// <summary>The key, in the cookie's authentication properties, that holds the sign-in time (round-trip UTC text).</summary>
    public const string SignedInItem = "gridsim.signedInUtc";

    /// <summary>The idle window.</summary>
    public TimeSpan Sliding { get; }

    /// <summary>The absolute cap from sign-in.</summary>
    public TimeSpan Absolute { get; }

    private SessionCookieSettings(TimeSpan sliding, TimeSpan absolute)
    {
        Sliding = sliding;
        Absolute = absolute;
    }

    /// <summary>The settings for the given whole hours and days, with the same checks as <see cref="Read"/>.</summary>
    public static SessionCookieSettings Create(int slidingHours, int absoluteDays)
    {
        if (slidingHours < 1 || slidingHours > MaxSlidingHours)
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {SlidingHoursSetting} must be a whole number of hours from 1 to {MaxSlidingHours}; it is {slidingHours}.");
        if (absoluteDays < 1 || absoluteDays > MaxAbsoluteDays)
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {AbsoluteDaysSetting} must be a whole number of days from 1 to {MaxAbsoluteDays}; it is {absoluteDays}.");
        TimeSpan sliding = TimeSpan.FromHours(slidingHours);
        TimeSpan absolute = TimeSpan.FromDays(absoluteDays);
        if (absolute < sliding)
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {AbsoluteDaysSetting} ({absoluteDays} days) must not be shorter than {SlidingHoursSetting} ({slidingHours} hours); "
                + "the absolute cap is the longest a sign-in can last, the sliding window is the longest it can idle.");
        return new SessionCookieSettings(sliding, absolute);
    }

    /// <summary>Read both settings (defaults when unset) and check them; throws <see cref="InvalidOperationException"/>
    /// naming the setting on a malformed value or a cap shorter than the window.</summary>
    public static SessionCookieSettings Read(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        int hours = ReadWhole(config, SlidingHoursSetting, DefaultSlidingHours);
        int days = ReadWhole(config, AbsoluteDaysSetting, DefaultAbsoluteDays);
        return Create(hours, days);
    }

    private static int ReadWhole(IConfiguration config, string key, int fallback)
    {
        string? raw = config[key];
        if (raw is null) return fallback;
        string text = raw.Trim();
        // Digits only: no sign, no decimal point, no thousands separator, so "8h" or "1.5" or "-3" are all refused.
        if (text.Length == 0 || text.Length > 9
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {key} must be a whole number; it is '{raw}'.");
        return value;
    }

    /// <summary>The text stored under <see cref="SignedInItem"/> for a sign-in at <paramref name="signedInUtc"/>.</summary>
    public static string FormatStamp(DateTimeOffset signedInUtc) =>
        signedInUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Decide what to do with a cookie. Pure: the caller passes the clock. A missing stamp is
    /// <see cref="CookieVerdict.NeedsStamp"/>; an unreadable one is <see cref="CookieVerdict.Expired"/> (fail closed);
    /// a sign-in older than <paramref name="cap"/> is <see cref="CookieVerdict.Expired"/>; anything else is accepted. A
    /// stamp in the future (clock skew between instances sharing a ring) is accepted: the cookie is encrypted, so it
    /// cannot have been forged, and rejecting would sign visitors out on a small skew.
    /// </summary>
    public static CookieVerdict Judge(string? stamp, DateTimeOffset now, TimeSpan cap)
    {
        if (stamp is null) return CookieVerdict.NeedsStamp;
        if (!DateTimeOffset.TryParseExact(stamp, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset signedIn))
            return CookieVerdict.Expired;
        return now - signedIn > cap ? CookieVerdict.Expired : CookieVerdict.Accept;
    }

    /// <summary>
    /// The cookie handler's <c>OnValidatePrincipal</c> for the absolute cap. A cookie past the cap (or with an unreadable
    /// stamp) is rejected and signed out. A cookie with no stamp is stamped from its <c>IssuedUtc</c> (so the shared estate
    /// cookie keeps working, the cap counting from that first issue) and re-issued so the stamp sticks; one with no
    /// <c>IssuedUtc</c> either cannot have its age known and is rejected.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context, TimeSpan cap, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        AuthenticationProperties props = context.Properties;
        props.Items.TryGetValue(SignedInItem, out string? stamp);
        CookieVerdict verdict = Judge(stamp, clock.GetUtcNow(), cap);
        if (verdict == CookieVerdict.NeedsStamp)
        {
            if (props.IssuedUtc is not DateTimeOffset issued)
                verdict = CookieVerdict.Expired;   // age unknowable: refuse rather than let it live for ever
            else
            {
                props.Items[SignedInItem] = FormatStamp(issued);
                context.ShouldRenew = true;        // write the stamp back into the cookie
                verdict = Judge(props.Items[SignedInItem], clock.GetUtcNow(), cap);
            }
        }
        if (verdict == CookieVerdict.Expired)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
