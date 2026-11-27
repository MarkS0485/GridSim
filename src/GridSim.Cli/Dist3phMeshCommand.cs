using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Lines;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-15: <c>gridsim dist3ph-mesh &lt;case.json&gt; [--steps steps.json] [--warm-start] [--tol A] [--max-iter N] [--scale F]
/// [--pin-floating]</c>, or <c>gridsim dist3ph-mesh --from-dist3ph &lt;feeder.json&gt; [--switch-z-pu pu]</c> (RC4 wiring: the legacy
/// <c>dist3ph</c> feeder, transformers and regulators included, lifted into the phase frame by Core's
/// <see cref="MultiPhaseFeederAdapter"/>). The meshed unbalanced load flow in the explicit phase frame (Core Distribution/Meshed): a strict
/// <c>gridsim-dist3ph-mesh/1</c> case of nodes with named wires, sources, full primitive branch matrices, loads by connection and
/// ZIP model, earthing, earth grids and transformer units. The existing <c>dist3ph</c> verb is unchanged. All arithmetic is in Core;
/// this file parses strictly, prints and fills the shared result writer. Exit codes: 0 every step converged; 1 the file cannot be
/// read or is not a valid case; 2 usage; 3 a step did not converge or is INCOMPLETE (the reasons are named and no number is invented).
/// </summary>
internal static class Dist3phMeshCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    internal const string Schema = "gridsim-dist3ph-mesh/1";
    internal const string StepsSchema = "gridsim-dist3ph-mesh-steps/1";

    internal sealed class InputException(string message) : Exception(message);

    // ---- strict reading ---------------------------------------------------------------------------------------------

    private static JsonElement Object(JsonElement e, string where, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InputException($"{where} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new InputException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
        return e;
    }

    private static JsonElement Req(JsonElement o, string key, string where)
        => o.TryGetProperty(key, out JsonElement v) ? v : throw new InputException($"{where}: \"{key}\" is required.");

    private static string Str(JsonElement o, string key, string where)
    {
        JsonElement v = Req(o, key, where);
        return v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InputException($"{where}: \"{key}\" must be a non-empty string.");
    }

    private static string? OptStr(JsonElement o, string key, string where)
        => o.TryGetProperty(key, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? Str(o, key, where) : null;

    private static double Num(JsonElement v, string where)
        => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : throw new InputException($"{where} must be a finite number.");

    private static double Num(JsonElement o, string key, string where) => Num(Req(o, key, where), $"{where}: \"{key}\"");

    private static double OptNum(JsonElement o, string key, string where, double fallback)
        => o.TryGetProperty(key, out JsonElement v) ? Num(v, $"{where}: \"{key}\"") : fallback;

    private static JsonElement Arr(JsonElement o, string key, string where)
    {
        JsonElement v = Req(o, key, where);
        return v.ValueKind == JsonValueKind.Array ? v : throw new InputException($"{where}: \"{key}\" must be an array.");
    }

    private static string[] Strings(JsonElement o, string key, string where)
    {
        JsonElement a = Arr(o, key, where);
        var list = new List<string>();
        foreach (JsonElement x in a.EnumerateArray())
            list.Add(x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()) ? x.GetString()! : throw new InputException($"{where}: \"{key}\" must hold non-empty strings."));
        return list.ToArray();
    }

    private static double[] Numbers(JsonElement o, string key, string where, int? count = null)
    {
        JsonElement a = Arr(o, key, where);
        double[] v = a.EnumerateArray().Select((x, i) => Num(x, $"{where}: \"{key}\"[{i}]")).ToArray();
        if (count is int c && v.Length != c) throw new InputException($"{where}: \"{key}\" needs {c} numbers but has {v.Length}.");
        return v;
    }

    private static double[,] Matrix(JsonElement o, string key, string where, int n)
    {
        JsonElement a = Arr(o, key, where);
        var rows = a.EnumerateArray().ToList();
        if (rows.Count != n) throw new InputException($"{where}: \"{key}\" needs {n} rows but has {rows.Count}.");
        var m = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (rows[i].ValueKind != JsonValueKind.Array) throw new InputException($"{where}: \"{key}\"[{i}] must be an array.");
            var cells = rows[i].EnumerateArray().ToList();
            if (cells.Count != n) throw new InputException($"{where}: \"{key}\"[{i}] needs {n} numbers but has {cells.Count}.");
            for (int j = 0; j < n; j++) m[i, j] = Num(cells[j], $"{where}: \"{key}\"[{i}][{j}]");
        }
        return m;
    }

    private static Complex[,]? ComplexMatrix(JsonElement o, string rKey, string xKey, string where, int n, bool required)
    {
        bool hasR = o.TryGetProperty(rKey, out _), hasX = o.TryGetProperty(xKey, out _);
        if (!hasR && !hasX)
            return required ? throw new InputException($"{where}: \"{rKey}\" and \"{xKey}\" are required.") : null;
        if (!hasR || !hasX) throw new InputException($"{where}: \"{rKey}\" and \"{xKey}\" go together.");
        double[,] r = Matrix(o, rKey, where, n), x = Matrix(o, xKey, where, n);
        var z = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) z[i, j] = new Complex(r[i, j], x[i, j]);
        return z;
    }

    private static Complex Impedance(JsonElement o, string where, string rKey = "r_ohm", string xKey = "x_ohm")
        => new(Num(o, rKey, where), OptNum(o, xKey, where, 0.0));

    // ---- case --------------------------------------------------------------------------------------------------------

    /// <summary>A parsed case: the assembled frame (banks and regulators included) and the names of its capacitor banks.</summary>
    internal sealed record ParsedCase(RegulatedFrame Frame, IReadOnlyList<string> Capacitors);

    /// <summary>Parse a case document strictly. Any unknown or missing key, wrong type or structurally impossible model is an <see cref="InputException"/>.</summary>
    internal static ParsedCase Parse(JsonElement root)
    {
        Object(root, "document", "schema", "name", "nodes", "sources", "branches", "loads", "earthing", "grids", "transformers", "regulators", "banks", "capacitors",
            "split_phase_sources", "centre_tap_transformers", "swer");
        string schema = Str(root, "schema", "document");
        if (schema != Schema) throw new InputException($"schema must be \"{Schema}\" but is \"{schema}\".");
        string name = Str(root, "name", "document");
        if (root.TryGetProperty("swer", out JsonElement swer))
        {
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "name" or "swer"))
                    throw new InputException($"document: \"swer\" is a complete single-wire earth-return case; \"{p.Name}\" cannot be combined with it.");
            return ParseSwer(swer, name);
        }

        var nodes = new List<FrameNode>();
        int k = 0;
        foreach (JsonElement n in Arr(root, "nodes", "document").EnumerateArray())
        {
            string w = $"nodes[{k++}]";
            Object(n, w, "name", "wires");
            nodes.Add(new FrameNode(Str(n, "name", w), Strings(n, "wires", w)));
        }

        var sources = new List<FrameSource>();
        k = 0;
        // "sources" may be left out when "split_phase_sources" gives the case its source.
        foreach (JsonElement s in (root.TryGetProperty("sources", out _) || !root.TryGetProperty("split_phase_sources", out _)
                     ? Arr(root, "sources", "document").EnumerateArray() : Enumerable.Empty<JsonElement>()))
        {
            string w = $"sources[{k++}]";
            Object(s, w, "node", "wires", "magnitude_v", "angle_deg", "series_r_ohm", "series_x_ohm");
            string[] wires = Strings(s, "wires", w);
            double[] mag = Numbers(s, "magnitude_v", w, wires.Length), ang = Numbers(s, "angle_deg", w, wires.Length);
            var volts = new Complex[wires.Length];
            for (int i = 0; i < wires.Length; i++) volts[i] = GridSim.Core.Numerics.DetMath.FromPolar(mag[i], ang[i] * Math.PI / 180.0);
            Complex[]? series = null;
            if (s.TryGetProperty("series_r_ohm", out _) || s.TryGetProperty("series_x_ohm", out _))
            {
                double[] sr = Numbers(s, "series_r_ohm", w, wires.Length), sx = Numbers(s, "series_x_ohm", w, wires.Length);
                series = new Complex[wires.Length];
                for (int i = 0; i < wires.Length; i++) series[i] = new Complex(sr[i], sx[i]);
            }
            sources.Add(new FrameSource(Str(s, "node", w), wires, volts, series));
        }
        // A 120/240 V split-phase source (Core SplitPhase.IdealSource): l1 = +V/2, l2 = -V/2, the neutral wire n fixed at 0 V.
        k = 0;
        if (root.TryGetProperty("split_phase_sources", out _))
            foreach (JsonElement s in Arr(root, "split_phase_sources", "document").EnumerateArray())
            {
                string w = $"split_phase_sources[{k++}]";
                Object(s, w, "node", "volts", "series_r_ohm", "series_x_ohm");
                double volts = Num(s, "volts", w);
                if (!(volts > 0)) throw new InputException($"{w}: \"volts\" is the full winding voltage (for example 240) and must be above 0.");
                bool hasSeries = s.TryGetProperty("series_r_ohm", out _) || s.TryGetProperty("series_x_ohm", out _);
                sources.Add(SplitPhase.IdealSource(Str(s, "node", w), volts, hasSeries ? new Complex(OptNum(s, "series_r_ohm", w, 0.0), OptNum(s, "series_x_ohm", w, 0.0)) : null));
            }

        var branches = new List<FrameBranch>();
        k = 0;
        if (root.TryGetProperty("branches", out JsonElement bl))
            foreach (JsonElement b in bl.EnumerateArray())
            {
                string w = $"branches[{k++}]";
                Object(b, w, "name", "from", "to", "wires", "r_ohm", "x_ohm", "shunt_g_siemens", "shunt_b_siemens", "kron_wire");
                string[] wires = Strings(b, "wires", w);
                Complex[,] z = ComplexMatrix(b, "r_ohm", "x_ohm", w, wires.Length, true)!;
                branches.Add(new FrameBranch(Str(b, "name", w), Str(b, "from", w), Str(b, "to", w), wires, z,
                    ComplexMatrix(b, "shunt_g_siemens", "shunt_b_siemens", w, wires.Length, false), OptStr(b, "kron_wire", w)));
            }

        var loads = new List<FrameLoad>();
        k = 0;
        if (root.TryGetProperty("loads", out JsonElement ll))
            foreach (JsonElement l in ll.EnumerateArray())
            {
                string w = $"loads[{k++}]";
                Object(l, w, "name", "node", "connection", "phases", "return_wire", "from", "to", "p_w", "q_var", "nominal_v", "zip");
                string lname = Str(l, "name", w), node = Str(l, "node", w), conn = Str(l, "connection", w);
                double vnom = Num(l, "nominal_v", w);
                Zip zip = Zip.ConstantPower;
                if (l.TryGetProperty("zip", out _))
                {
                    double[] zf = Numbers(l, "zip", w, 3);
                    zip = new Zip(zf[0], zf[1], zf[2]);
                }
                string[]? phases = l.TryGetProperty("phases", out _) ? Strings(l, "phases", w) : null;
                int legs = conn == "line-line" ? 1 : conn == "delta" ? 3 : (phases?.Length ?? 3);
                double[] p = Numbers(l, "p_w", w, legs), q = Numbers(l, "q_var", w, legs);
                var s = new Complex[legs];
                for (int i = 0; i < legs; i++) s[i] = new Complex(p[i], q[i]);
                loads.Add(conn switch
                {
                    "wye" => FrameLoad.Wye(lname, node, s, vnom, zip, OptStr(l, "return_wire", w) ?? "n", phases),
                    "wye-earthed" => FrameLoad.WyeEarthed(lname, node, s, vnom, zip, phases),
                    "wye-floating" => FrameLoad.WyeFloating(lname, node, s, vnom, zip, phases),
                    "delta" => FrameLoad.Delta(lname, node, s, vnom, zip),
                    "line-line" => FrameLoad.LineLine(lname, node, Str(l, "from", w), Str(l, "to", w), s[0], vnom, zip),
                    _ => throw new InputException($"{w}: connection must be wye, wye-earthed, wye-floating, delta or line-line but is \"{conn}\"."),
                });
            }

        var earthing = new List<EarthingElement>();
        k = 0;
        if (root.TryGetProperty("earthing", out JsonElement el))
            foreach (JsonElement e in el.EnumerateArray())
            {
                string w = $"earthing[{k++}]";
                Object(e, w, "node", "wire", "r_ohm", "x_ohm", "grid");
                earthing.Add(new EarthingElement(Str(e, "node", w), Str(e, "wire", w), Impedance(e, w), OptStr(e, "grid", w)));
            }

        var grids = new List<EarthGrid>();
        k = 0;
        if (root.TryGetProperty("grids", out JsonElement gl))
            foreach (JsonElement g in gl.EnumerateArray())
            {
                string w = $"grids[{k++}]";
                Object(g, w, "name", "r_ohm", "x_ohm");
                grids.Add(new EarthGrid(Str(g, "name", w), Impedance(g, w)));
            }

        var units = new List<FrameTransformerUnit>();
        k = 0;
        if (root.TryGetProperty("transformers", out JsonElement tl))
            foreach (JsonElement t in tl.EnumerateArray())
            {
                string w = $"transformers[{k++}]";
                Object(t, w, "name", "windings");
                var windings = new List<FrameWinding>();
                int m = 0;
                foreach (JsonElement wd in Arr(t, "windings", w).EnumerateArray())
                {
                    string ww = $"{w}.windings[{m++}]";
                    Object(wd, ww, "node", "wire", "return_wire", "turns", "r_ohm", "x_ohm");
                    windings.Add(new FrameWinding(Str(wd, "node", ww), Str(wd, "wire", ww), OptStr(wd, "return_wire", ww), Num(wd, "turns", ww), Impedance(wd, ww)));
                }
                units.Add(new FrameTransformerUnit(Str(t, "name", w), windings));
            }
        // A centre-tap distribution transformer (Core SplitPhase.CentreTapUnit): one unit, primary winding and the two secondary halves l1-n and n-l2.
        k = 0;
        if (root.TryGetProperty("centre_tap_transformers", out _))
            foreach (JsonElement t in Arr(root, "centre_tap_transformers", "document").EnumerateArray())
            {
                string w = $"centre_tap_transformers[{k++}]";
                Object(t, w, "name", "primary_node", "primary_wire", "primary_return", "primary_volts", "secondary_node", "secondary_volts",
                    "primary_r_ohm", "primary_x_ohm", "half_r_ohm", "half_x_ohm");
                try
                {
                    units.Add(SplitPhase.CentreTapUnit(Str(t, "name", w), Str(t, "primary_node", w), Str(t, "primary_wire", w), OptStr(t, "primary_return", w),
                        Num(t, "primary_volts", w), Str(t, "secondary_node", w), Num(t, "secondary_volts", w),
                        new Complex(Num(t, "primary_r_ohm", w), OptNum(t, "primary_x_ohm", w, 0.0)), new Complex(Num(t, "half_r_ohm", w), OptNum(t, "half_x_ohm", w, 0.0))));
                }
                catch (ArgumentException ex) { throw new InputException($"{w}: {ex.Message}"); }
            }

        // Capacitor banks are constant-impedance shunt legs (the model the incumbent adapters use), so they are ordinary wye-earthed
        // loads; a step switches one with a factor of 0 or 1 (the steps file's "capacitors" member).
        k = 0;
        var capacitorNames = new List<string>();
        if (root.TryGetProperty("capacitors", out _))
            foreach (JsonElement c in Arr(root, "capacitors", "document").EnumerateArray())
            {
                string w = $"capacitors[{k++}]";
                Object(c, w, "name", "node", "phases", "q_var", "nominal_v");
                string[] phases = Strings(c, "phases", w);
                double[] q = Numbers(c, "q_var", w, phases.Length);
                if (q.Any(v => !(v >= 0))) throw new InputException($"{w}: \"q_var\" is the reactive power supplied per leg at \"nominal_v\" and must not be negative.");
                double vnom = Num(c, "nominal_v", w);
                if (!(vnom > 0)) throw new InputException($"{w}: \"nominal_v\" must be above 0.");
                capacitorNames.Add(Str(c, "name", w));
                loads.Add(FrameLoad.WyeEarthed(Str(c, "name", w), Str(c, "node", w), q.Select(v => new Complex(0.0, -v)).ToArray(), vnom, Zip.ConstantImpedance, phases));
            }

        var banks = new List<BankSpec>();
        k = 0;
        if (root.TryGetProperty("banks", out _))
            foreach (JsonElement b in Arr(root, "banks", "document").EnumerateArray())
            {
                string w = $"banks[{k++}]";
                Object(b, w, "name", "from", "to", "connection", "kv_ll_from", "kv_ll_to", "unit_kva", "r_percent", "x_percent", "z_from_pu", "z_to_pu", "magnetising_pu", "from_return", "to_return");
                string conn = Str(b, "connection", w);
                BankConnection bc = BankConnectionNames.TryGetValue(conn, out BankConnection found) ? found
                    : throw new InputException($"{w}: connection must be one of {string.Join(", ", BankConnectionNames.Keys)} but is \"{conn}\".");
                Complex mag = Complex.Zero;
                if (b.TryGetProperty("magnetising_pu", out JsonElement mg))
                {
                    Object(mg, $"{w}: \"magnetising_pu\"", "g", "b");
                    mag = new Complex(OptNum(mg, "g", w, 0.0), -OptNum(mg, "b", w, 0.0));   // Core reads g - j b
                }
                bool split = b.TryGetProperty("r_percent", out _) || b.TryGetProperty("x_percent", out _);
                bool explicitZ = b.TryGetProperty("z_from_pu", out _) || b.TryGetProperty("z_to_pu", out _);
                if (split == explicitZ) throw new InputException($"{w}: give either \"r_percent\" and \"x_percent\" (the total impedance, split equally between the windings) or \"z_from_pu\" and \"z_to_pu\".");
                string bname = Str(b, "name", w), bfrom = Str(b, "from", w), bto = Str(b, "to", w);
                double kvF = Num(b, "kv_ll_from", w), kvT = Num(b, "kv_ll_to", w), kva = Num(b, "unit_kva", w);
                if (split)
                    banks.Add(BankSpec.Split(bname, bfrom, bto, bc, kvF, kvT, kva, Num(b, "r_percent", w), Num(b, "x_percent", w), mag)
                        with { FromReturn = OptStr(b, "from_return", w), ToReturn = OptStr(b, "to_return", w) });
                else
                    banks.Add(new BankSpec(bname, bfrom, bto, bc, kvF, kvT, kva, PuImpedance(Req(b, "z_from_pu", w), $"{w}: \"z_from_pu\""),
                        PuImpedance(Req(b, "z_to_pu", w), $"{w}: \"z_to_pu\""), mag, OptStr(b, "from_return", w), OptStr(b, "to_return", w)));
            }

        var regulators = new List<StepRegulator>();
        k = 0;
        if (root.TryGetProperty("regulators", out _))
            foreach (JsonElement r in Arr(root, "regulators", "document").EnumerateArray())
            {
                string w = $"regulators[{k++}]";
                Object(r, w, "name", "from", "to", "connection", "phases", "taps", "step_pu", "max_tap", "winding_ohm", "return_wire", "ldc", "ganged");
                string conn = Str(r, "connection", w);
                RegulatorConnection rc = conn switch
                {
                    "wye" => RegulatorConnection.Wye,
                    "open-delta" => RegulatorConnection.OpenDelta,
                    "closed-delta" => RegulatorConnection.ClosedDelta,
                    _ => throw new InputException($"{w}: connection must be wye, open-delta or closed-delta but is \"{conn}\"."),
                };
                string[] phases;
                if (rc == RegulatorConnection.Wye) phases = Strings(r, "phases", w);
                else if (r.TryGetProperty("phases", out _)) throw new InputException($"{w}: \"phases\" is for a wye regulator only (an open delta regulates lines a and c, a closed delta all three).");
                else phases = [];
                int[] taps = Arr(r, "taps", w).EnumerateArray().Select((x, i) => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int t)
                    ? t : throw new InputException($"{w}: \"taps\"[{i}] must be an integer.")).ToArray();
                LdcSettings[]? ldc = null;
                if (r.TryGetProperty("ldc", out JsonElement ld))
                {
                    if (ld.ValueKind != JsonValueKind.Array) throw new InputException($"{w}: \"ldc\" must be an array with one setting per listed phase.");
                    ldc = ld.EnumerateArray().Select((x, i) =>
                    {
                        string lw = $"{w}.ldc[{i}]";
                        Object(x, lw, "vset_volts", "bandwidth_volts", "pt_ratio", "ct_primary_amps", "r_volts", "x_volts");
                        return new LdcSettings(Num(x, "vset_volts", lw), Num(x, "bandwidth_volts", lw), Num(x, "pt_ratio", lw), Num(x, "ct_primary_amps", lw),
                            OptNum(x, "r_volts", lw, 0.0), OptNum(x, "x_volts", lw, 0.0));
                    }).ToArray();
                }
                bool ganged = false;
                if (r.TryGetProperty("ganged", out JsonElement gv))
                    ganged = gv.ValueKind is JsonValueKind.True or JsonValueKind.False ? gv.GetBoolean() : throw new InputException($"{w}: \"ganged\" must be true or false.");
                int maxTap = 16;
                if (r.TryGetProperty("max_tap", out JsonElement mt))
                    maxTap = mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out int mti) ? mti : throw new InputException($"{w}: \"max_tap\" must be an integer.");
                regulators.Add(new StepRegulator(Str(r, "name", w), Str(r, "from", w), Str(r, "to", w), rc, phases, taps,
                    OptNum(r, "step_pu", w, 0.00625), maxTap, OptNum(r, "winding_ohm", w, 1e-5), ldc, ganged, OptStr(r, "return_wire", w)));
            }

        try
        {
            var frame = new RegulatedFrame(name, nodes, sources, branches, loads, earthing, grids, units, banks, regulators);
            _ = frame.Build(frame.InitialTaps());   // validates the assembled model now: a bad case is an input error (exit 1), not a solve-time crash
            return new ParsedCase(frame, capacitorNames);
        }
        catch (ArgumentException ex) { throw new InputException(ex.Message); }
    }

    /// <summary>
    /// A single-wire earth-return feeder (Core <see cref="Swer"/>): the source EMF behind its earth electrode, one conductor with earth
    /// return, a load between the conductor and earth and the load-end electrode. The line is given as its total impedance, or as a
    /// conductor and a length from which Core's earth-return line constants give it (and, with <c>charging</c>, its shunt capacitance).
    /// The nodes are S (source end, wire a) and L (load end, wires a and e); the line is "line" and the load "load".
    /// </summary>
    private static ParsedCase ParseSwer(JsonElement s, string name)
    {
        const string w = "swer";
        Object(s, w, "emf_v", "emf_angle_deg", "source_earth_r_ohm", "source_earth_x_ohm", "line", "load_earth_r_ohm", "load_earth_x_ohm", "load");
        double emfV = Num(s, "emf_v", w);
        if (!(emfV > 0)) throw new InputException($"{w}: \"emf_v\" must be above 0.");
        Complex emf = GridSim.Core.Numerics.DetMath.FromPolar(emfV, OptNum(s, "emf_angle_deg", w, 0.0) * Math.PI / 180.0);
        Complex rSource = new(OptNum(s, "source_earth_r_ohm", w, 0.0), OptNum(s, "source_earth_x_ohm", w, 0.0));
        Complex rLoad = new(OptNum(s, "load_earth_r_ohm", w, 0.0), OptNum(s, "load_earth_x_ohm", w, 0.0));

        JsonElement line = Req(s, "line", w);
        const string lw = "swer.line";
        Complex zLine, shunt = Complex.Zero;
        if (line.ValueKind == JsonValueKind.Object && line.TryGetProperty("length_km", out _))
        {
            Object(line, lw, "length_km", "frequency_hz", "earth_resistivity_ohm_m", "conductor", "charging");
            double km = Num(line, "length_km", lw), f = Num(line, "frequency_hz", lw), rho = Num(line, "earth_resistivity_ohm_m", lw);
            if (!(km > 0 && f > 0 && rho > 0)) throw new InputException($"{lw}: \"length_km\", \"frequency_hz\" and \"earth_resistivity_ohm_m\" must be above 0.");
            JsonElement c = Req(line, "conductor", lw);
            Object(c, $"{lw}.conductor", "resistance_ohm_per_km", "gmr_m", "radius_m", "height_m");
            double r = Num(c, "resistance_ohm_per_km", $"{lw}.conductor"), gmr = Num(c, "gmr_m", $"{lw}.conductor"), rad = Num(c, "radius_m", $"{lw}.conductor"), h = Num(c, "height_m", $"{lw}.conductor");
            if (!(r > 0 && gmr > 0 && rad > 0 && h > 0)) throw new InputException($"{lw}.conductor: every value must be above 0.");
            bool charging = false;
            if (line.TryGetProperty("charging", out JsonElement ch))
                charging = ch.ValueKind is JsonValueKind.True or JsonValueKind.False ? ch.GetBoolean() : throw new InputException($"{lw}: \"charging\" must be true or false.");
            (Complex zKm, double cKm) = Swer.LineConstants(new ConductorData("W", ConductorRole.Phase, 0.0, h, r, gmr, rad), f, rho);
            zLine = zKm * km;
            if (charging) shunt = new Complex(0.0, 2.0 * Math.PI * f * cKm * km);
        }
        else
        {
            Object(line, lw, "r_ohm", "x_ohm", "shunt_b_siemens");
            zLine = new Complex(Num(line, "r_ohm", lw), OptNum(line, "x_ohm", lw, 0.0));
            shunt = new Complex(0.0, OptNum(line, "shunt_b_siemens", lw, 0.0));
        }

        JsonElement load = Req(s, "load", w);
        Object(load, "swer.load", "p_w", "q_var", "nominal_v", "zip");
        double vnom = Num(load, "nominal_v", "swer.load");
        if (!(vnom > 0)) throw new InputException("swer.load: \"nominal_v\" must be above 0.");
        Zip? zip = null;
        if (load.TryGetProperty("zip", out _))
        {
            double[] zf = Numbers(load, "zip", "swer.load", 3);
            zip = new Zip(zf[0], zf[1], zf[2]);
        }
        try
        {
            PhaseFrameModel m = Swer.Feeder(emf, rSource, zLine, shunt, rLoad, new Complex(Num(load, "p_w", "swer.load"), Num(load, "q_var", "swer.load")), vnom, zip);
            var frame = new RegulatedFrame(name, m.Nodes, m.Sources, m.Branches, m.Loads, m.Earthing);
            _ = frame.Build(frame.InitialTaps());
            return new ParsedCase(frame, []);
        }
        catch (ArgumentException ex) { throw new InputException(ex.Message); }
    }

    private static readonly Dictionary<string, BankConnection> BankConnectionNames = new(StringComparer.Ordinal)
    {
        ["grounded-wye-grounded-wye"] = BankConnection.GroundedWyeGroundedWye,
        ["delta-grounded-wye"] = BankConnection.DeltaGroundedWye,
        ["wye-delta"] = BankConnection.WyeDelta,
        ["delta-delta"] = BankConnection.DeltaDelta,
        ["open-wye-open-delta"] = BankConnection.OpenWyeOpenDelta,
        ["open-delta-open-delta"] = BankConnection.OpenDeltaOpenDelta,
    };

    /// <summary>A per-unit impedance written as <c>{"r": .., "x": ..}</c>.</summary>
    private static Complex PuImpedance(JsonElement e, string where)
    {
        Object(e, where, "r", "x");
        return new Complex(Num(e, "r", where), OptNum(e, "x", where, 0.0));
    }

    internal static IReadOnlyList<MeshedStep> ParseSteps(JsonElement root)
    {
        Object(root, "steps document", "schema", "steps");
        string schema = Str(root, "schema", "steps document");
        if (schema != StepsSchema) throw new InputException($"schema must be \"{StepsSchema}\" but is \"{schema}\".");
        var steps = new List<MeshedStep>();
        int k = 0;
        foreach (JsonElement s in Arr(root, "steps", "steps document").EnumerateArray())
        {
            string w = $"steps[{k++}]";
            Object(s, w, "label", "scale", "load_scale", "capacitors");
            Dictionary<string, double>? per = null;
            if (s.TryGetProperty("load_scale", out JsonElement ls))
            {
                if (ls.ValueKind != JsonValueKind.Object) throw new InputException($"{w}: \"load_scale\" must be an object.");
                per = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (JsonProperty p in ls.EnumerateObject()) per[p.Name] = Num(p.Value, $"{w}: load_scale \"{p.Name}\"");
            }
            if (s.TryGetProperty("capacitors", out JsonElement cs))
            {
                // A switched capacitor bank: 1 in service, 0 out. It is the bank's constant-impedance leg scaled by that factor.
                if (cs.ValueKind != JsonValueKind.Object) throw new InputException($"{w}: \"capacitors\" must be an object of bank name to 0 or 1.");
                per ??= new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (JsonProperty p in cs.EnumerateObject())
                {
                    double state = Num(p.Value, $"{w}: capacitors \"{p.Name}\"");
                    if (state != 0.0 && state != 1.0) throw new InputException($"{w}: capacitors \"{p.Name}\" must be 0 (out) or 1 (in) but is {R(state)}.");
                    if (!per.TryAdd(p.Name, state)) throw new InputException($"{w}: \"{p.Name}\" is given in both \"load_scale\" and \"capacitors\".");
                }
            }
            steps.Add(new MeshedStep(Str(s, "label", w), OptNum(s, "scale", w, 1.0), per));
        }
        if (steps.Count == 0) throw new InputException("steps document: \"steps\" must not be empty.");
        return steps;
    }

    // ---- reporting ---------------------------------------------------------------------------------------------------

    private static string R(double v) => v.ToString("R", Inv);

    private static double Ang(Complex c) => GridSim.Core.Numerics.DetMath.Phase(c) * 180.0 / Math.PI;

    private static string StatusText(MeshedStatus s) => s switch
    {
        MeshedStatus.Converged => "CONVERGED",
        MeshedStatus.MaxIterations => "NOT CONVERGED (iteration limit)",
        MeshedStatus.NonFinite => "NOT CONVERGED (non-finite)",
        _ => "INCOMPLETE",
    };

    /// <summary>Fill the shared result from the solved steps. <paramref name="steps"/> is null for a single solve (the step column reads "-").</summary>
    internal static void Fill(CliResult rep, string caseName, string sha, IReadOnlyList<string> labels, IReadOnlyList<MeshedResult> results, bool series, bool warm, Action<CliResult>? extra = null)
    {
        rep.Set("schema", Schema).Set("case", caseName).Set("case_sha256", sha).Set("steps", results.Count).Set("warm_start", warm)
            .Set("all_converged", results.All(r => r.Status == MeshedStatus.Converged));
        if (!series)
        {
            MeshedResult r0 = results[0];
            rep.Set("status", StatusText(r0.Status)).Set("iterations", r0.Iterations).Set("mismatch_amps", double.IsNaN(r0.MismatchAmps) ? null : r0.MismatchAmps);
        }
        CliResultTable status = rep.Table("steps", "step", "status", "iterations", "mismatch_amps", "notes");
        CliResultTable volts = rep.Table("voltages", "step", "node", "wire", "v_re", "v_im", "v_mag", "v_angle_deg");
        CliResultTable currents = rep.Table("branch_currents", "step", "branch", "wire", "i_from_re", "i_from_im", "i_from_mag", "i_to_re", "i_to_im", "i_to_mag");
        CliResultTable residual = rep.Table("branch_earth_return", "step", "branch", "i_re", "i_im", "i_mag");
        CliResultTable loads = rep.Table("loads", "step", "load", "leg", "v_re", "v_im", "i_re", "i_im", "p_w", "q_var");
        CliResultTable sources = rep.Table("sources", "step", "node", "wire", "i_re", "i_im", "i_mag");
        CliResultTable earth = rep.Table("earthing", "step", "node", "wire", "grid", "i_re", "i_im", "i_mag");
        CliResultTable grids = rep.Table("grids", "step", "grid", "v_re", "v_im", "i_re", "i_im");
        for (int s = 0; s < results.Count; s++)
        {
            MeshedResult r = results[s];
            string label = labels[s];
            status.Row(label, StatusText(r.Status), r.Iterations, double.IsNaN(r.MismatchAmps) ? null : r.MismatchAmps, string.Join(" | ", r.IncompleteReasons.Concat(r.Notes)));
            foreach (WireVoltage v in r.Voltages) volts.Row(label, v.Node, v.Wire, v.V.Real, v.V.Imaginary, Math.Sqrt(v.V.Real * v.V.Real + v.V.Imaginary * v.V.Imaginary), Ang(v.V));
            foreach (BranchCurrents b in r.Branches)
            {
                for (int w = 0; w < b.Wires.Count; w++)
                    currents.Row(label, b.Name, b.Wires[w], b.IFrom[w].Real, b.IFrom[w].Imaginary, Mag(b.IFrom[w]), b.ITo[w].Real, b.ITo[w].Imaginary, Mag(b.ITo[w]));
                residual.Row(label, b.Name, b.Residual.Real, b.Residual.Imaginary, Mag(b.Residual));
            }
            foreach (LoadState l in r.Loads)
                for (int g = 0; g < l.Current.Count; g++)
                    loads.Row(label, l.Name, g, l.Voltage[g].Real, l.Voltage[g].Imaginary, l.Current[g].Real, l.Current[g].Imaginary, l.Power[g].Real, l.Power[g].Imaginary);
            foreach (SourceCurrent c in r.Sources) sources.Row(label, c.Node, c.Wire, c.Current.Real, c.Current.Imaginary, Mag(c.Current));
            foreach (EarthingCurrent c in r.Earthing) earth.Row(label, c.Node, c.Wire, c.Grid ?? "", c.Current.Real, c.Current.Imaginary, Mag(c.Current));
            foreach (GridState g in r.Grids) grids.Row(label, g.Name, g.Voltage.Real, g.Voltage.Imaginary, g.Current.Real, g.Current.Imaginary);
        }
        extra?.Invoke(rep);
        rep.Complete();
    }

    private static double Mag(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);

    private static readonly string[] ValueFlags = ["--steps", "--tol", "--max-iter", "--scale", "--from-dist3ph", "--switch-z-pu"];
    private static readonly string[] SwitchFlags = ["--warm-start", "--pin-floating", "--hold-taps"];

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("usage:", StringComparison.Ordinal) ? message : "error: dist3ph-mesh: " + message);
        return CliExit.Usage;
    }

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a)
    {
        string? file = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (file is not null) return Usage($"dist3ph-mesh takes one case file but got: {file} {t}");
                file = t;
                continue;
            }
            if (SwitchFlags.Contains(t)) { if (!switches.Add(t)) return Usage($"{t} was given twice"); continue; }
            if (!ValueFlags.Contains(t))
                return Usage($"dist3ph-mesh does not use {t}. dist3ph-mesh accepts: {string.Join(", ", ValueFlags.Concat(SwitchFlags))}.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage($"{t} expected a value but none was given");
            if (!values.TryAdd(t, a[++i])) return Usage($"{t} was given twice");
        }
        values.TryGetValue("--from-dist3ph", out string? feederPath);
        if (file is not null && feederPath is not null) return Usage("give either a mesh case file or --from-dist3ph <feeder.json>, not both.");
        if (file is null && feederPath is null) return Usage("usage: gridsim dist3ph-mesh <case.json> [--steps steps.json] [--warm-start] [--tol A] [--max-iter N] [--scale F] [--pin-floating] [--hold-taps] (see 'gridsim dist3ph-mesh --help')");
        double? switchZ = null;
        if (values.TryGetValue("--switch-z-pu", out string? zv))
        {
            if (feederPath is null) return Usage("--switch-z-pu is for --from-dist3ph (the per-unit series impedance given to a closed switch of the lifted feeder).");
            if (!double.TryParse(zv, NumberStyles.Float, Inv, out double z) || !(z > 0) || !double.IsFinite(z)) return Usage($"--switch-z-pu expected a finite number above 0 but got '{zv}'");
            switchZ = z;
        }

        double tol = 1e-6, scale = 1.0;
        int maxIter = 40;
        if (values.TryGetValue("--tol", out string? tv) && (!double.TryParse(tv, NumberStyles.Float, Inv, out tol) || !(tol > 0) || !double.IsFinite(tol)))
            return Usage($"--tol expected a finite number above 0 but got '{tv}'");
        if (values.TryGetValue("--scale", out string? sv) && (!double.TryParse(sv, NumberStyles.Float, Inv, out scale) || !(scale >= 0) || !double.IsFinite(scale)))
            return Usage($"--scale expected a finite number not below 0 but got '{sv}'");
        if (values.TryGetValue("--max-iter", out string? mv) && (!int.TryParse(mv, NumberStyles.Integer, Inv, out maxIter) || maxIter < 1))
            return Usage($"--max-iter expected an integer of at least 1 but got '{mv}'");
        bool warm = switches.Contains("--warm-start");
        if (warm && !values.ContainsKey("--steps")) return Usage("--warm-start needs --steps (it chains the steps of a time series).");

        ParsedCase parsed;
        string sha;
        IReadOnlyList<MeshedStep>? steps = null;
        IReadOnlyList<string> adapterNotes = [];
        bool pinnedByAdapter = false;
        string sourceFile = feederPath ?? file!;
        try
        {
            if (feederPath is not null)
            {
                if (!File.Exists(feederPath)) { Console.Error.WriteLine($"error: feeder file not found: {feederPath}"); return CliExit.InputError; }
                sha = CaseIdentity.Hash(File.ReadAllBytes(feederPath));
                FeederAdapterResult ad = MultiPhaseFeederAdapter.FromFeeder(Gated.Dist3ph(feederPath), new FeederAdapterOptions { SwitchZPu = switchZ });
                if (ad.Frame is null)
                {
                    Console.Error.WriteLine($"error: dist3ph-mesh: {feederPath}: the feeder cannot be lifted into the phase frame, so nothing was solved ({ad.Unsupported.Count} element(s) not carried):");
                    foreach (string u in ad.Unsupported) Console.Error.WriteLine("  - " + u);
                    return CliExit.InputError;
                }
                parsed = new ParsedCase(ad.Frame, []);
                var notes = new List<string>(ad.Notes);
                pinnedByAdapter = ad.NeedsPinnedReference;
                if (pinnedByAdapter) notes.Add("a delta secondary leaves a floating component: the first wire is pinned as the reference (--pin-floating), so compare delta nodes line to line.");
                adapterNotes = notes;
            }
            else
            {
                if (!File.Exists(file)) { Console.Error.WriteLine($"error: case file not found: {file}"); return CliExit.InputError; }
                // CLI-08: the verb's own case format is held to the licence's bus limit like every other network (it used to be
                // solved at any size): one bus per node of the case ("nodes", or the two nodes of a "swer" case), bundled only by
                // the content rule checked before and after the read.
                byte[] bytes = GridSim.Core.Licensing.BundledNetworks.BundledAcrossLoad(file!, () => File.ReadAllBytes(file!), out bool meshBundled);
                sha = CaseIdentity.Hash(bytes);
                int meshBuses;
                using (JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { AllowTrailingCommas = false }))
                {
                    TransmissionSecurityCommand.CheckDuplicates(doc.RootElement);
                    parsed = Parse(doc.RootElement);
                    meshBuses = doc.RootElement.TryGetProperty("nodes", out JsonElement nodeList) && nodeList.ValueKind == JsonValueKind.Array ? nodeList.GetArrayLength() : 2;
                }
                Gated.AdmitCount(meshBuses, meshBundled);
            }
            if (values.TryGetValue("--steps", out string? sf))
            {
                if (!File.Exists(sf)) { Console.Error.WriteLine($"error: steps file not found: {sf}"); return CliExit.InputError; }
                using JsonDocument sd = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(sf)), new JsonDocumentOptions { AllowTrailingCommas = false });
                TransmissionSecurityCommand.CheckDuplicates(sd.RootElement);
                steps = ParseSteps(sd.RootElement);
            }
        }
        catch (Exception ex) when (ex is JsonException or InputException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: dist3ph-mesh: {sourceFile}: {ex.Message}");
            return CliExit.InputError;
        }

        var options = new MeshedOptions
        {
            ToleranceAmps = tol,
            MaxIterations = maxIter,
            GlobalLoadScale = scale,
            Floating = switches.Contains("--pin-floating") || pinnedByAdapter ? FloatingPolicy.PinFirstWire : FloatingPolicy.Refuse,
        };
        RegulatedFrame frame = parsed.Frame;
        bool hasLdc = frame.Regulators.Any(r => r.Ldc is not null);
        bool hold = switches.Contains("--hold-taps");
        if (hold && !hasLdc) return Usage("--hold-taps needs a regulator with an \"ldc\" compensator (it reports where each compensated phase stands without moving the taps).");
        if (hasLdc && !hold && steps is not null)
            return Usage("a regulator with an \"ldc\" compensator searches its taps on a single solve; with --steps give fixed taps or add --hold-taps (the taps stay as written).");

        // A capacitor bank is a constant-impedance leg and so would be scaled by the load factors; it is not a load, so its factor
        // is divided back out (in service 1, out 0 from a step's "capacitors" member). A zero load factor cannot be divided out.
        try
        {
            if (parsed.Capacitors.Count > 0)
            {
                double Compensate(double state, double globalFactor)
                    => globalFactor > 0.0 ? state / globalFactor
                        : throw new InputException($"a load factor of 0 would scale the capacitor bank(s) to nothing too ({string.Join(", ", parsed.Capacitors)}); switch a bank out with a step's \"capacitors\" member instead.");
                options = options with { LoadScale = parsed.Capacitors.ToDictionary(c => c, c => Compensate(1.0, scale), StringComparer.Ordinal) };
                if (steps is not null)
                    steps = steps.Select(st =>
                    {
                        var d = new Dictionary<string, double>(st.LoadScale ?? new Dictionary<string, double>(), StringComparer.Ordinal);
                        foreach (string c in parsed.Capacitors) d[c] = Compensate(d.TryGetValue(c, out double v) ? v : 1.0, scale * st.GlobalScale);
                        return st with { LoadScale = d };
                    }).ToList();
            }
        }
        catch (InputException ex) { Console.Error.WriteLine($"error: dist3ph-mesh: {ex.Message}"); return CliExit.InputError; }

        List<MeshedResult> results;
        List<string> labels;
        TapSearchResult? tap = null;
        PhaseFrameModel model;
        try
        {
            if (steps is null && hasLdc)
            {
                tap = hold ? RegulatorTapSearch.Evaluate(frame, options) : RegulatorTapSearch.Run(frame, options);
                model = tap.Model;
                results = [tap.Final];
                labels = ["-"];
            }
            else
            {
                model = frame.Build(frame.InitialTaps());
                if (steps is null)
                {
                    results = [Newton3Ph.Solve(model, options)];
                    labels = ["-"];
                }
                else
                {
                    results = UnbalancedTimeSeries.Run(model, steps, warm, options).Results.ToList();
                    labels = steps.Select(s => s.Label).ToList();
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: dist3ph-mesh: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        Console.WriteLine($"dist3ph-mesh - {model.Name}: {model.Nodes.Count} node(s), {model.Branches.Count} branch(es), {model.Loads.Count} load(s), {results.Count} step(s)");
        foreach (string n in adapterNotes) Console.WriteLine("  note: " + n);
        int exit = CliExit.Ok;
        for (int s = 0; s < results.Count; s++)
        {
            MeshedResult r = results[s];
            Console.WriteLine($"  [{labels[s]}] {StatusText(r.Status)}; {r.Iterations} iteration(s); current mismatch {(double.IsNaN(r.MismatchAmps) ? "n/a" : r.MismatchAmps.ToString("E2", Inv))} A");
            foreach (string reason in r.IncompleteReasons) Console.WriteLine("    INCOMPLETE: " + reason);
            foreach (string note in r.Notes.Where(n => !n.StartsWith("start:", StringComparison.Ordinal))) Console.WriteLine("    note: " + note);
            if (r.Status == MeshedStatus.Converged)
                foreach (WireVoltage v in r.Voltages)
                    Console.WriteLine($"    {v.Node,-8} {v.Wire,-4} {Mag(v.V),12:F4} V  {Ang(v.V),10:F4} deg");
            else exit = CliExit.Verdict;
        }

        // Regulators: the taps in force (found by the compensator search, or as written), and for a compensated phase where it stands.
        var tapRows = new List<(string Regulator, string Connection, string Phase, int Tap, double Ratio, double? RelayV, double? Low, double? High, bool? InBand, bool? AtLimit)>();
        if (frame.Regulators.Count > 0)
        {
            IReadOnlyList<int[]> taps = tap?.Taps ?? frame.InitialTaps();
            for (int i = 0; i < frame.Regulators.Count; i++)
            {
                StepRegulator reg = frame.Regulators[i];
                string[] legs = reg.Connection switch
                {
                    RegulatorConnection.Wye => reg.Phases.ToArray(),
                    RegulatorConnection.OpenDelta => ["ab", "cb"],
                    _ => ["ab", "bc", "ca"],
                };
                for (int p = 0; p < legs.Length; p++)
                {
                    TapRow? tr = tap?.Rows.FirstOrDefault(x => x.Regulator == reg.Name && x.Phase == legs[p]);
                    tapRows.Add((reg.Name, reg.Connection.ToString(), legs[p], taps[i][p], reg.Ratio(taps[i][p]),
                        tr?.RelayVolts, tr?.LowVolts, tr?.HighVolts, tr?.InBand, tr?.AtLimit));
                    Console.WriteLine($"  regulator {reg.Name} {legs[p]}: tap {taps[i][p]} (ratio {R(reg.Ratio(taps[i][p]))})"
                        + (tr is null ? "" : $"  relay {tr.RelayVolts.ToString("F3", Inv)} V in [{tr.LowVolts.ToString("F3", Inv)}, {tr.HighVolts.ToString("F3", Inv)}]{(tr.InBand ? "" : tr.AtLimit ? " (at tap limit)" : " (outside band)")}"));
                }
            }
            if (tap is not null)
            {
                Console.WriteLine($"  tap search: {tap.Status.ToString().ToUpperInvariant()} after {tap.OuterIterations} load flow(s)" + (hold ? " (taps held)" : ""));
                if (tap.Status != TapSearchStatus.Settled) exit = CliExit.Verdict;
            }
        }

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
            Fill(rep, model.Name, sha, labels, results, steps is not null, warm, tapRows.Count == 0 && feederPath is null ? null : r =>
            {
                if (feederPath is not null)
                {
                    r.Set("lifted_from_dist3ph", true);
                    CliResultTable an = r.Table("adapter_notes", "note");
                    foreach (string n in adapterNotes) an.Row(n);
                }
                if (tapRows.Count == 0) return;
                if (tap is not null) r.Set("tap_search", tap.Status.ToString().ToUpperInvariant()).Set("tap_search_load_flows", tap.OuterIterations).Set("taps_held", hold);
                CliResultTable t = r.Table("regulators", "regulator", "connection", "phase", "tap", "ratio", "relay_v", "band_low_v", "band_high_v", "in_band", "at_limit");
                foreach (var x in tapRows) t.Row(x.Regulator, x.Connection, x.Phase, x.Tap, x.Ratio, x.RelayV, x.Low, x.High, x.InBand, x.AtLimit);
            });
        return exit;
    }
}
