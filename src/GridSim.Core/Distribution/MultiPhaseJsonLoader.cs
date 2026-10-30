using System.Numerics;
using System.Text.Json;
// N062: the phase helper, aliased because this class has its own PhaseIndex(...) method (measurement phases).
using PhaseIx = GridSim.Core.Distribution.PhaseIndex;

namespace GridSim.Core.Distribution;

/// <summary>
/// A <see cref="MultiPhaseModel"/> loaded from a <c>gridsim-dist3ph/1</c> file, together with the engineering
/// context the per-unit model itself does not carry: the feeder's name and cited source, the three-phase MVA base,
/// each bus's line-to-line kV base, and the authored id of every branch and load (aligned by index with
/// <see cref="MultiPhaseModel.Branches"/> and <see cref="MultiPhaseModel.Loads"/>), so a report can speak in volts
/// and amps and name the elements the author wrote.
/// </summary>
/// <param name="Model">The per-unit model, ready for <see cref="ThreePhaseSweep"/> and the other Distribution solvers.</param>
/// <param name="SourceFile">The file (or source name) the feeder was read from, for messages.</param>
/// <param name="Name">The feeder name from the file.</param>
/// <param name="Description">The free-text description (the citation of where the data came from).</param>
/// <param name="BaseMva">The three-phase MVA base. The per-phase power base is BaseMva / 3.</param>
/// <param name="BaseKvLLByBus">Line-to-line kV base of every bus, keyed by bus id.</param>
/// <param name="BranchIds">Authored id of each branch, aligned with <see cref="MultiPhaseModel.Branches"/>.</param>
/// <param name="BranchKinds">"line", "transformer", "switch" or "regulator" for each branch, aligned with
/// <see cref="MultiPhaseModel.Branches"/>.</param>
/// <param name="LoadIds">Authored id of each load, aligned with <see cref="MultiPhaseModel.Loads"/>.</param>
public sealed record MultiPhaseFeeder(
    MultiPhaseModel Model,
    string SourceFile,
    string Name,
    string Description,
    double BaseMva,
    IReadOnlyDictionary<int, double> BaseKvLLByBus,
    IReadOnlyList<string> BranchIds,
    IReadOnlyList<string> BranchKinds,
    IReadOnlyList<string> LoadIds)
{
    /// <summary>The optional display name of each bus that has one (N062: the IEEE 123 feeder's regulator nodes RG1 to
    /// RG4), keyed by bus id. Empty when no bus is named.</summary>
    public IReadOnlyDictionary<int, string> BusNames { get; init; } = new Dictionary<int, string>();

    /// <summary>The authored ids of the switches given as open (N062). An open switch is not a branch: it is listed so a
    /// report can say it was read and left open, never silently dropped.</summary>
    public IReadOnlyList<string> OpenSwitchIds { get; init; } = Array.Empty<string>();

    /// <summary>The authored id of each capacitor, aligned with <see cref="MultiPhaseModel.Capacitors"/> (N062).</summary>
    public IReadOnlyList<string> CapacitorIds { get; init; } = Array.Empty<string>();

    /// <summary>N354 (CF5): the active-front-end converter units of the v1 file's "converters" section (rating and internal
    /// impedance on that rating), read by the three-phase harmonic study only (<c>dist3ph harmonics --afe feeder</c>).
    /// Empty for a feeder without the section, a v2 file or a lifted case.</summary>
    public IReadOnlyList<GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd> Converters { get; init; } = Array.Empty<GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd>();

    /// <summary>N354 (CF5): the authored id of each converter unit, aligned with <see cref="Converters"/>.</summary>
    public IReadOnlyList<string> ConverterIds { get; init; } = Array.Empty<string>();

    /// <summary>N339 (October 2026): the provenance a <c>gridsim-dist3ph/2</c> file gave the feeder (its default and any
    /// per-element entries); null for a feeder read from a v1 file or lifted from a positive-sequence case, which carry
    /// none. Informational: no solver reads it.</summary>
    public Dist3phProvenance? Provenance { get; init; }

    /// <summary>A bus's display name if it has one, else its id.</summary>
    public string BusLabel(int busId) => BusNames.TryGetValue(busId, out string? n) ? n : busId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Line-to-neutral voltage base of a bus, in volts: kV_LL * 1000 / sqrt(3).</summary>
    public double VoltBaseLN(int busId) => BaseKvLLByBus[busId] * 1000.0 / Math.Sqrt(3.0);

    /// <summary>Phase-current base of a bus, in amps: S_3ph / (sqrt(3) * V_LL). This is the same number as the
    /// per-phase power base divided by the line-to-neutral voltage base, so a per-unit current from the sweep
    /// (I = conj(S/V) per phase) times this gives amps.</summary>
    public double AmpBase(int busId) => BaseMva * 1e6 / (Math.Sqrt(3.0) * BaseKvLLByBus[busId] * 1000.0);
}

/// <summary>A measurement set for the three-phase estimators, read from a <c>gridsim-dist3ph-measurements/1</c> file.</summary>
/// <param name="VoltageMagnitudes">Per-phase voltage-magnitude measurements (per-unit).</param>
/// <param name="Currents">Per-phase branch current-phasor measurements (per-unit, rectangular).</param>
/// <param name="PseudoVoltageValuePu">When set, nominal voltage-magnitude pseudo-measurements at this value are added
/// for every non-source bus that has no voltage measurement (<see cref="DistributionPseudoMeasurements"/>).</param>
/// <param name="PseudoVoltageSigmaPu">Sigma of those pseudo-measurements (per-unit); set together with the value.</param>
public sealed record MultiPhaseMeasurementSet(
    IReadOnlyList<ThreePhaseVMag> VoltageMagnitudes,
    IReadOnlyList<ThreePhaseCurrent> Currents,
    double? PseudoVoltageValuePu,
    double? PseudoVoltageSigmaPu);

/// <summary>
/// EXPERIMENTAL. Strict loader for the <c>gridsim-dist3ph/1</c> JSON format, the first way to build a
/// <see cref="MultiPhaseModel"/> from a file (R059). It is a plain System.Text.Json reader in the style of
/// JsonGridLoader, kept deliberately strict: it will move under the v2 schema family (technical chapter 28) later.
///
/// What the format holds (see user manual ch. 7 and technical ch. 18, "The gridsim-dist3ph/1 feeder file", for a full
/// example; D-DOC-155: this used to cite "technical chapter 18.5", a section number that no longer exists):
///   - "schema": exactly "gridsim-dist3ph/1" (no other version or spelling is accepted);
///   - "name", "description" (cite the data source here), "baseMva" (three-phase MVA base);
///   - "buses": id, baseKvLL, and on exactly one bus a "source" block (the infinite-bus voltage);
///   - "lineCodes": a 3x3 series phase-impedance matrix (r and x) per unit length, in ohm/mile or ohm/km, and
///     optionally (N062) a 3x3 shunt susceptance "b" with "bUnit" uS/mile or uS/km (the line charging);
///   - "lines": from, to, code, length and lengthUnit (ft, mile, m, km), and optionally (N062) "phases" ("abc" by
///     default; "ab", "ac", "bc", "a", "b" or "c" for a lateral, whose code must be zero on the other phases);
///   - "transformers": grounded-wye/grounded-wye, or (1.95.251 / 1.95.252) delta / grounded-wye, at rated ratio (see below);
///   - "switches" (N062): from, to, optional phases, and "closed" (true or false). A closed switch is a branch with no
///     impedance; an open one is not a branch (it is recorded, so a normally-open tie never closes a loop);
///   - "regulators" (N062): from, to, optional phases, "tapStepPercent" and "taps" (one integer per phase, 0 on a phase
///     the regulator does not have): an ideal step-voltage regulator from the From bus to its output bus, voltage
///     ratio 1 + step * tap per phase (<see cref="PhaseBranch.StepRegulatorRatio"/>);
///   - "capacitors" (N062): bus and "kvar" per phase at the bus's nominal voltage (wye, constant susceptance);
///   - "loads": kW and kvar per phase at nominal voltage, "connection" wye or (N062) delta (then the three entries are
///     a-b, b-c, c-a) and "model" constant-power or (N062) constant-current or constant-impedance. A ZIP load is up to
///     three loads on one bus, one of each model.
///   - buses may carry an optional display "name" (N062), for nodes the published data names, such as RG1.
///
/// Per-unit conversion. Voltages are per-unit of each bus's line-to-neutral base (kV_LL / sqrt 3). Per-phase
/// power is per-unit of BaseMva / 3. So the impedance base is kV_LL^2 / BaseMva ohm and the current base is
/// BaseMva / (sqrt 3 kV_LL). A line's per-unit matrix is (r + jx) * length / Zbase of its (shared) bus kV base.
///
/// Transformers (1.95.251 / 1.95.252: "d-gy", a delta / grounded-wye at rated ratio with the delta on the From side,
/// step-down or step-up by the rated voltages, is also modelled; see <see cref="PhaseBranch.DeltaWyeStepDown"/> and
/// <see cref="PhaseBranch.DeltaWyeStepUp"/>). A grounded-wye/grounded-wye transformer whose rated kV equal the kV bases of the buses it joins is,
/// in per-unit, exactly its leakage impedance on every phase (the ideal transformer becomes 1:1 and all three
/// sequences pass), so the loader turns it into a diagonal <see cref="PhaseBranch"/> with
/// z = (r% + j x%) / 100 * (BaseMva * 1000 / kVA). That needs no new solver element. N334 (CF5): "oy-od" (open wye /
/// open delta: primaries on From-side phases A and B to earth; the step-down connection only, a step-up is refused, N357)
/// and "od-od" (open delta / open delta) banks are modelled
/// (<see cref="PhaseBranch.OpenWyeOpenDelta"/>, <see cref="PhaseBranch.OpenDeltaOpenDelta"/>). N335 (CF5): "y-d", "d-d",
/// "oy-od" and "od-od" take an optional "windings" array of per-winding {"rPercent", "xPercent"} on the bank's kVA
/// (three, ab / bc / ca, for "y-d" and "d-d"; two, ab / bc, for the open banks) in place of the bank's rPercent /
/// xPercent, for unequal winding impedances (see <see cref="TransformerWindings"/>). Every other connection and
/// magnetising branches are REFUSED with a message saying the sweep does not model them yet, rather than approximated.
///
/// Strictness. Member names are case-sensitive; an unknown member, a duplicate member, a missing required member,
/// a wrong JSON type, a non-finite or out-of-range number, a matrix that is not 3x3, a duplicate id, an unknown bus
/// or line-code reference, a branch joining a bus to itself, a line joining buses of different kV base, and a
/// network that is not one radial tree from the single source are all errors. Every error names the file and the
/// JSON path of the offending value. Comments are allowed (for annotation); trailing commas are not.
/// </summary>
public static partial class MultiPhaseJsonLoader
{
    /// <summary>1.95.254 (N168): the band a transformer's rated kV may lie in, as a multiple of its bus's kV base, for an
    /// off-nominal ratio (outside it the value is refused as a data error).</summary>
    public const double OffNominalLow = 0.8;

    /// <summary>1.95.254: the upper end of that band.</summary>
    public const double OffNominalHigh = 1.2;

    /// <summary>The one schema id this loader accepts, matched exactly (case-sensitive).</summary>
    public const string Schema = "gridsim-dist3ph/1";

    /// <summary>The one schema id accepted for a measurement file, matched exactly.</summary>
    public const string MeasurementSchema = "gridsim-dist3ph-measurements/1";

    // Feet per mile and metres per kilometre: the only length conversions the format needs.
    private const double FeetPerMile = 5280.0;
    private const double MetresPerKm = 1000.0;

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
        // A member given twice ("kw": [...], ... "kw": [...]) would otherwise be read as last-one-wins: refuse it.
        AllowDuplicateProperties = false,
    };

    /// <summary>Read and validate a feeder file. Throws <see cref="InvalidDataException"/> naming the file and the
    /// JSON path on any problem; throws <see cref="FileNotFoundException"/> if the file does not exist.</summary>
    public static MultiPhaseFeeder Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"three-phase feeder file not found: {path}", path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Validate and build a feeder from JSON text. <paramref name="sourceName"/> is used in every message.</summary>
    public static MultiPhaseFeeder Parse(string json, string sourceName)
    {
        using JsonDocument doc = OpenDocument(json, sourceName);
        var ctx = new Ctx(sourceName);
        JsonElement root = doc.RootElement;
        ctx.RequireObject(root, "$");
        // The schema id first: a file of another type (or another version) must not be half-read as this one,
        // and its error should say "wrong file type" rather than complain about the other type's members.
        string schema = ctx.Str(root, "$", "schema");
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
            throw ctx.Fail("$.schema", $"expected exactly \"{Schema}\" but found \"{schema}\".");
        ctx.AllowOnly(root, "$", "schema", "name", "description", "baseMva", "buses", "lineCodes", "lines", "transformers",
            "switches", "regulators", "capacitors", "loads", "converters");

        string name = ctx.Str(root, "$", "name");
        string description = ctx.Str(root, "$", "description");
        double baseMva = ctx.Number(root, "$", "baseMva");
        if (!(baseMva > 0)) throw ctx.Fail("$.baseMva", $"must be above 0 MVA (got {Fmt(baseMva)}).");

        // ---- buses -----------------------------------------------------------------------------------------
        var buses = new List<PhaseBus>();
        var baseKv = new Dictionary<int, double>();
        var busNames = new Dictionary<int, string>();
        int sourceCount = 0;
        JsonElement busArr = ctx.Arr(root, "$", "buses");
        if (busArr.GetArrayLength() < 2) throw ctx.Fail("$.buses", "a feeder needs at least two buses (a source and one more).");
        int bi = 0;
        foreach (JsonElement b in busArr.EnumerateArray())
        {
            string p = $"$.buses[{bi}]";
            ctx.RequireObject(b, p);
            ctx.AllowOnly(b, p, "id", "baseKvLL", "source", "name");
            int id = ctx.Int(b, p, "id");
            if (baseKv.ContainsKey(id)) throw ctx.Fail($"{p}.id", $"duplicate bus id {id}.");
            double kv = ctx.Number(b, p, "baseKvLL");
            if (!(kv > 0)) throw ctx.Fail($"{p}.baseKvLL", $"must be above 0 kV (got {Fmt(kv)}).");
            baseKv[id] = kv;
            if (b.TryGetProperty("name", out _))
            {
                // N062: an optional display name, unique, so a report can use the published node names.
                string nm = ctx.Str(b, p, "name");
                if (nm.Length == 0) throw ctx.Fail($"{p}.name", "must not be empty.");
                if (busNames.ContainsValue(nm)) throw ctx.Fail($"{p}.name", $"duplicate bus name \"{nm}\".");
                busNames[id] = nm;
            }

            if (b.TryGetProperty("source", out JsonElement src))
            {
                sourceCount++;
                Complex[] v = ReadSource(ctx, src, $"{p}.source");
                buses.Add(new PhaseBus(id, true, v));
            }
            else buses.Add(new PhaseBus(id, false));
            bi++;
        }
        if (sourceCount != 1)
            throw ctx.Fail("$.buses", $"exactly one bus must carry a \"source\" block (found {sourceCount}).");

        // ---- line codes ------------------------------------------------------------------------------------
        // Each code is stored as a 3x3 complex impedance in OHMS PER MILE so a line only needs its length.
        var codes = new Dictionary<string, Complex[,]>(StringComparer.Ordinal);
        // N062: the optional shunt admittance of each code, j * b, in SIEMENS PER MILE (null where the code has none).
        var codeShunts = new Dictionary<string, Complex[,]?>(StringComparer.Ordinal);
        if (root.TryGetProperty("lineCodes", out _))
        {
            JsonElement codeArr = ctx.Arr(root, "$", "lineCodes");
            int ci = 0;
            foreach (JsonElement c in codeArr.EnumerateArray())
            {
                string p = $"$.lineCodes[{ci}]";
                ctx.RequireObject(c, p);
                ctx.AllowOnly(c, p, "id", "unit", "r", "x", "b", "bUnit");
                string id = ctx.Str(c, p, "id");
                if (id.Length == 0) throw ctx.Fail($"{p}.id", "must not be empty.");
                if (codes.ContainsKey(id)) throw ctx.Fail($"{p}.id", $"duplicate line code id \"{id}\".");
                string unit = ctx.Str(c, p, "unit");
                double toPerMile = unit switch
                {
                    "ohm/mile" => 1.0,
                    "ohm/km" => FeetPerMile * 0.3048 / MetresPerKm,   // 1 mile = 1.609344 km exactly
                    _ => throw ctx.Fail($"{p}.unit", $"must be \"ohm/mile\" or \"ohm/km\" (got \"{unit}\")."),
                };
                double[,] r = ctx.Matrix3(c, p, "r");
                double[,] x = ctx.Matrix3(c, p, "x");
                // GR-580: a passive line's series impedance matrix is symmetric (mutual coupling is reciprocal). A code
                // computed offline may differ from its transpose in the last bits (the shipped IEEE 4-node code does, by
                // about 2e-16 relative), so the check allows a relative difference of 1e-9, far above that rounding and far
                // below any real data; beyond it the code is refused (a transposed or mistyped entry), never repaired. The
                // matrix is used exactly as written, so an accepted code solves bit for bit as before.
                RequireNearSymmetric(ctx, r, $"{p}.r", "series resistance");
                RequireNearSymmetric(ctx, x, $"{p}.x", "series reactance");
                var z = new Complex[3, 3];
                for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    z[i, j] = new Complex(r[i, j] * toPerMile, x[i, j] * toPerMile);
                // A negative self resistance is not a physical conductor; refuse it rather than solve it.
                for (int i = 0; i < 3; i++)
                    if (r[i, i] < 0) throw ctx.Fail($"{p}.r[{i}][{i}]", $"self resistance must not be negative (got {Fmt(r[i, i])}).");
                codes[id] = z;

                // N062: the line charging, a 3x3 shunt susceptance per unit length, given with its unit. Both members
                // or neither: a "b" with no unit (or a unit with no "b") is refused, never guessed.
                bool hasB = c.TryGetProperty("b", out _), hasBUnit = c.TryGetProperty("bUnit", out _);
                if (hasB != hasBUnit)
                    throw ctx.Fail(hasB ? $"{p}.bUnit" : $"{p}.b", "\"b\" and \"bUnit\" must be given together.");
                Complex[,]? yPerMile = null;
                if (hasB)
                {
                    string bUnit = ctx.Str(c, p, "bUnit");
                    double bToSiemensPerMile = bUnit switch
                    {
                        "uS/mile" => 1e-6,
                        "uS/km" => 1e-6 * FeetPerMile * 0.3048 / MetresPerKm,   // per km to per mile: times 1.609344
                        _ => throw ctx.Fail($"{p}.bUnit", $"must be \"uS/mile\" or \"uS/km\" (got \"{bUnit}\")."),
                    };
                    double[,] bm = ctx.Matrix3(c, p, "b");
                    yPerMile = new Complex[3, 3];
                    for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        if (bm[i, j] != bm[j, i])
                            throw ctx.Fail($"{p}.b[{i}][{j}]", $"the shunt susceptance matrix must be symmetric (b[{i}][{j}] = {Fmt(bm[i, j])}, b[{j}][{i}] = {Fmt(bm[j, i])}).");
                        yPerMile[i, j] = new Complex(0.0, bm[i, j] * bToSiemensPerMile);
                    }
                    for (int i = 0; i < 3; i++)
                        if (bm[i, i] < 0) throw ctx.Fail($"{p}.b[{i}][{i}]", $"self susceptance must not be negative (got {Fmt(bm[i, i])}).");
                }
                codeShunts[id] = yPerMile;
                ci++;
            }
        }

        var branches = new List<PhaseBranch>();
        var branchIds = new List<string>();
        var branchKinds = new List<string>();
        var seenBranchIds = new HashSet<string>(StringComparer.Ordinal);

        // ---- lines -----------------------------------------------------------------------------------------
        if (root.TryGetProperty("lines", out _))
        {
            JsonElement lineArr = ctx.Arr(root, "$", "lines");
            int li = 0;
            foreach (JsonElement l in lineArr.EnumerateArray())
            {
                string p = $"$.lines[{li}]";
                ctx.RequireObject(l, p);
                ctx.AllowOnly(l, p, "id", "from", "to", "code", "length", "lengthUnit", "phases");
                string id = BranchId(ctx, l, p, seenBranchIds);
                Phase phases = Phases(ctx, l, p);
                (int from, int to) = Endpoints(ctx, l, p, baseKv);
                if (baseKv[from] != baseKv[to])
                    throw ctx.Fail(p, $"line \"{id}\" joins bus {from} ({Fmt(baseKv[from])} kV) to bus {to} ({Fmt(baseKv[to])} kV); " +
                                      "a line must join buses of the same kV base (use a transformer between voltage levels).");
                string code = ctx.Str(l, p, "code");
                if (!codes.TryGetValue(code, out Complex[,]? zPerMile))
                    throw ctx.Fail($"{p}.code", $"unknown line code \"{code}\" (not in $.lineCodes).");
                double length = ctx.Number(l, p, "length");
                if (!(length > 0)) throw ctx.Fail($"{p}.length", $"must be above 0 (got {Fmt(length)}).");
                string lengthUnit = ctx.Str(l, p, "lengthUnit");
                double miles = lengthUnit switch
                {
                    "ft" => length / FeetPerMile,
                    "mile" => length,
                    "m" => length / (FeetPerMile * 0.3048),
                    "km" => length * MetresPerKm / (FeetPerMile * 0.3048),
                    _ => throw ctx.Fail($"{p}.lengthUnit", $"must be \"ft\", \"mile\", \"m\" or \"km\" (got \"{lengthUnit}\")."),
                };
                // Per-unit on the line's own voltage level: Zbase = kV_LL^2 / MVA_3ph ohm.
                double zBase = baseKv[from] * baseKv[from] / baseMva;
                // N062: a lateral's code must be zero on the phases the line does not carry, and every phase it does
                // carry must have a conductor (a non-zero self impedance), or the declared phases and the data disagree.
                CheckCodePhases(ctx, p, code, zPerMile, codeShunts[code], phases);
                var zPu = new Complex[3, 3];
                for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    zPu[i, j] = zPerMile[i, j] * miles / zBase;
                // The shunt in per-unit is Y (siemens) times Zbase (ohm).
                PhaseImpedance? yPu = null;
                if (codeShunts[code] is { } yPerMile)
                {
                    var y = new Complex[3, 3];
                    for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        y[i, j] = yPerMile[i, j] * miles * zBase;
                    yPu = new PhaseImpedance(y);
                }
                // A three-phase line with no shunt is exactly the v1 PhaseBranch (the defaults), so a v1 file builds
                // bit-for-bit the model it always did.
                branches.Add(new PhaseBranch(from, to, new PhaseImpedance(zPu), phases, yPu));
                branchIds.Add(id);
                branchKinds.Add("line");
                li++;
            }
        }

        // ---- transformers ----------------------------------------------------------------------------------
        if (root.TryGetProperty("transformers", out _))
        {
            JsonElement txArr = ctx.Arr(root, "$", "transformers");
            int ti = 0;
            foreach (JsonElement t in txArr.EnumerateArray())
            {
                string p = $"$.transformers[{ti}]";
                ctx.RequireObject(t, p);
                ctx.AllowOnly(t, p, "id", "from", "to", "connection", "kva", "kvLLFrom", "kvLLTo", "rPercent", "xPercent", "windings");
                string id = BranchId(ctx, t, p, seenBranchIds);
                (int from, int to) = Endpoints(ctx, t, p, baseKv);
                string conn = ctx.Str(t, p, "connection");
                // 1.95.251 / 1.95.252 (N168): "d-gy" is a delta / grounded-wye transformer (delta on the From side),
                // validated against the IEEE 4 Node Test Feeder's published D-GrY step-down and step-up results.
                bool deltaWye = string.Equals(conn, "d-gy", StringComparison.Ordinal);
                bool wyeDelta = string.Equals(conn, "y-d", StringComparison.Ordinal);       // 1.95.253
                bool deltaDelta = string.Equals(conn, "d-d", StringComparison.Ordinal);     // 1.95.253
                bool openWye = string.Equals(conn, "oy-od", StringComparison.Ordinal);      // N334 (CF5)
                bool openDelta = string.Equals(conn, "od-od", StringComparison.Ordinal);    // N334 (CF5)
                if (!deltaWye && !wyeDelta && !deltaDelta && !openWye && !openDelta && !string.Equals(conn, "gy-gy", StringComparison.Ordinal))
                    throw ctx.Fail($"{p}.connection",
                        $"only \"gy-gy\" (grounded wye / grounded wye), \"d-gy\" (delta / grounded wye, the delta on the From side), \"y-d\" " +
                        $"(ungrounded wye / delta), \"d-d\" (delta / delta), \"oy-od\" (open wye / open delta, primaries on phases A and B) " +
                        $"and \"od-od\" (open delta / open delta) are supported (got \"{conn}\").");
                bool hasWindings = t.TryGetProperty("windings", out _);
                if (hasWindings && !(wyeDelta || deltaDelta || openWye || openDelta))
                    throw ctx.Fail($"{p}.windings", $"per-winding impedances are read for \"y-d\", \"d-d\", \"oy-od\" and \"od-od\" only (got \"{conn}\").");
                double kva = ctx.Number(t, p, "kva");
                if (!(kva > 0)) throw ctx.Fail($"{p}.kva", $"must be above 0 (got {Fmt(kva)}).");
                double kvFrom = ctx.Number(t, p, "kvLLFrom");
                double kvTo = ctx.Number(t, p, "kvLLTo");
                // 1.95.254 (N168): off-nominal ratios. The per-unit ideal transformer is 1:1 when the rated kV equal the
                // bus kV bases; otherwise its per-unit ratio is t = (base_from / rated_from) (rated_to / base_to). A rated
                // voltage more than 20 % from its bus base is not a tap (normal tap ranges are about +/-10 %) but a
                // data error (a wrong bus, or kV given as V), so it is refused by name.
                if (!(kvFrom >= OffNominalLow * baseKv[from] && kvFrom <= OffNominalHigh * baseKv[from]))
                    throw ctx.Fail($"{p}.kvLLFrom", $"rated {Fmt(kvFrom)} kV is more than 20 % from bus {from}'s baseKvLL {Fmt(baseKv[from])}; an off-nominal ratio is accepted within 20 % of the bus base (check the bus and the units).");
                if (!(kvTo >= OffNominalLow * baseKv[to] && kvTo <= OffNominalHigh * baseKv[to]))
                    throw ctx.Fail($"{p}.kvLLTo", $"rated {Fmt(kvTo)} kV is more than 20 % from bus {to}'s baseKvLL {Fmt(baseKv[to])}; an off-nominal ratio is accepted within 20 % of the bus base (check the bus and the units).");
                bool rated = kvFrom == baseKv[from] && kvTo == baseKv[to];
                double ratio = rated ? 1.0 : (baseKv[from] / kvFrom) * (kvTo / baseKv[to]);
                // Rebase from the nameplate (kVA, own kV) to the system base (BaseMva, same kV): scale by S_sys / S_rated.
                double rebase = baseMva * 1000.0 / kva;
                // Off-nominal (1.95.254): the nameplate impedance is on the To winding's rated kV, so it is moved to the To
                // bus's kV base by (rated_to / base_to)^2. At the rated ratio this factor is not applied (the old arithmetic).
                if (!rated) rebase *= (kvTo / baseKv[to]) * (kvTo / baseKv[to]);
                // N335 (CF5): with "windings" the bank's rPercent / xPercent are not given (one source for each number).
                if (hasWindings && (t.TryGetProperty("rPercent", out _) || t.TryGetProperty("xPercent", out _)))
                    throw ctx.Fail(p, $"transformer \"{id}\" gives both \"windings\" and the bank's rPercent / xPercent; give one or the other.");
                Complex z = Complex.Zero;
                if (!hasWindings)
                {
                    double rPct = ctx.Number(t, p, "rPercent");
                    double xPct = ctx.Number(t, p, "xPercent");
                    if (rPct < 0) throw ctx.Fail($"{p}.rPercent", $"must not be negative (got {Fmt(rPct)}).");
                    if (xPct < 0) throw ctx.Fail($"{p}.xPercent", $"must not be negative (got {Fmt(xPct)}).");
                    if (rPct == 0 && xPct == 0) throw ctx.Fail(p, $"transformer \"{id}\" has zero impedance (rPercent = xPercent = 0).");
                    z = new Complex(rPct / 100.0 * rebase, xPct / 100.0 * rebase);
                }
                Complex[]? windingZ = hasWindings ? ReadWindings(ctx, t, p, openWye || openDelta ? 2 : 3, rebase) : null;
                // N334 (CF5): the open banks. Without "windings" both units take the bank's impedance.
                if (openWye || openDelta)
                {
                    // N357 (endpass-v, 2026-10-11): the open wye / open delta element is the STEP-DOWN standard
                    // connection (units on A and B feed secondaries ab and bc, the To side lags by 30 degrees), which
                    // reproduces the IEEE 4 Node Test Feeder's published "Open Gr.Y-D" step-down results. The IEEE
                    // step-up connection is a different wiring (the higher-voltage side leads by 30 degrees, the units'
                    // secondaries on ab and ca): fed the IEEE step-up case, this element gave line-to-line voltages up to
                    // 2,669.2 V from the published ones (unbalanced, node 4 phase ab). A step-up open wye / open delta is
                    // therefore refused by name rather than solved with the wrong phase relation (the closed "y-d"
                    // picks its shift by voltage).
                    // GR-574: like "y-d" and "d-gy", the open bank's 30 degree shift is defined by which side is the higher
                    // voltage, so equal rated voltages are refused by name (they were given the step-down shift).
                    if (openWye && kvFrom == kvTo)
                        throw ctx.Fail($"{p}.connection",
                            $"\"oy-od\" needs different rated voltages on its two sides (got {Fmt(kvFrom)} kV on both): the ANSI 30 degree shift is defined by which side is the higher voltage.");
                    if (openWye && kvFrom < kvTo)
                        throw ctx.Fail($"{p}.connection",
                            $"\"oy-od\" is modelled as the step-down connection only (the To side lags by 30 degrees); a step-up open wye / open delta " +
                            $"(rated {Fmt(kvFrom)} kV to {Fmt(kvTo)} kV, the higher-voltage side leading) is not modelled yet.");
                    Complex zab = windingZ is null ? z : windingZ[0];
                    Complex zbc = windingZ is null ? z : windingZ[1];
                    PhaseImpedance zOpen = TransformerWindings.OpenDeltaSeriesZ(zab, zbc);
                    branches.Add(openWye ? new PhaseBranch(from, to, zOpen, OpenWyeOpenDelta: true, TransformerRatio: ratio)
                        : new PhaseBranch(from, to, zOpen, OpenDeltaOpenDelta: true, TransformerRatio: ratio));
                    branchIds.Add(id);
                    branchKinds.Add("transformer");
                    ti++;
                    continue;
                }
                // 1.95.253: "y-d" (ungrounded wye From side, delta To side) and "d-d" (delta / delta), equal winding
                // impedances. Their To side is a three-wire system (see PhaseBranch.HasDeltaSecondary).
                if (wyeDelta || deltaDelta)
                {
                    if (wyeDelta && kvFrom == kvTo)
                        throw ctx.Fail($"{p}.connection",
                            $"\"y-d\" needs different rated voltages on its two sides (got {Fmt(kvFrom)} kV on both): the ANSI 30 degree shift is defined by which side is the higher voltage.");
                    // N335 (CF5): per-winding impedances give the equivalent series matrix (equal windings: exactly the
                    // diagonal below); without "windings" the arithmetic is exactly what it was.
                    PhaseImpedance zt = windingZ is null ? PhaseImpedance.Diagonal(z)
                        : deltaDelta ? TransformerWindings.DeltaDeltaSeriesZ(windingZ) : TransformerWindings.WyeDeltaSeriesZ(windingZ);
                    branches.Add(deltaDelta ? new PhaseBranch(from, to, zt, DeltaDelta: true, TransformerRatio: ratio)
                        : kvFrom > kvTo ? new PhaseBranch(from, to, zt, WyeDeltaStepDown: true, TransformerRatio: ratio)
                        : new PhaseBranch(from, to, zt, WyeDeltaStepUp: true, TransformerRatio: ratio));
                    branchIds.Add(id);
                    branchKinds.Add("transformer");
                    ti++;
                    continue;
                }
                // The delta is always the From side. ANSI fixes the 30 degree shift by voltage: the higher-voltage side
                // leads. So a delta on the higher-voltage From side is a step-down (the wye lags, 1.95.251) and a delta on
                // the lower-voltage From side a step-up (the wye leads, 1.95.252). Equal rated voltages have no ANSI
                // direction and are refused.
                if (deltaWye && kvFrom == kvTo)
                    throw ctx.Fail($"{p}.connection",
                        $"\"d-gy\" needs different rated voltages on its two sides (got {Fmt(kvFrom)} kV on both): the ANSI 30 degree shift is defined by which side is the higher voltage.");
                // An off-nominal gy-gy unit is its ideal ratio at the From end followed by its leakage impedance: exactly
                // the step-voltage regulator element (RegulatorRatio, validated on the IEEE 123 feeder), with t on every phase.
                branches.Add(!deltaWye ? (rated ? new PhaseBranch(from, to, PhaseImpedance.Diagonal(z))
                                                : new PhaseBranch(from, to, PhaseImpedance.Diagonal(z), RegulatorRatio: new[] { ratio, ratio, ratio }))
                    : kvFrom > kvTo ? new PhaseBranch(from, to, PhaseImpedance.Diagonal(z), DeltaWyeStepDown: true, TransformerRatio: ratio)
                    : new PhaseBranch(from, to, PhaseImpedance.Diagonal(z), DeltaWyeStepUp: true, TransformerRatio: ratio));
                branchIds.Add(id);
                branchKinds.Add("transformer");
                ti++;
            }
        }

        // ---- switches (N062) ------------------------------------------------------------------------------
        var openSwitches = new List<string>();
        if (root.TryGetProperty("switches", out _))
        {
            int si = 0;
            foreach (JsonElement s in ctx.Arr(root, "$", "switches").EnumerateArray())
            {
                string p = $"$.switches[{si}]";
                ctx.RequireObject(s, p);
                ctx.AllowOnly(s, p, "id", "from", "to", "phases", "closed");
                string id = BranchId(ctx, s, p, seenBranchIds);
                (int from, int to) = Endpoints(ctx, s, p, baseKv);
                Phase phases = Phases(ctx, s, p);
                if (baseKv[from] != baseKv[to])
                    throw ctx.Fail(p, $"switch \"{id}\" joins bus {from} ({Fmt(baseKv[from])} kV) to bus {to} ({Fmt(baseKv[to])} kV); a switch joins buses of one kV base.");
                bool closed = ctx.Bool(s, p, "closed");
                si++;
                // An open switch is no branch at all (a normally-open tie must not close a loop); it is recorded.
                if (!closed) { openSwitches.Add(id); continue; }
                // A closed switch: no impedance, so the forward sweep carries the voltage across unchanged.
                branches.Add(new PhaseBranch(from, to, new PhaseImpedance(new Complex[3, 3]), phases));
                branchIds.Add(id);
                branchKinds.Add("switch");
            }
        }

        // ---- regulators (N062) ----------------------------------------------------------------------------
        if (root.TryGetProperty("regulators", out _))
        {
            int ri = 0;
            foreach (JsonElement g in ctx.Arr(root, "$", "regulators").EnumerateArray())
            {
                string p = $"$.regulators[{ri}]";
                ctx.RequireObject(g, p);
                ctx.AllowOnly(g, p, "id", "from", "to", "phases", "tapStepPercent", "taps");
                string id = BranchId(ctx, g, p, seenBranchIds);
                (int from, int to) = Endpoints(ctx, g, p, baseKv);
                Phase phases = Phases(ctx, g, p);
                if (baseKv[from] != baseKv[to])
                    throw ctx.Fail(p, $"regulator \"{id}\" joins bus {from} ({Fmt(baseKv[from])} kV) to bus {to} ({Fmt(baseKv[to])} kV); a regulator's input and output share one kV base.");
                double step = ctx.Number(g, p, "tapStepPercent");
                if (!(step > 0)) throw ctx.Fail($"{p}.tapStepPercent", $"must be above 0 (got {Fmt(step)}).");
                int[] taps = ctx.IntVector3(g, p, "taps");
                var ratio = new double[3];
                for (int ph = 0; ph < 3; ph++)
                {
                    if (!PhaseIx.Has(phases, ph))
                    {
                        if (taps[ph] != 0) throw ctx.Fail($"{p}.taps[{ph}]", $"phase {"abc"[ph]} is not one of the regulator's phases ({PhaseIx.Name(phases)}), so its tap must be 0 (got {taps[ph]}).");
                        ratio[ph] = 1.0;
                        continue;
                    }
                    ratio[ph] = PhaseBranch.StepRegulatorRatio(taps[ph], step / 100.0);
                    if (!(ratio[ph] > 0)) throw ctx.Fail($"{p}.taps[{ph}]", $"tap {taps[ph]} at {Fmt(step)} % per step gives a ratio of {Fmt(ratio[ph])}, which is not above 0.");
                }
                // An ideal regulator: no series impedance; its output bus is the To bus.
                branches.Add(new PhaseBranch(from, to, new PhaseImpedance(new Complex[3, 3]), phases, RegulatorRatio: ratio));
                branchIds.Add(id);
                branchKinds.Add("regulator");
                ri++;
            }
        }

        // ---- capacitors (N062) ----------------------------------------------------------------------------
        var capacitors = new List<PhaseCapacitor>();
        var capacitorIds = new List<string>();
        var capacitorPaths = new List<string>();
        if (root.TryGetProperty("capacitors", out _))
        {
            var seenCapIds = new HashSet<string>(StringComparer.Ordinal);
            int ci = 0;
            foreach (JsonElement c in ctx.Arr(root, "$", "capacitors").EnumerateArray())
            {
                string p = $"$.capacitors[{ci}]";
                ctx.RequireObject(c, p);
                ctx.AllowOnly(c, p, "id", "bus", "kvar");
                string id = ctx.Str(c, p, "id");
                if (id.Length == 0) throw ctx.Fail($"{p}.id", "must not be empty.");
                if (!seenCapIds.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate capacitor id \"{id}\".");
                int bus = ctx.Int(c, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                double[] kvar = ctx.Vector3(c, p, "kvar");
                for (int ph = 0; ph < 3; ph++)
                    if (kvar[ph] < 0) throw ctx.Fail($"{p}.kvar[{ph}]", $"must not be negative (got {Fmt(kvar[ph])}).");
                if (kvar.All(q => q == 0)) throw ctx.Fail($"{p}.kvar", "a capacitor bank needs a unit on at least one phase.");
                // Per-phase per-unit at nominal voltage: kvar / (BaseMva * 1000 / 3); that is also its susceptance in pu.
                double perPhaseKva = baseMva * 1000.0 / 3.0;
                capacitors.Add(new PhaseCapacitor(bus, new[] { kvar[0] / perPhaseKva, kvar[1] / perPhaseKva, kvar[2] / perPhaseKva }));
                capacitorIds.Add(id);
                capacitorPaths.Add(p);
                ci++;
            }
        }

        // ---- converters (N354, CF5) --------------------------------------------------------------------------
        // Active-front-end converter units, for the three-phase harmonic study only (dist3ph harmonics --afe feeder):
        // id, bus, kva (the unit's three-phase rating), xPercent (its internal reactance at the fundamental, percent on
        // that rating) and optional rPercent (series resistance, default 0). No solver but the harmonic study reads them,
        // so a feeder without the section, or a study without --afe, is exactly as before. No default impedance exists
        // on disk [VERIFY]: the file states it (the converter's coupling reactor or filter, from the manufacturer).
        var converters = new List<GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd>();
        var converterIds = new List<string>();
        if (root.TryGetProperty("converters", out _))
        {
            var seenConvIds = new HashSet<string>(StringComparer.Ordinal);
            int vi = 0;
            foreach (JsonElement c in ctx.Arr(root, "$", "converters").EnumerateArray())
            {
                string p = $"$.converters[{vi}]";
                ctx.RequireObject(c, p);
                ctx.AllowOnly(c, p, "id", "bus", "kva", "xPercent", "rPercent");
                string id = ctx.Str(c, p, "id");
                if (id.Length == 0) throw ctx.Fail($"{p}.id", "must not be empty.");
                if (!seenConvIds.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate converter id \"{id}\".");
                int bus = ctx.Int(c, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                double kva = ctx.Number(c, p, "kva");
                if (!(kva > 0)) throw ctx.Fail($"{p}.kva", $"must be above 0 (got {Fmt(kva)}).");
                double x = ctx.Number(c, p, "xPercent");
                if (!(x > 0)) throw ctx.Fail($"{p}.xPercent", $"must be above 0 (got {Fmt(x)}): the converter's internal reactance.");
                double r = c.TryGetProperty("rPercent", out _) ? ctx.Number(c, p, "rPercent") : 0.0;
                if (r < 0) throw ctx.Fail($"{p}.rPercent", $"must not be negative (got {Fmt(r)}).");
                converters.Add(new GridSim.Core.Harmonics.ThreePhaseActiveFrontEnd(bus, kva / 1000.0, x / 100.0, r / 100.0));
                converterIds.Add(id);
                vi++;
            }
        }

        // ---- loads -----------------------------------------------------------------------------------------
        var loadPaths = new List<string>();
        var loads = new List<PhaseLoad>();
        var loadIds = new List<string>();
        var seenLoadIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("loads", out _))
        {
            JsonElement loadArr = ctx.Arr(root, "$", "loads");
            int ki = 0;
            foreach (JsonElement l in loadArr.EnumerateArray())
            {
                string p = $"$.loads[{ki}]";
                ctx.RequireObject(l, p);
                ctx.AllowOnly(l, p, "id", "bus", "connection", "model", "kw", "kvar");
                string id = ctx.Str(l, p, "id");
                if (id.Length == 0) throw ctx.Fail($"{p}.id", "must not be empty.");
                if (!seenLoadIds.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate load id \"{id}\".");
                int bus = ctx.Int(l, p, "bus");
                if (!baseKv.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in $.buses).");
                string conn = ctx.Str(l, p, "connection");
                PhaseLoadConnection connection = conn switch
                {
                    "wye" => PhaseLoadConnection.Wye,
                    "delta" => PhaseLoadConnection.Delta,   // N062
                    _ => throw ctx.Fail($"{p}.connection", $"must be \"wye\" or \"delta\" (got \"{conn}\")."),
                };
                string model = ctx.Str(l, p, "model");
                PhaseLoadModel loadModel = model switch
                {
                    "constant-power" => PhaseLoadModel.ConstantPower,
                    "constant-current" => PhaseLoadModel.ConstantCurrent,       // N062
                    "constant-impedance" => PhaseLoadModel.ConstantImpedance,   // N062
                    _ => throw ctx.Fail($"{p}.model",
                        $"must be \"constant-power\", \"constant-current\" or \"constant-impedance\" (got \"{model}\"); " +
                        "a ZIP load is written as up to three loads on the same bus, one of each model."),
                };
                double[] kw = ctx.Vector3(l, p, "kw");
                double[] kvar = ctx.Vector3(l, p, "kvar");
                // Per-phase per-unit power: kW / (BaseMva * 1000 / 3).
                double perPhaseKva = baseMva * 1000.0 / 3.0;
                var s = new Complex[3];
                for (int ph = 0; ph < 3; ph++) s[ph] = new Complex(kw[ph] / perPhaseKva, kvar[ph] / perPhaseKva);
                // A constant-power wye load is built with the record's defaults, exactly the v1 load.
                loads.Add(loadModel == PhaseLoadModel.ConstantPower && connection == PhaseLoadConnection.Wye
                    ? new PhaseLoad(bus, s)
                    : new PhaseLoad(bus, s, loadModel, connection));
                loadIds.Add(id);
                loadPaths.Add(p);
                ki++;
            }
        }

        CheckRadialTree(ctx, buses, branches);
        CheckThreeWireTransformers(ctx, buses, branches, branchIds, branchKinds);
        // N062: the phases each bus has (from the source outward), and every element checked against them.
        Dictionary<int, Phase> busPhases = BusPhases(ctx, buses, branches, branchIds, branchKinds);
        CheckElementPhases(ctx, busPhases, loads, loadPaths, capacitors, capacitorPaths);

        var modelOut = new MultiPhaseModel(buses, branches, loads, baseMva, capacitors);
        return new MultiPhaseFeeder(modelOut, sourceName, name, description, baseMva, baseKv, branchIds, branchKinds, loadIds)
        {
            BusNames = busNames,
            OpenSwitchIds = openSwitches,
            CapacitorIds = capacitorIds,
            Converters = converters,       // N354 (CF5)
            ConverterIds = converterIds,
        };
    }

    /// <summary>Read a <c>gridsim-dist3ph-measurements/1</c> file and check every reference against <paramref name="feeder"/>.</summary>
    public static MultiPhaseMeasurementSet LoadMeasurements(string path, MultiPhaseFeeder feeder)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"three-phase measurement file not found: {path}", path);
        return ParseMeasurements(File.ReadAllText(path), path, feeder);
    }

    /// <summary>Validate and build a measurement set from JSON text against <paramref name="feeder"/>.</summary>
    public static MultiPhaseMeasurementSet ParseMeasurements(string json, string sourceName, MultiPhaseFeeder feeder)
    {
        using JsonDocument doc = OpenDocument(json, sourceName);
        var ctx = new Ctx(sourceName);
        JsonElement root = doc.RootElement;
        ctx.RequireObject(root, "$");
        string schema = ctx.Str(root, "$", "schema");
        if (!string.Equals(schema, MeasurementSchema, StringComparison.Ordinal))
            throw ctx.Fail("$.schema", $"expected exactly \"{MeasurementSchema}\" but found \"{schema}\".");
        ctx.AllowOnly(root, "$", "schema", "description", "voltageMagnitudes", "currents", "pseudoVoltage");
        if (root.TryGetProperty("description", out _)) ctx.Str(root, "$", "description");

        var vm = new List<ThreePhaseVMag>();
        if (root.TryGetProperty("voltageMagnitudes", out _))
        {
            int i = 0;
            foreach (JsonElement m in ctx.Arr(root, "$", "voltageMagnitudes").EnumerateArray())
            {
                string p = $"$.voltageMagnitudes[{i}]";
                ctx.RequireObject(m, p);
                ctx.AllowOnly(m, p, "bus", "phase", "valuePu", "sigmaPu");
                int bus = ctx.Int(m, p, "bus");
                if (!feeder.BaseKvLLByBus.ContainsKey(bus)) throw ctx.Fail($"{p}.bus", $"unknown bus {bus} (not in the feeder {feeder.SourceFile}).");
                int ph = PhaseIndex(ctx, m, p);
                double v = ctx.Number(m, p, "valuePu");
                if (!(v > 0)) throw ctx.Fail($"{p}.valuePu", $"a voltage magnitude must be above 0 pu (got {Fmt(v)}).");
                double sg = Sigma(ctx, m, p);
                vm.Add(new ThreePhaseVMag(bus, ph, v, sg));
                i++;
            }
        }

        var cur = new List<ThreePhaseCurrent>();
        if (root.TryGetProperty("currents", out _))
        {
            // The estimator matches a current on (from, to) in either orientation, so accept either here too.
            var pairs = new HashSet<(int, int)>();
            foreach (PhaseBranch br in feeder.Model.Branches) { pairs.Add((br.From, br.To)); pairs.Add((br.To, br.From)); }
            int i = 0;
            foreach (JsonElement m in ctx.Arr(root, "$", "currents").EnumerateArray())
            {
                string p = $"$.currents[{i}]";
                ctx.RequireObject(m, p);
                ctx.AllowOnly(m, p, "from", "to", "phase", "realPu", "imagPu", "sigmaPu");
                int from = ctx.Int(m, p, "from");
                int to = ctx.Int(m, p, "to");
                if (!pairs.Contains((from, to))) throw ctx.Fail(p, $"no branch joins bus {from} and bus {to} in the feeder {feeder.SourceFile}.");
                int ph = PhaseIndex(ctx, m, p);
                double re = ctx.Number(m, p, "realPu");
                double im = ctx.Number(m, p, "imagPu");
                double sg = Sigma(ctx, m, p);
                cur.Add(new ThreePhaseCurrent(from, to, ph, new Complex(re, im), sg));
                i++;
            }
        }

        double? pv = null, ps = null;
        if (root.TryGetProperty("pseudoVoltage", out JsonElement pseudo))
        {
            const string p = "$.pseudoVoltage";
            ctx.RequireObject(pseudo, p);
            ctx.AllowOnly(pseudo, p, "valuePu", "sigmaPu");
            pv = ctx.Number(pseudo, p, "valuePu");
            if (!(pv > 0)) throw ctx.Fail($"{p}.valuePu", $"must be above 0 pu (got {Fmt(pv.Value)}).");
            ps = Sigma(ctx, pseudo, p);
        }

        if (vm.Count == 0 && cur.Count == 0 && pv is null)
            throw ctx.Fail("$", "the measurement set is empty (no voltageMagnitudes, currents or pseudoVoltage).");
        return new MultiPhaseMeasurementSet(vm, cur, pv, ps);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Parse the text, turning a JSON syntax error (including a duplicate member) into an error that names the file.</summary>
    private static JsonDocument OpenDocument(string json, string sourceName)
    {
        try { return JsonDocument.Parse(json, DocOptions); }
        catch (JsonException ex)
        {
            // JsonException carries the path and line of the syntax error in its message.
            throw new InvalidDataException($"{sourceName}: not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The "source" block: optional per-phase magnitude (pu) and angle (degrees); default balanced 1.0 pu.</summary>
    private static Complex[] ReadSource(Ctx ctx, JsonElement src, string p)
    {
        ctx.RequireObject(src, p);
        ctx.AllowOnly(src, p, "magnitudePu", "angleDeg");
        double[] mag = src.TryGetProperty("magnitudePu", out _) ? ctx.Vector3(src, p, "magnitudePu") : new[] { 1.0, 1.0, 1.0 };
        double[] ang = src.TryGetProperty("angleDeg", out _) ? ctx.Vector3(src, p, "angleDeg") : new[] { 0.0, -120.0, 120.0 };
        for (int i = 0; i < 3; i++)
            if (!(mag[i] > 0)) throw ctx.Fail($"{p}.magnitudePu[{i}]", $"must be above 0 pu (got {Fmt(mag[i])}).");
        // The default (no angles given) is built exactly as MultiPhaseModel.BalancedNominal builds it, so a source
        // block with no angles gives bit-for-bit the same voltages as a hand-built model using BalancedNominal.
        if (!src.TryGetProperty("angleDeg", out _))
        {
            // D1 (October 2026): from the same angles, not FromPolar(mag, Phase(BalancedNominal()[i])): that round trip
            // relied on atan2 giving back the angle exactly, which the Windows C runtime happened to do and the
            // deterministic atan2 (correct to within an ulp, the same on every OS) does not.
            var v = new Complex[3];
            for (int i = 0; i < 3; i++) v[i] = DetMath.FromPolar(mag[i], MultiPhaseModel.BalancedAngleRad(i));
            return v;
        }
        var outV = new Complex[3];
        for (int i = 0; i < 3; i++) outV[i] = DetMath.FromPolar(mag[i], ang[i] * Math.PI / 180.0);
        return outV;
    }

    private static string BranchId(Ctx ctx, JsonElement e, string p, HashSet<string> seen)
    {
        string id = ctx.Str(e, p, "id");
        if (id.Length == 0) throw ctx.Fail($"{p}.id", "must not be empty.");
        // Lines and transformers share one id space so a branch is named unambiguously in every report.
        if (!seen.Add(id)) throw ctx.Fail($"{p}.id", $"duplicate branch id \"{id}\" (lines and transformers share one id space).");
        return id;
    }

    private static (int From, int To) Endpoints(Ctx ctx, JsonElement e, string p, Dictionary<int, double> baseKv)
    {
        int from = ctx.Int(e, p, "from");
        int to = ctx.Int(e, p, "to");
        if (!baseKv.ContainsKey(from)) throw ctx.Fail($"{p}.from", $"unknown bus {from} (not in $.buses).");
        if (!baseKv.ContainsKey(to)) throw ctx.Fail($"{p}.to", $"unknown bus {to} (not in $.buses).");
        if (from == to) throw ctx.Fail(p, $"a branch cannot join bus {from} to itself.");
        return (from, to);
    }

    private static int PhaseIndex(Ctx ctx, JsonElement m, string p)
    {
        string ph = ctx.Str(m, p, "phase");
        return ph switch
        {
            "a" => 0,
            "b" => 1,
            "c" => 2,
            _ => throw ctx.Fail($"{p}.phase", $"must be \"a\", \"b\" or \"c\" (got \"{ph}\")."),
        };
    }

    private static double Sigma(Ctx ctx, JsonElement m, string p)
    {
        double s = ctx.Number(m, p, "sigmaPu");
        if (!(s > 0)) throw ctx.Fail($"{p}.sigmaPu", $"must be above 0 (got {Fmt(s)}).");
        return s;
    }

    /// <summary>N062: the optional "phases" member of a branch: the letters of the phases it carries, in a, b, c order
    /// ("abc", "ab", "ac", "bc", "a", "b", "c"). Absent: all three.</summary>
    private static Phase Phases(Ctx ctx, JsonElement e, string p)
    {
        if (!e.TryGetProperty("phases", out _)) return Phase.Abc;
        string s = ctx.Str(e, p, "phases");
        return s switch
        {
            "abc" => Phase.Abc,
            "ab" => Phase.A | Phase.B,
            "ac" => Phase.A | Phase.C,
            "bc" => Phase.B | Phase.C,
            "a" => Phase.A,
            "b" => Phase.B,
            "c" => Phase.C,
            _ => throw ctx.Fail($"{p}.phases", $"must be one of \"abc\", \"ab\", \"ac\", \"bc\", \"a\", \"b\", \"c\" (got \"{s}\")."),
        };
    }

    /// <summary>N062: a line's code must agree with the phases the line carries: every entry of an absent phase's row and
    /// column zero (impedance and shunt), and every present phase with a conductor (a non-zero self impedance).</summary>
    private static void CheckCodePhases(Ctx ctx, string p, string code, Complex[,] z, Complex[,]? y, Phase phases)
    {
        for (int i = 0; i < 3; i++)
        {
            bool present = PhaseIx.Has(phases, i);
            if (present && z[i, i] == Complex.Zero)
                throw ctx.Fail($"{p}.phases", $"the line carries phase {"abc"[i]} but line code \"{code}\" has no conductor on it (zero self impedance).");
            for (int j = 0; j < 3; j++)
            {
                if (present && PhaseIx.Has(phases, j)) continue;
                if (z[i, j] != Complex.Zero || (y is not null && y[i, j] != Complex.Zero))
                    throw ctx.Fail($"{p}.phases",
                        $"the line carries phases {PhaseIx.Name(phases)} but line code \"{code}\" has a non-zero entry [{i}][{j}] on another phase.");
            }
        }
    }

    /// <summary>N062: the phases of every bus, walking out from the source (the source has all three; every other bus the
    /// phases of the branch reaching it). A branch that carries a phase its upstream bus lacks, or a regulator whose From
    /// bus is downstream, is refused, naming the branch. Runs after <see cref="CheckRadialTree"/>, so the tree is sound.</summary>
    private static Dictionary<int, Phase> BusPhases(Ctx ctx, List<PhaseBus> buses, List<PhaseBranch> branches,
        List<string> branchIds, List<string> branchKinds)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (PhaseBus b in buses) adj[b.Id] = new List<int>();
        for (int k = 0; k < branches.Count; k++) { adj[branches[k].From].Add(k); adj[branches[k].To].Add(k); }
        int source = buses.First(b => b.IsSource).Id;
        var phases = new Dictionary<int, Phase> { [source] = Phase.Abc };
        var queue = new Queue<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int k in adj[u])
            {
                PhaseBranch br = branches[k];
                int other = br.From == u ? br.To : br.From;
                if (phases.ContainsKey(other)) continue;
                if ((br.Phases & ~phases[u]) != 0)
                    throw ctx.Fail("$", $"{branchKinds[k]} \"{branchIds[k]}\" carries phase(s) {PhaseIx.Name(br.Phases)} but bus {u}, " +
                                        $"which feeds it, has only {PhaseIx.Name(phases[u])}.");
                if (br.RegulatorRatio is not null && br.From != u)
                    throw ctx.Fail("$", $"regulator \"{branchIds[k]}\" faces the source: its \"from\" bus {br.From} is downstream of " +
                                        $"its \"to\" bus {br.To}; \"from\" must be the bus nearer the source.");
                phases[other] = br.Phases;
                queue.Enqueue(other);
            }
        }
        return phases;
    }

    /// <summary>N062: loads and capacitors draw only on phases their bus has (a wye load or a capacitor unit on each
    /// phase with a non-zero value; a delta load on both phases of each pair with a non-zero value).</summary>
    private static void CheckElementPhases(Ctx ctx, Dictionary<int, Phase> busPhases, List<PhaseLoad> loads,
        List<string> loadPaths, List<PhaseCapacitor> caps, List<string> capPaths)
    {
        for (int k = 0; k < loads.Count; k++)
        {
            PhaseLoad l = loads[k];
            Phase have = busPhases[l.Bus];
            for (int ph = 0; ph < 3; ph++)
            {
                if (l.SPerPhase[ph] == Complex.Zero) continue;
                bool ok = l.Connection == PhaseLoadConnection.Wye
                    ? PhaseIx.Has(have, ph)
                    : PhaseIx.Has(have, ph) && PhaseIx.Has(have, (ph + 1) % 3);
                if (!ok)
                {
                    string where = l.Connection == PhaseLoadConnection.Wye ? $"phase {"abc"[ph]}" : $"phases {"abc"[ph]}-{"abc"[(ph + 1) % 3]}";
                    throw ctx.Fail($"{loadPaths[k]}.kw", $"the load draws on {where} but bus {l.Bus} has only phases {PhaseIx.Name(have)}.");
                }
            }
        }
        for (int k = 0; k < caps.Count; k++)
        {
            Phase have = busPhases[caps[k].Bus];
            for (int ph = 0; ph < 3; ph++)
                if (caps[k].QPerPhase[ph] != 0 && !PhaseIx.Has(have, ph))
                    throw ctx.Fail($"{capPaths[k]}.kvar[{ph}]", $"bus {caps[k].Bus} has only phases {PhaseIx.Name(have)}, so it cannot carry a unit on phase {"abc"[ph]}.");
        }
    }

    /// <summary>N335 (CF5): the "windings" array of a transformer: exactly <paramref name="count"/> objects with rPercent and
    /// xPercent (on the bank's kVA and the To winding's kV, like the bank's own pair), rebased by <paramref name="rebase"/>
    /// exactly as the bank's impedance is. Each must be non-negative and not both zero.</summary>
    private static Complex[] ReadWindings(Ctx ctx, JsonElement t, string p, int count, double rebase)
    {
        JsonElement arr = ctx.Arr(t, p, "windings");
        int n = arr.GetArrayLength();
        if (n != count)
            throw ctx.Fail($"{p}.windings", $"needs exactly {count} windings ({(count == 3 ? "ab, bc, ca" : "ab, bc")}), got {n}.");
        var z = new Complex[count];
        int k = 0;
        foreach (JsonElement w in arr.EnumerateArray())
        {
            string wp = $"{p}.windings[{k}]";
            ctx.RequireObject(w, wp);
            ctx.AllowOnly(w, wp, "rPercent", "xPercent");
            double r = ctx.Number(w, wp, "rPercent");
            double x = ctx.Number(w, wp, "xPercent");
            if (r < 0) throw ctx.Fail($"{wp}.rPercent", $"must not be negative (got {Fmt(r)}).");
            if (x < 0) throw ctx.Fail($"{wp}.xPercent", $"must not be negative (got {Fmt(x)}).");
            if (r == 0 && x == 0) throw ctx.Fail(wp, "a winding has zero impedance (rPercent = xPercent = 0).");
            z[k] = new Complex(r / 100.0 * rebase, x / 100.0 * rebase);
            k++;
        }
        return z;
    }

    /// <summary>1.95.253 (N168): a grounded-wye / grounded-wye transformer needs an earthed primary neutral, so one fed
    /// from a three-wire system (downstream of a "y-d" or "d-d" secondary, before any "d-gy") is refused by name. The
    /// solver cannot see it (a gy-gy unit is a plain series impedance there); the other three-wire rules (no wye loads,
    /// capacitors, line charging or regulators) are the sweep's.</summary>
    private static void CheckThreeWireTransformers(Ctx ctx, List<PhaseBus> buses, List<PhaseBranch> branches, List<string> ids, List<string> kinds,
        string transformersPath = "$.transformers")
    {
        if (!branches.Any(b => b.HasDeltaSecondary)) return;
        var adj = new Dictionary<int, List<int>>();
        foreach (PhaseBus b in buses) adj[b.Id] = new List<int>();
        for (int k = 0; k < branches.Count; k++) { adj[branches[k].From].Add(k); adj[branches[k].To].Add(k); }
        int source = buses.First(b => b.IsSource).Id;
        var threeWire = new Dictionary<int, bool> { [source] = false };
        var queue = new Queue<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int k in adj[u])
            {
                PhaseBranch br = branches[k];
                int other = br.From == u ? br.To : br.From;
                if (threeWire.ContainsKey(other)) continue;
                if (threeWire[u] && kinds[k] == "transformer" && !br.IsTransformerElement)
                    throw ctx.Fail(transformersPath, $"transformer \"{ids[k]}\" (gy-gy) is fed from a three-wire system (downstream of a delta secondary), which has no earthed neutral for its grounded-wye primary.");
                // N334 (CF5): an open wye / open delta bank's primaries are phase-to-earth units: they need an earthed system too.
                if (threeWire[u] && br.OpenWyeOpenDelta)
                    throw ctx.Fail(transformersPath, $"transformer \"{ids[k]}\" (oy-od) is fed from a three-wire system (downstream of a delta secondary), which has no earth for its phase-to-earth primaries.");
                threeWire[other] = br.HasDeltaSecondary || (threeWire[u] && !br.IsDeltaWye);
                queue.Enqueue(other);
            }
        }
    }

    /// <summary>The v1 format is radial only: exactly n - 1 branches and every bus reached from the source.
    /// The solvers would also report this (NonRadial), but it is an input error, so it is refused at load.</summary>
    private static void CheckRadialTree(Ctx ctx, List<PhaseBus> buses, List<PhaseBranch> branches)
    {
        int n = buses.Count;
        if (branches.Count != n - 1)
            throw ctx.Fail("$", $"the feeder must be one radial tree: {n} buses need exactly {n - 1} branches (lines + transformers + closed switches + regulators), found {branches.Count}.");
        var adj = new Dictionary<int, List<int>>();
        foreach (PhaseBus b in buses) adj[b.Id] = new List<int>();
        foreach (PhaseBranch br in branches) { adj[br.From].Add(br.To); adj[br.To].Add(br.From); }
        int source = buses.First(b => b.IsSource).Id;
        var seen = new HashSet<int> { source };
        var queue = new Queue<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
            foreach (int o in adj[queue.Dequeue()])
                if (seen.Add(o)) queue.Enqueue(o);
        if (seen.Count != n)
        {
            var unreached = buses.Select(b => b.Id).Where(id => !seen.Contains(id)).OrderBy(id => id).ToList();
            throw ctx.Fail("$", $"the feeder must be one radial tree from the source: bus(es) {string.Join(", ", unreached)} " +
                                $"are not reached from source bus {source} (with n - 1 branches this means there is a loop elsewhere).");
        }
    }

    private static string Fmt(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>GR-580: the relative asymmetry a line code's series r or x matrix may have (offline rounding).</summary>
    private const double LineCodeSymmetryTolerance = 1e-9;

    /// <summary>GR-580: refuses a series matrix whose entry differs from its transpose by more than
    /// <see cref="LineCodeSymmetryTolerance"/> of the larger magnitude (two exact zeros, or two equal values, always pass).
    /// The entries are finite here (Matrix3 reads finite numbers only), so the comparison is defined.</summary>
    private static void RequireNearSymmetric(Ctx ctx, double[,] m, string path, string what)
    {
        for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 3; j++)
        {
            double a = m[i, j], b = m[j, i];
            if (a == b) continue;
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));
            if (!(Math.Abs(a - b) <= LineCodeSymmetryTolerance * scale))
                throw ctx.Fail($"{path}[{i}][{j}]",
                    $"the {what} matrix must be symmetric (entry [{i}][{j}] = {Fmt(a)}, [{j}][{i}] = {Fmt(b)}; a relative difference above {Fmt(LineCodeSymmetryTolerance)} is a transposed or mistyped entry).");
        }
    }

    /// <summary>Per-file reading context: every accessor checks the type and range and fails with file + path.</summary>
    private sealed class Ctx(string file)
    {
        public InvalidDataException Fail(string path, string message) => new($"{file}: {path}: {message}");

        public void RequireObject(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Fail(path, $"expected a JSON object but found {e.ValueKind}.");
        }

        /// <summary>Refuse any member not in <paramref name="allowed"/> (case-sensitive): a misspelt key must not
        /// be silently ignored with a default taking its place.</summary>
        public void AllowOnly(JsonElement obj, string path, params string[] allowed)
        {
            foreach (JsonProperty prop in obj.EnumerateObject())
                if (Array.IndexOf(allowed, prop.Name) < 0)
                    throw Fail($"{path}.{prop.Name}", $"unknown member (allowed here: {string.Join(", ", allowed)}).");
        }

        private JsonElement Required(JsonElement obj, string path, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement v)) throw Fail($"{path}.{name}", "required member is missing.");
            return v;
        }

        public string Str(JsonElement obj, string path, string name)
        {
            JsonElement v = Required(obj, path, name);
            if (v.ValueKind != JsonValueKind.String) throw Fail($"{path}.{name}", $"expected a string but found {v.ValueKind}.");
            return v.GetString()!;
        }

        public double Number(JsonElement obj, string path, string name) => AsNumber(Required(obj, path, name), $"{path}.{name}");

        public int Int(JsonElement obj, string path, string name)
        {
            JsonElement v = Required(obj, path, name);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i))
                throw Fail($"{path}.{name}", $"expected a 32-bit integer but found {Describe(v)}.");
            return i;
        }

        public bool Bool(JsonElement obj, string path, string name)
        {
            JsonElement v = Required(obj, path, name);
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Fail($"{path}.{name}", $"expected true or false but found {Describe(v)}."),
            };
        }

        public int[] IntVector3(JsonElement obj, string path, string name)
        {
            JsonElement v = Arr(obj, path, name);
            if (v.GetArrayLength() != 3) throw Fail($"{path}.{name}", $"expected exactly 3 integers (phases a, b, c) but found {v.GetArrayLength()}.");
            var r = new int[3];
            for (int i = 0; i < 3; i++)
            {
                JsonElement e = v[i];
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out r[i]))
                    throw Fail($"{path}.{name}[{i}]", $"expected a 32-bit integer but found {Describe(e)}.");
            }
            return r;
        }

        public JsonElement Arr(JsonElement obj, string path, string name)
        {
            JsonElement v = Required(obj, path, name);
            if (v.ValueKind != JsonValueKind.Array) throw Fail($"{path}.{name}", $"expected an array but found {v.ValueKind}.");
            return v;
        }

        public double[] Vector3(JsonElement obj, string path, string name)
        {
            JsonElement v = Arr(obj, path, name);
            if (v.GetArrayLength() != 3) throw Fail($"{path}.{name}", $"expected exactly 3 numbers (phases a, b, c) but found {v.GetArrayLength()}.");
            var r = new double[3];
            for (int i = 0; i < 3; i++) r[i] = AsNumber(v[i], $"{path}.{name}[{i}]");
            return r;
        }

        public double[,] Matrix3(JsonElement obj, string path, string name)
        {
            JsonElement v = Arr(obj, path, name);
            if (v.GetArrayLength() != 3) throw Fail($"{path}.{name}", $"expected a 3x3 matrix (3 rows) but found {v.GetArrayLength()} rows.");
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                JsonElement row = v[i];
                string rp = $"{path}.{name}[{i}]";
                if (row.ValueKind != JsonValueKind.Array) throw Fail(rp, $"expected a row array but found {row.ValueKind}.");
                if (row.GetArrayLength() != 3) throw Fail(rp, $"expected 3 columns but found {row.GetArrayLength()}.");
                for (int j = 0; j < 3; j++) m[i, j] = AsNumber(row[j], $"{rp}[{j}]");
            }
            return m;
        }

        /// <summary>A JSON number that is finite as a double. JSON has no NaN/Infinity literal, but a huge literal
        /// such as 1e999 would overflow to infinity, so finiteness is checked explicitly.</summary>
        private double AsNumber(JsonElement v, string path)
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d))
                throw Fail(path, $"expected a number but found {Describe(v)}.");
            if (!double.IsFinite(d)) throw Fail(path, $"the number {v.GetRawText()} is not finite as a double.");
            return d;
        }

        private static string Describe(JsonElement v) =>
            v.ValueKind is JsonValueKind.Number or JsonValueKind.String ? $"{v.ValueKind} {v.GetRawText()}" : v.ValueKind.ToString();
    }
}
