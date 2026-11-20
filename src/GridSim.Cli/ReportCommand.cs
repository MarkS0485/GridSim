using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Presentation.Workbench;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-10: <c>gridsim report &lt;template.json&gt; &lt;result.json&gt; --out &lt;file&gt; [--format md|html|csv]</c> renders a
/// result document through a <c>gridsim-report-template/1</c> template, so a report made in the workbench can be made again
/// headless. <c>gridsim report --template-for &lt;result.json&gt; --out &lt;template.json&gt;</c> writes the default template for a
/// result. Order: load the template, verify the result hash, resolve the bindings in template order, build the tables and
/// plots, render, write once (a new file; an existing path is refused). Exit 0 written, 1 an unusable input or an existing
/// output, 2 a usage error.
/// </summary>
internal static class ReportCommand
{
    public static int Run(List<string> arguments)
    {
        if (!TryParse(arguments, out string? template, out string? result, out string? templateFor, out string? outPath, out string? format))
        {
            Console.Error.WriteLine("usage: gridsim report <template.json> <result.json> --out <file.md|html|csv> [--format md|html|csv]");
            Console.Error.WriteLine("       gridsim report --template-for <result.json> --out <template.json>");
            return CliExit.Usage;
        }
        try
        {
            string full = Path.GetFullPath(outPath!);
            if (File.Exists(full) || Directory.Exists(full)) throw new IOException("--out names an existing path; a report is written only to a new file.");
            string text;
            if (templateFor is not null)
                text = ReportTemplate.DefaultJson(File.ReadAllBytes(Path.GetFullPath(templateFor)));
            else
            {
                string fmt = format ?? FormatOf(outPath!) ?? throw new ArgumentException("Cannot tell the format from the --out extension; give --format md, html or csv.");
                string version = typeof(ReportCommand).Assembly.GetName().Version?.ToString(3) ?? "";
                ReportDocument doc = ReportRenderer.Build(File.ReadAllBytes(Path.GetFullPath(template!)), File.ReadAllBytes(Path.GetFullPath(result!)),
                    version.Length > 0 ? version : null);
                text = fmt switch
                {
                    "md" => ReportRenderer.ToMarkdown(doc),
                    "html" => ReportRenderer.ToHtml(doc),
                    "csv" => ReportRenderer.ToCsv(doc),
                    _ => throw new ArgumentException($"--format '{fmt}' is not md, html or csv."),
                };
            }
            PlanningStudyCommand.SeamWriteNew(full, text);
            CliResultFromJson.Fill(PlanningStudyCommand.SeamJson(new
            {
                kind = templateFor is not null ? "template" : "report",
                file = full,
                format = templateFor is not null ? "json" : (format ?? FormatOf(outPath!)),
                bytes = Encoding.UTF8.GetByteCount(text),
            }));
            Console.WriteLine("report: wrote " + full);
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is ReportException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine("report: " + CliExit.UserMessage(ex));
            return CliExit.InputError;
        }
    }

    private static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "md",
        ".html" or ".htm" => "html",
        ".csv" => "csv",
        _ => null,
    };

    private static bool TryParse(List<string> a, out string? template, out string? result, out string? templateFor, out string? outPath, out string? format)
    {
        template = result = templateFor = outPath = format = null;
        int i = 1;
        if (a.Count > 1 && !a[1].StartsWith('-'))
        {
            if (a.Count < 3 || a[2].StartsWith('-')) return false;
            template = a[1];
            result = a[2];
            i = 3;
        }
        for (; i < a.Count; i += 2)
        {
            if (i + 1 >= a.Count || a[i + 1].StartsWith('-')) return false;
            switch (a[i])
            {
                case "--out" when outPath is null: outPath = a[i + 1]; break;
                case "--format" when format is null: format = a[i + 1].ToLowerInvariant(); break;
                case "--template-for" when templateFor is null && template is null: templateFor = a[i + 1]; break;
                default: return false;
            }
        }
        return outPath is not null && (template is not null || templateFor is not null) && !(template is not null && templateFor is not null);
    }
}
