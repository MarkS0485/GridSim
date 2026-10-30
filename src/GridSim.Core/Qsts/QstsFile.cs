using System.Globalization;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Qsts;

/// <summary>
/// Reader of the QSTS study file, format <c>gridsim-qsts/1</c> (manual chapter 19, <c>qsts</c>). Strict: every key is
/// checked, an unknown key or a value of the wrong type is refused with its JSON path, and generators are named by index
/// or name against the case. Profiles come inline (<c>values</c>) or from a CSV file (<c>csv</c> + <c>column</c>,
/// resolved against the study file's folder). The result still goes through <see cref="QstsStudy.Validate"/> when run.
/// </summary>
public static class QstsFile
{
    /// <summary>The format tag.</summary>
    public const string Format = "gridsim-qsts/1";

    /// <summary>Read the study file at <paramref name="path"/> for <paramref name="model"/>.</summary>
    public static QstsStudy Load(string path, GridModel model)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"QSTS file {path}: cannot be read ({ex.Message})."); }
        return Parse(text, Path.GetFileName(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", model);
    }

    /// <summary>Parse study JSON; <paramref name="baseDir"/> resolves relative CSV paths.</summary>
    public static QstsStudy Parse(string json, string label, string baseDir, GridModel model)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false }); }
        catch (JsonException ex) { throw new InvalidDataException($"QSTS file {label}: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            var r = new Obj(doc.RootElement, label, "");
            string format = r.Str("format", required: true)!;
            if (format != Format) throw new InvalidDataException($"QSTS file {label}: \"format\" must be \"{Format}\" (got \"{format}\").");
            int step = r.Int("stepSeconds", required: true)!.Value;
            int steps = r.Int("steps", required: true)!.Value;
            string modeText = r.Str("mode") ?? "timed";
            QstsControlMode mode = modeText switch
            {
                "timed" => QstsControlMode.Timed,
                "steady" => QstsControlMode.Steady,
                _ => throw r.Bad("mode", "\"timed\" or \"steady\""),
            };
            var study = new QstsStudy
            {
                StepSeconds = step, Steps = steps, Mode = mode,
                StartSecondOfDay = r.TimeOfDay("startTime") ?? 0,
                MaxControlIterations = r.Int("maxControlIterations") ?? PowerFlow.SteadyStateSolver.DefaultMaxOltcPasses,
                MaxContinuousIterations = r.Int("maxContinuousIterations") ?? 200,
                ContinuousTolerance = r.Num("continuousTolerance") ?? 1e-7,
                RecordStates = r.Bool("recordStates") ?? true,
                GenerationFollowsLoad = r.Bool("generationFollowsLoad") ?? false,
            };

            var profiles = new List<QstsProfile>();
            if (r.Child("profiles") is { } pObj)
                foreach (string name in pObj.Keys())
                {
                    Obj p = pObj.Object(name)!;
                    profiles.Add(ReadProfile(p, name, baseDir));
                }
            var loads = new List<QstsLoadBinding>();
            foreach (Obj l in r.Array("loads"))
            {
                IReadOnlyList<int>? buses;
                JsonElement be = l.Raw("buses", required: true)!.Value;
                if (be.ValueKind == JsonValueKind.String && be.GetString() == "all") buses = null;
                else if (be.ValueKind == JsonValueKind.Array) buses = be.EnumerateArray().Select((e, i) => IntOf(e, l.Path("buses") + $"[{i}]")).ToArray();
                else throw l.Bad("buses", "\"all\" or an array of bus ids");
                // Bug hunt 2026-10-29, finding 81: an empty list binds the profile to no bus, so it would be silently
                // inert for the whole study. Refused; a profile that should not apply is left out of "loads".
                if (buses is { Count: 0 }) throw l.Bad("buses", "\"all\" or a non-empty array of bus ids (an empty list binds the profile to nothing)");
                loads.Add(new QstsLoadBinding(buses, l.Str("profile", required: true)!, l.Bool("scaleP") ?? true, l.Bool("scaleQ") ?? true));
                l.Done();
            }
            var gens = new List<QstsGeneratorBinding>();
            foreach (Obj g in r.Array("generators"))
            {
                int? index = g.Int("index");
                string? name = g.Str("name");
                if ((index is null) == (name is null)) throw g.Bad("index", "exactly one of \"index\" (from 0) or \"name\"");
                if (name is not null)
                {
                    int found = -1;
                    for (int i = 0; i < model.Generators.Count; i++) if (model.Generators[i].Name == name) { if (found >= 0) throw new InvalidDataException($"QSTS file {label}: {g.Path("name")}: generator name '{name}' is not unique in the case; use \"index\"."); found = i; }
                    if (found < 0) throw new InvalidDataException($"QSTS file {label}: {g.Path("name")}: no in-service generator named '{name}'.");
                    index = found;
                }
                gens.Add(new QstsGeneratorBinding(index!.Value, g.Str("profile", required: true)!, g.Bool("absoluteMw") ?? false));
                g.Done();
            }
            var adds = new List<QstsLoadAddition>();
            foreach (Obj a in r.Array("loadAdditions"))
            {
                adds.Add(new QstsLoadAddition(a.Str("name", required: true)!, a.Int("bus", required: true)!.Value, a.Num("mw", required: true)!.Value,
                    a.Str("profile"), a.Num("powerFactor") ?? 1.0));
                a.Done();
            }
            var pvs = new List<QstsPvUnit>();
            foreach (Obj u in r.Array("pv"))
            {
                InverterSettings inv = new();
                if (u.Child("control") is { } c)
                {
                    InverterFunction f = (c.Str("function") ?? "unity") switch
                    {
                        "unity" => InverterFunction.Unity,
                        "pf" => InverterFunction.FixedPowerFactor,
                        "volt-var" => InverterFunction.VoltVar,
                        "volt-watt" => InverterFunction.VoltWatt,
                        "volt-var-volt-watt" => InverterFunction.VoltVarVoltWatt,
                        _ => throw c.Bad("function", "unity, pf, volt-var, volt-watt or volt-var-volt-watt"),
                    };
                    VoltVarCurve? vv = null;
                    if (c.Raw("voltVar") is { } vve)
                    {
                        if (vve.ValueKind == JsonValueKind.String)
                            vv = vve.GetString() switch
                            {
                                "1547-A" => VoltVarCurve.Ieee1547CategoryA,
                                "1547-B" => VoltVarCurve.Ieee1547CategoryB,
                                _ => throw c.Bad("voltVar", "\"1547-A\", \"1547-B\" or an object {v1,q1,v2,q2,v3,q3,v4,q4}"),
                            };
                        else
                        {
                            Obj o = c.Object("voltVar")!;
                            vv = new VoltVarCurve(o.Num("v1", true)!.Value, o.Num("q1", true)!.Value, o.Num("v2", true)!.Value, o.Num("q2", true)!.Value,
                                o.Num("v3", true)!.Value, o.Num("q3", true)!.Value, o.Num("v4", true)!.Value, o.Num("q4", true)!.Value);
                            o.Done();
                        }
                    }
                    VoltWattCurve? vw = null;
                    if (c.Raw("voltWatt") is { } vwe)
                    {
                        if (vwe.ValueKind == JsonValueKind.String)
                            vw = vwe.GetString() == "1547" ? VoltWattCurve.Ieee1547Default : throw c.Bad("voltWatt", "\"1547\" or an object {v1,p1,v2,p2}");
                        else
                        {
                            Obj o = c.Object("voltWatt")!;
                            vw = new VoltWattCurve(o.Num("v1", true)!.Value, o.Num("p1", true)!.Value, o.Num("v2", true)!.Value, o.Num("p2", true)!.Value);
                            o.Done();
                        }
                    }
                    InverterPriority pr = (c.Str("priority") ?? "var") switch
                    {
                        "var" => InverterPriority.Var,
                        "watt" => InverterPriority.Watt,
                        _ => throw c.Bad("priority", "\"var\" or \"watt\""),
                    };
                    string? pfMode = c.Str("pfMode");
                    bool absorbing = pfMode switch
                    {
                        null or "absorbing" => true,
                        "injecting" => false,
                        _ => throw c.Bad("pfMode", "\"absorbing\" or \"injecting\""),
                    };
                    inv = new InverterSettings(f, c.Num("powerFactor") ?? 1.0, absorbing, vv, vw, c.Num("responseSeconds") ?? 0.0, pr, c.Int("voltageBus"));
                    c.Done();
                }
                pvs.Add(new QstsPvUnit(u.Str("name", required: true)!, u.Int("bus", required: true)!.Value, u.Num("ratedMw", required: true)!.Value,
                    u.Num("ratedMva") ?? u.Num("ratedMw", required: true)!.Value, u.Str("profile"), inv));
                u.Done();
            }
            var stores = new List<QstsStorageUnit>();
            foreach (Obj u in r.Array("storage"))
            {
                Obj d = u.Child("dispatch") ?? throw u.Bad("dispatch", "an object {mode, ...}");
                StorageMode sm = d.Str("mode", required: true) switch
                {
                    "schedule" => StorageMode.Schedule,
                    "peak-shave" => StorageMode.PeakShave,
                    "self-consumption" => StorageMode.SelfConsumption,
                    _ => throw d.Bad("mode", "schedule, peak-shave or self-consumption"),
                };
                stores.Add(new QstsStorageUnit(u.Str("name", required: true)!, u.Int("bus", required: true)!.Value, u.Num("ratedMw", required: true)!.Value,
                    u.Num("capacityMwh", required: true)!.Value, u.Num("initialSoc") ?? 0.5, u.Num("socMin") ?? 0.0, u.Num("socMax") ?? 1.0,
                    u.Num("chargeEfficiency") ?? 1.0, u.Num("dischargeEfficiency") ?? 1.0, sm,
                    d.Str("profile"), d.Str("branch"), d.Num("highMw") ?? 0.0, d.Num("lowMw") ?? 0.0));
                d.Done();
                u.Done();
            }
            var caps = new List<QstsCapacitor>();
            foreach (Obj c in r.Array("capacitors"))
            {
                CapacitorControl cc = (c.Str("control") ?? "voltage") switch
                {
                    "voltage" => CapacitorControl.Voltage,
                    "var" => CapacitorControl.Var,
                    "time" => CapacitorControl.Time,
                    _ => throw c.Bad("control", "voltage, var or time"),
                };
                (double on, double off) = cc switch
                {
                    CapacitorControl.Voltage => (c.Num("onPu", true)!.Value, c.Num("offPu", true)!.Value),
                    CapacitorControl.Var => (c.Num("onMvar", true)!.Value, c.Num("offMvar", true)!.Value),
                    _ => ((double)c.TimeOfDay("onTime", true)!.Value, (double)c.TimeOfDay("offTime", true)!.Value),
                };
                caps.Add(new QstsCapacitor(c.Str("name", required: true)!, c.Int("bus", required: true)!.Value, c.Num("stepMvar", required: true)!.Value,
                    c.Int("minSteps") ?? 0, c.Int("maxSteps", required: true)!.Value, c.Int("initialSteps") ?? 0, cc, on, off,
                    c.Num("delaySeconds") ?? 0.0, c.Num("offDelaySeconds") ?? c.Num("delaySeconds") ?? 0.0, c.Num("deadTimeSeconds") ?? 0.0,
                    c.Int("controlsBus"), c.Str("branch")));
                c.Done();
            }
            bool oltcEnabled = true;
            OltcTiming oltcDefault = new();
            var oltcSettings = new Dictionary<string, OltcTiming>(StringComparer.Ordinal);
            OltcCoordination coord = OltcCoordination.Independent;
            if (r.Child("oltc") is { } o2)
            {
                oltcEnabled = o2.Bool("enabled") ?? true;
                coord = (o2.Str("coordination") ?? "independent") switch
                {
                    "independent" => OltcCoordination.Independent,
                    "sequential" => OltcCoordination.Sequential,
                    _ => throw o2.Bad("coordination", "\"independent\" or \"sequential\""),
                };
                if (o2.Child("default") is { } d) { oltcDefault = ReadTiming(d, new OltcTiming()); d.Done(); }
                foreach (Obj s in o2.Array("settings"))
                {
                    string tx = s.Str("transformer", required: true)!;
                    if (oltcSettings.ContainsKey(tx)) throw new InvalidDataException($"QSTS file {label}: {s.Path("transformer")}: transformer '{tx}' is set twice.");
                    oltcSettings[tx] = ReadTiming(s, oltcDefault);
                    s.Done();
                }
                o2.Done();
            }
            QstsLimits limits = new();
            if (r.Child("limits") is { } lim)
            {
                limits = new QstsLimits(lim.Num("vminPu") ?? 0.94, lim.Num("vmaxPu") ?? 1.06, lim.Num("loadingPercent") ?? 100.0, lim.Bool("perBusVoltageLimits") ?? false);
                lim.Done();
            }
            r.Done();
            return study with
            {
                Profiles = profiles, Loads = loads, Generators = gens, LoadAdditions = adds, Pv = pvs, Storage = stores, Capacitors = caps,
                OltcEnabled = oltcEnabled, OltcDefault = oltcDefault, OltcSettings = oltcSettings, OltcCoordination = coord, Limits = limits,
            };
        }
    }

    private static OltcTiming ReadTiming(Obj o, OltcTiming d) => new(
        o.Num("delaySeconds") ?? d.DelaySeconds,
        o.Num("interTapSeconds") ?? d.InterTapSeconds,
        (o.Str("sequence") ?? (d.Sequence == OltcSequence.Sequential ? "sequential" : "continuous")) switch
        {
            "continuous" => OltcSequence.Continuous,
            "sequential" => OltcSequence.Sequential,
            _ => throw o.Bad("sequence", "\"continuous\" or \"sequential\""),
        },
        (o.Str("characteristic") ?? (d.Characteristic == OltcCharacteristic.Inverse ? "inverse" : "definite")) switch
        {
            "definite" => OltcCharacteristic.Definite,
            "inverse" => OltcCharacteristic.Inverse,
            _ => throw o.Bad("characteristic", "\"definite\" or \"inverse\""),
        },
        o.Num("minDelaySeconds") ?? d.MinDelaySeconds,
        o.Num("ldcRPu") ?? d.LdcRPu,
        o.Num("ldcXPu") ?? d.LdcXPu,
        o.Int("priority") ?? d.Priority);

    private static QstsProfile ReadProfile(Obj p, string name, string baseDir)
    {
        int step = p.Int("stepSeconds", required: true)!.Value;
        QstsInterpolation interp = (p.Str("interpolation") ?? "hold") switch
        {
            "hold" => QstsInterpolation.Hold,
            "linear" => QstsInterpolation.Linear,
            _ => throw p.Bad("interpolation", "\"hold\" or \"linear\""),
        };
        bool wrap = p.Bool("wrap") ?? false;
        double scale = p.Num("scale") ?? 1.0;
        JsonElement? inline = p.Raw("values");
        string? csv = p.Str("csv");
        if ((inline is null) == (csv is null)) throw p.Bad("values", "exactly one of \"values\" (an array) or \"csv\" (a file)");
        double[] values;
        if (inline is { } arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) throw p.Bad("values", "an array of numbers");
            values = arr.EnumerateArray().Select((e, i) => NumOf(e, p.Path("values") + $"[{i}]")).ToArray();
            if (p.Raw("column") is not null) throw p.Bad("column", "only with \"csv\"");
        }
        else
        {
            string path = Path.IsPathRooted(csv!) ? csv! : Path.Combine(baseDir, csv!);
            values = ReadCsvColumn(path, p.Raw("column"), p.Path("column"));
        }
        if (scale != 1.0) for (int i = 0; i < values.Length; i++) values[i] *= scale;
        p.Done();
        return new QstsProfile(name, step, values, interp, wrap);
    }

    /// <summary>One column of a CSV file: a header row naming the columns when the first row is not numeric; the column
    /// is chosen by its header (string) or index from 0 (number); default the first column when the file has one,
    /// else refused. Every data cell must be a finite number (invariant culture).</summary>
    public static double[] ReadCsvColumn(string path, JsonElement? column, string where)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"{where}: CSV file {path} cannot be read ({ex.Message})."); }
        // Fields are split by SplitCsvLine, which honours double-quoted fields (a spreadsheet exports a header such as
        // "load, kW" with quotes); a plain Split(',') cut such a field in two and shifted every later column by one.
        var rows = new List<string[]>();
        for (int li = 0; li < lines.Length; li++)
        {
            string l = lines[li].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            rows.Add(SplitCsvLine(l, $"{where}: CSV file {path}, line {li + 1}"));
        }
        if (rows.Count == 0) throw new InvalidDataException($"{where}: CSV file {path} has no rows.");
        bool header = !double.TryParse(rows[0][0], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        string[]? names = header ? rows[0] : null;
        int col;
        if (column is null)
        {
            if (rows[0].Length != 1) throw new InvalidDataException($"{where}: CSV file {path} has {rows[0].Length} columns; name one with \"column\".");
            col = 0;
        }
        else if (column.Value.ValueKind == JsonValueKind.String)
        {
            if (names is null) throw new InvalidDataException($"{where}: CSV file {path} has no header row to find column '{column.Value.GetString()}' in.");
            col = Array.IndexOf(names, column.Value.GetString());
            if (col < 0) throw new InvalidDataException($"{where}: CSV file {path} has no column '{column.Value.GetString()}' (has: {string.Join(", ", names)}).");
        }
        else
        {
            col = IntOf(column.Value, where);
            // A negative index is not a column; it used to pass the "col >= length" guard and die on rows[r][col] with a
            // bare IndexOutOfRangeException.
            if (col < 0)
                throw new InvalidDataException($"{where}: CSV column index {col} is negative; columns are numbered from 0.");
        }
        var values = new List<double>();
        for (int r = header ? 1 : 0; r < rows.Count; r++)
        {
            if (col >= rows[r].Length || !double.TryParse(rows[r][col], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                throw new InvalidDataException($"{where}: CSV file {path}, data row {r + 1}: column {col} is not a finite number.");
            values.Add(v);
        }
        if (values.Count == 0) throw new InvalidDataException($"{where}: CSV file {path} has no data rows.");
        return values.ToArray();
    }

    /// <summary>Split one CSV line into trimmed fields, RFC 4180 style: a field may be wrapped in double quotes (and then
    /// hold commas), and a doubled quote inside a quoted field is one quote. A quote that is never closed, or text after
    /// a closing quote before the next comma, is refused by name rather than guessed at.</summary>
    internal static string[] SplitCsvLine(string line, string where)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        int i = 0;
        while (true)
        {
            // Leading blanks of a field are not part of it (fields are trimmed, as before).
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= line.Length) throw new InvalidDataException($"{where}: a quoted field is never closed.");
                    char c = line[i++];
                    if (c == '"')
                    {
                        if (i < line.Length && line[i] == '"') { cur.Append('"'); i++; }
                        else break;
                    }
                    else cur.Append(c);
                }
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i < line.Length && line[i] != ',')
                    throw new InvalidDataException($"{where}: text follows a closing quote before the next comma.");
                fields.Add(cur.ToString());
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                fields.Add(line[start..i].Trim());
            }
            cur.Clear();
            if (i >= line.Length) break;
            i++;   // the comma
            if (i == line.Length) { fields.Add(""); break; }   // a trailing comma ends with an empty field
        }
        return fields.ToArray();
    }

    private static int IntOf(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int i)) throw new InvalidDataException($"{where}: must be a whole number.");
        return i;
    }

    private static double NumOf(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || !double.IsFinite(d)) throw new InvalidDataException($"{where}: must be a finite number.");
        return d;
    }

    /// <summary>A JSON object being read: tracks the keys taken, so <see cref="Done"/> can refuse any unknown key.</summary>
    private sealed class Obj
    {
        private readonly JsonElement _e;
        private readonly string _label, _path;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public Obj(JsonElement e, string label, string path)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"QSTS file {label}: {(path.Length == 0 ? "the top level" : path)} must be an object.");
            _e = e; _label = label; _path = path;
        }

        public string Path(string key) => _path.Length == 0 ? "/" + key : _path + "/" + key;

        public InvalidDataException Bad(string key, string expected) =>
            new($"QSTS file {_label}: {Path(key)}: expected {expected}.");

        public IEnumerable<string> Keys() { foreach (JsonProperty p in _e.EnumerateObject()) { _used.Add(p.Name); yield return p.Name; } }

        public JsonElement? Raw(string key, bool required = false)
        {
            _used.Add(key);
            if (_e.TryGetProperty(key, out JsonElement v) && v.ValueKind != JsonValueKind.Null) return v;
            if (required) throw new InvalidDataException($"QSTS file {_label}: {Path(key)} is required.");
            return null;
        }

        public string? Str(string key, bool required = false)
        {
            JsonElement? v = Raw(key, required);
            if (v is null) return null;
            if (v.Value.ValueKind != JsonValueKind.String) throw Bad(key, "a string");
            return v.Value.GetString();
        }

        public double? Num(string key, bool required = false)
        {
            JsonElement? v = Raw(key, required);
            return v is null ? null : NumOf(v.Value, $"QSTS file {_label}: {Path(key)}");
        }

        public int? Int(string key, bool required = false)
        {
            JsonElement? v = Raw(key, required);
            return v is null ? null : IntOf(v.Value, $"QSTS file {_label}: {Path(key)}");
        }

        public bool? Bool(string key)
        {
            JsonElement? v = Raw(key);
            if (v is null) return null;
            if (v.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad(key, "true or false");
            return v.Value.GetBoolean();
        }

        /// <summary>A time of day as "HH:MM" or "HH:MM:SS", or a number of seconds; returned in seconds [0, 86400).</summary>
        public int? TimeOfDay(string key, bool required = false)
        {
            JsonElement? v = Raw(key, required);
            if (v is null) return null;
            if (v.Value.ValueKind == JsonValueKind.Number)
            {
                int s = IntOf(v.Value, $"QSTS file {_label}: {Path(key)}");
                if (s < 0 || s >= 86400) throw Bad(key, "seconds of the day in [0, 86400)");
                return s;
            }
            if (v.Value.ValueKind == JsonValueKind.String)
            {
                string[] parts = v.Value.GetString()!.Split(':');
                if (parts.Length is 2 or 3 && parts.All(x => x.Length == 2 && x.All(char.IsAsciiDigit)))
                {
                    int h = int.Parse(parts[0], CultureInfo.InvariantCulture), m = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    int sec = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
                    if (h < 24 && m < 60 && sec < 60) return h * 3600 + m * 60 + sec;
                }
            }
            throw Bad(key, "a time of day \"HH:MM\" or \"HH:MM:SS\", or seconds of the day");
        }

        public Obj? Child(string key)
        {
            JsonElement? v = Raw(key);
            return v is null ? null : new Obj(v.Value, _label, Path(key));
        }

        public Obj? Object(string key) => Child(key);

        public IEnumerable<Obj> Array(string key)
        {
            JsonElement? v = Raw(key);
            if (v is null) yield break;
            if (v.Value.ValueKind != JsonValueKind.Array) throw Bad(key, "an array");
            int i = 0;
            foreach (JsonElement e in v.Value.EnumerateArray()) { yield return new Obj(e, _label, $"{Path(key)}[{i}]"); i++; }
        }

        /// <summary>Refuse any key of this object that was never read.</summary>
        public void Done()
        {
            foreach (JsonProperty p in _e.EnumerateObject())
                if (!_used.Contains(p.Name))
                    throw new InvalidDataException($"QSTS file {_label}: {Path(p.Name)}: unknown key '{p.Name}'.");
        }
    }
}
