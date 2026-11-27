using System.Globalization;
using System.Text.Json;
using GridSim.Core.Dynamics.Models.Iec61400;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC5 WP23: reads one member of a dynamics study file's optional <c>iec61400</c> array: an IEC 61400-27-1 style Type 4A or 4B
/// wind turbine bound to one unit of the RAW case. Strict: every object's fields are checked against its required and optional
/// names, numbers must be finite, and a parameter the turbine type does not read is refused (Type 4A takes <c>tpwtref</c> and no
/// <c>mechanical</c>; Type 4B takes <c>tpaero</c> and <c>mechanical</c>). The parameter names are the CGMES 2.4.15 DY names where
/// CGMES has one, else Dynawo's 2020 names in lower case (see <see cref="Iec61400Data"/>).
/// <code>
/// { "machine": {"bus": 2, "id": "1"}, "type": "4A" | "4B", "edition": 2020,
///   "genSystem": {"dipmax", "diqmin", "diqmax", "tg"},
///   "pControl": {"dpmax", "tpord", "dprefmax", "dprefmin", "mpuscale" (true/false), "updip", "tpwtref" (4A) | "tpaero" (4B)},
///   "qControl": {"windQcontrolModesType" 0..4, "windLVRTQcontrolModesType" 0..2, "iqh1", "iqmax", "iqmin", "iqpost", "kiq",
///                "kiu", "kpq", "kpu", "kpufrt", "kqv", "qmax", "qmin", "rdroop", "xdroop", "tpost", "tqord", "tuss", "udb1",
///                "udb2", "umax", "umin", "uqdip", "uqrise", "uref0"},
///   "currentLimit": {"imax", "imaxdip", "mdfslim", "mqpri", "kpqu", "upqumax", "ipvdl": [[u, i], ...], "iqvdl": [[u, i], ...]},
///   "mechanical": {"cdrt", "hgen", "hwtr", "kdrt"}            (4B only),
///   "pll": {"tpll", "upll1", "upll2"},
///   "measurement": {"tufilt", "tpfilt", "tqfilt"},
///   "protection": {"uover", "uunder", "fover", "funder", "tuover", "tuunder", "tfover", "tfunder" (tables [[level, s], ...]),
///                  "tufilt", "tffilt", "dfmax"}                (optional) }
/// </code>
/// </summary>
public static class Iec61400StudyFile
{
    private static readonly string[] UnitRequired = { "machine", "type", "edition", "genSystem", "pControl", "qControl", "currentLimit", "pll", "measurement" };
    private static readonly string[] UnitOptional = { "mechanical", "protection" };
    private static readonly string[] MachineFields = { "bus", "id" };
    private static readonly string[] GenFields = { "dipmax", "diqmin", "diqmax", "tg" };
    private static readonly string[] PRequired = { "dpmax", "tpord", "dprefmax", "dprefmin", "mpuscale", "updip" };
    private static readonly string[] PTypeA = { "tpwtref" };
    private static readonly string[] PTypeB = { "tpaero" };
    private static readonly string[] QFields =
    {
        "windQcontrolModesType", "windLVRTQcontrolModesType", "iqh1", "iqmax", "iqmin", "iqpost", "kiq", "kiu", "kpq", "kpu", "kpufrt", "kqv",
        "qmax", "qmin", "rdroop", "xdroop", "tpost", "tqord", "tuss", "udb1", "udb2", "umax", "umin", "uqdip", "uqrise", "uref0",
    };
    private static readonly string[] LimFields = { "imax", "imaxdip", "mdfslim", "mqpri", "kpqu", "upqumax", "ipvdl", "iqvdl" };
    private static readonly string[] MechFields = { "cdrt", "hgen", "hwtr", "kdrt" };
    private static readonly string[] PllFields = { "tpll", "upll1", "upll2" };
    private static readonly string[] MeasFields = { "tufilt", "tpfilt", "tqfilt" };
    private static readonly string[] ProtFields = { "uover", "uunder", "fover", "funder", "tuover", "tuunder", "tfover", "tfunder", "tufilt", "tffilt", "dfmax" };

    /// <summary>One unit. Throws <see cref="InvalidDataException"/> naming the field (with <paramref name="what"/> as the path)
    /// for any problem, including every value problem <see cref="Iec61400Data.Problems"/> finds.</summary>
    public static Iec61400StudyUnit Unit(JsonElement e, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        Obj(e, what, UnitRequired, UnitOptional);
        JsonElement me = e.GetProperty("machine");
        Obj(me, what + ".machine", MachineFields, Array.Empty<string>());
        var machine = new DynamicsMachineId(Int(me.GetProperty("bus"), what + ".machine.bus"), Str(me.GetProperty("id"), what + ".machine.id"));
        Iec61400TurbineType type = Str(e.GetProperty("type"), what + ".type") switch
        {
            "4A" => Iec61400TurbineType.Type4A,
            "4B" => Iec61400TurbineType.Type4B,
            string s => throw new InvalidDataException($"{what}.type must be \"4A\" or \"4B\" (got \"{s}\")."),
        };
        bool b = type == Iec61400TurbineType.Type4B;
        int edition = Int(e.GetProperty("edition"), what + ".edition");

        JsonElement g = Part(e, "genSystem", what, GenFields, Array.Empty<string>());
        var gen = new Iec61400GenSystem(N(g, "dipmax", what), N(g, "diqmin", what), N(g, "diqmax", what), N(g, "tg", what));

        JsonElement p = Part(e, "pControl", what, PRequired.Concat(b ? PTypeB : PTypeA).ToArray(), Array.Empty<string>());
        var pc = new Iec61400PControl(N(p, "dpmax", what), N(p, "tpord", what), N(p, "dprefmax", what), N(p, "dprefmin", what),
            b ? 0.0 : N(p, "tpwtref", what), b ? N(p, "tpaero", what) : 0.0, Flag(p.GetProperty("mpuscale"), what + ".pControl.mpuscale"), N(p, "updip", what));

        JsonElement q = Part(e, "qControl", what, QFields, Array.Empty<string>());
        int mode = Int(q.GetProperty("windQcontrolModesType"), what + ".qControl.windQcontrolModesType");
        if (mode is < 0 or > 4) throw new InvalidDataException($"{what}.qControl.windQcontrolModesType must be 0 to 4 (got {mode.ToString(CultureInfo.InvariantCulture)}).");
        var qc = new Iec61400QControl((Iec61400QControlMode)mode, Int(q.GetProperty("windLVRTQcontrolModesType"), what + ".qControl.windLVRTQcontrolModesType"),
            N(q, "iqh1", what), N(q, "iqmax", what), N(q, "iqmin", what), N(q, "iqpost", what), N(q, "kiq", what), N(q, "kiu", what), N(q, "kpq", what),
            N(q, "kpu", what), N(q, "kpufrt", what), N(q, "kqv", what), N(q, "qmax", what), N(q, "qmin", what), N(q, "rdroop", what), N(q, "xdroop", what),
            N(q, "tpost", what), N(q, "tqord", what), N(q, "tuss", what), N(q, "udb1", what), N(q, "udb2", what), N(q, "umax", what), N(q, "umin", what),
            N(q, "uqdip", what), N(q, "uqrise", what), N(q, "uref0", what));

        JsonElement l = Part(e, "currentLimit", what, LimFields, Array.Empty<string>());
        var lim = new Iec61400CurrentLimit(N(l, "imax", what), N(l, "imaxdip", what), Flag(l.GetProperty("mdfslim"), what + ".currentLimit.mdfslim"),
            Flag(l.GetProperty("mqpri"), what + ".currentLimit.mqpri"), N(l, "kpqu", what), N(l, "upqumax", what),
            Table(l.GetProperty("ipvdl"), what + ".currentLimit.ipvdl"), Table(l.GetProperty("iqvdl"), what + ".currentLimit.iqvdl"));

        Iec61400Mechanical? mech = null;
        if (e.TryGetProperty("mechanical", out _))
        {
            if (!b) throw new InvalidDataException($"{what}.mechanical: a Type 4A unit has no drive train model; remove it.");
            JsonElement m = Part(e, "mechanical", what, MechFields, Array.Empty<string>());
            mech = new Iec61400Mechanical(N(m, "cdrt", what), N(m, "hgen", what), N(m, "hwtr", what), N(m, "kdrt", what));
        }
        else if (b) throw new InvalidDataException($"{what}: a Type 4B unit needs mechanical (cdrt, hgen, hwtr, kdrt).");

        JsonElement pl = Part(e, "pll", what, PllFields, Array.Empty<string>());
        var pll = new Iec61400Pll(N(pl, "tpll", what), N(pl, "upll1", what), N(pl, "upll2", what));
        JsonElement ms = Part(e, "measurement", what, MeasFields, Array.Empty<string>());
        var meas = new Iec61400Measurement(N(ms, "tufilt", what), N(ms, "tpfilt", what), N(ms, "tqfilt", what));

        Iec61400Protection? prot = null;
        if (e.TryGetProperty("protection", out _))
        {
            JsonElement r = Part(e, "protection", what, ProtFields, Array.Empty<string>());
            string w = what + ".protection.";
            prot = new Iec61400Protection(N(r, "uover", what), N(r, "uunder", what), N(r, "fover", what), N(r, "funder", what),
                Table(r.GetProperty("tuover"), w + "tuover"), Table(r.GetProperty("tuunder"), w + "tuunder"),
                Table(r.GetProperty("tfover"), w + "tfover"), Table(r.GetProperty("tfunder"), w + "tfunder"),
                N(r, "tufilt", what), N(r, "tffilt", what), N(r, "dfmax", what));
        }

        var data = new Iec61400Data(type, edition, gen, pc, qc, lim, mech, pll, meas, prot);
        IReadOnlyList<string> problems = Iec61400Data.Problems(data);
        if (problems.Count > 0) throw new InvalidDataException($"{what}: " + string.Join(" ", problems));
        return new Iec61400StudyUnit(machine, data);
    }

    // A named sub-object, checked.
    private static JsonElement Part(JsonElement e, string name, string what, string[] required, string[] optional)
    {
        JsonElement p = e.GetProperty(name);
        Obj(p, what + "." + name, required, optional);
        return p;
    }

    private static double N(JsonElement parent, string name, string what)
    {
        JsonElement v = parent.GetProperty(name);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double x) || !double.IsFinite(x))
            throw new InvalidDataException($"{what}: {name} must be a finite number.");
        return x;
    }

    private static Iec61400Table Table(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0) throw new InvalidDataException($"{what} must be a non-empty array of [input, output] pairs.");
        var xs = new List<double>();
        var ys = new List<double>();
        int i = 0;
        foreach (JsonElement pt in e.EnumerateArray())
        {
            string at = $"{what}[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() != 2) throw new InvalidDataException($"{at} must be an [input, output] pair.");
            double x = Finite(pt[0], at), y = Finite(pt[1], at);
            xs.Add(x); ys.Add(y);
            i++;
        }
        return new Iec61400Table(xs, ys);
    }

    private static double Finite(JsonElement v, string what)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double x) || !double.IsFinite(x))
            throw new InvalidDataException($"{what} must hold finite numbers.");
        return x;
    }

    private static void Obj(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw new InvalidDataException($"{what}: unknown field '{p.Name}'.");
        foreach (string r in required)
            if (!e.TryGetProperty(r, out _)) throw new InvalidDataException($"{what}: missing required field '{r}'.");
    }

    private static bool Flag(JsonElement e, string what) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"{what} must be true or false."),
    };

    private static int Int(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw new InvalidDataException($"{what} must be a whole number.");
        return v;
    }

    private static string Str(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{what} must be a string.");
        return e.GetString()!;
    }
}
