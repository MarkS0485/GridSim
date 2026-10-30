using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GridSim.UiSmoke;

/// <summary>One captured page: its case, its name, the PNG (relative to the output folder) and what it holds.</summary>
/// <param name="Case">The case's short name (its sub-folder of the output folder).</param>
/// <param name="Page">The page name (the PNG's name without .png; the AssetSmoke name where a WPF page exists).</param>
/// <param name="File">The PNG, relative to the output folder, with forward slashes (as index.html links it).</param>
/// <param name="Width">The PNG's width in pixels (always the --size width).</param>
/// <param name="Height">The PNG's height in pixels (always the --size height).</param>
/// <param name="Bytes">The PNG file's length in bytes.</param>
/// <param name="Sha256">The SHA-256 of the PNG file's bytes, lower-case hex.</param>
/// <param name="ContentInk">How many pixels of the page's own area (the selected tab's content, without the window's
/// header, tab strip, transport bar and status bar) differ from that area's most common colour. Zero means the page
/// drew nothing, which is a failure.</param>
/// <param name="Milliseconds">How long the page took, from opening it to the PNG on disk.</param>
public sealed record UiSmokePage(string Case, string Page, string File, int Width, int Height, long Bytes, string Sha256,
    long ContentInk, long Milliseconds);

/// <summary>A page (or a whole case) that failed, with the reason.</summary>
/// <param name="Case">The case's short name.</param>
/// <param name="Page">The page name, or "(case)" when the case itself failed (not found, refused, a run error).</param>
/// <param name="Reason">What went wrong, in plain words (an exception's type and message).</param>
public sealed record UiSmokeFailure(string Case, string Page, string Reason);

/// <summary>A page that was not captured because the case has nothing for it (for example no interconnector).</summary>
/// <param name="Case">The case's short name.</param>
/// <param name="Page">The page name (or the page kind) that was passed over.</param>
/// <param name="Reason">Why there was nothing to capture.</param>
public sealed record UiSmokeSkip(string Case, string Page, string Reason);

/// <summary>One case of the run.</summary>
/// <param name="Case">The case's short name (its sub-folder).</param>
/// <param name="Argument">The --case value it came from.</param>
/// <param name="Label">The source's label, as the NETWORK picker shows it (empty when the case was not found).</param>
/// <param name="Source">The file or folder the case was read from (empty when it was not found).</param>
/// <param name="Ticks">The tick of the frame shown when the pages were captured (equal to --ticks when it ran).</param>
/// <param name="SettingsFile">The isolated settings file the window read and wrote (never the user's).</param>
public sealed record UiSmokeCase(string Case, string Argument, string Label, string Source, int Ticks, string SettingsFile);

/// <summary>
/// Everything one run produced. <see cref="Write"/> puts the manifest.json and the index.html contact sheet in the output
/// folder; <see cref="Ok"/> is the run's verdict (no failure, at least one page).
/// </summary>
public sealed class UiSmokeReport
{
    /// <summary>The output folder (full path).</summary>
    public required string OutputFolder { get; init; }

    /// <summary>The window size every PNG has.</summary>
    public required int Width { get; init; }

    /// <summary>The window size every PNG has.</summary>
    public required int Height { get; init; }

    /// <summary>The ticks played before capturing.</summary>
    public required int Ticks { get; init; }

    /// <summary>The cases, in the order run.</summary>
    public List<UiSmokeCase> Cases { get; } = new();

    /// <summary>Every page captured, in the order captured.</summary>
    public List<UiSmokePage> Pages { get; } = new();

    /// <summary>Every failure (a page that did not render, a case that did not load, a --pages entry that matched nothing).</summary>
    public List<UiSmokeFailure> Failures { get; } = new();

    /// <summary>Pages passed over because the case has nothing for them.</summary>
    public List<UiSmokeSkip> Skipped { get; } = new();

    /// <summary>Any call into a dialog, picker or Author-mode question (none is expected; listed so one is never hidden).</summary>
    public List<string> ShellCalls { get; } = new();

    /// <summary>The whole run's wall time.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>True when every page rendered and at least one page was captured.</summary>
    public bool Ok => Failures.Count == 0 && Pages.Count > 0;

    /// <summary>The manifest's path (after <see cref="Write"/>).</summary>
    public string ManifestPath => Path.Combine(OutputFolder, "manifest.json");

    /// <summary>The contact sheet's path (after <see cref="Write"/>).</summary>
    public string IndexPath => Path.Combine(OutputFolder, "index.html");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Write manifest.json and index.html into the output folder.</summary>
    public void Write()
    {
        Directory.CreateDirectory(OutputFolder);
        File.WriteAllText(ManifestPath, ManifestJson(), new UTF8Encoding(false));
        File.WriteAllText(IndexPath, IndexHtml(), new UTF8Encoding(false));
    }

    /// <summary>
    /// The manifest: the run's settings, every case, and per page its name, file, size in pixels and bytes, and SHA-256,
    /// then the failures, the skipped pages and any shell call. Written with System.Text.Json's writer (no reflection),
    /// in a fixed field order, so two runs that drew the same pixels give the same page entries.
    /// </summary>
    public string ManifestJson()
    {
        using var stream = new MemoryStream();
        using (var j = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("tool", "UiSmoke");
            j.WriteString("version", GridSim.Core.BuildInfo.Version);
            j.WriteString("size", string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}"));
            j.WriteNumber("ticks", Ticks);
            j.WriteNumber("elapsedSeconds", Math.Round(Elapsed.TotalSeconds, 1));
            j.WriteBoolean("ok", Ok);

            j.WriteStartArray("cases");
            foreach (UiSmokeCase c in Cases)
            {
                j.WriteStartObject();
                j.WriteString("case", c.Case);
                j.WriteString("argument", c.Argument);
                j.WriteString("label", c.Label);
                j.WriteString("source", c.Source);
                j.WriteNumber("ticks", c.Ticks);
                j.WriteString("settingsFile", c.SettingsFile);
                j.WriteEndObject();
            }
            j.WriteEndArray();

            j.WriteStartArray("pages");
            foreach (UiSmokePage p in Pages)
            {
                j.WriteStartObject();
                j.WriteString("case", p.Case);
                j.WriteString("page", p.Page);
                j.WriteString("file", p.File);
                j.WriteNumber("width", p.Width);
                j.WriteNumber("height", p.Height);
                j.WriteNumber("bytes", p.Bytes);
                j.WriteString("sha256", p.Sha256);
                j.WriteNumber("contentInk", p.ContentInk);
                j.WriteNumber("milliseconds", p.Milliseconds);
                j.WriteEndObject();
            }
            j.WriteEndArray();

            j.WriteStartArray("failures");
            foreach (UiSmokeFailure f in Failures)
            {
                j.WriteStartObject();
                j.WriteString("case", f.Case);
                j.WriteString("page", f.Page);
                j.WriteString("reason", f.Reason);
                j.WriteEndObject();
            }
            j.WriteEndArray();

            j.WriteStartArray("skipped");
            foreach (UiSmokeSkip s in Skipped)
            {
                j.WriteStartObject();
                j.WriteString("case", s.Case);
                j.WriteString("page", s.Page);
                j.WriteString("reason", s.Reason);
                j.WriteEndObject();
            }
            j.WriteEndArray();

            j.WriteStartArray("shellCalls");
            foreach (string call in ShellCalls) j.WriteStringValue(call);
            j.WriteEndArray();
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// The contact sheet: plain HTML with no script and no outside resource, one section per case with a thumbnail per
    /// page (each a relative link to its full-size PNG), then the failures and the skipped pages. Every text is
    /// HTML-encoded.
    /// </summary>
    public string IndexHtml()
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var h = new StringBuilder();
        h.Append("<!doctype html>\n<html lang=\"en-GB\">\n<head>\n<meta charset=\"utf-8\">\n");
        h.Append("<title>UiSmoke contact sheet</title>\n");
        h.Append("<style>\n");
        h.Append("body{font-family:sans-serif;background:#10161d;color:#d8e1ea;margin:16px}\n");
        h.Append("h1,h2{font-weight:600}a{color:#7fc4ff}\n");
        h.Append(".sheet{display:flex;flex-wrap:wrap;gap:12px}\n");
        h.Append("figure{margin:0;width:400px}figure img{width:400px;border:1px solid #33414f}\n");
        h.Append("figcaption{font-size:12px;margin-top:4px;word-break:break-all}\n");
        h.Append("table{border-collapse:collapse}td,th{border:1px solid #33414f;padding:4px 8px;text-align:left}\n");
        h.Append("</style>\n</head>\n<body>\n");
        h.Append("<h1>UiSmoke contact sheet</h1>\n");
        h.Append(CultureInfo.InvariantCulture, $"<p>GridSim {E(GridSim.Core.BuildInfo.Version)}, window {Width}x{Height}, {Ticks} ticks, ");
        h.Append(CultureInfo.InvariantCulture, $"{Pages.Count} pages, {Failures.Count} failures, {Elapsed.TotalSeconds:0.0} s. ");
        h.Append("Each picture links to its full-size PNG; manifest.json lists every file with its SHA-256.</p>\n");

        foreach (UiSmokeCase c in Cases)
        {
            h.Append(CultureInfo.InvariantCulture, $"<h2>{E(c.Case)}: {E(c.Label)}</h2>\n");
            h.Append(CultureInfo.InvariantCulture, $"<p>Read from {E(c.Source)}; captured at tick {c.Ticks}.</p>\n");
            h.Append("<div class=\"sheet\">\n");
            foreach (UiSmokePage p in Pages.Where(p => p.Case == c.Case))
            {
                string href = string.Join("/", p.File.Split('/').Select(Uri.EscapeDataString));
                h.Append(CultureInfo.InvariantCulture,
                    $"<figure><a href=\"{E(href)}\"><img src=\"{E(href)}\" alt=\"{E(p.Page)}\" loading=\"lazy\"></a><figcaption>{E(p.Page)}</figcaption></figure>\n");
            }
            h.Append("</div>\n");
        }

        if (Failures.Count > 0)
        {
            h.Append("<h2>Failures</h2>\n<table><tr><th>Case</th><th>Page</th><th>Reason</th></tr>\n");
            foreach (UiSmokeFailure f in Failures)
                h.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(f.Case)}</td><td>{E(f.Page)}</td><td>{E(f.Reason)}</td></tr>\n");
            h.Append("</table>\n");
        }
        if (Skipped.Count > 0)
        {
            h.Append("<h2>Not captured (nothing in the case for them)</h2>\n<table><tr><th>Case</th><th>Page</th><th>Reason</th></tr>\n");
            foreach (UiSmokeSkip s in Skipped)
                h.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(s.Case)}</td><td>{E(s.Page)}</td><td>{E(s.Reason)}</td></tr>\n");
            h.Append("</table>\n");
        }
        h.Append("</body>\n</html>\n");
        return h.ToString();
    }
}
