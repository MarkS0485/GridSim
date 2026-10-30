using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Estimation;

namespace GridSim.Core.Interpose;

/// <summary>
/// One binding in a <see cref="RtuPointMap"/>: how a protocol-native point address maps to an estimator
/// measurement. Bus kinds address a bus by <see cref="Bus"/> id; branch kinds by
/// (<see cref="From"/>, <see cref="To"/>, <see cref="Circuit"/>, <see cref="End"/>), exactly as
/// <see cref="Measurement"/> does. The value is converted to engineering units by dividing by
/// <see cref="BaseKv"/> (voltage) or <see cref="BaseAmps"/> (current); power kinds pass through as MW/MVAr.
/// Angle kinds are re-referenced by subtracting <see cref="DatumAngleDeg"/> so an external feed's arbitrary
/// reference aligns with the estimator's slack (see <see cref="MeasurementKind.VaDeg"/>).
/// </summary>
public sealed record RtuPointBinding
{
    public string Address { get; init; } = "";
    public MeasurementKind Kind { get; init; }
    public int Bus { get; init; }
    public int From { get; init; }
    public int To { get; init; }
    public string? Circuit { get; init; }
    public BranchEnd End { get; init; } = BranchEnd.From;

    /// <summary>Voltage base (kV) for a <see cref="MeasurementKind.VmPu"/> point whose value arrives in kV;
    /// leave at 1.0 when the RTU already reports per-unit.</summary>
    public double BaseKv { get; init; } = 1.0;

    /// <summary>Current base (A) for an <see cref="MeasurementKind.IMagPu"/> point reported in amps;
    /// leave at 1.0 when the RTU already reports per-unit.</summary>
    public double BaseAmps { get; init; } = 1.0;

    /// <summary>A raw-to-engineering scale applied to the decoded value before any per-unit base division.
    /// Registers on protocols with no native units (Modbus) are commonly stored as value×10 or ×100; set
    /// this to the reciprocal (e.g. 0.01) to recover engineering units. Defaults to 1.0 (no scaling).</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Measurement standard deviation (the estimator weight) for a good-quality reading.</summary>
    public double Sigma { get; init; } = 0.01;

    /// <summary>Datum angle (deg) subtracted from angle kinds to re-reference the feed onto the slack.</summary>
    public double DatumAngleDeg { get; init; }

    /// <summary>
    /// How the raw Modbus register(s) encode the value (R272). <see cref="RtuRegisterType.AsDecoded"/> (the
    /// default) uses the decoded value as it stands - right for every protocol that carries typed values
    /// (IEC-104 floats, scaled values) and for an unsigned 16-bit Modbus register. A Modbus register holding a
    /// signed quantity (a reverse power flow) must say <see cref="RtuRegisterType.S16"/>, or -120 MW arrives as
    /// +65,416 MW. The 32-bit types read this register and the next one from the same response, combined in
    /// <see cref="WordOrder"/>. The non-default types apply only to Modbus (<c>MB:</c>) addresses.
    /// </summary>
    public RtuRegisterType DataType { get; init; } = RtuRegisterType.AsDecoded;

    /// <summary>For a 32-bit <see cref="DataType"/>: which register holds the high 16 bits.</summary>
    public RtuWordOrder WordOrder { get; init; } = RtuWordOrder.HighWordFirst;
}

/// <summary>How a binding's raw register value(s) are interpreted (see <see cref="RtuPointBinding.DataType"/>).</summary>
public enum RtuRegisterType
{
    /// <summary>Use the decoded value unchanged (default; an unsigned 16-bit register for Modbus).</summary>
    AsDecoded,
    /// <summary>Unsigned 16-bit integer, 0..65535 (checked).</summary>
    U16,
    /// <summary>Signed 16-bit two's-complement integer, -32768..32767.</summary>
    S16,
    /// <summary>Unsigned 32-bit integer over this register and the next.</summary>
    U32,
    /// <summary>Signed 32-bit two's-complement integer over this register and the next.</summary>
    S32,
    /// <summary>IEEE 754 single-precision float over this register and the next.</summary>
    F32,
}

/// <summary>The 16-bit word order of a 32-bit value spread over two consecutive registers.</summary>
public enum RtuWordOrder
{
    /// <summary>The bound (lower-numbered) register holds the high word (Modbus "big-endian" convention).</summary>
    HighWordFirst,
    /// <summary>The bound register holds the low word ("word-swapped").</summary>
    LowWordFirst,
}

/// <summary>
/// The user-authored crosswalk from a protocol's point addresses (IOA / DNP3 index / Modbus register /
/// GOOSE member) to estimator measurements. Wire format <c>gridsim-rtu-pointmap/1</c> (camelCase JSON,
/// enums by name) - the same serializer contract as <see cref="MeasurementSet"/>. Addresses have no
/// intrinsic bus meaning, so this map is authored per RTU; a skeleton can be pre-filled from the GDA
/// <c>asset_crosswalk.json</c> (bus ids) with the native addresses added by hand.
/// </summary>
public sealed record RtuPointMap
{
    public const string SchemaId = "gridsim-rtu-pointmap/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now resolved through the source-generated RtuPointMapJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // The metadata comes from the source-generated RtuPointMapJsonContext (below), with GridSim.Core.IO.Json.InitMemberDefaults
    // chained in front of it: that resolver describes RtuPointMap (and its nested init-only types) so a member the file
    // leaves out keeps its initialiser, as it did under reflection (see InitMemberDefaults for why the generated
    // metadata alone would not). Internal, not private, so JsonSourceGenEquivalenceTests uses this very instance.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
            GridSim.Core.IO.Json.InitMemberDefaults.Resolver, RtuPointMapJsonContext.Default),
    };

    /// <summary>Phase 0.1: the metadata every read and write of this type goes through (cached by the options).</summary>
    internal static System.Text.Json.Serialization.Metadata.JsonTypeInfo<RtuPointMap> JsonInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RtuPointMap>)JsonOptions.GetTypeInfo(typeof(RtuPointMap));

    public string Schema { get; init; } = SchemaId;

    /// <summary>The network case these bindings are authored against (informational).</summary>
    public string Case { get; init; } = "";

    /// <summary>System base the bindings assume, MVA.</summary>
    public double BaseMva { get; init; } = 100.0;

    public IReadOnlyList<RtuPointBinding> Bindings { get; init; } = Array.Empty<RtuPointBinding>();

    private Dictionary<string, RtuPointBinding>? _byAddress;

    /// <summary>Resolve a native address to its binding; false if the map has no entry for it (the point
    /// is decoded but not wired to the estimator - a common, benign case).</summary>
    public bool TryResolve(string address, out RtuPointBinding binding)
    {
        _byAddress ??= BuildIndex();
        return _byAddress.TryGetValue(address, out binding!);
    }

    private Dictionary<string, RtuPointBinding> BuildIndex()
    {
        var d = new Dictionary<string, RtuPointBinding>(Bindings.Count, StringComparer.Ordinal);
        // A map read by Load has unique addresses (ValidateUniqueAddresses, N080). A map built in code is indexed as it
        // always was: the last binding of a repeated address wins.
        foreach (RtuPointBinding b in Bindings) d[b.Address] = b;
        return d;
    }

    /// <summary>Load and schema-check a point-map from JSON.</summary>
    public static RtuPointMap Load(string path)
    {
        string json = File.ReadAllText(path);
        // D-DOC-070: the file must declare its schema; a file with no "schema" member used to load as a point map because
        // the Schema property defaults to SchemaId.
        GridSim.Core.IO.SchemaMember.Require(json, path, SchemaId, "RTU point map");
        RtuPointMap m = JsonSerializer.Deserialize(json, JsonInfo)
            ?? throw new InvalidDataException($"Not an RTU point-map: {path}");
        if (!string.Equals(m.Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException($"'{path}' declares schema '{m.Schema}'; this build reads '{SchemaId}'.");
        foreach (RtuPointBinding b in m.Bindings) ValidateDataType(b);   // fail at load, not mid-stream
        // The numbers each value is converted with: a baseKv of 0 turned every voltage into +Inf, and a zero, negative
        // or NaN sigma gave the estimator an infinite or NaN weight.
        foreach (RtuPointBinding b in m.Bindings)
        {
            if (!(double.IsFinite(b.Sigma) && b.Sigma > 0))
                throw new InvalidDataException($"'{path}': binding '{b.Address}' has sigma {b.Sigma}; it must be a finite number above 0.");
            if (!(double.IsFinite(b.BaseKv) && b.BaseKv > 0))
                throw new InvalidDataException($"'{path}': binding '{b.Address}' has baseKv {b.BaseKv}; it must be a finite number above 0.");
            if (!(double.IsFinite(b.BaseAmps) && b.BaseAmps > 0))
                throw new InvalidDataException($"'{path}': binding '{b.Address}' has baseAmps {b.BaseAmps}; it must be a finite number above 0.");
            if (!(double.IsFinite(b.Scale) && b.Scale != 0))
                throw new InvalidDataException($"'{path}': binding '{b.Address}' has scale {b.Scale}; it must be a finite number other than 0.");
        }
        ValidateUniqueAddresses(m.Bindings, path);
        ValidateRegisterLayout(m.Bindings);
        return m;
    }

    /// <summary>
    /// N080 (October 2026): each native address may be bound once. Two bindings for one address used to load silently,
    /// and the index kept the LAST of them, so the point fed the estimator as whichever measurement happened to be
    /// written second and the other binding was dropped without a word. Throws <see cref="InvalidDataException"/> naming
    /// the file and every repeated address (up to twenty) with how many bindings share it. Addresses are compared
    /// exactly (ordinal), as the index looks them up.
    /// </summary>
    public static void ValidateUniqueAddresses(IReadOnlyList<RtuPointBinding> bindings, string source)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RtuPointBinding b in bindings) seen[b.Address] = seen.GetValueOrDefault(b.Address) + 1;
        List<string> dup = seen.Where(kv => kv.Value > 1).Select(kv => $"'{kv.Key}' ({kv.Value} bindings)").ToList();
        if (dup.Count > 0)
            throw new InvalidDataException(
                $"'{source}': {dup.Count} address(es) bound more than once (each native address may be bound once): " +
                $"{string.Join(", ", dup.Take(20))}{(dup.Count > 20 ? ", ..." : "")}.");
    }

    /// <summary>The highest Modbus register number (registers are addressed 0..65535).</summary>
    private const int MaxModbusRegister = ushort.MaxValue;

    /// <summary>
    /// Check, at load, the parts of a 32-bit binding's layout that can be known without a live response: a 32-bit
    /// value occupies its own register AND the next one, so (1) it cannot sit on the last register (there is no
    /// next one), and (2) the register it borrows cannot also be the first register of another binding (two values
    /// would claim the same 16 bits, and one of them is certainly wrong). Throws <see cref="InvalidDataException"/>
    /// naming the offending bindings. Whether a live response actually CONTAINS the second register can only be
    /// known per window; that is checked by the mapper, and the interposer counts and logs a failing window.
    /// </summary>
    public static void ValidateRegisterLayout(IReadOnlyList<RtuPointBinding> bindings)
    {
        // Every bound register, keyed by address, so a borrowed second register can be looked up.
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (RtuPointBinding b in bindings) bound.Add(b.Address);

        foreach (RtuPointBinding b in bindings)
        {
            if (b.DataType is not (RtuRegisterType.U32 or RtuRegisterType.S32 or RtuRegisterType.F32)) continue;
            // ValidateDataType has already proved a non-default type sits on a Modbus register address.
            if (!TryParseModbusRegister(b.Address, out string prefix, out int register))
                throw new InvalidDataException($"Binding '{b.Address}': {b.DataType} needs a Modbus register address.");
            if (register >= MaxModbusRegister)
                throw new InvalidDataException(
                    $"Binding '{b.Address}': {b.DataType} spans two registers but {register} is the last Modbus register.");
            string second = prefix + (register + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (bound.Contains(second))
                throw new InvalidDataException(
                    $"Binding '{b.Address}' is {b.DataType} and so also occupies register '{second}', which is bound " +
                    "separately. Two bindings cannot share a register; remove one or change the data type.");
        }
    }

    /// <summary>
    /// Check a binding's <see cref="RtuPointBinding.DataType"/> is usable: a non-default type needs a Modbus
    /// address (<c>MB:U&lt;unit&gt;:&lt;fc&gt;:&lt;index&gt;</c> or <c>...:@&lt;register&gt;</c>), because only Modbus
    /// carries untyped raw registers. Throws <see cref="InvalidDataException"/> otherwise.
    /// </summary>
    public static void ValidateDataType(RtuPointBinding b)
    {
        if (b.DataType == RtuRegisterType.AsDecoded) return;
        if (!TryParseModbusRegister(b.Address, out _, out _))
            throw new InvalidDataException(
                $"Binding '{b.Address}': dataType {b.DataType} applies only to a Modbus register address " +
                "(MB:U<unit>:<fc>:<index> or MB:U<unit>:<fc>:@<register>).");
    }

    /// <summary>Split a Modbus address into its prefix (everything up to and including the last ':' and any
    /// '@') and its register number. False for any other address form.</summary>
    internal static bool TryParseModbusRegister(string address, out string prefix, out int register)
    {
        prefix = "";
        register = 0;
        if (!address.StartsWith("MB:", StringComparison.Ordinal)) return false;
        int colon = address.LastIndexOf(':');
        if (colon < 0) return false;
        int start = colon + 1;
        if (start < address.Length && address[start] == '@') start++;
        if (!int.TryParse(address.AsSpan(start), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out register))
            return false;
        prefix = address[..start];
        return true;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonInfo));
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for an RTU point map and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RtuPointMap))]
internal sealed partial class RtuPointMapJsonContext : JsonSerializerContext;
