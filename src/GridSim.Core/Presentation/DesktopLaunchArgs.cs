using System.Globalization;

namespace GridSim.Core.Presentation;

/// <summary>
/// The desktop app's (GridSim.Wpf) launch arguments, read and checked in one place so the rules can be unit-tested
/// without a window (D-DOC-074 and D-DOC-142, docs-session findings, October 2026).
/// <para>
/// The two defects this replaces, both in <c>MainWindow.OnLoaded</c>:
/// <list type="bullet">
/// <item><description>D-DOC-074: the model text was "the first word after the program name that does not start with
/// '-'", so the VALUE of a flag was taken as the model: <c>--back 30</c> booted case30, and <c>--tab OPERATOR</c> on its
/// own searched the model picker for "OPERATOR" and fell back to the first model.</description></item>
/// <item><description>D-DOC-142: a <c>--zone</c>, <c>--back</c> or <c>--bridge-source</c> value that matched no picker
/// entry was dropped without a word, so a typing slip quietly streamed a different zone, start or reconstruction than
/// the one asked for.</description></item>
/// </list>
/// </para>
/// <para>
/// The rules now: every flag takes its value as the NEXT word (two-token form, as the manual always said), and that
/// word is never the model text. At most one other word may be given and it is the model text. Anything that cannot
/// be honoured is refused with a message naming it (fail loud): an unknown option (including the <c>--zone=ukpn</c>
/// one-token form and a wrong-case <c>--Zone</c>, which used to be ignored), a flag with no value, a flag given twice,
/// a second model word, a <c>--back</c> that is not a whole number, and (in <see cref="Resolve"/>) any value that
/// matches no entry of its picker. The app shows the refusal and closes, like a CLI usage error, rather than open on
/// something the user did not ask for.
/// </para>
/// Presentation only: nothing here touches a computed number.
/// </summary>
public sealed record DesktopLaunchArgs(
    string? ModelText,
    string? Zone,
    int? BackDays,
    string? BridgeSource,
    string? Tab,
    IReadOnlyList<string> Errors)
{
    /// <summary>The flags that take a value (the next word on the command line). Matched exactly, case-sensitively.</summary>
    public static readonly IReadOnlyList<string> ValueFlags = new[] { "--zone", "--back", "--bridge-source", "--tab" };

    /// <summary>True when nothing on the command line was refused.</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>
    /// Read the command line. <paramref name="args"/> is the argument list WITHOUT the program path (that is,
    /// <c>Environment.GetCommandLineArgs()</c> with element 0 removed). Never throws for bad input: every problem is
    /// collected in <see cref="Errors"/>, so the app can show them all at once.
    /// </summary>
    public static DesktopLaunchArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var errors = new List<string>();
        string? model = null, zone = null, back = null, source = null, tab = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i] ?? "";
            if (a.Length == 0) continue;   // an empty word (an empty quoted argument) carries nothing: skip it

            if (a.StartsWith('-'))
            {
                if (!ValueFlags.Contains(a))
                {
                    errors.Add(UnknownOptionMessage(a));
                    continue;
                }
                // The value is the next word, and it must exist and must not itself be an option.
                if (i + 1 >= args.Count || string.IsNullOrEmpty(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    errors.Add($"{a} needs a value (the next word), for example: {Example(a)}.");
                    continue;
                }
                string value = args[++i];   // consumed here: a flag's value is never the model text (D-DOC-074)
                if (!seen.Add(a))
                {
                    errors.Add($"{a} is given more than once; give it once.");
                    continue;
                }
                switch (a)
                {
                    case "--zone": zone = value; break;
                    case "--back": back = value; break;
                    case "--bridge-source": source = value; break;
                    case "--tab": tab = value; break;
                }
                continue;
            }

            // A plain word is the model text; only one is allowed (two would leave the choice to chance).
            if (model is null) model = a;
            else errors.Add($"More than one model text: '{model}' and '{a}'. Give one (quote a label that contains spaces).");
        }

        int? backDays = null;
        if (back is not null)
        {
            // Whole days, invariant culture, no sign or thousands separators: exactly what the preset list holds.
            if (int.TryParse(back, NumberStyles.None, CultureInfo.InvariantCulture, out int d)) backDays = d;
            else errors.Add($"--back '{back}' is not a whole number of days.");
        }

        return new DesktopLaunchArgs(model, zone, backDays, source, tab, errors);
    }

    /// <summary>
    /// Match each given value against the entries the app actually offers and return the picker indices, adding a
    /// refusal for every value that matches nothing (D-DOC-142). Matching keeps the documented rules: the zone, tab and
    /// model text pick the FIRST entry whose text contains the value, ignoring case; the bridge source must equal an
    /// entry, ignoring case; the look-back must equal a preset's whole number of days. A value that was not given
    /// resolves to null (the app keeps its default).
    /// </summary>
    /// <param name="zoneCases">The bridge ZONE picker's case names, in picker order.</param>
    /// <param name="backPresetDays">The bridge FROM picker's presets in whole days, in picker order.</param>
    /// <param name="bridgeSources">The bridge SRC picker's source names, in picker order.</param>
    /// <param name="tabHeaders">The tab headers, left to right.</param>
    /// <param name="modelLabels">The model picker's labels, in picker order.</param>
    public DesktopLaunchPlan Resolve(
        IReadOnlyList<string> zoneCases, IReadOnlyList<int> backPresetDays, IReadOnlyList<string> bridgeSources,
        IReadOnlyList<string> tabHeaders, IReadOnlyList<string> modelLabels)
    {
        ArgumentNullException.ThrowIfNull(zoneCases);
        ArgumentNullException.ThrowIfNull(backPresetDays);
        ArgumentNullException.ThrowIfNull(bridgeSources);
        ArgumentNullException.ThrowIfNull(tabHeaders);
        ArgumentNullException.ThrowIfNull(modelLabels);
        var errors = new List<string>(Errors);

        int? zone = null, back = null, source = null, tab = null, model = null;
        if (Zone is not null)
        {
            zone = FirstContaining(zoneCases, Zone);
            if (zone is null) errors.Add($"--zone '{Zone}' matches no bridge zone. Zones: {string.Join(", ", zoneCases)}.");
        }
        if (BackDays is int days)
        {
            int at = IndexOf(backPresetDays, days);
            if (at >= 0) back = at;
            else errors.Add($"--back {days.ToString(CultureInfo.InvariantCulture)} is not a start preset. Presets (days): "
                            + string.Join(", ", backPresetDays.Select(d => d.ToString(CultureInfo.InvariantCulture))) + ".");
        }
        if (BridgeSource is not null)
        {
            for (int i = 0; i < bridgeSources.Count; i++)
                if (string.Equals(bridgeSources[i], BridgeSource, StringComparison.OrdinalIgnoreCase)) { source = i; break; }
            if (source is null) errors.Add($"--bridge-source '{BridgeSource}' is not a bridge source. Sources: {string.Join(", ", bridgeSources)}.");
        }
        if (Tab is not null)
        {
            tab = FirstContaining(tabHeaders, Tab);
            if (tab is null) errors.Add($"--tab '{Tab}' matches no tab. Tabs: {string.Join(", ", tabHeaders)}.");
        }
        if (ModelText is not null)
        {
            model = FirstContaining(modelLabels, ModelText);
            if (model is null) errors.Add($"The model text '{ModelText}' matches no entry in the model picker.");
        }

        return new DesktopLaunchPlan(model, zone, back, source, tab, errors);
    }

    /// <summary>The message for a word that starts with '-' but is not one of <see cref="ValueFlags"/>.</summary>
    private static string UnknownOptionMessage(string a)
    {
        int eq = a.IndexOf('=', StringComparison.Ordinal);
        if (eq > 0 && ValueFlags.Contains(a[..eq]))
            return $"'{a}': give the value as the next word ({a[..eq]} {a[(eq + 1)..]}), not after '='.";
        string? sameButCase = ValueFlags.FirstOrDefault(f => string.Equals(f, a, StringComparison.OrdinalIgnoreCase));
        if (sameButCase is not null)
            return $"Unknown option '{a}'. Options are case-sensitive: did you mean {sameButCase}?";
        return $"Unknown option '{a}'. The desktop app takes: {string.Join(", ", ValueFlags)} (each with a value) and one model text.";
    }

    private static string Example(string flag) => flag switch
    {
        "--zone" => "--zone ukpn",
        "--back" => "--back 30",
        "--bridge-source" => "--bridge-source estimated",
        _ => "--tab operator",
    };

    private static int? FirstContaining(IReadOnlyList<string> entries, string text)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i] is not null && entries[i].Contains(text, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    private static int IndexOf(IReadOnlyList<int> values, int v)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == v) return i;
        return -1;
    }
}

/// <summary>
/// The launch arguments matched to the app's pickers: an index per given value (null when the value was not given),
/// and every refusal. The app applies the indices only when <see cref="Ok"/> is true.
/// </summary>
public sealed record DesktopLaunchPlan(
    int? ModelIndex,
    int? ZoneIndex,
    int? BackIndex,
    int? BridgeSourceIndex,
    int? TabIndex,
    IReadOnlyList<string> Errors)
{
    /// <summary>True when every given value was understood and matched.</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>The refusal shown to the user: one problem per line, then how to start the app without them.</summary>
    public string RefusalText() =>
        "GridSim could not start with these launch arguments:" + Environment.NewLine + Environment.NewLine
        + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)) + Environment.NewLine + Environment.NewLine
        + "Fix or remove them and start the app again (see manual chapter 8, Launch arguments).";
}
