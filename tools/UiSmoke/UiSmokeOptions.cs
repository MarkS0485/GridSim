using System.Globalization;

namespace GridSim.UiSmoke;

/// <summary>
/// What one UiSmoke run does: which cases, where the pictures go, how many ticks are played before the first capture,
/// the window size and which pages are captured. Built from the command line by <see cref="Parse"/>, or directly by a
/// test (every property has the same default the command line has).
/// </summary>
public sealed class UiSmokeOptions
{
    /// <summary>The cases captured when no --case is given: the bundled reduced GB case and MATPOWER case9.</summary>
    public static IReadOnlyList<string> DefaultCases { get; } = new[] { "uk", "case9" };

    /// <summary>
    /// The ticks played when no --ticks is given: 30 frames, enough for the dashboard's tiles, the frequency trace, THE
    /// MACHINE's speed trace and the sparklines to have visible shape (AssetSmoke played 4).
    /// </summary>
    public const int DefaultTicks = 30;

    /// <summary>The window width when no --size is given: the WPF main window's 1580 (MainWindow.xaml).</summary>
    public const int DefaultWidth = 1580;

    /// <summary>The window height when no --size is given: the WPF main window's 940 (MainWindow.xaml).</summary>
    public const int DefaultHeight = 940;

    /// <summary>The most ticks a run may ask for (a guard against a typing slip such as 30000).</summary>
    public const int MaxTicks = 10_000;

    /// <summary>The smallest and largest window side accepted by --size, in pixels.</summary>
    public const int MinSide = 320, MaxSide = 8192;

    /// <summary>The cases to capture, each a name (uk, gb-full, case9, ...) or a path (a case folder, a .m file or a
    /// *.case.json manifest), resolved by <see cref="UiSmokeCases.Resolve"/>.</summary>
    public IReadOnlyList<string> Cases { get; init; } = DefaultCases;

    /// <summary>The output folder (null: results/uismoke/&lt;UTC stamp&gt; under the repository root, see
    /// <see cref="ResolveOutputFolder"/>).</summary>
    public string? OutputFolder { get; init; }

    /// <summary>Ticks played (one STEP ONCE each, so the count is exact) before the first page is captured.</summary>
    public int Ticks { get; init; } = DefaultTicks;

    /// <summary>The window's client width in pixels; every PNG has exactly this width.</summary>
    public int Width { get; init; } = DefaultWidth;

    /// <summary>The window's client height in pixels; every PNG has exactly this height.</summary>
    public int Height { get; init; } = DefaultHeight;

    /// <summary>
    /// The pages to capture (null: all). Each entry is a page name (for example <c>scada</c>) or the start of one up to
    /// a dash (<c>generator-detail</c> takes <c>generator-detail-(Peterhead)</c>). An entry that matches no page of any
    /// case is a failure of the run, so a misspelt name never passes as "nothing to do".
    /// </summary>
    public IReadOnlyList<string>? Pages { get; init; }

    /// <summary>True when the command line asked for the usage text.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>The usage text (--help).</summary>
    public const string Usage =
        "UiSmoke: capture every page of the cross-platform GridSim window to PNG, headless (Avalonia + Skia).\n" +
        "\n" +
        "  dotnet run --project tools/UiSmoke -c Release -- [options]\n" +
        "\n" +
        "Options:\n" +
        "  --case <name or path>  a case to capture; repeat for more. A name: uk, gb-synthetic, gb-full, or a MATPOWER\n" +
        "                         case under data/matpower (case9, case30, ...). A path: a v1 case folder (system.json),\n" +
        "                         a MATPOWER .m file or a v2 *.case.json manifest. Default: uk and case9.\n" +
        "  --out <folder>         where the PNGs, index.html and manifest.json go.\n" +
        "                         Default: results/uismoke/<UTC stamp> under the repository root.\n" +
        "  --ticks <N>            frames played (exactly N, one step at a time) before capturing. Default: 30.\n" +
        "  --size <W>x<H>         window size in pixels; every PNG is this size. Default: 1580x940 (the WPF window).\n" +
        "  --pages <a,b,...>      capture only these pages (a name, or its start up to a dash). Default: all.\n" +
        "  --help                 this text.\n" +
        "\n" +
        "Exit codes: 0 every page rendered; 1 a page failed (named in the output); 2 the command line was refused.\n";

    /// <summary>
    /// Read the command line. Throws <see cref="ArgumentException"/> with the reason for anything it cannot use (an
    /// unknown option, a missing or malformed value, a value out of range), so a typing slip is never silently ignored.
    /// </summary>
    public static UiSmokeOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var cases = new List<string>();
        string? output = null;
        int ticks = DefaultTicks;
        int width = DefaultWidth, height = DefaultHeight;
        List<string>? pages = null;
        bool help = false;
        bool ticksSeen = false, sizeSeen = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            // Every option but --help takes one value, the next argument.
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{arg} needs a value (see --help).");
                return args[++i];
            }

            switch (arg)
            {
                case "--help" or "-h" or "help":
                    help = true;
                    break;
                case "--case":
                    string c = Value().Trim();
                    if (c.Length == 0) throw new ArgumentException("--case needs a case name or a path, not an empty value.");
                    cases.Add(c);
                    break;
                case "--out":
                    if (output is not null) throw new ArgumentException("--out was given twice.");
                    output = Value();
                    if (output.Trim().Length == 0) throw new ArgumentException("--out needs a folder, not an empty value.");
                    break;
                case "--ticks":
                    if (ticksSeen) throw new ArgumentException("--ticks was given twice.");
                    ticksSeen = true;
                    string t = Value();
                    if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks < 1 || ticks > MaxTicks)
                        throw new ArgumentException($"--ticks must be a whole number from 1 to {MaxTicks}, not \"{t}\".");
                    break;
                case "--size":
                    if (sizeSeen) throw new ArgumentException("--size was given twice.");
                    sizeSeen = true;
                    (width, height) = ParseSize(Value());
                    break;
                case "--pages":
                    if (pages is not null) throw new ArgumentException("--pages was given twice.");
                    pages = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (pages.Count == 0) throw new ArgumentException("--pages needs at least one page name.");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument \"{arg}\" (see --help).");
            }
        }

        return new UiSmokeOptions
        {
            Cases = cases.Count > 0 ? cases : DefaultCases,
            OutputFolder = output,
            Ticks = ticks,
            Width = width,
            Height = height,
            Pages = pages,
            ShowHelp = help,
        };
    }

    /// <summary>Read "WxH" (for example 1580x940; an upper-case X is accepted too). Throws with the reason otherwise.</summary>
    public static (int Width, int Height) ParseSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            throw new ArgumentException($"--size must be WIDTHxHEIGHT in whole pixels (for example 1580x940), not \"{text}\".");
        if (w < MinSide || w > MaxSide || h < MinSide || h > MaxSide)
            throw new ArgumentException($"--size sides must each be from {MinSide} to {MaxSide} pixels, not {w}x{h}.");
        return (w, h);
    }

    /// <summary>
    /// The folder this run writes to, as a full path: <see cref="OutputFolder"/> when given (relative to the working
    /// folder), else results/uismoke/&lt;UTC stamp&gt; under the repository root (the results/ folder is git-ignored),
    /// or under the working folder when no repository is found above it.
    /// </summary>
    public string ResolveOutputFolder(DateTime utcNow)
    {
        if (OutputFolder is not null) return Path.GetFullPath(OutputFolder);
        string root = UiSmokeCases.FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
        string stamp = utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(root, "results", "uismoke", stamp);
    }

    /// <summary>
    /// True when page <paramref name="page"/> is wanted: every page when <see cref="Pages"/> is null, else a page whose
    /// name equals an entry or starts with an entry followed by a dash.
    /// </summary>
    public bool Wants(string page) => Pages is null || Pages.Any(p => Matches(p, page));

    /// <summary>True when the --pages entry <paramref name="entry"/> takes page <paramref name="page"/>.</summary>
    public static bool Matches(string entry, string page) =>
        string.Equals(entry, page, StringComparison.OrdinalIgnoreCase)
        || page.StartsWith(entry + "-", StringComparison.OrdinalIgnoreCase);
}
