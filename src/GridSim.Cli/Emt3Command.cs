using System.Globalization;
using System.Text.Json;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.FrequencyDependent;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Lines;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-16: <c>gridsim emt3 &lt;study.json&gt;</c>, EMT depth A on the nodal solver (GridSim.Core Dynamics/Emt/Nodal, Nonlinear,
/// FrequencyDependent): three-phase networks in SI units with line-constants line references (constant-parameter or
/// frequency-dependent), faults, switching, arresters and saturable devices. Reads a strict <c>gridsim-emt3/1</c> document and
/// prints and writes the traces. The existing <c>emt</c> verb is untouched.
/// Exit codes: 0 completed; 1 the file is missing, is not valid gridsim-emt3/1, or describes an unusable network or a refused
/// line fit; 2 usage; 3 the run ended with a named status other than completed (Newton non-convergence, singular matrix).
/// </summary>
internal static class Emt3Command
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The input is not a valid gridsim-emt3/1 document (exit 1).</summary>
    internal sealed class InputException(string message) : Exception(message);

    /// <summary>A strict object reader: every key must be known, types are checked, errors name the place.</summary>
    private sealed class Obj
    {
        private readonly JsonElement _e;
        private readonly string _where;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public Obj(JsonElement e, string where, params string[] allowed)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new InputException($"{where} must be an object.");
            _e = e; _where = where;
            foreach (JsonProperty p in e.EnumerateObject())
                if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                    throw new InputException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
        }

        public bool Has(string key) => _e.TryGetProperty(key, out _);
        public JsonElement Raw(string key) => _e.TryGetProperty(key, out JsonElement v) ? v : throw new InputException($"{_where}: \"{key}\" is required.");

        public string Str(string key, string? def = null)
        {
            if (!_e.TryGetProperty(key, out JsonElement v))
                return def ?? throw new InputException($"{_where}: \"{key}\" is required.");
            if (v.ValueKind != JsonValueKind.String) throw new InputException($"{_where}: \"{key}\" must be a string.");
            return v.GetString()!;
        }

        public double Num(string key, double? def = null)
        {
            if (!_e.TryGetProperty(key, out JsonElement v))
                return def ?? throw new InputException($"{_where}: \"{key}\" is required.");
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
                throw new InputException($"{_where}: \"{key}\" must be a finite number.");
            return d;
        }

        public int Int(string key, int? def = null)
        {
            double d = Num(key, def);
            if (d != Math.Floor(d) || Math.Abs(d) > int.MaxValue) throw new InputException($"{_where}: \"{key}\" must be a whole number.");
            return (int)d;
        }

        public bool Bool(string key, bool? def = null)
        {
            if (!_e.TryGetProperty(key, out JsonElement v))
                return def ?? throw new InputException($"{_where}: \"{key}\" is required.");
            if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InputException($"{_where}: \"{key}\" must be true or false.");
            return v.GetBoolean();
        }

        public IEnumerable<(JsonElement El, string Where)> Array(string key, bool required = false)
        {
            if (!_e.TryGetProperty(key, out JsonElement v))
            {
                if (required) throw new InputException($"{_where}: \"{key}\" is required.");
                return [];
            }
            if (v.ValueKind != JsonValueKind.Array) throw new InputException($"{_where}: \"{key}\" must be an array.");
            int i = 0;
            return v.EnumerateArray().Select(x => (x, $"{_where}.{key}[{i++}]")).ToList();
        }

        public List<(double X, double Y)> Points(string key)
        {
            var list = new List<(double, double)>();
            foreach ((JsonElement el, string w) in Array(key, true))
            {
                if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2
                    || el.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double d) || !double.IsFinite(d)))
                    throw new InputException($"{w} must be a pair of finite numbers [x, y].");
                list.Add((el[0].GetDouble(), el[1].GetDouble()));
            }
            if (list.Count == 0) throw new InputException($"{_where}: \"{key}\" needs at least one point.");
            return list;
        }
    }

    /// <summary>Parse the study strictly.</summary>
    internal static Emt3Study Parse(JsonElement root)
    {
        var top = new Obj(root, "document", "schema", "time_step_s", "duration_s", "sample_every", "frequency_hz", "integration", "event_rule",
            "newton", "buses", "sources", "branches", "shunts", "lines", "transformers", "saturable_inductors", "arresters", "switches", "faults", "outputs",
            "machines", "dc_buses", "converters", "hvdc");
        string schema = top.Str("schema");
        if (schema != "gridsim-emt3/1") throw new InputException($"schema must be \"gridsim-emt3/1\" but is \"{schema}\".");
        double dt = top.Num("time_step_s"), dur = top.Num("duration_s");
        int every = top.Int("sample_every", 1);
        double f = top.Num("frequency_hz", 50.0);
        IntegrationRule rule = top.Str("integration", "trapezoidal") switch
        {
            "trapezoidal" => IntegrationRule.Trapezoidal,
            "backward-euler" => IntegrationRule.BackwardEuler,
            var s => throw new InputException($"integration must be \"trapezoidal\" or \"backward-euler\" but is \"{s}\"."),
        };
        NodalEventRule ev = top.Str("event_rule", "trapezoidal") switch
        {
            "trapezoidal" => NodalEventRule.Trapezoidal,
            "backward-euler-step" => NodalEventRule.BackwardEulerStep,
            var s => throw new InputException($"event_rule must be \"trapezoidal\" or \"backward-euler-step\" but is \"{s}\"."),
        };
        var np = new NewtonPolicy();
        if (top.Has("newton"))
        {
            var n = new Obj(top.Raw("newton"), "newton", "max_iterations", "voltage_abs_tol", "voltage_rel_tol", "residual_abs_tol", "residual_rel_tol", "max_backtracks");
            np = new NewtonPolicy
            {
                MaxIterations = n.Int("max_iterations", np.MaxIterations), VoltageAbsTol = n.Num("voltage_abs_tol", np.VoltageAbsTol),
                VoltageRelTol = n.Num("voltage_rel_tol", np.VoltageRelTol), ResidualAbsTol = n.Num("residual_abs_tol", np.ResidualAbsTol),
                ResidualRelTol = n.Num("residual_rel_tol", np.ResidualRelTol), MaxBacktracks = n.Int("max_backtracks", np.MaxBacktracks),
            };
        }
        var buses = new List<string>();
        foreach ((JsonElement el, string w) in top.Array("buses", true))
        {
            if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString())) throw new InputException($"{w} must be a non-empty string.");
            buses.Add(el.GetString()!);
        }
        var sources = new List<Emt3Source>();
        foreach ((JsonElement el, string w) in top.Array("sources"))
        {
            var o = new Obj(el, w, "name", "bus", "v_ll_rms", "frequency_hz", "angle_deg", "rs_ohm", "ls_h");
            sources.Add(new Emt3Source(o.Str("name"), o.Str("bus"), o.Num("v_ll_rms"), o.Num("frequency_hz", f), o.Num("angle_deg", 0), o.Num("rs_ohm", 0), o.Num("ls_h", 0)));
        }
        var branches = new List<Emt3Branch>();
        foreach ((JsonElement el, string w) in top.Array("branches"))
        {
            var o = new Obj(el, w, "name", "from", "to", "r1_ohm", "l1_h", "r0_ohm", "l0_h");
            double r1 = o.Num("r1_ohm"), l1 = o.Num("l1_h");
            branches.Add(new Emt3Branch(o.Str("name"), o.Str("from"), o.Str("to"), r1, l1, o.Num("r0_ohm", r1), o.Num("l0_h", l1)));
        }
        var shunts = new List<Emt3Shunt>();
        foreach ((JsonElement el, string w) in top.Array("shunts"))
        {
            var o = new Obj(el, w, "name", "bus", "r_ohm", "c_f", "l_h");
            shunts.Add(new Emt3Shunt(o.Str("name"), o.Str("bus"), o.Num("r_ohm", 0), o.Num("c_f", 0), o.Num("l_h", 0)));
        }
        var lines = new List<Emt3Line>();
        foreach ((JsonElement el, string w) in top.Array("lines"))
        {
            var o = new Obj(el, w, "name", "from", "to", "length_km", "model", "geometry", "band_hz", "order", "samples", "iterations", "enforce_passivity");
            string model = o.Str("model");
            if (model is not ("fd" or "bergeron")) throw new InputException($"{w}: model must be \"fd\" or \"bergeron\" but is \"{model}\".");
            LineGeometry g = LineConstantsCommand.Parse(o.Raw("geometry"));
            double lo = 1.0, hi = 10_000.0;
            if (o.Has("band_hz"))
            {
                JsonElement b = o.Raw("band_hz");
                if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() != 2 || b.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number))
                    throw new InputException($"{w}: band_hz must be [low, high] in Hz.");
                lo = b[0].GetDouble(); hi = b[1].GetDouble();
            }
            var fit = new FdFitOptions
            {
                BandHzLow = lo, BandHzHigh = hi, Order = o.Int("order", 8), Samples = o.Int("samples", 80),
                Iterations = o.Int("iterations", 20), EnforcePassivity = o.Bool("enforce_passivity", true),
            };
            lines.Add(new Emt3Line(o.Str("name"), o.Str("from"), o.Str("to"), o.Num("length_km"), model, g, fit));
        }
        var trs = new List<Emt3SaturableTransformer>();
        foreach ((JsonElement el, string w) in top.Array("transformers"))
        {
            var o = new Obj(el, w, "name", "primary_bus", "secondary_bus", "r1_ohm", "ll1_h", "r2_ohm", "ll2_h", "core_loss_ohm", "curve", "residual_flux_wb");
            double[] res = [0.0];
            if (o.Has("residual_flux_wb"))
            {
                JsonElement r = o.Raw("residual_flux_wb");
                if (r.ValueKind == JsonValueKind.Number) res = [r.GetDouble()];
                else if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 3 && r.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)) res = r.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                else throw new InputException($"{w}: residual_flux_wb must be a number or three numbers (phases a, b, c).");
            }
            trs.Add(new Emt3SaturableTransformer(o.Str("name"), o.Str("primary_bus"), o.Str("secondary_bus"), o.Num("r1_ohm", 0), o.Num("ll1_h"),
                o.Num("r2_ohm", 0), o.Num("ll2_h"), o.Num("core_loss_ohm", 0), o.Points("curve"), res));
        }
        var sats = new List<Emt3SaturableInductor>();
        foreach ((JsonElement el, string w) in top.Array("saturable_inductors"))
        {
            var o = new Obj(el, w, "name", "bus", "phases", "curve", "initial_flux_wb");
            sats.Add(new Emt3SaturableInductor(o.Str("name"), o.Str("bus"), o.Str("phases", "abc"), o.Points("curve"), o.Num("initial_flux_wb", 0)));
        }
        var arr = new List<Emt3Arrester>();
        foreach ((JsonElement el, string w) in top.Array("arresters"))
        {
            var o = new Obj(el, w, "name", "bus", "phases", "model", "vref_v", "iref_a", "alpha", "points");
            string model = o.Str("model");
            if (model == "power-law") arr.Add(new Emt3Arrester(o.Str("name"), o.Str("bus"), o.Str("phases", "abc"), o.Num("vref_v"), o.Num("iref_a"), o.Num("alpha"), null));
            else if (model == "piecewise") arr.Add(new Emt3Arrester(o.Str("name"), o.Str("bus"), o.Str("phases", "abc"), 0, 0, 0, o.Points("points")));
            else throw new InputException($"{w}: model must be \"power-law\" or \"piecewise\" but is \"{model}\".");
        }
        var sws = new List<Emt3Switch>();
        foreach ((JsonElement el, string w) in top.Array("switches"))
        {
            var o = new Obj(el, w, "name", "from", "to", "phases", "ron_ohm", "roff_ohm", "closed", "events");
            var events = new List<Emt3SwitchEvent>();
            foreach ((JsonElement ee, string ew) in o.Array("events"))
            {
                var eo = new Obj(ee, ew, "at_s", "close");
                events.Add(new Emt3SwitchEvent(eo.Num("at_s"), eo.Bool("close")));
            }
            sws.Add(new Emt3Switch(o.Str("name"), o.Str("from"), o.Has("to") ? o.Str("to") : null, o.Str("phases", "abc"), o.Num("ron_ohm", 1e-6), o.Num("roff_ohm", 1e9), o.Bool("closed", false), events));
        }
        var flts = new List<Emt3Fault>();
        foreach ((JsonElement el, string w) in top.Array("faults"))
        {
            var o = new Obj(el, w, "name", "bus", "phases", "r_ohm", "to_ground", "at_s", "clear_s");
            flts.Add(new Emt3Fault(o.Str("name"), o.Str("bus"), o.Str("phases", "abc"), o.Num("r_ohm"), o.Bool("to_ground", true), o.Num("at_s"), o.Has("clear_s") ? o.Num("clear_s") : null));
        }
        var machines = new List<Emt3Machine>();
        foreach ((JsonElement el, string w) in top.Array("machines")) machines.Add(ParseMachine(el, w, f));
        var dcBuses = new List<Emt3DcBus>();
        foreach ((JsonElement el, string w) in top.Array("dc_buses"))
        {
            var o = new Obj(el, w, "name", "capacitance_f", "initial_v", "supply_v", "supply_ohm");
            dcBuses.Add(new Emt3DcBus(o.Str("name"), o.Num("capacitance_f"), o.Num("initial_v"), o.Num("supply_v", 0), o.Num("supply_ohm", 0)));
        }
        var converters = new List<Emt3Converter>();
        foreach ((JsonElement el, string w) in top.Array("converters")) converters.Add(ParseConverter(el, w, f));
        var hvdc = new List<Emt3Hvdc>();
        foreach ((JsonElement el, string w) in top.Array("hvdc")) hvdc.Add(ParseHvdc(el, w, f));
        var outs = new List<Emt3Output>();
        foreach ((JsonElement el, string w) in top.Array("outputs", true))
        {
            var o = new Obj(el, w, "name", "kind", "target", "phase", "end");
            string oname = o.Str("name");
            CheckOutputName(oname, w);
            string okind = o.Str("kind");
            // a machine's own quantities (speed, torque, ...) have no phase; every other kind keeps its required phase
            string ophase = Emt3Runner.TakesNoPhase(okind) ? o.Str("phase", "") : o.Str("phase");
            outs.Add(new Emt3Output(oname, okind, o.Str("target"), ophase, o.Str("end", "from")));
        }
        return new Emt3Study(dt, dur, every, f, rule, ev, np, buses, sources, branches, shunts, lines, trs, sats, arr, sws, flts, outs,
            machines.Count == 0 ? null : machines, dcBuses.Count == 0 ? null : dcBuses, converters.Count == 0 ? null : converters, hvdc.Count == 0 ? null : hvdc);
    }

    private static List<Emt3ValueStep> ValueSteps(Obj o, string key)
    {
        var list = new List<Emt3ValueStep>();
        foreach ((JsonElement el, string w) in o.Array(key))
        {
            var so = new Obj(el, w, "at_s", "delta_pu");
            list.Add(new Emt3ValueStep(so.Num("at_s"), so.Num("delta_pu")));
        }
        return list;
    }

    /// <summary>One <c>hvdc[]</c> entry: <c>vsc</c> (a station), <c>lcc</c> (a bridge group into an R-L load) or <c>dc-line</c> (two poles between stations).</summary>
    private static Emt3Hvdc ParseHvdc(JsonElement el, string w, double studyFrequencyHz)
    {
        string kind = new Obj(el, w, "name", "kind", "grid_v_ll_rms", "frequency_hz", "grid_angle_deg", "grid_r_ohm", "grid_l_h", "filter_r_ohm", "filter_l_h",
            "converter_rs_ohm", "dc_nominal_v", "dc_cap_f", "converter", "mmc", "mode", "set_point", "current_bandwidth_rad_s", "outer_bandwidth_rad_s",
            "control_every", "reactive_current_a", "current_limit_a", "initial_dc_v", "suppression_bandwidth_rad_s", "dc_supply_v", "dc_supply_ohm",
            "v_ll_rms", "commutation_l_h", "source_r_ohm", "pulses", "valve", "neutral_reference_ohm", "alpha_deg", "alpha_steps", "load",
            "from", "to", "r_ohm", "surge_ohm", "delay_s", "initial_current_a").Str("kind");
        string[] vscKeys = ["grid_v_ll_rms", "frequency_hz", "grid_angle_deg", "grid_r_ohm", "grid_l_h", "filter_r_ohm", "filter_l_h", "converter_rs_ohm", "dc_nominal_v", "dc_cap_f",
            "converter", "mmc", "mode", "set_point", "current_bandwidth_rad_s", "outer_bandwidth_rad_s", "control_every", "reactive_current_a", "current_limit_a", "initial_dc_v",
            "suppression_bandwidth_rad_s", "dc_supply_v", "dc_supply_ohm"];
        string[] lccKeys = ["v_ll_rms", "frequency_hz", "commutation_l_h", "source_r_ohm", "pulses", "valve", "neutral_reference_ohm", "alpha_deg", "alpha_steps", "load"];
        string[] lineKeys = ["from", "to", "r_ohm", "surge_ohm", "delay_s", "initial_current_a"];
        string[] mine = kind switch
        {
            "vsc" => vscKeys, "lcc" => lccKeys, "dc-line" => lineKeys,
            _ => throw new InputException($"{w}: kind must be \"vsc\", \"lcc\" or \"dc-line\" but is \"{kind}\"."),
        };
        var o = new Obj(el, w, ["name", "kind", .. mine]);   // the keys of the other kinds are unknown here
        string name = o.Str("name");
        if (kind == "vsc")
        {
            string conv = o.Str("converter", "average");
            Emt3MmcData? mmc = null;
            if (conv is "mmc-average" or "mmc-detailed")
            {
                var mo = new Obj(o.Raw("mmc"), w + ".mmc", "submodules_per_arm", "submodule_cap_f", "arm_resistance_ohm", "arm_inductance_h");
                mmc = new Emt3MmcData(mo.Int("submodules_per_arm"), mo.Num("submodule_cap_f"), mo.Num("arm_resistance_ohm"), mo.Num("arm_inductance_h"));
            }
            else if (conv == "average" && o.Has("mmc")) throw new InputException($"{w}: \"mmc\" is used by an MMC converter only.");
            else if (conv != "average") throw new InputException($"{w}: converter must be \"average\", \"mmc-average\" or \"mmc-detailed\" but is \"{conv}\".");
            string mode = o.Str("mode");
            if (mode is not ("power" or "dc-voltage")) throw new InputException($"{w}: mode must be \"power\" or \"dc-voltage\" but is \"{mode}\".");
            var st = new Emt3HvdcStation(o.Num("grid_v_ll_rms"), o.Num("frequency_hz", studyFrequencyHz), o.Num("grid_angle_deg", 0), o.Num("grid_r_ohm", 0), o.Num("grid_l_h", 0),
                o.Num("filter_r_ohm", 0), o.Num("filter_l_h"), o.Num("converter_rs_ohm", 1e-3), o.Num("dc_nominal_v"), o.Num("dc_cap_f"), conv, mmc, mode,
                o.Num("set_point"), o.Num("current_bandwidth_rad_s", 1000), o.Num("outer_bandwidth_rad_s", 60), o.Int("control_every", 2), o.Num("reactive_current_a", 0),
                o.Num("current_limit_a", 1e6), o.Num("initial_dc_v", 0), o.Num("suppression_bandwidth_rad_s", 0), o.Has("dc_supply_v") ? o.Num("dc_supply_v") : null, o.Num("dc_supply_ohm", 0));
            return new Emt3Hvdc(name, kind, st, null, null);
        }
        if (kind == "lcc")
        {
            double ron = 1e-3, roff = 1e7;
            if (o.Has("valve"))
            {
                var vo = new Obj(o.Raw("valve"), w + ".valve", "ron_ohm", "roff_ohm");
                ron = vo.Num("ron_ohm", ron); roff = vo.Num("roff_ohm", roff);
            }
            var steps = new List<Emt3AlphaStep>();
            foreach ((JsonElement se, string sw) in o.Array("alpha_steps"))
            {
                var so = new Obj(se, sw, "at_s", "alpha_deg");
                steps.Add(new Emt3AlphaStep(so.Num("at_s"), so.Num("alpha_deg")));
            }
            var lo = new Obj(o.Raw("load"), w + ".load", "r_ohm", "l_h");
            var lcc = new Emt3HvdcLcc(o.Num("v_ll_rms"), o.Num("frequency_hz", studyFrequencyHz), o.Num("commutation_l_h"), o.Num("source_r_ohm", 1e-3), o.Int("pulses", 6), ron, roff,
                o.Num("neutral_reference_ohm", 1e6), o.Num("alpha_deg"), steps, lo.Num("r_ohm"), lo.Num("l_h"));
            return new Emt3Hvdc(name, kind, null, lcc, null);
        }
        return new Emt3Hvdc(name, kind, null, null, new Emt3DcLine(o.Str("from"), o.Str("to"), o.Num("r_ohm"), o.Num("surge_ohm"), o.Num("delay_s"), o.Num("initial_current_a", 0)));
    }

    /// <summary>One <c>converters[]</c> entry: strict keys; the keys of the other kind and of the other reference are refused.</summary>
    private static Emt3Converter ParseConverter(JsonElement el, string w, double studyFrequencyHz)
    {
        var o = new Obj(el, w, "name", "kind", "ac_bus", "dc_bus", "rs_ohm", "roff_ohm", "nominal_vdc_v", "filter", "modulation", "pwm", "control");
        string name = o.Str("name"), kind = o.Str("kind");
        if (kind is not ("average" or "switched")) throw new InputException($"{w}: kind must be \"average\" or \"switched\" but is \"{kind}\".");
        bool switched = kind == "switched";
        if (!switched && (o.Has("pwm") || o.Has("roff_ohm"))) throw new InputException($"{w}: \"{(o.Has("pwm") ? "pwm" : "roff_ohm")}\" is used by a switched converter only.");
        if (switched && !o.Has("pwm")) throw new InputException($"{w}: \"pwm\" is required for a switched converter.");
        if (o.Has("modulation") == o.Has("control")) throw new InputException($"{w}: give exactly one of \"modulation\" and \"control\".");
        Emt3Filter? filter = null;
        if (o.Has("filter"))
        {
            var fo = new Obj(o.Raw("filter"), w + ".filter", "r_ohm", "l_h");
            filter = new Emt3Filter(fo.Num("r_ohm", 0), fo.Num("l_h"));
        }
        Emt3Modulation? mod = null;
        if (o.Has("modulation"))
        {
            var mo = new Obj(o.Raw("modulation"), w + ".modulation", "index", "frequency_hz", "phase_deg");
            mod = new Emt3Modulation(mo.Num("index"), mo.Num("frequency_hz", studyFrequencyHz), mo.Num("phase_deg", 0));
        }
        Emt3Pwm? pwm = null;
        if (o.Has("pwm"))
        {
            var po = new Obj(o.Raw("pwm"), w + ".pwm", "carrier_hz", "carrier_phase");
            pwm = new Emt3Pwm(po.Num("carrier_hz"), po.Num("carrier_phase", 0));
        }
        Emt3Control? ctl = null;
        if (o.Has("control"))
        {
            var co = new Obj(o.Raw("control"), w + ".control", "bandwidth_rad_s", "control_every", "frequency_hz", "phase_deg", "id_a", "iq_a", "steps");
            var steps = new List<Emt3RefStep>();
            foreach ((JsonElement se, string sw) in co.Array("steps"))
            {
                var so = new Obj(se, sw, "at_s", "id_a", "iq_a");
                steps.Add(new Emt3RefStep(so.Num("at_s"), so.Num("id_a"), so.Num("iq_a", 0)));
            }
            ctl = new Emt3Control(co.Num("bandwidth_rad_s"), co.Int("control_every", 1), co.Num("frequency_hz", studyFrequencyHz), co.Num("phase_deg", 0),
                co.Num("id_a", 0), co.Num("iq_a", 0), steps);
        }
        return new Emt3Converter(name, kind, o.Str("ac_bus"), o.Str("dc_bus"), o.Num("rs_ohm", 1e-3), o.Num("roff_ohm", 1e7),
            o.Has("nominal_vdc_v") ? o.Num("nominal_vdc_v") : null, filter, mod, pwm, ctl);
    }

    /// <summary>One <c>machines[]</c> entry: strict keys, kind-specific keys refused for the other kind.</summary>
    private static Emt3Machine ParseMachine(JsonElement el, string w, double studyFrequencyHz)
    {
        var o = new Obj(el, w, "name", "kind", "bus", "neutral", "rating_mva", "rated_kv_ll", "frequency_hz", "parameters", "initial",
            "torque_steps", "field_voltage_steps", "start_speed_pu", "load_torque");
        string name = o.Str("name"), kind = o.Str("kind");
        if (kind is not ("synchronous" or "induction")) throw new InputException($"{w}: kind must be \"synchronous\" or \"induction\" but is \"{kind}\".");
        bool sync = kind == "synchronous";
        foreach (string k in sync ? new[] { "start_speed_pu", "load_torque" } : new[] { "neutral", "initial", "torque_steps", "field_voltage_steps" })
            if (o.Has(k)) throw new InputException($"{w}: \"{k}\" is not used by a{(sync ? " synchronous" : "n induction")} machine.");
        string neutral = o.Str("neutral", "grounded");
        if (neutral is not ("grounded" or "isolated")) throw new InputException($"{w}: neutral must be \"grounded\" or \"isolated\" but is \"{neutral}\".");
        double rating = o.Num("rating_mva"), kv = o.Num("rated_kv_ll"), fhz = o.Num("frequency_hz", studyFrequencyHz);
        Emt3SynchronousData? sd = null;
        Emt3InductionData? id = null;
        Emt3MachineInitial? init = null;
        Emt3LoadTorque? load = null;
        if (sync)
        {
            var p = new Obj(o.Raw("parameters"), w + ".parameters", "inertia_s", "damping_pu", "xd_pu", "xdp_pu", "xdpp_pu", "xq_pu", "xqp_pu", "xqpp_pu",
                "tdop_s", "tdopp_s", "tqop_s", "tqopp_s", "xl_pu", "ra_pu", "x0_pu");
            sd = new Emt3SynchronousData(p.Num("inertia_s"), p.Num("damping_pu", 0), p.Num("xd_pu"), p.Num("xdp_pu"), p.Num("xdpp_pu"), p.Num("xq_pu"),
                p.Num("xqp_pu"), p.Num("xqpp_pu"), p.Num("tdop_s"), p.Num("tdopp_s"), p.Num("tqop_s"), p.Num("tqopp_s"), p.Num("xl_pu", 0), p.Num("ra_pu", 0),
                p.Has("x0_pu") ? p.Num("x0_pu") : null);
            if (o.Has("initial"))
            {
                var io = new Obj(o.Raw("initial"), w + ".initial", "mode", "v_pu", "angle_deg", "p_mw", "q_mvar");
                string mode = io.Str("mode");
                if (mode is not ("no-load" or "phasor")) throw new InputException($"{w}.initial: mode must be \"no-load\" or \"phasor\" but is \"{mode}\".");
                if (mode == "no-load" && (io.Has("p_mw") || io.Has("q_mvar"))) throw new InputException($"{w}.initial: a no-load start has no p_mw or q_mvar.");
                init = new Emt3MachineInitial(mode, io.Num("v_pu", 1.0), io.Num("angle_deg", 0.0), io.Num("p_mw", 0.0), io.Num("q_mvar", 0.0));
            }
        }
        else
        {
            var p = new Obj(o.Raw("parameters"), w + ".parameters", "ra_pu", "xa_pu", "xm_pu", "r1_pu", "x1_pu", "inertia_s");
            id = new Emt3InductionData(p.Num("ra_pu"), p.Num("xa_pu"), p.Num("xm_pu"), p.Num("r1_pu"), p.Num("x1_pu"), p.Num("inertia_s"));
            if (o.Has("load_torque"))
            {
                var lo = new Obj(o.Raw("load_torque"), w + ".load_torque", "k_pu", "speed_exponent");
                load = new Emt3LoadTorque(lo.Num("k_pu"), lo.Num("speed_exponent", 2.0));
            }
        }
        return new Emt3Machine(name, kind, o.Str("bus"), neutral == "grounded", rating, kv, fhz, sd, id, init, ValueSteps(o, "torque_steps"),
            ValueSteps(o, "field_voltage_steps"), o.Num("start_speed_pu", 0.0), load);
    }

    /// <summary>
    /// An output's name is its trace column and, as <c>max_abs_&lt;name&gt;</c>, a summary key, so it follows the shared result
    /// writer's rule (lower-case snake_case: a letter, then letters, digits or underscores) and may not be the time column. Checking it
    /// here refuses a bad name as bad input (exit 1) instead of failing inside the result writer after the run.
    /// </summary>
    private static void CheckOutputName(string name, string where)
    {
        bool ok = name.Length > 0 && name[0] is >= 'a' and <= 'z';
        foreach (char ch in name) ok &= ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
        if (!ok) throw new InputException($"{where}: output name '{name}' must be lower-case snake_case (a letter, then letters, digits or underscores): it is a trace column and a summary key.");
        if (name == "time_s") throw new InputException($"{where}: output name 'time_s' is the time column; choose another name.");
    }

    private static string R(double v) => v.ToString("R", Inv);

    /// <summary>Fill the shared result writer.</summary>
    internal static void Fill(CliResult rep, Emt3Study s, Emt3Result r)
    {
        NodalResult n = r.Run;
        rep.Set("schema", "gridsim-emt3/1").Set("status", n.Status.ToString()).Set("status_message", n.StatusMessage)
            .Set("failed_at_s", double.IsNaN(n.FailedAtSeconds) ? null : n.FailedAtSeconds)
            .Set("time_step_s", n.StepSeconds).Set("duration_s", s.DurationS).Set("sample_every", s.SampleEvery).Set("method", n.Method)
            .Set("steps", n.Stats.Steps).Set("refactorisations", n.Stats.Refactorisations)
            .Set("newton_iterations", n.Stats.NewtonIterations).Set("newton_max_iterations_per_step", n.Stats.MaxNewtonIterations)
            .Set("newton_max_iterations", n.Newton.MaxIterations).Set("newton_voltage_abs_tol_v", n.Newton.VoltageAbsTol)
            .Set("newton_voltage_rel_tol", n.Newton.VoltageRelTol).Set("newton_residual_abs_tol_a", n.Newton.ResidualAbsTol)
            .Set("newton_residual_rel_tol", n.Newton.ResidualRelTol).Set("newton_max_backtracks", n.Newton.MaxBacktracks)
            .Set("samples", n.Times.Length);
        for (int k = 0; k < n.ProbeNames.Count; k++)
        {
            double max = 0.0;
            foreach (double x in n.Series[k]) max = Math.Max(max, Math.Abs(x));
            rep.Set("max_abs_" + n.ProbeNames[k], max);
        }
        if (r.Machines is { Count: > 0 } || r.Converters is { Count: > 0 }) rep.Set("comparator_status", "COMPARATOR-UNVERIFIED");
        if (r.Converters is { Count: > 0 } cr)
        {
            rep.Set("switching_timing", r.SwitchingTiming);
            CliResultTable ct = rep.Table("converters", "converter", "model_class", "description");
            foreach ((string name, string cls, string desc) in cr) ct.Row(name, cls, desc);
        }
        if (r.Machines is { Count: > 0 } mr)
        {
            CliResultTable mt = rep.Table("machines", "machine", "kind", "initial_load_angle_rad", "initial_field_voltage_pu", "initial_field_current_pu",
                "initial_mechanical_torque_pu", "initial_id_pu", "initial_iq_pu", "start_speed_pu");
            foreach (Emt3MachineReport m in mr)
                mt.Row(m.Name, m.Kind, m.LoadAngleRad, m.FieldVoltagePu, m.FieldCurrentPu, m.MechanicalTorquePu, m.IdPu, m.IqPu, m.StartSpeedPu);
        }
        CliResultTable fits = rep.Table("line_fits", "line", "mode", "order", "delay_s", "yc_max_rel_error", "yc_rms_rel_error", "a_max_rel_error",
            "a_rms_rel_error", "yc_passivity_shift_s", "a_passivity_scale", "band_hz_low", "band_hz_high");
        foreach ((string line, FdLineFitReport? fit) in r.LineFits)
            if (fit is not null)
                foreach (FdModeFitReport m in fit.Modes)
                    fits.Row(line, m.Name, m.Order, m.DelaySeconds, m.YcMaxRelError, m.YcRmsRelError, m.AMaxRelError, m.ARmsRelError,
                        m.YcPassivityShiftSiemens, m.APassivityScale, fit.BandHzLow, fit.BandHzHigh);
        var cols = new List<string> { "time_s" };
        cols.AddRange(n.ProbeNames);
        CliResultTable tr = rep.Table("traces", cols.ToArray());
        for (int i = 0; i < n.Times.Length; i++)
        {
            var row = new object?[cols.Count];
            row[0] = n.Times[i];
            for (int k = 0; k < n.ProbeNames.Count; k++) row[k + 1] = n.Series[k][i];
            tr.Row(row);
        }
        rep.Complete();
    }

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a)
    {
        string? file = null;
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (t.StartsWith("--", StringComparison.Ordinal)) return Usage($"emt3 does not use {t}. emt3 takes a study file and the result-file options --json, --csv and --out only.");
            if (file is not null) return Usage($"emt3 takes one study file but got: {file} {t}");
            file = t;
        }
        if (file is null) return Usage("usage: gridsim emt3 <study.json> [--json <file> --csv <file> --out <file>] (see 'gridsim emt3 --help')");

        Emt3Study study;
        try
        {
            if (!File.Exists(file)) { Console.Error.WriteLine($"error: study file not found: {file}"); return CliExit.InputError; }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = false });
            study = Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InputException or LineConstantsCommand.InputException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: emt3: {file}: {ex.Message}");
            return CliExit.InputError;
        }

        Emt3Result res;
        try { res = Emt3Runner.Run(study); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: emt3: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        NodalResult n = res.Run;
        Console.WriteLine($"EMT3 - {n.Method}, step {R(n.StepSeconds)} s, {n.Stats.Steps} step(s), {study.Buses.Count} bus(es), {n.ProbeNames.Count} output(s)");
        Console.WriteLine($"  linear matrix factorisations {n.Stats.Refactorisations}; Newton iterations {n.Stats.NewtonIterations} (most in one step {n.Stats.MaxNewtonIterations}); " +
            $"Newton tolerances: voltage {R(n.Newton.VoltageAbsTol)} V + {R(n.Newton.VoltageRelTol)} relative, residual {R(n.Newton.ResidualAbsTol)} A + {R(n.Newton.ResidualRelTol)} relative");
        foreach ((string line, FdLineFitReport? fit) in res.LineFits)
        {
            if (fit is null) { Console.WriteLine($"  line {line}: constant-parameter (no fit)"); continue; }
            foreach (FdModeFitReport m in fit.Modes)
                Console.WriteLine($"  line {line} mode {m.Name}: order {m.Order}, delay {R(m.DelaySeconds)} s, fit error Yc max {m.YcMaxRelError:E2} rms {m.YcRmsRelError:E2}; A max {m.AMaxRelError:E2} rms {m.ARmsRelError:E2}; passivity shift {m.YcPassivityShiftSiemens:E2} S, scale {R(m.APassivityScale)}");
        }
        if (res.Machines is { Count: > 0 } mach)
            foreach (Emt3MachineReport m in mach)
                Console.WriteLine(m.Kind == "synchronous"
                    ? $"  machine {m.Name} (synchronous): initial load angle {R(m.LoadAngleRad!.Value)} rad, field voltage {R(m.FieldVoltagePu!.Value)} pu, mechanical torque {R(m.MechanicalTorquePu!.Value)} pu (COMPARATOR-UNVERIFIED)"
                    : $"  machine {m.Name} (induction): starts at {R(m.StartSpeedPu!.Value)} pu speed (COMPARATOR-UNVERIFIED)");
        if (res.Converters is { Count: > 0 } convs)
        {
            foreach ((string cname, string cls, string desc) in convs) Console.WriteLine($"  converter {cname} ({cls}): {desc} (COMPARATOR-UNVERIFIED)");
            Console.WriteLine($"  switching: {res.SwitchingTiming}");
        }
        for (int k = 0; k < n.ProbeNames.Count; k++)
        {
            double max = 0.0;
            foreach (double x in n.Series[k]) max = Math.Max(max, Math.Abs(x));
            Console.WriteLine($"  {n.ProbeNames[k],-16} max |x| = {max:E6}");
        }
        int exit = CliExit.Ok;
        if (n.Status != NodalStatus.Completed)
        {
            Console.Error.WriteLine($"emt3: the run did not complete: {n.StatusMessage}");
            exit = CliExit.Verdict;
        }
        else Console.WriteLine("  status: completed");

        CliResult? rep = CliOutput.Report;
        if (rep is not null) Fill(rep, study, res);
        return exit;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("usage:", StringComparison.Ordinal) ? message : "error: emt3: " + message);
        return CliExit.Usage;
    }
}
