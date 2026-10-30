using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GridSim.Core.Estimation;
using GridSim.Core.Harmonics;
using GridSim.Core.Interpose;

namespace GridSim.Core.IO.Json;

/// <summary>
/// UI programme Phase 0.1 (source-generated JSON): hand-written JSON metadata for the four types whose init-only
/// properties have initialisers that a file may leave out: <see cref="MeasurementSet"/>, <see cref="HarmonicSourceSet"/>,
/// <see cref="RtuPointMap"/> and <see cref="RtuPointBinding"/>.
/// <para><b>Why it exists.</b> The System.Text.Json source generator cannot call an init accessor after construction, so
/// it builds such a type in one object initializer that sets EVERY init-only property, and a member absent from the file
/// is given default(T) there, overwriting the initialiser. A measurement set with no <c>baseMva</c> then loaded as
/// 0 MVA instead of 100 MVA, an RTU binding with no <c>sigma</c> as 0 instead of 0.01, and so on (found by
/// JsonSourceGenEquivalenceTests on docs/_captures/interpose/bad-datatype.json). It also changes the wording of some
/// errors, because a member then fails as a constructor argument rather than as a property.</para>
/// <para><b>What this does.</b> Exactly what the reflection serializer (up to 2.0.48) did: make the object with its
/// parameterless constructor, so every initialiser runs, then call the setter of each member the file holds, in file
/// order, and no other. The setters are the properties' own init accessors, reached through
/// <see cref="UnsafeAccessorAttribute"/> (resolved at compile time, supported by NativeAOT and trimming, no reflection);
/// the reflection serializer called the same accessors. So an absent member keeps its initialiser, a member written as
/// null is null, a wrong value fails with the property-path message, and a present member is the value read. Every
/// property's JSON name, order, getter and type are as the generator describes them (CLR name, the options' naming
/// policy applied), so the written bytes are unchanged too. Every other type still comes from the call site's generated
/// context, which this resolver is chained in front of.</para>
/// <para>JsonSourceGenEquivalenceTests pins all of this against the reflection path: the fixtures, a member-by-member
/// absent and null test for each of these four types, and a scan that fails if any other loaded type gains an
/// init-only property with an initialiser (it would then need describing here).</para>
/// </summary>
internal sealed class InitMemberDefaults : IJsonTypeInfoResolver
{
    /// <summary>The one instance (stateless).</summary>
    internal static readonly InitMemberDefaults Resolver = new();

    /// <summary>The types this resolver describes; every other type is left to the next resolver in the chain.</summary>
    internal static readonly IReadOnlyList<Type> Types =
        new[] { typeof(MeasurementSet), typeof(HarmonicSourceSet), typeof(RtuPointMap), typeof(RtuPointBinding) };

    private InitMemberDefaults()
    {
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        if (type == typeof(MeasurementSet)) return MeasurementSetInfo(options);
        if (type == typeof(HarmonicSourceSet)) return HarmonicSourceSetInfo(options);
        if (type == typeof(RtuPointMap)) return RtuPointMapInfo(options);
        if (type == typeof(RtuPointBinding)) return RtuPointBindingInfo(options);
        return null;
    }

    // ---- The four types: every public property in declaration order (the generator's order) ---------------------------

    private static JsonTypeInfo<MeasurementSet> MeasurementSetInfo(JsonSerializerOptions o) => Object(o, static () => new MeasurementSet(),
        Member<MeasurementSet, string>("Schema", static x => x.Schema, Ms.Schema, nonNullableReference: true),
        Member<MeasurementSet, string>("Case", static x => x.Case, Ms.Case, nonNullableReference: true),
        Member<MeasurementSet, double>("BaseMva", static x => x.BaseMva, Ms.BaseMva, nonNullableReference: false),
        Member<MeasurementSet, DateTime?>("TakenUtc", static x => x.TakenUtc, Ms.TakenUtc, nonNullableReference: false),
        Member<MeasurementSet, string?>("Note", static x => x.Note, Ms.Note, nonNullableReference: false),
        Member<MeasurementSet, IReadOnlyList<Measurement>>("Measurements", static x => x.Measurements, Ms.Measurements, nonNullableReference: true));

    private static JsonTypeInfo<HarmonicSourceSet> HarmonicSourceSetInfo(JsonSerializerOptions o) => Object(o, static () => new HarmonicSourceSet(),
        Member<HarmonicSourceSet, string>("Schema", static x => x.Schema, Hs.Schema, nonNullableReference: true),
        Member<HarmonicSourceSet, string>("Case", static x => x.Case, Hs.Case, nonNullableReference: true),
        Member<HarmonicSourceSet, double>("BaseMva", static x => x.BaseMva, Hs.BaseMva, nonNullableReference: false),
        Member<HarmonicSourceSet, DateTime?>("TakenUtc", static x => x.TakenUtc, Hs.TakenUtc, nonNullableReference: false),
        Member<HarmonicSourceSet, string?>("Note", static x => x.Note, Hs.Note, nonNullableReference: false),
        Member<HarmonicSourceSet, IReadOnlyList<HarmonicSource>>("Sources", static x => x.Sources, Hs.Sources, nonNullableReference: true));

    private static JsonTypeInfo<RtuPointMap> RtuPointMapInfo(JsonSerializerOptions o) => Object(o, static () => new RtuPointMap(),
        Member<RtuPointMap, string>("Schema", static x => x.Schema, Rm.Schema, nonNullableReference: true),
        Member<RtuPointMap, string>("Case", static x => x.Case, Rm.Case, nonNullableReference: true),
        Member<RtuPointMap, double>("BaseMva", static x => x.BaseMva, Rm.BaseMva, nonNullableReference: false),
        Member<RtuPointMap, IReadOnlyList<RtuPointBinding>>("Bindings", static x => x.Bindings, Rm.Bindings, nonNullableReference: true));

    private static JsonTypeInfo<RtuPointBinding> RtuPointBindingInfo(JsonSerializerOptions o) => Object(o, static () => new RtuPointBinding(),
        Member<RtuPointBinding, string>("Address", static x => x.Address, Rb.Address, nonNullableReference: true),
        Member<RtuPointBinding, MeasurementKind>("Kind", static x => x.Kind, Rb.Kind, nonNullableReference: false),
        Member<RtuPointBinding, int>("Bus", static x => x.Bus, Rb.Bus, nonNullableReference: false),
        Member<RtuPointBinding, int>("From", static x => x.From, Rb.From, nonNullableReference: false),
        Member<RtuPointBinding, int>("To", static x => x.To, Rb.To, nonNullableReference: false),
        Member<RtuPointBinding, string?>("Circuit", static x => x.Circuit, Rb.Circuit, nonNullableReference: false),
        Member<RtuPointBinding, BranchEnd>("End", static x => x.End, Rb.End, nonNullableReference: false),
        Member<RtuPointBinding, double>("BaseKv", static x => x.BaseKv, Rb.BaseKv, nonNullableReference: false),
        Member<RtuPointBinding, double>("BaseAmps", static x => x.BaseAmps, Rb.BaseAmps, nonNullableReference: false),
        Member<RtuPointBinding, double>("Scale", static x => x.Scale, Rb.Scale, nonNullableReference: false),
        Member<RtuPointBinding, double>("Sigma", static x => x.Sigma, Rb.Sigma, nonNullableReference: false),
        Member<RtuPointBinding, double>("DatumAngleDeg", static x => x.DatumAngleDeg, Rb.DatumAngleDeg, nonNullableReference: false),
        Member<RtuPointBinding, RtuRegisterType>("DataType", static x => x.DataType, Rb.DataType, nonNullableReference: false),
        Member<RtuPointBinding, RtuWordOrder>("WordOrder", static x => x.WordOrder, Rb.WordOrder, nonNullableReference: false));

    // ---- The init accessors, by name (UnsafeAccessor: compile-time bound, AOT- and trim-safe) -------------------------
    // Each extern method is bound by the runtime to the property's own init accessor (set_X). The reflection serializer
    // called these same accessors when it read a file.

    private static class Ms
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Schema")] internal static extern void Schema(MeasurementSet o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Case")] internal static extern void Case(MeasurementSet o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BaseMva")] internal static extern void BaseMva(MeasurementSet o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TakenUtc")] internal static extern void TakenUtc(MeasurementSet o, DateTime? v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Note")] internal static extern void Note(MeasurementSet o, string? v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Measurements")] internal static extern void Measurements(MeasurementSet o, IReadOnlyList<Measurement> v);
    }

    private static class Hs
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Schema")] internal static extern void Schema(HarmonicSourceSet o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Case")] internal static extern void Case(HarmonicSourceSet o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BaseMva")] internal static extern void BaseMva(HarmonicSourceSet o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TakenUtc")] internal static extern void TakenUtc(HarmonicSourceSet o, DateTime? v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Note")] internal static extern void Note(HarmonicSourceSet o, string? v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Sources")] internal static extern void Sources(HarmonicSourceSet o, IReadOnlyList<HarmonicSource> v);
    }

    private static class Rm
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Schema")] internal static extern void Schema(RtuPointMap o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Case")] internal static extern void Case(RtuPointMap o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BaseMva")] internal static extern void BaseMva(RtuPointMap o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Bindings")] internal static extern void Bindings(RtuPointMap o, IReadOnlyList<RtuPointBinding> v);
    }

    private static class Rb
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Address")] internal static extern void Address(RtuPointBinding o, string v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Kind")] internal static extern void Kind(RtuPointBinding o, MeasurementKind v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Bus")] internal static extern void Bus(RtuPointBinding o, int v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_From")] internal static extern void From(RtuPointBinding o, int v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_To")] internal static extern void To(RtuPointBinding o, int v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Circuit")] internal static extern void Circuit(RtuPointBinding o, string? v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_End")] internal static extern void End(RtuPointBinding o, BranchEnd v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BaseKv")] internal static extern void BaseKv(RtuPointBinding o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BaseAmps")] internal static extern void BaseAmps(RtuPointBinding o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Scale")] internal static extern void Scale(RtuPointBinding o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Sigma")] internal static extern void Sigma(RtuPointBinding o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_DatumAngleDeg")] internal static extern void DatumAngleDeg(RtuPointBinding o, double v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_DataType")] internal static extern void DataType(RtuPointBinding o, RtuRegisterType v);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_WordOrder")] internal static extern void WordOrder(RtuPointBinding o, RtuWordOrder v);
    }

    // ---- The two building blocks -------------------------------------------------------------------------------------

    /// <summary>
    /// Describe one init-only property as the generator does (public, non-virtual, the CLR name with no
    /// JsonPropertyName so the options' naming policy applies, no per-property converter, ignore condition or number
    /// handling), except that it has a real setter: its own init accessor.
    /// </summary>
    private static Func<JsonSerializerOptions, JsonPropertyInfo> Member<TOwner, TValue>(
        string name, Func<TOwner, TValue> get, Action<TOwner, TValue> set, bool nonNullableReference)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return options =>
        {
            JsonPropertyInfo p = JsonMetadataServices.CreatePropertyInfo(options, new JsonPropertyInfoValues<TValue>
            {
                IsProperty = true,
                IsPublic = true,
                IsVirtual = false,
                DeclaringType = typeof(TOwner),
                Converter = null,
                Getter = obj => get((TOwner)obj),
                // The value as read (a JSON null arrives as null, exactly as the reflection path passed it on).
                Setter = (obj, value) => set((TOwner)obj, value!),
                IgnoreCondition = null,
                HasJsonInclude = false,
                IsExtensionData = false,
                NumberHandling = null,
                PropertyName = name,
                JsonPropertyName = null,
            });
            // As the generator marks a non-nullable reference type (read only when RespectNullableAnnotations is on,
            // which no GridSim options set).
            if (nonNullableReference)
            {
                p.IsGetNullable = false;
                p.IsSetNullable = false;
            }
            return p;
        };
    }

    /// <summary>The object metadata: made by its parameterless constructor (so every initialiser runs), then each member
    /// present in the file set through its property.</summary>
    private static JsonTypeInfo<T> Object<T>(JsonSerializerOptions options, Func<T> create,
        params Func<JsonSerializerOptions, JsonPropertyInfo>[] members)
        where T : notnull
    {
        var values = new JsonObjectInfoValues<T>
        {
            ObjectCreator = create,
            ObjectWithParameterizedConstructorCreator = null,
            PropertyMetadataInitializer = _ => members.Select(m => m(options)).ToArray(),
            ConstructorParameterMetadataInitializer = null,
            SerializeHandler = null,
        };
        JsonTypeInfo<T> info = JsonMetadataServices.CreateObjectInfo(options, values);
        info.NumberHandling = null;
        info.OriginatingResolver = Resolver;
        return info;
    }
}
