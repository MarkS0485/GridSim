using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Lines;
using GridSim.Core.Lines.Cables;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-04 (CLI part): <c>gridsim line-constants &lt;geometry.json&gt; [--length-km L --base-kv kV --base-mva MVA]</c>.
/// Reads a strict <c>gridsim-lineconstants/1</c> geometry file, runs <see cref="LineConstantsSolver"/> (GridSim.Core/Lines,
/// unchanged) and prints and writes every stage of the calculation: primitive Z and P, the Kron-reduced phase Z and C, the
/// transposition average, the sequence constants and, when a length and base are given, the positive-sequence R, X, B in
/// per unit. All arithmetic is in Core; this file parses, prints and fills the shared result writer
/// (<see cref="CliOutput.Report"/>). The in-process Python endpoint (<c>gridsim rpc</c>) calls <see cref="Parse"/> and
/// <see cref="Fill"/> for its typed <c>line-constants</c> operation, so both paths give the same numbers.
/// Exit codes: 0 done; 1 the file cannot be read, is not valid <c>gridsim-lineconstants/1</c>, or describes an unusable
/// geometry; 2 usage; 3 per-unit values were asked for but the line has no positive-sequence constants (not three phases).
/// RC5 WP35: <c>gridsim line-constants --cable &lt;cables.json&gt;</c> reads a <c>gridsim-cableconstants/1</c> document of
/// buried single-core cables instead and runs <see cref="CableConstantsSolver"/>; the overhead path above is unchanged.
/// </summary>
internal static class LineConstantsCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A per-unit conversion request: line length (km) and the bases.</summary>
    internal sealed record PerUnitBase(double LengthKm, double BaseKv, double BaseMva);

    /// <summary>The input is not a valid gridsim-lineconstants/1 document (exit 1 from the CLI).</summary>
    internal sealed class InputException(string message) : Exception(message);

    private static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "method", "frequency_hz", "earth_resistivity_ohm_m", "transposed", "conductors",
    };

    private static readonly HashSet<string> ConductorKeys = new(StringComparer.Ordinal)
    {
        "name", "role", "x_m", "height_m", "resistance_ohm_per_km", "gmr_m", "radius_m",
    };

    /// <summary>Parse the geometry document strictly: an unknown or missing key, or a wrong type, is an <see cref="InputException"/>.</summary>
    internal static LineGeometry Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InputException("the document must be a JSON object.");
        foreach (JsonProperty p in root.EnumerateObject())
            if (!TopKeys.Contains(p.Name)) throw new InputException($"unknown key '{p.Name}' (gridsim-lineconstants/1 allows: {string.Join(", ", TopKeys)}).");
        string schema = Str(root, "schema", "document");
        if (schema != LineConstantsSolver.Schema) throw new InputException($"schema must be \"{LineConstantsSolver.Schema}\" but is \"{schema}\".");
        string methodText = Str(root, "method", "document");
        EarthReturnMethod method = methodText switch
        {
            "perfect-ground" => EarthReturnMethod.PerfectGround,
            "earth-integral" => EarthReturnMethod.EarthIntegral,
            _ => throw new InputException($"method must be \"perfect-ground\" or \"earth-integral\" but is \"{methodText}\"."),
        };
        double f = Num(root, "frequency_hz", "document");
        double rho = 0.0;
        if (root.TryGetProperty("earth_resistivity_ohm_m", out _)) rho = Num(root, "earth_resistivity_ohm_m", "document");
        else if (method == EarthReturnMethod.EarthIntegral) throw new InputException("earth_resistivity_ohm_m is required for method \"earth-integral\".");
        bool transposed = true;
        if (root.TryGetProperty("transposed", out JsonElement t))
        {
            if (t.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InputException("transposed must be true or false.");
            transposed = t.GetBoolean();
        }
        if (!root.TryGetProperty("conductors", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InputException("conductors must be an array.");
        var conductors = new List<ConductorData>();
        int index = 0;
        foreach (JsonElement c in list.EnumerateArray())
        {
            string where = $"conductors[{index++}]";
            if (c.ValueKind != JsonValueKind.Object) throw new InputException($"{where} must be an object.");
            foreach (JsonProperty p in c.EnumerateObject())
                if (!ConductorKeys.Contains(p.Name)) throw new InputException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", ConductorKeys)}).");
            string roleText = Str(c, "role", where);
            ConductorRole role = roleText switch
            {
                "phase" => ConductorRole.Phase,
                "ground-wire" => ConductorRole.GroundWire,
                _ => throw new InputException($"{where}: role must be \"phase\" or \"ground-wire\" but is \"{roleText}\"."),
            };
            conductors.Add(new ConductorData(Str(c, "name", where), role, Num(c, "x_m", where), Num(c, "height_m", where),
                Num(c, "resistance_ohm_per_km", where), Num(c, "gmr_m", where), Num(c, "radius_m", where)));
        }
        return new LineGeometry(conductors, f, rho, method, transposed);
    }

    private static string Str(JsonElement o, string key, string where)
    {
        if (!o.TryGetProperty(key, out JsonElement v)) throw new InputException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.String) throw new InputException($"{where}: \"{key}\" must be a string.");
        return v.GetString()!;
    }

    private static double Num(JsonElement o, string key, string where)
    {
        if (!o.TryGetProperty(key, out JsonElement v)) throw new InputException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InputException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    /// <summary>Fill <paramref name="rep"/> from a solved result, in the stage order of the calculation.</summary>
    internal static void Fill(CliResult rep, LineGeometry g, LineConstantsResult r, PerUnitBase? pu)
    {
        bool integral = g.Method == EarthReturnMethod.EarthIntegral;
        rep.Set("schema", r.Schema).Set("method", r.Method).Set("frequency_hz", r.FrequencyHz)
            .Set("earth_resistivity_ohm_m", integral ? r.EarthResistivityOhmM : null)
            .Set("conductors", r.AllConductors.Count).Set("phase_conductors", r.PhaseConductors.Count)
            .Set("transposed", g.Transposed).Set("integral_tail_bound", r.IntegralTailBound)
            .Set("sequence_available", r.Sequence is not null).Set("sequence_note", r.SequenceNote);
        if (r.Sequence is { } s)
            rep.Set("z1_r_ohm_per_km", s.Z1.Real).Set("z1_x_ohm_per_km", s.Z1.Imaginary)
                .Set("z0_r_ohm_per_km", s.Z0.Real).Set("z0_x_ohm_per_km", s.Z0.Imaginary)
                .Set("c1_f_per_km", s.C1FaradPerKm).Set("c0_f_per_km", s.C0FaradPerKm);
        if (pu is not null)
        {
            rep.Set("length_km", pu.LengthKm).Set("base_kv", pu.BaseKv).Set("base_mva", pu.BaseMva);
            if (r.Sequence is not null)
            {
                (double pr, double px, double pb) = r.PositiveSequencePerUnit(pu.LengthKm, pu.BaseKv, pu.BaseMva);
                rep.Set("r1_pu", pr).Set("x1_pu", px).Set("b1_pu", pb);
            }
        }
        ComplexTable(rep, "primitive_z", r.AllConductors, r.PrimitiveZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "primitive_p", r.AllConductors, r.PrimitiveP, "p_km_per_f");
        ComplexTable(rep, "phase_z", r.PhaseConductors, r.PhaseZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "phase_c", r.PhaseConductors, r.PhaseC, "c_f_per_km");
        ComplexTable(rep, "transposed_z", r.PhaseConductors, r.TransposedZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "transposed_c", r.PhaseConductors, r.TransposedC, "c_f_per_km");
        rep.Complete();
    }

    private static void ComplexTable(CliResult rep, string name, IReadOnlyList<string> names, Complex[,] m, string re, string im)
    {
        CliResultTable t = rep.Table(name, "row", "col", re, im);
        for (int i = 0; i < names.Count; i++)
            for (int k = 0; k < names.Count; k++) t.Row(names[i], names[k], m[i, k].Real, m[i, k].Imaginary);
    }

    private static void RealTable(CliResult rep, string name, IReadOnlyList<string> names, double[,] m, string column)
    {
        CliResultTable t = rep.Table(name, "row", "col", column);
        for (int i = 0; i < names.Count; i++)
            for (int k = 0; k < names.Count; k++) t.Row(names[i], names[k], m[i, k]);
    }

    private static string R(double v) => v.ToString("R", Inv);

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a)
    {
        string? file = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (file is not null) return Usage($"line-constants takes one geometry file but got: {file} {t}");
                file = t;
                continue;
            }
            if (t is not ("--length-km" or "--base-kv" or "--base-mva" or "--cable"))
                return Usage($"line-constants does not use {t}. line-constants accepts: --length-km, --base-kv, --base-mva, --cable.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage($"{t} expected a value but none was given");
            if (!flags.TryAdd(t, a[++i])) return Usage($"{t} was given twice");
        }
        string? cable = flags.GetValueOrDefault("--cable");
        if (cable is not null)
        {
            if (file is not null) return Usage($"line-constants takes a geometry file or --cable <cables.json>, not both (got {file}).");
            flags.Remove("--cable");
        }
        if (file is null && cable is null) return Usage("usage: gridsim line-constants <geometry.json> [--length-km L --base-kv kV --base-mva MVA] (see 'gridsim line-constants --help')");
        PerUnitBase? pu = null;
        if (flags.Count > 0)
        {
            if (flags.Count != 3) return Usage("per-unit output needs all three of --length-km, --base-kv and --base-mva.");
            double[] v = new double[3];
            string[] names = ["--length-km", "--base-kv", "--base-mva"];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(flags[names[i]], NumberStyles.Float, Inv, out v[i]) || !double.IsFinite(v[i]) || v[i] <= 0)
                    return Usage($"{names[i]} expected a finite number above 0 but got '{flags[names[i]]}'");
            pu = new PerUnitBase(v[0], v[1], v[2]);
        }
        if (cable is not null) return RunCable(cable, pu);
        if (file is null) return Usage("line-constants needs a geometry file.");

        LineGeometry g;
        try
        {
            if (!File.Exists(file)) { Console.Error.WriteLine($"error: geometry file not found: {file}"); return CliExit.InputError; }
            using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(file)), new JsonDocumentOptions { AllowTrailingCommas = false });
            g = Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InputException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: line-constants: {file}: {ex.Message}");
            return CliExit.InputError;
        }

        LineConstantsResult r;
        try { r = LineConstantsSolver.Solve(g); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: line-constants: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        Console.WriteLine($"Line constants - {r.Method}, {R(r.FrequencyHz)} Hz, {r.AllConductors.Count} conductor(s), {r.PhaseConductors.Count} phase(s)"
            + (g.Method == EarthReturnMethod.EarthIntegral ? $", earth resistivity {R(r.EarthResistivityOhmM)} ohm m (integral tail bound {r.IntegralTailBound:E1})" : ""));
        PrintMatrix("Phase impedance (Kron-reduced), ohm/km", r.PhaseConductors, r.PhaseZ);
        if (r.Sequence is { } s)
        {
            if (r.SequenceNote is not null) Console.WriteLine("  note: " + r.SequenceNote);
            Console.WriteLine($"  Z1 = {s.Z1.Real:F6} + j{s.Z1.Imaginary:F6} ohm/km    Z0 = {s.Z0.Real:F6} + j{s.Z0.Imaginary:F6} ohm/km");
            Console.WriteLine($"  C1 = {s.C1FaradPerKm * 1e9:F4} nF/km    C0 = {s.C0FaradPerKm * 1e9:F4} nF/km");
        }
        else Console.WriteLine("  " + r.SequenceNote);

        int exit = CliExit.Ok;
        if (pu is not null)
        {
            if (r.Sequence is null)
            {
                Console.Error.WriteLine("line-constants: per-unit values need positive-sequence constants; " + r.SequenceNote);
                exit = CliExit.Verdict;
            }
            else
            {
                (double pr, double px, double pb) = r.PositiveSequencePerUnit(pu.LengthKm, pu.BaseKv, pu.BaseMva);
                Console.WriteLine($"  per unit on {R(pu.BaseMva)} MVA, {R(pu.BaseKv)} kV, {R(pu.LengthKm)} km: R1 = {pr:F8}  X1 = {px:F8}  B1 = {pb:F8}");
            }
        }

        CliResult? rep = CliOutput.Report;
        if (rep is not null) Fill(rep, g, r, pu);
        return exit;
    }

    // ---- RC5 WP35: buried single-core cables (gridsim-cableconstants/1) ------------------------------------------------

    private static readonly HashSet<string> CableTopKeys = new(StringComparer.Ordinal)
    {
        "schema", "earth", "bonding", "frequency_hz", "earth_resistivity_ohm_m", "transposed", "cables",
    };

    private static readonly HashSet<string> CableKeys = new(StringComparer.Ordinal)
    {
        "name", "x_m", "depth_m", "outer_radius_m", "conductors",
    };

    private static readonly HashSet<string> CableConductorKeys = new(StringComparer.Ordinal)
    {
        "inner_radius_m", "outer_radius_m", "resistivity_ohm_m", "relative_permeability", "insulation",
    };

    private static readonly HashSet<string> CableInsulationKeys = new(StringComparer.Ordinal)
    {
        "relative_permittivity", "loss_tangent", "relative_permeability",
    };

    private static void Keys(JsonElement o, HashSet<string> allowed, string where)
    {
        if (o.ValueKind != JsonValueKind.Object) throw new InputException($"{where} must be a JSON object.");
        foreach (JsonProperty p in o.EnumerateObject())
            if (!allowed.Contains(p.Name)) throw new InputException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
    }

    private static double OptNum(JsonElement o, string key, string where, double absent) =>
        o.TryGetProperty(key, out _) ? Num(o, key, where) : absent;

    /// <summary>Parse a <c>gridsim-cableconstants/1</c> document strictly: an unknown or missing key, or a wrong type, is an <see cref="InputException"/>.</summary>
    internal static CableGeometry ParseCable(JsonElement root)
    {
        Keys(root, CableTopKeys, "the document");
        string schema = Str(root, "schema", "document");
        if (schema != CableConstantsSolver.Schema) throw new InputException($"schema must be \"{CableConstantsSolver.Schema}\" but is \"{schema}\".");
        string earthText = Str(root, "earth", "document");
        CableEarthMethod earth = earthText switch
        {
            "wedepohl" => CableEarthMethod.Wedepohl,
            "pollaczek" => CableEarthMethod.Pollaczek,
            _ => throw new InputException($"earth must be \"wedepohl\" or \"pollaczek\" but is \"{earthText}\"."),
        };
        string bondingText = Str(root, "bonding", "document");
        CableBonding bonding = bondingText switch
        {
            "both" => CableBonding.BothEnds,
            "single" => CableBonding.SinglePoint,
            "cross" => CableBonding.CrossBonded,
            _ => throw new InputException($"bonding must be \"both\", \"single\" or \"cross\" but is \"{bondingText}\"."),
        };
        double f = Num(root, "frequency_hz", "document");
        double rho = Num(root, "earth_resistivity_ohm_m", "document");
        bool transposed = true;
        if (root.TryGetProperty("transposed", out JsonElement t))
        {
            if (t.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InputException("transposed must be true or false.");
            transposed = t.GetBoolean();
        }
        if (!root.TryGetProperty("cables", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InputException("cables must be an array.");
        var cables = new List<SingleCoreCable>();
        int index = 0;
        foreach (JsonElement c in list.EnumerateArray())
        {
            string where = $"cables[{index++}]";
            Keys(c, CableKeys, where);
            if (!c.TryGetProperty("conductors", out JsonElement cl) || cl.ValueKind != JsonValueKind.Array)
                throw new InputException($"{where}: conductors must be an array.");
            var conductors = new List<CableConductor>();
            var insulation = new List<CableInsulation>();
            int k = 0;
            foreach (JsonElement m in cl.EnumerateArray())
            {
                string mw = $"{where}.conductors[{k++}]";
                Keys(m, CableConductorKeys, mw);
                conductors.Add(new CableConductor(Num(m, "inner_radius_m", mw), Num(m, "outer_radius_m", mw),
                    Num(m, "resistivity_ohm_m", mw), OptNum(m, "relative_permeability", mw, 1.0)));
                if (!m.TryGetProperty("insulation", out JsonElement ins)) throw new InputException($"{mw}: \"insulation\" is required.");
                string iw = mw + ".insulation";
                Keys(ins, CableInsulationKeys, iw);
                insulation.Add(new CableInsulation(Num(ins, "relative_permittivity", iw), OptNum(ins, "loss_tangent", iw, 0.0),
                    OptNum(ins, "relative_permeability", iw, 1.0)));
            }
            cables.Add(new SingleCoreCable(Str(c, "name", where), Num(c, "x_m", where), Num(c, "depth_m", where),
                conductors, insulation, Num(c, "outer_radius_m", where)));
        }
        return new CableGeometry(cables, f, rho, earth, bonding, transposed);
    }

    private static string BondingName(CableBonding b) => b switch
    {
        CableBonding.BothEnds => "both",
        CableBonding.SinglePoint => "single",
        _ => "cross",
    };

    /// <summary>Fill <paramref name="rep"/> from a solved cable result, in the stage order of the calculation.</summary>
    internal static void FillCable(CliResult rep, CableGeometry g, CableConstantsResult cr, PerUnitBase? pu)
    {
        LineConstantsResult r = cr.Line;
        rep.Set("schema", r.Schema).Set("earth", r.Method).Set("bonding", BondingName(cr.Bonding))
            .Set("frequency_hz", r.FrequencyHz).Set("earth_resistivity_ohm_m", r.EarthResistivityOhmM)
            .Set("cables", g.Cables.Count).Set("conductors", r.AllConductors.Count).Set("phase_conductors", r.PhaseConductors.Count)
            .Set("transposed", g.Transposed).Set("integral_tail_bound", r.IntegralTailBound)
            .Set("wedepohl_argument_max", cr.MaxWedepohlArgument).Set("warnings", cr.Warnings.Count == 0 ? null : string.Join(" | ", cr.Warnings))
            .Set("sequence_available", r.Sequence is not null).Set("sequence_note", r.SequenceNote);
        if (r.Sequence is { } s)
            rep.Set("z1_r_ohm_per_km", s.Z1.Real).Set("z1_x_ohm_per_km", s.Z1.Imaginary)
                .Set("z0_r_ohm_per_km", s.Z0.Real).Set("z0_x_ohm_per_km", s.Z0.Imaginary)
                .Set("c1_f_per_km", s.C1FaradPerKm).Set("c0_f_per_km", s.C0FaradPerKm);
        if (pu is not null)
        {
            rep.Set("length_km", pu.LengthKm).Set("base_kv", pu.BaseKv).Set("base_mva", pu.BaseMva);
            if (r.Sequence is not null)
            {
                (double pr, double px, double pb) = r.PositiveSequencePerUnit(pu.LengthKm, pu.BaseKv, pu.BaseMva);
                rep.Set("r1_pu", pr).Set("x1_pu", px).Set("b1_pu", pb);
            }
        }
        ComplexTable(rep, "primitive_z", r.AllConductors, r.PrimitiveZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "primitive_p", r.AllConductors, r.PrimitiveP, "p_km_per_f");
        ComplexTable(rep, "conductor_y", r.AllConductors, cr.ConductorY, "g_s_per_km", "b_s_per_km");
        ComplexTable(rep, "phase_z", r.PhaseConductors, r.PhaseZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "phase_c", r.PhaseConductors, r.PhaseC, "c_f_per_km");
        RealTable(rep, "phase_g", r.PhaseConductors, cr.PhaseG, "g_s_per_km");
        ComplexTable(rep, "transposed_z", r.PhaseConductors, r.TransposedZ, "r_ohm_per_km", "x_ohm_per_km");
        RealTable(rep, "transposed_c", r.PhaseConductors, r.TransposedC, "c_f_per_km");
        rep.Complete();
    }

    private static int RunCable(string file, PerUnitBase? pu)
    {
        CableGeometry g;
        try
        {
            if (!File.Exists(file)) { Console.Error.WriteLine($"error: cable file not found: {file}"); return CliExit.InputError; }
            using JsonDocument doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(file)), new JsonDocumentOptions { AllowTrailingCommas = false });
            g = ParseCable(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InputException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: line-constants: {file}: {ex.Message}");
            return CliExit.InputError;
        }

        CableConstantsResult cr;
        try { cr = CableConstantsSolver.Solve(g); }
        catch (Exception ex) when (ex is ArgumentException || (ex is InvalidOperationException && DomainRefusal.IsMarked(ex)))
        {
            Console.Error.WriteLine($"error: line-constants: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }
        LineConstantsResult r = cr.Line;

        Console.WriteLine($"Cable constants - {r.Method} earth return, {BondingName(cr.Bonding)} bonding, {R(r.FrequencyHz)} Hz, {g.Cables.Count} cable(s), "
            + $"{r.AllConductors.Count} conductor(s), earth resistivity {R(r.EarthResistivityOhmM)} ohm m"
            + (g.Earth == CableEarthMethod.Pollaczek ? $" (integral tail bound {r.IntegralTailBound.ToString("E1", Inv)})" : ""));
        foreach (string w in cr.Warnings) Console.Error.WriteLine("warning: line-constants: " + w);
        PrintMatrix("Phase impedance (bonded), ohm/km", r.PhaseConductors, r.PhaseZ);
        if (r.Sequence is { } s)
        {
            if (r.SequenceNote is not null) Console.WriteLine("  note: " + r.SequenceNote);
            Console.WriteLine(string.Create(Inv, $"  Z1 = {s.Z1.Real:F6} + j{s.Z1.Imaginary:F6} ohm/km    Z0 = {s.Z0.Real:F6} + j{s.Z0.Imaginary:F6} ohm/km"));
            Console.WriteLine(string.Create(Inv, $"  C1 = {s.C1FaradPerKm * 1e9:F4} nF/km    C0 = {s.C0FaradPerKm * 1e9:F4} nF/km"));
        }
        else Console.WriteLine("  " + r.SequenceNote);

        int exit = CliExit.Ok;
        if (pu is not null)
        {
            if (r.Sequence is null)
            {
                Console.Error.WriteLine("line-constants: per-unit values need positive-sequence constants; " + r.SequenceNote);
                exit = CliExit.Verdict;
            }
            else
            {
                (double pr, double px, double pb) = r.PositiveSequencePerUnit(pu.LengthKm, pu.BaseKv, pu.BaseMva);
                Console.WriteLine(string.Create(Inv, $"  per unit on {R(pu.BaseMva)} MVA, {R(pu.BaseKv)} kV, {R(pu.LengthKm)} km: R1 = {pr:F8}  X1 = {px:F8}  B1 = {pb:F8}"));
            }
        }

        CliResult? rep = CliOutput.Report;
        if (rep is not null) FillCable(rep, g, cr, pu);
        return exit;
    }

    private static void PrintMatrix(string title, IReadOnlyList<string> names, Complex[,] m)
    {
        Console.WriteLine("  " + title + ":");
        for (int i = 0; i < names.Count; i++)
        {
            var cells = new List<string>();
            for (int k = 0; k < names.Count; k++) cells.Add($"{m[i, k].Real,10:F5} {(m[i, k].Imaginary < 0 ? "-" : "+")} j{Math.Abs(m[i, k].Imaginary):F5}");
            Console.WriteLine($"    {names[i],-8} " + string.Join("   ", cells));
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("usage:", StringComparison.Ordinal) ? message : "error: line-constants: " + message);
        return CliExit.Usage;
    }
}
