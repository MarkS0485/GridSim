using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>One transcribed number with where it was read: the clause, the figure (null when it is in the text) and
/// whether it was read off a figure (the GB figures are marked NOT TO SCALE; a figure-read value is a labelled one).</summary>
public sealed record ExtValue(double Value, string Clause, string? Figure, bool FigureRead);

/// <summary>A labelled corner of a voltage-duration profile: a dip to <see cref="VoltagePu"/> lasting
/// <see cref="DurationSeconds"/>.</summary>
public sealed record DurationCorner(double DurationSeconds, double VoltagePu);

/// <summary>ECC.6.3.16 fast fault current injection as transcribed (see the ext file for each source).</summary>
public sealed record FfciRequirement(string Clause, IReadOnlyList<string> ModuleTypes, IReadOnlyList<string> Technologies,
    string GridFormingClause, string GridFormingText, string UnbalancedClause, string UnbalancedText,
    ExtValue InceptionVoltage, ExtValue MaxReactiveCurrent,
    string LineClause, string LineFigure, double LineV1, double LineI1, double LineV2, double LineI2,
    string EnvelopeClause, string EnvelopeFigure, IReadOnlyList<(double Seconds, double Fraction)> Envelope,
    string BlockingClause, string BlockingText, ExtValue MaxClearance, string AgreedDeviationsClause, string AgreedDeviationsText);

/// <summary>An ECC.6.3.15.9 voltage-duration profile for dips over 140 ms. <see cref="Sloped"/> lists the indices i whose
/// segment from corner i to corner i + 1 is sloped (a duration strictly inside it has no labelled voltage).</summary>
public sealed record LongFaultProfile(string Clause, string Figure, string AppliesTo, IReadOnlyList<string> ModuleTypes, IReadOnlyList<string> Technologies,
    IReadOnlyList<DurationCorner> Corners, IReadOnlyList<int> Sloped)
{
    /// <summary>The labelled dip voltage for a dip of <paramref name="durationSeconds"/>: the voltage of the line at that
    /// duration when it lies on a flat segment or on a corner (at a vertical step, the lower value, which is on the line);
    /// null when it lies strictly inside a sloped segment (the figure labels no voltage there) or outside the profile.</summary>
    public double? LabelledVoltage(double durationSeconds)
    {
        if (Corners.Any(c => c.DurationSeconds == durationSeconds))
            return Corners.Where(c => c.DurationSeconds == durationSeconds).Min(c => c.VoltagePu);
        for (int i = 0; i + 1 < Corners.Count; i++)
        {
            DurationCorner a = Corners[i], b = Corners[i + 1];
            if (!(durationSeconds > a.DurationSeconds && durationSeconds < b.DurationSeconds)) continue;
            if (Sloped.Contains(i)) return null;
            return a.VoltagePu;
        }
        return null;
    }
}

/// <summary>ECC.A.6.2.4.1 open-circuit excitation step response.</summary>
public sealed record AvrRequirement(string Clause, IReadOnlyList<string> ModuleTypes, IReadOnlyList<string> Technologies, string Text,
    double FromPu, double ToPu, double FirstReachSeconds, double SettleBandFraction, double SettleSeconds);

/// <summary>ECC.A.7.2.3.1 PPM / HVDC converter transient voltage control.</summary>
public sealed record VctrlRequirement(string Clause, IReadOnlyList<string> Technologies, string Text, double CommenceSeconds, double NinetyFraction,
    double NinetyZeroToMaxSeconds, double NinetyLeadToLagSeconds, double OscillationWindowSeconds, double OscillationFraction,
    string MvarSecondsClause, string MvarSecondsText);

/// <summary>The GB extension requirement set (<c>gridsim-gridcode-requirements-ext/1</c>).</summary>
public sealed record GridCodeExtRequirementSet(string Id, string Extends, string Title, string Source, string NotInIssue, FfciRequirement Ffci,
    ExtValue LongFaultMaxDuration, string HvdcLongClause, string HvdcLongText, IReadOnlyList<LongFaultProfile> LongFaultProfiles,
    AvrRequirement Avr, VctrlRequirement Vctrl);

/// <summary>
/// The embedded GB extension requirement file <c>Compliance/GridCode/Requirements/gb-ecc-issue6-rev33-ext.json</c>
/// (resource <c>GridSim.Core.gridcode-ext.&lt;id&gt;.json</c>, a prefix of its own, so <see cref="GridCodeRequirements.BuiltInIds"/>
/// is unchanged). Read only by the extension checks.
/// </summary>
public static class GridCodeExtRequirements
{
    public const string Schema = "gridsim-gridcode-requirements-ext/1";
    public const string GbId = "gb-ecc-issue6-rev33-ext";
    private const string Prefix = "GridSim.Core.gridcode-ext.";

    /// <summary>The raw bytes of an embedded extension file.</summary>
    public static byte[] Bytes(string id)
    {
        using Stream? s = typeof(GridCodeExtRequirements).Assembly.GetManifestResourceStream(Prefix + id + ".json")
            ?? throw new ArgumentException($"no built-in grid-code extension set '{id}'.", nameof(id));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The GB extension set.</summary>
    public static GridCodeExtRequirementSet LoadGb() => Parse(Bytes(GbId));

    /// <summary>Parse an extension file; every problem is refused by name.</summary>
    public static GridCodeExtRequirementSet Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        if (Str(r, "schema") != Schema) throw new InvalidDataException($"extension file: schema must be \"{Schema}\".");

        JsonElement f = Obj(r, "ffci");
        JsonElement line = Obj(f, "retainedVoltageLine");
        JsonElement[] pts = Arr(line, "points");
        if (pts.Length != 2) throw new InvalidDataException("extension file: ffci.retainedVoltageLine.points must hold two points.");
        JsonElement env = Obj(f, "envelope");
        var envelope = Arr(env, "points").Select(p => (Num(p, "seconds"), Num(p, "fraction"))).ToArray();
        for (int i = 1; i < envelope.Length; i++)
            if (!(envelope[i].Item1 > envelope[i - 1].Item1)) throw new InvalidDataException("extension file: ffci.envelope.points must increase in time.");
        JsonElement blocking = Obj(f, "blocking");
        var ffci = new FfciRequirement(Str(f, "clause"), Strings(f, "moduleTypes"), Strings(f, "technologies"),
            Str(f, "gridFormingClause"), Str(f, "gridFormingText"), Str(f, "unbalancedClause"), Str(f, "unbalancedText"),
            Value(Obj(f, "inceptionVoltagePu")), Value(Obj(f, "maxReactiveCurrentPu")),
            Str(line, "clause"), Str(line, "figure"), Num(pts[0], "voltagePu"), Num(pts[0], "reactiveCurrentPu"), Num(pts[1], "voltagePu"), Num(pts[1], "reactiveCurrentPu"),
            Str(env, "clause"), Str(env, "figure"), envelope, Str(blocking, "clause"), Str(blocking, "text"), Value(Obj(f, "maxClearanceSeconds")),
            Str(f, "agreedDeviationsClause"), Str(f, "agreedDeviationsText"));
        if (!(ffci.LineV1 > ffci.LineV2)) throw new InvalidDataException("extension file: the retained-voltage line points must fall in voltage.");

        JsonElement fl = Obj(r, "frtLong");
        var profiles = new List<LongFaultProfile>();
        foreach (JsonElement p in Arr(fl, "profiles"))
        {
            var corners = Arr(p, "corners").Select(c => new DurationCorner(Num(c, "durationSeconds"), Num(c, "voltagePu"))).ToArray();
            for (int i = 1; i < corners.Length; i++)
                if (corners[i].DurationSeconds < corners[i - 1].DurationSeconds) throw new InvalidDataException("extension file: frtLong corners must not go back in duration.");
            profiles.Add(new LongFaultProfile(Str(p, "clause"), Str(p, "figure"), Str(p, "appliesTo"), Strings(p, "moduleTypes"), Strings(p, "technologies"),
                corners, Arr(p, "sloped").Select(x => x.GetInt32()).ToArray()));
        }

        JsonElement a = Obj(r, "avr");
        var avr = new AvrRequirement(Str(a, "clause"), Strings(a, "moduleTypes"), Strings(a, "technologies"), Str(a, "text"),
            Num(a, "fromPu"), Num(a, "toPu"), Num(a, "firstReachSeconds"), Num(a, "settleBandFraction"), Num(a, "settleSeconds"));
        JsonElement v = Obj(r, "vctrl");
        var vctrl = new VctrlRequirement(Str(v, "clause"), Strings(v, "technologies"), Str(v, "text"), Num(v, "commenceSeconds"), Num(v, "ninetyFraction"),
            Num(v, "ninetyZeroToMaxSeconds"), Num(v, "ninetyLeadToLagSeconds"), Num(v, "oscillationWindowSeconds"), Num(v, "oscillationFraction"),
            Str(v, "mvarSecondsClause"), Str(v, "mvarSecondsText"));

        return new GridCodeExtRequirementSet(Str(r, "id"), Str(r, "extends"), Str(r, "title"), Str(r, "source"), Str(r, "notInIssue"), ffci,
            Value(Obj(fl, "maxDurationSeconds")), Str(fl, "hvdcClause"), Str(fl, "hvdcText"), profiles, avr, vctrl);
    }

    private static ExtValue Value(JsonElement e) => new(Num(e, "value"), Str(e, "clause"),
        e.TryGetProperty("figure", out JsonElement fg) && fg.ValueKind == JsonValueKind.String ? fg.GetString() : null,
        e.TryGetProperty("figureRead", out JsonElement fr) && fr.ValueKind == JsonValueKind.True);

    private static JsonElement Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Object ? v : throw new InvalidDataException($"extension file: '{key}' must be an object.");

    private static JsonElement[] Arr(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : throw new InvalidDataException($"extension file: '{key}' must be an array.");

    private static IReadOnlyList<string> Strings(JsonElement e, string key) => Arr(e, key).Select(x => x.GetString() ?? throw new InvalidDataException($"extension file: '{key}' must hold strings.")).ToArray();

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"extension file: '{key}' is required.");

    private static double Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"extension file: '{key}' must be a number."));
}
