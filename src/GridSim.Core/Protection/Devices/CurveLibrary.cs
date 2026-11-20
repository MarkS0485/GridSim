using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Protection.Coordination;

namespace GridSim.Core.Protection.Devices;

/// <summary>A curve data file that is invalid or has no provenance (refused, never loaded partially).</summary>
public sealed class CurveDataException(string message) : StudyCaseException(message);

/// <summary>
/// The curve library of the placed-device workflow (R4-07). Two sources of curves:
/// the built-in standard equation families (ids <c>iec-si</c>, <c>iec-vi</c>, <c>iec-ei</c>, <c>iec-lti</c>, <c>ieee-mi</c>,
/// <c>ieee-vi</c>, <c>ieee-ei</c>, <c>dt</c>, evaluated by <see cref="OvercurrentCoordination"/>) and tabulated curves loaded
/// from <c>data/protection-curves/*.json</c> (<c>gridsim-protection-curve/1</c>). Every data file must carry a
/// <c>source</c> (kind, title, publisher, url, locator, retrieved; optional sha256 of the source document). A file with a
/// <c>families</c> block must repeat the standard constants and they must equal the ones in code, so the data and the
/// equations cannot drift apart. Vendor data enters only as data with a named public source, never as code.
/// </summary>
public sealed class CurveLibrary
{
    public const string FormatVersion = "gridsim-protection-curve/1";
    private readonly Dictionary<string, TabulatedCurve> _tabulated = new(StringComparer.Ordinal);
    private readonly List<CurveSource> _sources = new();

    public IReadOnlyCollection<TabulatedCurve> Tabulated => _tabulated.Values;
    public IReadOnlyList<CurveSource> Sources => _sources;

    /// <summary>The standard family for a built-in id, or null.</summary>
    public static OvercurrentCurve? Standard(string id) => id.ToLowerInvariant() switch
    {
        "iec-si" => OvercurrentCurve.IecStandardInverse,
        "iec-vi" => OvercurrentCurve.IecVeryInverse,
        "iec-ei" => OvercurrentCurve.IecExtremelyInverse,
        "iec-lti" => OvercurrentCurve.IecLongTimeInverse,
        "ieee-mi" => OvercurrentCurve.IeeeModeratelyInverse,
        "ieee-vi" => OvercurrentCurve.IeeeVeryInverse,
        "ieee-ei" => OvercurrentCurve.IeeeExtremelyInverse,
        "dt" => OvercurrentCurve.DefiniteTime,
        _ => null,
    };

    public bool TryGetTabulated(string id, out TabulatedCurve curve) => _tabulated.TryGetValue(id, out curve!);
    public bool Knows(string id) => Standard(id) is not null || _tabulated.ContainsKey(id);

    /// <summary>Load every <c>*.json</c> under <paramref name="directory"/> (ordinal file order) into a new library.</summary>
    public static CurveLibrary LoadDirectory(string directory)
    {
        var lib = new CurveLibrary();
        foreach (string f in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            lib.LoadFile(f);
        return lib;
    }

    public void LoadFile(string path) => Load(File.ReadAllBytes(path), Path.GetFileName(path));

    public void Load(byte[] bytes, string label)
    {
        try { LoadCore(bytes, label); }
        catch (CurveDataException) { throw; }
        catch (StudyCaseException ex) { throw new CurveDataException($"{label}: {ex.Message}"); }
    }

    private void LoadCore(byte[] bytes, string label)
    {
        using JsonDocument doc = StudyCaseJson.Parse(bytes, "The curve file");
        JsonElement root = doc.RootElement;
        StudyCaseJson.Obj(root, label, ["format", "source", "families", "curves", "note"], ["format"]);
        string format = StudyCaseJson.Str(root.GetProperty("format"), "format");
        if (format != FormatVersion) throw new CurveDataException($"{label}: format must be \"{FormatVersion}\", not \"{format}\".");
        if (!root.TryGetProperty("source", out JsonElement s))
            throw new CurveDataException($"{label}: refused, no provenance. A curve file must carry a source (kind, title, publisher, url, locator, retrieved).");
        string[] keys = ["kind", "title", "publisher", "url", "locator", "retrieved", "sha256"];
        StudyCaseJson.Obj(s, label + ".source", keys, keys[..6]);
        string Field(string k) => StudyCaseJson.Str(s.GetProperty(k), label + ".source." + k);
        string kind = Field("kind");
        if (kind is not ("standard" or "manufacturer" or "synthetic"))
            throw new CurveDataException($"{label}.source.kind must be standard, manufacturer or synthetic, not \"{kind}\".");
        foreach (string k in keys[..6])
            if (string.IsNullOrWhiteSpace(Field(k))) throw new CurveDataException($"{label}.source.{k} is empty: provenance must be named.");
        if (kind != "synthetic" && !Field("url").StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new CurveDataException($"{label}.source.url must be a public http(s) address for a {kind} source.");
        string? sha = s.TryGetProperty("sha256", out JsonElement sh) ? StudyCaseJson.Str(sh, label + ".source.sha256").ToLowerInvariant() : null;
        if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit)))
            throw new CurveDataException($"{label}.source.sha256 must be 64 hex digits.");
        var source = new CurveSource(kind, Field("title"), Field("publisher"), Field("url"), Field("locator"), Field("retrieved"), sha);

        if (root.TryGetProperty("families", out JsonElement fams))
            foreach ((JsonElement f, int i) in StudyCaseJson.Arr(fams, label + ".families"))
                CheckFamily(f, $"{label}.families[{i}]");

        var staged = new List<TabulatedCurve>();
        if (root.TryGetProperty("curves", out JsonElement curves))
            foreach ((JsonElement c, int i) in StudyCaseJson.Arr(curves, label + ".curves"))
            {
                string at = $"{label}.curves[{i}]";
                StudyCaseJson.Obj(c, at, ["id", "description", "kind", "points"], ["id", "kind", "points"]);
                string id = StudyCaseJson.Str(c.GetProperty("id"), at + ".id");
                if (Standard(id) is not null) throw new CurveDataException($"{at}.id '{id}' is a built-in family id.");
                if (_tabulated.ContainsKey(id) || staged.Any(x => x.Id == id)) throw new CurveDataException($"{at}.id '{id}' is defined twice.");
                string ck = StudyCaseJson.Str(c.GetProperty("kind"), at + ".kind");
                CurveKind kindEnum = ck switch
                {
                    "min-melt" => CurveKind.MinimumMelt, "total-clear" => CurveKind.TotalClear, "operate" => CurveKind.Operate,
                    _ => throw new CurveDataException($"{at}.kind must be min-melt, total-clear or operate, not \"{ck}\"."),
                };
                var pts = new List<(double, double)>();
                foreach ((JsonElement p, int pi) in StudyCaseJson.Arr(c.GetProperty("points"), at + ".points"))
                {
                    var pair = StudyCaseJson.Arr(p, $"{at}.points[{pi}]");
                    if (pair.Count != 2) throw new CurveDataException($"{at}.points[{pi}] must be [amps, seconds].");
                    pts.Add((StudyCaseJson.Num(pair[0].Item, $"{at}.points[{pi}][0]"), StudyCaseJson.Num(pair[1].Item, $"{at}.points[{pi}][1]")));
                }
                string desc = c.TryGetProperty("description", out JsonElement d) ? StudyCaseJson.Str(d, at + ".description") : "";
                try { staged.Add(new TabulatedCurve(id, desc, kindEnum, source, pts)); }
                catch (ArgumentException ex) { throw new CurveDataException($"{at}: {ex.Message}"); }
            }
        foreach (TabulatedCurve t in staged) _tabulated[t.Id] = t;
        _sources.Add(source);
    }

    private static void CheckFamily(JsonElement f, string at)
    {
        StudyCaseJson.Obj(f, at, ["id", "k", "alpha", "a", "b", "p"], ["id"]);
        string id = StudyCaseJson.Str(f.GetProperty("id"), at + ".id");
        OvercurrentCurve curve = Standard(id) ?? throw new CurveDataException($"{at}.id '{id}' is not a built-in family.");
        if (curve is OvercurrentCurve.IecStandardInverse or OvercurrentCurve.IecVeryInverse or OvercurrentCurve.IecExtremelyInverse or OvercurrentCurve.IecLongTimeInverse)
        {
            (double k, double alpha) = OvercurrentCoordination.IecConstants(curve);
            Same(f, at, "k", k); Same(f, at, "alpha", alpha);
        }
        else if (curve != OvercurrentCurve.DefiniteTime)
        {
            (double a, double b, double p) = OvercurrentCoordination.IeeeConstants(curve);
            Same(f, at, "a", a); Same(f, at, "b", b); Same(f, at, "p", p);
        }
    }

    private static void Same(JsonElement f, string at, string name, double code)
    {
        if (!f.TryGetProperty(name, out JsonElement e)) throw new CurveDataException($"{at}.{name} is required for this family.");
        double v = StudyCaseJson.Num(e, $"{at}.{name}");
        if (v != code)
            throw new CurveDataException($"{at}.{name} = {v.ToString("R", CultureInfo.InvariantCulture)} disagrees with the equation constant in code ({code.ToString("R", CultureInfo.InvariantCulture)}).");
    }
}
