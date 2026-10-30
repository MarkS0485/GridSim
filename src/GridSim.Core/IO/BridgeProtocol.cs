using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.IO;

/// <summary>
/// The wire schema for the GDA<->GridSim WebSocket bridge. A single socket carries three message kinds,
/// each tagged by a <c>type</c> discriminator:
/// <list type="bullet">
/// <item><c>topology</c> - sent once at the start of a stream: <c>{type,model:{...}}</c>, where <c>model</c>
/// is exactly the <c>model</c> block of a <c>gridsim-solve-export/1</c> document (so it reuses
/// <see cref="SolveImport"/>'s bus/generator/branch shapes). Cached by the client and reused for every frame.</item>
/// <item><c>state</c> - one per tick, a <b>slim</b> solved snapshot: <c>{type,t,result:{buses,lineFlows},
/// scalars:{...},overlays:{...}}</c>. The <c>result</c> block reuses the solve-export <c>result</c> shape;
/// <see cref="StateMessage.ToPowerFlowResult"/> rebuilds a <see cref="PowerFlowResult"/> from it against the
/// cached topology, and <see cref="StateMessage.ToSimFrame"/> assembles the <see cref="SimFrame"/> the views bind to.</item>
/// <item><c>result</c> - the response to an on-demand <c>query</c>: <c>{type,op,at,payload:{solveExport,verdict,
/// bindingConstraint,violations}}</c>. <c>payload.solveExport</c> is a full <c>gridsim-solve-export/1</c> document
/// that goes straight through <see cref="SolveImport.Read(string)"/>.</item>
/// </list>
/// Deserialisation mirrors <see cref="SolveImport"/>: case-insensitive (PascalCase and camelCase both bind) and
/// tolerant of named float literals (Infinity limits).
/// </summary>
public static class BridgeProtocol
{
    /// <summary>An interconnector whose |flow| is at or below this (MW) is treated as idle and left out of the
    /// interconnector rows (display cut, R177).</summary>
    public const double MinDisplayFlowMw = 0.001;

    /// <summary>The text that stands in for a removed secret in <see cref="DisplayEndpoint"/>.</summary>
    public const string RedactedText = "<redacted>";

    /// <summary>
    /// The bridge endpoint as it may be written to the event log or the status bar (2026-10-20 follow-up: the connect
    /// line printed the endpoint verbatim, so a URL carrying a token in its query string, a password in its user-info
    /// or a secret in its fragment would have reached the log). The scheme, host, port and path are kept, so an
    /// operator still sees which server was used; user-info, query and fragment are each replaced by
    /// <see cref="RedactedText"/> when present. An endpoint with none of them is returned exactly as given, so the
    /// ordinary log line is unchanged byte for byte. A string that is not an absolute URI cannot be shown safely and
    /// reads as <see cref="RedactedText"/> (the connect refuses such an address anyway).
    /// </summary>
    public static string DisplayEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)) return RedactedText;
        bool hasUser = uri.UserInfo.Length > 0;
        bool hasQuery = uri.Query.Length > 0;
        bool hasFragment = uri.Fragment.Length > 0;
        if (!hasUser && !hasQuery && !hasFragment) return endpoint;   // nothing secret: exactly as given
        // Uri.Authority is host and port only (no user-info), with IPv6 brackets and the default port left out.
        return uri.Scheme + "://"
            + (hasUser ? RedactedText + "@" : "")
            + uri.Authority
            + uri.AbsolutePath
            + (hasQuery ? "?" + RedactedText : "")
            + (hasFragment ? "#" + RedactedText : "");
    }

    /// <summary>Shared options for both reading bridge messages and serialising client requests. Mirrors <see cref="SolveImport"/>.</summary>
    /// <remarks>Phase 0.1: still a plain (reflection-capable) options object, unchanged, so an outside caller that
    /// serialises its own types with it keeps working. GridSim's own reads and writes go through
    /// <see cref="Json"/>, a source-generated context built from a second instance of exactly the same settings
    /// (<see cref="NewJsonOptions"/>), so they need no runtime reflection (NativeAOT).</remarks>
    public static readonly JsonSerializerOptions JsonOptions = NewJsonOptions();

    /// <summary>Phase 0.1: the source-generated bridge metadata, over the same settings as <see cref="JsonOptions"/>.</summary>
    internal static readonly BridgeProtocolJsonContext Json = new(NewJsonOptions());

    // The one definition of the bridge settings (both instances above are built here, so they cannot drift apart).
    private static JsonSerializerOptions NewJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Peek the <c>type</c> discriminator of a raw bridge message without fully deserialising it. A truncated or
    /// otherwise malformed message returns <c>null</c> rather than throwing; the client then rejects that one
    /// message through <c>GridBridgeClient.MessageRejected</c> (reported and counted, not silently skipped) while
    /// the receive pump carries on - one bad frame must not be loop-fatal.
    /// </summary>
    public static string? PeekType(string json)
    {
        // Forward-only scan for the top-level "type" property instead of building a whole JsonDocument DOM (which
        // would be the second parse on top of the typed Deserialize that follows). Skip() jumps over nested
        // object/array values so only root-level keys are inspected. Semantics match the old JsonDocument.Parse
        // (default reader options, no comments/trailing commas - the bridge emits clean JSON). (fix #11)
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            // GR-632: the whole top level is scanned, and a message with more than one "type" key is refused (null, then
            // reported as a bad message): this scan took the first while the typed deserialiser binds the last, so the
            // two could disagree about what the message is.
            string? found = null;
            int typeKeys = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // Case-INSENSITIVE like the deserializers (PropertyNameCaseInsensitive=true): a PascalCase
                // 'Type' discriminator must peek the same as camelCase 'type', or Dispatch silently drops it.
                bool isType = string.Equals(reader.GetString(), "type", StringComparison.OrdinalIgnoreCase);
                reader.Read();   // advance onto the property value
                if (isType)
                {
                    typeKeys++;
                    found = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                reader.Skip();   // step over this value, including any nested object/array (a no-op on a scalar)
            }
            return typeKeys == 1 ? found : null;
        }
        catch (JsonException)
        {
            // Truncated / malformed JSON: skip this message instead of faulting the whole client.
            return null;
        }
    }

    public static TopologyMessage ReadTopology(string json) =>
        JsonSerializer.Deserialize(json, Json.TopologyMessage)
        ?? throw new InvalidDataException("Empty or invalid bridge topology message.");

    public static StateMessage ReadState(string json) =>
        JsonSerializer.Deserialize(json, Json.StateMessage)
        ?? throw new InvalidDataException("Empty or invalid bridge state message.");

    public static ResultMessage ReadResult(string json) =>
        JsonSerializer.Deserialize(json, Json.ResultMessage)
        ?? throw new InvalidDataException("Empty or invalid bridge result message.");

    /// <summary>Read the human-readable <c>message</c> of an <c>info</c>/<c>error</c> envelope, or <c>null</c>
    /// if absent/malformed. Used to surface a server stream-level error to the client. (GS-H2)</summary>
    public static string? ReadMessage(string json)
    {
        try { return JsonSerializer.Deserialize(json, Json.NoteMessage)?.Message; }
        catch (JsonException) { return null; }
    }

    /// <summary><c>{type, message}</c> - an <c>info</c> or <c>error</c> envelope carrying a human-readable note.</summary>
    public sealed class NoteMessage
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Rebuild a <see cref="GridModel"/> from a <c>topology</c> message's model block. This is also the model
    /// reader <see cref="SolveImport"/> uses, so a topology and a full solve-export reconstruct exactly the same
    /// network (bus type parse, 1.0 tap default, sensible defaults for fields an older writer did not carry).
    ///
    /// <para>R031: the branch state (<c>inService</c>), the zero-sequence and winding data, the bus ZIP load
    /// model, the generator <c>pmin</c> and the OLTCs are all read. They used to be dropped, so an exported
    /// post-outage state re-imported with its opened branches back in service while its result still showed
    /// zero flow on them, and a ZIP-load case re-imported as constant power. Every new field is optional with the
    /// model's own default, so a document from an older writer still reads exactly as before.</para>
    /// </summary>
    /// <param name="m">The model block.</param>
    /// <param name="defaultName">The model name to use when the block carries none.</param>
    public static GridModel BuildModel(ModelBlock m, string defaultName = "bridge")
    {
        // D-DOC-067 (October 2026): every field the solve export now writes is read back, so an exported state re-imports
        // as the same network field for field: the bus angle, layout and own voltage limits; the generator's service
        // state, fault, converter, cost and dynamics-class data; the branch's length, equipment, short-term and emergency
        // ratings and dynamics class. Each is optional with the model's own default, so an older document (or the GDA
        // bridge's topology message, which carries none of them) reads exactly as before.
        var buses = (m.Buses ?? new()).Select(b => new Bus(
            b.Id,
            ParseBusType(b),
            Pd: b.Pd, Qd: b.Qd, Gs: b.Gs, Bs: b.Bs, BaseKv: b.BaseKv, Vm: b.Vm, VaDeg: b.VaDeg, Name: b.Name,
            X: b.X, Y: b.Y,
            Zip: b.Zip is ZipDto z ? new ZipLoad(z.Ap, z.Bp, z.Cp, z.Aq, z.Bq, z.Cq) : null,
            VminPu: b.VminPu, VmaxPu: b.VmaxPu)).ToList();

        // D-DOC-084: the bus's own voltage limits (read since D-DOC-067) must be usable when given, by the same rule as a
        // JSON case (F17): finite and above 0 pu, and the lower not above the upper (equal is a fixed-voltage bus). A
        // NaN limit used to pass straight into the per-bus limit checks, where every comparison with it is false.
        // Absent limits (null) are fine: the global band applies, as before.
        var badLimits = new List<string>();
        foreach (Bus b in buses)
        {
            if (b.VminPu is double lo && !(double.IsFinite(lo) && lo > 0)) badLimits.Add($"bus {b.Id} vminPu {lo.ToString("R", CultureInfo.InvariantCulture)}");
            if (b.VmaxPu is double hi && !(double.IsFinite(hi) && hi > 0)) badLimits.Add($"bus {b.Id} vmaxPu {hi.ToString("R", CultureInfo.InvariantCulture)}");
            if (b.VminPu is double l && b.VmaxPu is double h && double.IsFinite(l) && double.IsFinite(h) && l > h)
                badLimits.Add($"bus {b.Id} vminPu {l.ToString("R", CultureInfo.InvariantCulture)} above vmaxPu {h.ToString("R", CultureInfo.InvariantCulture)}");
        }
        if (badLimits.Count > 0)
            throw new InvalidDataException(
                $"The model block has {badLimits.Count} unusable per-bus voltage limit(s) (each must be a finite voltage above 0 pu, " +
                $"and vminPu not above vmaxPu): {string.Join(", ", badLimits.Take(20))}{(badLimits.Count > 20 ? ", ..." : "")}.");

        var gens = (m.Generators ?? new()).Select(g => new Generator(
            g.Bus, g.Pg, Qg: g.Qg, Vg: g.Vg, Qmin: g.Qmin, Qmax: g.Qmax,
            Pmin: g.Pmin, Pmax: g.Pmax, MBase: g.MBase, H: g.H, Name: g.Name, Type: g.Type ?? "",
            DynamicsClass: g.DynamicsClass,
            SubtransientXdPu: g.SubtransientXdPu, NegativeSeqXPu: g.NegativeSeqXPu,
            ConverterInterfaced: g.ConverterInterfaced, ConverterFaultCurrentPu: g.ConverterFaultCurrentPu,
            PowerFlowOnly: g.PowerFlowOnly, UnitTransformerXPu: g.UnitTransformerXPu, GridForming: g.GridForming,
            InService: g.InService,
            Cost: ParseCost(g.Cost, g, "cost"), ReactiveCost: ParseCost(g.ReactiveCost, g, "reactiveCost"))).ToList();

        var branches = (m.Branches ?? new()).Select(e => new Branch(
            e.From, e.To, e.R, e.X, B: e.B, TapRatio: e.Tap == 0 ? 1.0 : e.Tap,
            PhaseShiftDeg: e.PhaseShiftDeg, RateAMva: e.RateAMva, Name: e.Name, IsTransformer: e.IsTransformer,
            DynamicsClass: e.DynamicsClass,
            InService: e.InService,
            LengthKm: e.LengthKm, Equipment: e.Equipment,
            R0: e.R0, X0: e.X0,
            FromWinding: ParseWinding(e.FromWinding, e, "fromWinding"),
            ToWinding: ParseWinding(e.ToWinding, e, "toWinding"),
            FromNeutralXPu: e.FromNeutralXPu, ToNeutralXPu: e.ToNeutralXPu,
            RateBMva: e.RateBMva, RateCMva: e.RateCMva)).ToList();

        // A boundary must be named, and each of its branches counts with direction +1 or -1 (0 zeroed a branch's
        // contribution and any other value scaled it). A boundary with no branches is still left out, as before.
        var boundaries = (m.Boundaries ?? new()).Select(bd => new Boundary(
            string.IsNullOrWhiteSpace(bd.Name) ? throw new InvalidDataException("A boundary in the model block has no name.") : bd.Name,
            bd.LimitMw,
            (bd.Branches ?? new()).Select(bb => new BoundaryBranch(bb.Branch ?? "",
                bb.Direction is 1 or -1
                    ? bb.Direction
                    : throw new InvalidDataException($"Boundary '{bd.Name}': branch '{bb.Branch}' has direction {bb.Direction}; it must be 1 or -1."))).ToList()))
            // Bug hunt 2026-10-29, finding 39: a boundary with an empty branch list is kept, as JsonGridLoader keeps it,
            // GridModel.Validate accepts it and SolveExport writes it. It used to be dropped here, so the documented
            // field-for-field solve-export round trip lost boundaries and model.Boundaries.Count changed on import.
            .ToList();

        var oltcs = (m.Oltcs ?? new()).Select(o => new Oltc(
            o.Transformer ?? throw new InvalidDataException("An OLTC in the model block names no transformer."),
            o.ControlsBus, o.TargetVpu, o.MinTap, o.MaxTap, o.StepPu, o.DeadbandPu)).ToList();

        // R177 / Q13 (October 2026): the nominal frequency now travels in the model block. Absent (every older
        // writer, and the GDA bridge today) means the GB nominal 50 Hz, exactly the GridModel default the reader
        // used before, so an older document reads bit-for-bit as it did. Present, it must be a real, positive
        // frequency: a zero, negative or non-finite value is invalid data and fails loud rather than being
        // replaced by 50 Hz behind the caller's back.
        double frequencyHz = Dynamics.FrequencyLimits.NominalHz;
        if (m.FrequencyHz is double f)
        {
            if (!double.IsFinite(f) || f <= 0)
                throw new InvalidDataException(
                    $"The model block's frequencyHz is {f.ToString("R", CultureInfo.InvariantCulture)}; a nominal frequency must be finite and positive (Hz).");
            frequencyHz = f;
        }

        // N268 (October 2026): the system base must be a real, positive MVA. An absent baseMva still reads as 100 (the
        // ModelBlock default, so an older writer reads exactly as before), but a PRESENT value at or below 0, or a
        // non-finite one, is refused by name. It used to be replaced by 100 without a word, so a document written on a
        // different base re-imported with every per-unit quantity silently rescaled; since D-DOC-083 the same value in
        // a JSON case is refused by GridModel.Validate, and the bridge now agrees. (The NaN test is written as
        // !(x > 0) so that NaN, for which every comparison is false, is refused too.)
        if (!double.IsFinite(m.BaseMva) || !(m.BaseMva > 0))
            throw new InvalidDataException(
                $"The model block's baseMva is {m.BaseMva.ToString("R", CultureInfo.InvariantCulture)}; a system base must be finite and positive (MVA).");

        var model = new GridModel
        {
            FrequencyHz = frequencyHz,
            BaseMva = m.BaseMva,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            Oltcs = oltcs,
            Boundaries = boundaries,
            Name = m.Name ?? defaultName,
        };
        // GR-147: the post-build structural check every other entry loader applies (JSON, MATPOWER, CIM): duplicate bus
        // ids, a branch or generator on a bus that does not exist, a self-loop, a zero-impedance branch or no reference
        // bus are refused here, by name, instead of surfacing later as a KeyNotFound or a NaN deep in a view.
        var errors = model.Validate()
            .Where(d => d.Severity == ValidationSeverity.Error)
            .Select(d => d.Message)
            .ToList();
        if (errors.Count > 0)
            throw new InvalidDataException("The model block is not a valid grid model:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", errors));
        // N138: a boundary branch name in the model block must name exactly one branch, as in a JSON case (N097);
        // it used to be skipped (or the last namesake taken) every frame without a word.
        Boundary.RequireResolvedBranchNames(model);
        return model;
    }

    /// <summary>
    /// D-DOC-084 (October 2026): the bus type of a model-block bus. It arrives as the enum name the writers emit
    /// ("Slack", "Pv", "Pq"; any letter case). ABSENT (no "type" key, or JSON null) still means Pq, exactly as before, so
    /// a document from a writer that leaves the type out reads as it always did. Anything else used to be read as Pq
    /// without a word (Enum.TryParse failing), so a misspelt "Slak" quietly lost the reference bus and a number such as
    /// "0" parsed as whatever enum value it happened to be (or an undefined one). Both are now refused, naming the bus
    /// and the value, as the JSON v1 loader refuses an unknown bus type (N096 / N098).
    /// </summary>
    private static BusType ParseBusType(BusDto b)
    {
        if (b.Type is null) return BusType.Pq;   // absent: the historical default
        string t = b.Type.Trim();
        // Names only: a numeric string ("2") is not a bus type here, whatever enum value it would map to, and neither is
        // a comma list ("Slack,Pv"), which Enum.TryParse would OR together.
        if (t.Length > 0 && !char.IsDigit(t[0]) && t[0] is not ('-' or '+') && !t.Contains(',')
            && Enum.TryParse(t, ignoreCase: true, out BusType parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidDataException(
            $"Bus {b.Id}{(b.Name is { Length: > 0 } nm ? $" ('{nm}')" : "")} in the model block has type '{b.Type}', which is not " +
            $"a bus type (expected one of {string.Join(", ", Enum.GetNames<BusType>())}, any letter case; leave it out for Pq).");
    }

    // A winding connection arrives as its enum name ("Delta", "WyeGrounded", "WyeUngrounded"); absent means
    // a line (null). An unrecognised name is invalid data and fails loud rather than silently becoming a line.
    private static WindingConnection? ParseWinding(string? value, BranchDto e, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Enum.TryParse(value, ignoreCase: true, out WindingConnection w) && Enum.IsDefined(w)) return w;
        throw new InvalidDataException(
            $"Branch {e.Name ?? $"{e.From}->{e.To}"}: {field} '{value}' is not a winding connection " +
            $"(expected one of {string.Join(", ", Enum.GetNames<WindingConnection>())}).");
    }

    // D-DOC-067: a generator cost curve arrives as its model name ("PiecewiseLinear" or "Polynomial") with the start-up
    // and shut-down costs and the coefficient list; absent means no curve (null). An unknown model name, or a curve with
    // no coefficient list, is invalid data and fails loud rather than silently dropping the curve.
    private static GeneratorCost? ParseCost(CostDto? c, GenDto g, string field)
    {
        if (c is null) return null;
        string who = g.Name ?? $"at bus {g.Bus}";
        if (!Enum.TryParse(c.Model, ignoreCase: true, out GeneratorCostModel model) || !Enum.IsDefined(model)
            || int.TryParse(c.Model, out _))
            throw new InvalidDataException(
                $"Generator {who}: {field}.model '{c.Model}' is not a cost model (expected one of {string.Join(", ", Enum.GetNames<GeneratorCostModel>())}).");
        if (c.Coefficients is null)
            throw new InvalidDataException($"Generator {who}: {field} has no coefficients list.");
        // The same shape rules as a cost file (GeneratorCostCurve.From): a piecewise-linear curve is [MW, cost] pairs,
        // at least two points; a polynomial has at least one coefficient; every number is finite.
        double[] coeff = c.Coefficients.ToArray();
        if (Array.Exists(coeff, v => !double.IsFinite(v)) || !double.IsFinite(c.StartupCost) || !double.IsFinite(c.ShutdownCost))
            throw new InvalidDataException($"Generator {who}: {field} holds a number that is not finite.");
        if (model == GeneratorCostModel.PiecewiseLinear && (coeff.Length % 2 != 0 || coeff.Length < 4))
            throw new InvalidDataException($"Generator {who}: {field} is piecewise linear but has {coeff.Length} numbers; it needs [MW, cost] pairs, at least two points.");
        if (model == GeneratorCostModel.Polynomial && coeff.Length < 1)
            throw new InvalidDataException($"Generator {who}: {field} is a polynomial with no coefficient.");
        return new GeneratorCost(model, c.StartupCost, c.ShutdownCost, coeff);
    }

    /// <summary>The wire timestamp forms: an ISO-8601 date and time to the second, optional fractional seconds, and
    /// an optional offset ("Z" or +hh:mm; none means UTC).</summary>
    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    /// <summary>Parse an ISO-8601 wire timestamp to UTC. Empty/missing -> <see cref="DateTime.MinValue"/> (UTC), which
    /// the bridge client refuses as undated state. Only the ISO forms in <see cref="IsoFormats"/> are read: the old
    /// permissive parse also took "01/07/2026 12:00" (read as 7 January) and date-only text; anything else now throws
    /// <see cref="FormatException"/> (as the old parse did for text it could not read).</summary>
    public static DateTime ParseUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        if (!DateTime.TryParseExact(iso, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
            throw new FormatException($"Timestamp '{iso}' is not an ISO-8601 date and time (for example 2024-03-01T12:00:00Z).");
        return utc;
    }

    // ---- Message envelopes ----

    /// <summary><c>{type:"topology", model:{...}}</c> - the network, sent once per stream.</summary>
    public sealed class TopologyMessage
    {
        public string? Type { get; set; }
        public ModelBlock? Model { get; set; }

        /// <summary>Build the <see cref="GridModel"/> this message carries.</summary>
        public GridModel ToModel() =>
            BuildModel(Model ?? throw new InvalidDataException("topology message has no model block."));
    }

    /// <summary><c>{type:"state", t, result:{...}, scalars:{...}, overlays:{...}}</c> - one slim solved tick.</summary>
    public sealed class StateMessage
    {
        public string? Type { get; set; }
        public string? T { get; set; }
        public StateResultBlock? Result { get; set; }
        public ScalarsBlock? Scalars { get; set; }
        public OverlaysBlock? Overlays { get; set; }
        public OperatorBlock? Operator { get; set; }
        public MarketBlock? Market { get; set; }

        /// <summary>Per-tick raw generation MIX (per-fuel MW) streamed by the <c>source=raw</c> replay - the real
        /// published fuel breakdown for that instant, so GENERATION &amp; INERTIA moves off the raw lake instead of
        /// the static topology fleet. Null from solve/presolved sources.</summary>
        public GenerationMixBlock? GenerationMix { get; set; }

        /// <summary>Per-tick raw WEATHER (temperature / 100 m wind / solar) streamed by the <c>source=raw</c>
        /// replay. Null from solve/presolved sources and when no weather record joins the tick.</summary>
        public WeatherBlock? Weather { get; set; }

        /// <summary>Per-tick raw INTERCONNECTOR flows (named links + net, MW) streamed by the <c>source=raw</c>
        /// replay, so the raw GENERATION panel shows real cross-border flows moving with the replay. Null otherwise.</summary>
        public RawInterconnectorsBlock? Interconnectors { get; set; }

        /// <summary>Which reconstruction produced the frame: <c>live</c>, <c>presolved</c> or <c>estimated</c>.
        /// Null from servers that predate the field.</summary>
        public string? Source { get; set; }

        /// <summary>Per-frame estimation quality (<c>source=estimated</c> only) - the honesty layer a
        /// measurement-fitted state carries that a demand-scaled re-solve cannot. Null otherwise.</summary>
        public EstimateBlock? Estimate { get; set; }

        /// <summary>The tick's timestamp, in UTC - the value that must pass the forward-inference horizon.</summary>
        public DateTime Timestamp => ParseUtc(T);

        /// <summary>
        /// Rebuild the full <see cref="PowerFlowResult"/> from the slim state block. Bus voltages/injections come
        /// straight from <c>result.buses</c> (index->id via <see cref="PowerFlowResult.BusIds"/>). Line-flow names and
        /// ratings, if the slim block omits them, are backfilled from the cached <paramref name="cachedTopology"/>'s
        /// matching branch so the rendered flows still carry asset identity and loading context.
        /// </summary>
        public PowerFlowResult ToPowerFlowResult(GridModel cachedTopology)
        {
            StateResultBlock r = Result ?? throw new InvalidDataException("state message has no result block.");
            var rb = r.Buses ?? new();
            var flows = BackfillFlows(r.LineFlows ?? new(), cachedTopology);

            // The converged flag. An explicit "converged": false is honoured (the frame then reads as not converged
            // everywhere it is shown). An ABSENT flag still reads as converged, deliberately, and unlike SolveImport
            // (which refuses it, GR-180): the bridge protocol's slim state block is a state the SERVER vouches for,
            // and the GDA bridge server as shipped never writes the flag into it (it streams only buses and line
            // flows), so refusing or negating an absent flag would blank or mislabel every live frame. The weak
            // point is therefore on the server: any server path that could stream a state that did not converge must
            // write "converged": false into the result block, and this reader will show it as such. Bug hunt
            // 2026-10-29 finding 38; the server-side gap is recorded with the GDA bridge.
            return new PowerFlowResult
            {
                Converged = r.Converged ?? true,
                Iterations = r.Iterations ?? 0,
                MaxMismatch = r.MaxMismatch ?? 0,
                BusIds = rb.Select(x => x.Id).ToArray(),
                Vm = rb.Select(x => x.Vm).ToArray(),
                VaDeg = rb.Select(x => x.VaDeg).ToArray(),
                PInjMw = rb.Select(x => x.PInjMw).ToArray(),
                QInjMvar = rb.Select(x => x.QInjMvar).ToArray(),
                TotalLossMw = r.TotalLossMw ?? 0,
                TotalLossMvar = r.TotalLossMvar ?? 0,
                LineFlows = flows,
            };
        }

        /// <summary>
        /// Assemble the <see cref="SimFrame"/> the live/replay views bind to: the cached topology as the model, the
        /// reconstructed result, and the per-tick scalars (frequency, RoCoF, generation, demand) from <c>scalars</c>,
        /// each falling back to a computed value when the wire omits it. The clock hour is derived from <c>t</c>.
        /// </summary>
        public SimFrame ToSimFrame(GridModel cachedTopology, int tick = 0)
        {
            PowerFlowResult result = ToPowerFlowResult(cachedTopology);
            ScalarsBlock s = Scalars ?? new();
            double hour = Timestamp == DateTime.MinValue ? 0.0 : Timestamp.TimeOfDay.TotalHours;
            double gen = s.GenMw ?? result.PInjMw.Where(p => p > 0).Sum();
            // Demand: when the tick carries no scalar demand, treat it as UNKNOWN (0) rather than substituting the
            // cached topology's static Pd sum. Unlike presolved replay (whose per-frame model carries the correct
            // per-tick Pd), the bridge caches ONE topology-time model, so its Pd is frozen at load-time demand and is
            // ~2x wrong on ticks with no condition record. The bus-injection derivation only holds in presolved mode,
            // so there is no correct source here - 0 is absorbed by the HUD's existing Math.Max guards. (fix #10)
            double demand = s.DemandMw ?? 0.0;
            return new SimFrame(cachedTopology, result, hour, tick,
                // R177: a tick with no frequency scalar shows the topology's nominal frequency rather than a
                // hard-coded 50 Hz. The model block's frequencyHz sets the topology's nominal (BuildModel; 50 Hz
                // when the block omits it), so this is the feed's own nominal frequency.
                s.FreqHz ?? cachedTopology.FrequencyHz, gen, demand, s.RocofHzPerS ?? 0.0);
        }

        /// <summary>
        /// Turn the wire flows into <see cref="LineFlow"/>s, backfilling a missing name or rating from the cached
        /// topology. R266: parallel circuits are matched IN ORDER - the k-th flow on a bus pair takes the k-th
        /// branch on that pair (either orientation, in topology order), which is the order the solver emits flows
        /// in (one per branch). The old lookup took the first branch with matching endpoints for every flow, so
        /// all twins between the same buses got the first twin's name and rating and name-keyed overlays landed
        /// on the wrong circuit. A flow that needs a backfill but has no branch left to take on its pair means
        /// the state does not match the cached topology: that fails loud instead of producing an anonymous row.
        /// </summary>
        private static List<LineFlow> BackfillFlows(List<FlowDto> wire, GridModel topo)
        {
            // Topology branches grouped by unordered bus pair, keeping topology order within each group.
            var byPair = new Dictionary<(int, int), List<Branch>>();
            foreach (Branch b in topo.Branches)
            {
                var key = PairKey(b.From, b.To);
                if (!byPair.TryGetValue(key, out List<Branch>? list)) byPair[key] = list = new List<Branch>();
                list.Add(b);
            }

            var taken = new Dictionary<(int, int), int>();   // how many flows on each pair we have consumed
            var flows = new List<LineFlow>(wire.Count);
            foreach (FlowDto l in wire)
            {
                var key = PairKey(l.From, l.To);
                int k = taken.GetValueOrDefault(key);
                taken[key] = k + 1;   // every flow on the pair advances the cursor, named or not, to stay aligned

                string name = l.Name ?? "";
                double rating = l.RatingMva;
                if (string.IsNullOrEmpty(name) || rating == 0)
                {
                    if (!byPair.TryGetValue(key, out List<Branch>? twins) || k >= twins.Count)
                        throw new InvalidDataException(
                            $"State flow {l.From}->{l.To} (number {k + 1} on that bus pair) carries no name/rating " +
                            $"and the cached topology has only {(twins?.Count ?? 0)} branch(es) between those buses: " +
                            "the state does not match the topology.");
                    Branch br = twins[k];
                    if (string.IsNullOrEmpty(name)) name = br.Name ?? "";
                    if (rating == 0) rating = br.RateAMva;
                }
                flows.Add(new LineFlow(l.From, l.To, l.PFromMw, l.QFromMvar, l.PToMw, l.QToMvar,
                    l.PLossMw, l.QLossMvar, name, rating, l.LoadingPercent));
            }
            return flows;
        }

        private static (int, int) PairKey(int a, int b) => a <= b ? (a, b) : (b, a);
    }

    /// <summary>Per-frame WLS estimation quality riding a <c>source=estimated</c> state message: the chi-square
    /// verdict, objective, redundancy and bad-data removals of the fit that produced the frame. Every
    /// field mirrors a column of the estimated_state lake row (see technical ch. 9, GDA integration,
    /// docs/technical/0009 GDA integration.md, the lake table; and manual ch. 23, Replaying estimated states).</summary>
    public sealed class EstimateBlock
    {
        public double? ObjectiveJ { get; set; }
        public double? ChiThreshold { get; set; }
        public bool ChiPassed { get; set; }

        /// <summary>
        /// N214 (October 2026): the chi-square outcome as three states, the <c>chi_test</c> column of the GDA
        /// estimated_state lake row (GDA fc31d32): <c>"passed"</c>, <c>"failed"</c> or <c>"not-run"</c> (camelCase
        /// <c>chiTest</c> on the wire). Optional and additive: null when the server does not send it (an older bridge),
        /// which reads exactly as before from <see cref="ChiPassed"/> alone. <see cref="ChiPassed"/> cannot say "not run"
        /// (GDA writes it false when the test did not run, so both shells showed such a period as "chi-square FAIL");
        /// with this field they show "chi-square not run". Checked by <see cref="RequireConsistentChiTest"/>.
        /// </summary>
        public string? ChiTest { get; set; }

        /// <summary>The <see cref="ChiTest"/> wire values.</summary>
        public const string ChiTestPassed = "passed", ChiTestFailed = "failed", ChiTestNotRun = "not-run";

        /// <summary>
        /// N214: fail loud on a <see cref="ChiTest"/> the shells cannot show honestly: a value other than the three wire
        /// values (matched exactly), or one that contradicts <see cref="ChiPassed"/> (<c>"passed"</c> needs ChiPassed
        /// true; <c>"failed"</c> and <c>"not-run"</c> need it false, as GDA writes them). Absent is fine. The bridge
        /// client calls this on every state frame before it is published, so a bad frame is rejected (reported and
        /// counted), never shown with a verdict it does not carry.
        /// </summary>
        public void RequireConsistentChiTest()
        {
            if (ChiTest is null) return;
            bool? expected = ChiTest switch
            {
                ChiTestPassed => true,
                ChiTestFailed => false,
                ChiTestNotRun => false,
                _ => null,
            };
            if (expected is null)
                throw new InvalidDataException(
                    $"The estimate block's chiTest is '{ChiTest}'; it must be '{ChiTestPassed}', '{ChiTestFailed}' or '{ChiTestNotRun}'.");
            if (expected.Value != ChiPassed)
                throw new InvalidDataException(
                    $"The estimate block's chiTest '{ChiTest}' contradicts chiPassed {(ChiPassed ? "true" : "false")}.");
        }

        public int? Dof { get; set; }
        public int? MeasurementsN { get; set; }
        public int? RemovedN { get; set; }
        public int? Iterations { get; set; }

        /// <summary>N151 (R175 / N064): true when the fit was a robust estimator whose iteratively re-weighted least
        /// squares (IRLS) loop stopped on its cap before the weights settled, so the state is not a fixed point of the
        /// robust estimator (<c>EstimationResult.IrlsCapReached</c>, the <c>irlsCapReached</c> field of
        /// <c>gridsim-estimate-export/1</c>). Optional and additive on the wire (camelCase <c>irlsCapReached</c>): null
        /// when the server does not send it (every server today: the estimated_state lake has no such column yet), which
        /// reads as "not reported", never as a settled fit being claimed.</summary>
        public bool? IrlsCapReached { get; set; }

        /// <summary>The warning both apps add to the quality text when <see cref="IrlsCapReached"/> is true; the same
        /// meaning as the CLI <c>estimate</c> warning line.</summary>
        public const string IrlsCapWarning = "WARNING: robust re-weighting (IRLS) hit its cap, weights not settled";

        /// <summary>True when the estimate may be shown as good: the chi-square test passed AND the robust re-weighting did
        /// not stop on its cap. Both shells colour the quality pill with this (green when true, red otherwise), so a capped
        /// robust fit is never shown green. With <see cref="IrlsCapReached"/> absent or false it equals
        /// <see cref="ChiPassed"/>, exactly the old colouring.</summary>
        [JsonIgnore]   // derived for display, never a wire field
        public bool QualityOk => ChiPassed && IrlsCapReached != true;

        /// <summary>The quality text both apps show on an estimated frame: <c>chi-square PASS|FAIL  *  J=&lt;J to 0.1&gt;
        /// n=&lt;measurements&gt;</c>, plus <c>  removed=&lt;n&gt;</c> when the bad-data pass removed any. One source, so
        /// the desktop status pill and the web quality pill cannot drift apart. A missing J or n prints as blank, as
        /// the desktop always has. N151: when <see cref="IrlsCapReached"/> is true, <c>  *  </c> and
        /// <see cref="IrlsCapWarning"/> follow; otherwise the text is exactly what it always was.</summary>
        public string QualitySummary()
        {
            // N214: a test that did not run says so (it used to read "chi-square FAIL", and before N208 "PASS"). Without
            // chiTest (an older bridge) the verdict is exactly the old one, from ChiPassed alone.
            string verdict = ChiTest == ChiTestNotRun ? "chi-square not run"
                : ChiPassed ? "chi-square PASS" : "chi-square FAIL";
            string stats = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"J={ObjectiveJ:0.0}  n={MeasurementsN}{(RemovedN is > 0 ? $"  removed={RemovedN}" : "")}");
            string irls = IrlsCapReached == true ? $"  *  {IrlsCapWarning}" : "";
            return $"{verdict}  *  {stats}{irls}";
        }
    }

    /// <summary><c>{type:"result", op, id, at, payload:{...}}</c> - the response to an on-demand query.
    /// <c>id</c> echoes the request's correlation id so a reply is matched to its exact query even when
    /// same-op queries complete out of order (the server runs them as independent tasks). (GS-H4)</summary>
    public sealed class ResultMessage
    {
        public string? Type { get; set; }
        public string? Op { get; set; }
        /// <summary>Correlation id echoed from the originating <c>query</c> request, or <c>null</c> from a legacy server.</summary>
        public string? Id { get; set; }
        public string? At { get; set; }
        public QueryPayload? Payload { get; set; }

        /// <summary>Project the raw query response into a <see cref="QueryResult"/>, running <c>payload.solveExport</c>
        /// (a full <c>gridsim-solve-export/1</c> document) straight through <see cref="SolveImport.Read(string)"/>.</summary>
        public QueryResult ToQueryResult()
        {
            SolveSnapshot? snap = null;
            if (Payload?.SolveExport is JsonElement e &&
                e.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                snap = SolveImport.Read(e.GetRawText());

            var violations = (Payload?.Violations ?? new())
                .Select(v => new Violation(v.Kind, v.Element, v.Value, v.Limit, v.Message))
                .ToList();

            return new QueryResult(
                Op ?? "", ParseUtc(At), snap,
                Payload?.Verdict, Payload?.BindingConstraint, violations,
                AssetData.From(Payload), PropagationData.From(Payload));
        }
    }

    // ---- Shared blocks ----

    /// <summary>The <c>model</c> block: same shape as a <c>gridsim-solve-export/1</c> model.</summary>
    public sealed class ModelBlock
    {
        public string? Name { get; set; }
        public double BaseMva { get; set; } = 100;

        /// <summary>The model's nominal frequency (Hz), R177 / Q13 (October 2026). Optional: absent (older writers,
        /// the GDA bridge today) means 50 Hz, the value the reader always assumed. When present it must be finite
        /// and positive, or <see cref="BuildModel"/> fails loud.</summary>
        public double? FrequencyHz { get; set; }

        public List<BusDto>? Buses { get; set; }
        public List<GenDto>? Generators { get; set; }
        public List<BranchDto>? Branches { get; set; }

        /// <summary>ETYS transfer boundaries (named branch cuts + limits). Optional - older
        /// servers omit it; carrying it makes the RP1/OPERATOR boundary bars live in bridge
        /// mode (previously always empty for streamed cases).</summary>
        public List<BoundaryDto>? Boundaries { get; set; }

        /// <summary>On-load tap changers (R031). Optional - older writers omit it, which means none.</summary>
        public List<OltcDto>? Oltcs { get; set; }
    }

    /// <summary>An on-load tap changer, mirroring <see cref="Oltc"/> (defaults are the model's own).</summary>
    public sealed class OltcDto
    {
        public string? Transformer { get; set; }
        public int ControlsBus { get; set; }
        public double TargetVpu { get; set; } = 1.0;
        public double MinTap { get; set; } = 0.9;
        public double MaxTap { get; set; } = 1.1;
        public double StepPu { get; set; } = 0.0125;
        public double DeadbandPu { get; set; } = 0.01;
    }

    /// <summary>A bus ZIP load model, mirroring <see cref="ZipLoad"/> (absent = constant power).</summary>
    public sealed class ZipDto
    {
        public double Ap { get; set; }
        public double Bp { get; set; }
        public double Cp { get; set; } = 1.0;
        public double Aq { get; set; }
        public double Bq { get; set; }
        public double Cq { get; set; } = 1.0;
    }

    public sealed class BoundaryDto
    {
        public string? Name { get; set; }
        public double LimitMw { get; set; }
        public List<BoundaryBranchDto>? Branches { get; set; }
    }

    public sealed class BoundaryBranchDto
    {
        public string? Branch { get; set; }
        public int Direction { get; set; } = 1;
    }

    public sealed class BusDto
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public double BaseKv { get; set; }
        public double Pd { get; set; }
        public double Qd { get; set; }
        public double Gs { get; set; }
        public double Bs { get; set; }
        public double Vm { get; set; } = 1.0;
        public string? Name { get; set; }
        public double X { get; set; }   // geolocated layout coordinate (0..1), 0 if the stream omits it
        public double Y { get; set; }
        public ZipDto? Zip { get; set; }   // voltage-dependent load model; absent = constant power (R031)
        // D-DOC-067: the initial / fixed angle (degrees) and the bus's own voltage limits (pu; absent = the global band).
        public double VaDeg { get; set; }
        public double? VminPu { get; set; }
        public double? VmaxPu { get; set; }
    }

    /// <summary>D-DOC-067: a generator cost curve, mirroring <see cref="GeneratorCost"/> (model by name).</summary>
    public sealed class CostDto
    {
        public string? Model { get; set; }
        public double StartupCost { get; set; }
        public double ShutdownCost { get; set; }
        public List<double>? Coefficients { get; set; }
    }

    public sealed class GenDto
    {
        public int Bus { get; set; }
        public double Pg { get; set; }
        public double Qg { get; set; }
        public double Vg { get; set; } = 1.0;
        public double Qmin { get; set; } = double.NegativeInfinity;
        public double Qmax { get; set; } = double.PositiveInfinity;
        public double Pmin { get; set; }   // R031: carried so a re-imported unit keeps its minimum output
        public double Pmax { get; set; } = double.PositiveInfinity;
        public double MBase { get; set; } = 100;
        public double H { get; set; }
        public string? Type { get; set; }
        public string? Name { get; set; }

        // D-DOC-067: the rest of the generator, each with the model's own default when absent (an older writer, the GDA
        // bridge): in service, no dynamics class, synthesised fault reactances, converter type inferred, no cost curve.
        public bool InService { get; set; } = true;
        public string? DynamicsClass { get; set; }
        public double SubtransientXdPu { get; set; }
        public double NegativeSeqXPu { get; set; }
        public bool? ConverterInterfaced { get; set; }
        public double? ConverterFaultCurrentPu { get; set; }
        public bool PowerFlowOnly { get; set; }
        public double UnitTransformerXPu { get; set; }
        public bool GridForming { get; set; }
        public CostDto? Cost { get; set; }
        public CostDto? ReactiveCost { get; set; }
    }

    public sealed class BranchDto
    {
        public int From { get; set; }
        public int To { get; set; }
        public double R { get; set; }
        public double X { get; set; }
        public double B { get; set; }
        public double Tap { get; set; } = 1.0;
        public double PhaseShiftDeg { get; set; }
        public double RateAMva { get; set; }
        public string? Name { get; set; }
        public bool IsTransformer { get; set; }

        // R031: the branch state and the short-circuit data. Absent = the model's own default (in service,
        // no zero-sequence data, a line rather than a transformer winding pair).
        public bool InService { get; set; } = true;
        public double R0 { get; set; }
        public double X0 { get; set; }
        public string? FromWinding { get; set; }
        public string? ToWinding { get; set; }
        public double FromNeutralXPu { get; set; }
        public double ToNeutralXPu { get; set; }

        // D-DOC-067: length (km, 0 = unknown), conductor type ("ohl" / "cable"), the short-term and emergency ratings
        // (MVA, 0 = not given) and the passive-plant dynamics class. Absent = the model's own default.
        public double LengthKm { get; set; }
        public string? Equipment { get; set; }
        public double RateBMva { get; set; }
        public double RateCMva { get; set; }
        public string? DynamicsClass { get; set; }
    }

    /// <summary>The slim <c>result</c> block of a <c>state</c> message: solved bus values + line flows.</summary>
    public sealed class StateResultBlock
    {
        public bool? Converged { get; set; }
        public int? Iterations { get; set; }
        public double? MaxMismatch { get; set; }
        public double? TotalLossMw { get; set; }
        public double? TotalLossMvar { get; set; }
        public List<ResBusDto>? Buses { get; set; }
        public List<FlowDto>? LineFlows { get; set; }
    }

    public sealed class ResBusDto
    {
        public int Id { get; set; }
        public double Vm { get; set; } = 1.0;
        public double VaDeg { get; set; }
        public double PInjMw { get; set; }
        public double QInjMvar { get; set; }
    }

    public sealed class FlowDto
    {
        public int From { get; set; }
        public int To { get; set; }
        public double PFromMw { get; set; }
        public double QFromMvar { get; set; }
        public double PToMw { get; set; }
        public double QToMvar { get; set; }
        public double PLossMw { get; set; }
        public double QLossMvar { get; set; }
        public string? Name { get; set; }
        public double RatingMva { get; set; }
        public double LoadingPercent { get; set; }
    }

    /// <summary>Per-tick system scalars streamed alongside the slim result, for the HUD.</summary>
    public sealed class ScalarsBlock
    {
        private double? _fragilityIndex;

        public double? FreqHz { get; set; }
        public double? RocofHzPerS { get; set; }
        public double? GenMw { get; set; }
        public double? DemandMw { get; set; }
        public double? InertiaMwS { get; set; }

        /// <summary>
        /// The wire's <c>conditionIndex</c>: GDA's system fragility index, which GDA builds on a 0..100 scale
        /// (v1/Scripts/build_system_condition_index.py: "absolute physical fragility, 0-100"). The stream used to be read
        /// as if it were already GridSim's 0..1 condition index, so a real reading such as 33.49 saturated the HUD's
        /// condition colour ramp (2026-10-24). Setting it also sets <see cref="ConditionIndex"/>.
        /// </summary>
        [JsonPropertyName("conditionIndex")]
        public double? FragilityIndex
        {
            get => _fragilityIndex;
            set
            {
                _fragilityIndex = value;
                ConditionIndex = ConditionFromFragility(value);
            }
        }

        /// <summary>
        /// GridSim's condition index (0 healthy .. 1 critical), the scale the HUD's colour ramp, the map overlays and the
        /// web projection use: <see cref="FragilityIndex"/> / 100. Null when the wire sent none, or sent a value that is not
        /// a finite number in 0..100 (fail loud: an impossible reading is shown as no reading, never clamped into a
        /// colour). Not on the wire.
        /// </summary>
        [JsonIgnore]
        public double? ConditionIndex { get; set; }

        public double? RocofHeadroomHzS { get; set; }

        /// <summary>The wire's 0..100 fragility index as GridSim's 0..1 condition index, or null when absent or out of range.</summary>
        public static double? ConditionFromFragility(double? fragility0To100)
            => fragility0To100 is double f && double.IsFinite(f) && f >= 0 && f <= FragilityIndexFullScale
                ? f / FragilityIndexFullScale
                : null;

        /// <summary>The full scale of GDA's fragility index (its 0..100 scale).</summary>
        public const double FragilityIndexFullScale = 100.0;
    }

    /// <summary>Per-asset overlay scalars: stress keyed by branch name, condition keyed by bus id.</summary>
    public sealed class OverlaysBlock
    {
        public Dictionary<string, double>? StressByBranch { get; set; }
        public Dictionary<int, double>? ConditionByBus { get; set; }
    }

    /// <summary>
    /// Per-tick OPERATOR-domain series streamed from the GDA lake alongside the solved state: the
    /// system-operator view (balancing-mechanism dispatch, active network constraints, response/reserve
    /// headroom, largest-loss securing, interconnector net, system warnings). All optional  -  a tick
    /// omits whatever the lake has no record for at that instant.
    /// </summary>
    public sealed class OperatorBlock
    {
        public double? LargestLossMw { get; set; }
        public double? ResponseMw { get; set; }
        public double? ReserveMw { get; set; }
        public double? DeratedMarginMw { get; set; }    // derated capacity margin (LOLPDRM), MW
        public double? NetInterconnectorMw { get; set; }
        // ---- inertia stratification for the NESO RP1 stability panel (GVA.s) ----
        public double? SynchronousInertiaGvaS { get; set; }   // GVA.s(i) - intrinsic, bottom-up synchronous mass
        public double? OutturnInertiaGvaS { get; set; }       // NESO published "Outturn Inertia"
        public double? MarketInertiaGvaS { get; set; }        // NESO "Market Provided" (contracted; not pure proxy)
        public string? Warning { get; set; }
        public List<BalancingActionDto>? BalancingActions { get; set; }
        public List<ConstraintDto>? Constraints { get; set; }
        public List<string>? Warnings { get; set; }
    }

    /// <summary>One balancing-mechanism acceptance (a BID or OFFER dispatched on a BMU).</summary>
    public sealed class BalancingActionDto
    {
        public string? BmUnit { get; set; }
        public string? Kind { get; set; }         // "BID" | "OFFER"
        public double? LevelMw { get; set; }
        public double? PriceGbpMwh { get; set; }
        public string? FuelType { get; set; }     // Elexon fuelType (CCGT, WIND, BIOMASS, ...) if resolved
        public bool? Synchronous { get; set; }    // true if the BMU's fuel provides intrinsic inertia
    }

    /// <summary>One active network constraint (boundary/thermal) with its flow, limit and constraint cost.</summary>
    public sealed class ConstraintDto
    {
        public string? Name { get; set; }
        public double? FlowMw { get; set; }
        public double? LimitMw { get; set; }
        public double? CostGbp { get; set; }
        public bool? Binding { get; set; }
    }

    /// <summary>
    /// Per-tick MARKET-domain series streamed from the GDA lake: imbalance price (SBP/SSP), market index,
    /// net imbalance volume, day-ahead price, grid carbon intensity, and interconnector flows/prices.
    /// </summary>
    public sealed class MarketBlock
    {
        public double? ImbalancePriceGbpMwh { get; set; }
        public double? SystemSellPriceGbpMwh { get; set; }
        public double? MarketIndexGbpMwh { get; set; }
        public double? NetImbalanceVolumeMwh { get; set; }
        public double? DayAheadGbpMwh { get; set; }
        public double? BsuosGbpMwh { get; set; }         // Balancing Services Use of System charge, £/MWh
        public double? ConstraintCostGbp { get; set; }   // total thermal-constraint cost for the day, £
        public double? CarbonIntensityGco2PerKwh { get; set; }
        public double? DemandForecastMw { get; set; }
        // Balancing market (Elexon Disaggregated BSAD, aggregated per settlement period) - "what NESO spent
        // balancing the system" this period, the total volume of accepted actions, and how many there were.
        public double? BalancingCostGbp { get; set; }
        public double? BalancingVolumeMwh { get; set; }
        public int? BalancingActionsN { get; set; }
        // Balancing MECHANISM spend estimate (Elexon QAS accepted volume x BOD marginal price, per period) - the
        // big one, dwarfing the BSAD adjustment above. Estimated (Elexon publishes no ready per-period BM cost).
        public double? BmCostGbp { get; set; }
        public double? BmVolumeMwh { get; set; }
        public int? BmUnitsN { get; set; }
        // Timed feed of individual balancing acceptances (Elexon BOALF) for the MARKET console; newest first.
        public List<BmAcceptanceDto>? Actions { get; set; }
        public List<InterconnectorDto>? Interconnectors { get; set; }
    }

    /// <summary>One accepted balancing action for the MARKET console: the BM unit was moved From-&gt;To MW at time
    /// T (offer = To &gt; From, bid = To &lt; From); So flags a system-operator (non-energy) instruction.</summary>
    public sealed class BmAcceptanceDto
    {
        public string? T { get; set; }        // acceptance time, HH:MM:SS
        public string? Unit { get; set; }     // BM unit id
        public double? From { get; set; }
        public double? To { get; set; }
        public bool So { get; set; }
    }

    /// <summary>One interconnector's instantaneous flow (import +ve) and, if known, price/capacity.</summary>
    public sealed class InterconnectorDto
    {
        public string? Name { get; set; }
        public double? FlowMw { get; set; }
        public double? CapacityMw { get; set; }
        public double? PriceGbpMwh { get; set; }
    }

    /// <summary>
    /// Per-tick raw generation MIX (MW by fuel) from the <c>source=raw</c> replay's
    /// <c>historic_generation_mix_and_carbon_intensity</c> lake row - the real published fuel breakdown at that
    /// instant. This is what lets the raw view's GENERATION panel move with the lake instead of showing the frozen
    /// topology fleet. All optional; a fuel the row omits is null.
    /// </summary>
    public sealed class GenerationMixBlock
    {
        public double? Gas { get; set; }
        public double? Coal { get; set; }
        public double? Nuclear { get; set; }
        public double? Wind { get; set; }
        public double? WindEmb { get; set; }     // embedded (distribution-connected) wind
        public double? Hydro { get; set; }
        public double? Imports { get; set; }      // net interconnector imports
        public double? Biomass { get; set; }
        public double? Other { get; set; }
        public double? Solar { get; set; }
        public double? Storage { get; set; }

        /// <summary>Ordered (label, MW) rows for the raw GENERATION panel: the fuels the tick carries a finite value
        /// for, largest first. Keeps fuel labels + order in one place so the raw projection binds generically.</summary>
        public IReadOnlyList<(string Fuel, double Mw)> Rows() =>
            new (string Label, double? Value)[]
            {
                ("Gas", Gas), ("Coal", Coal), ("Nuclear", Nuclear), ("Wind", Wind),
                ("Embedded wind", WindEmb), ("Hydro", Hydro), ("Imports", Imports),
                ("Biomass", Biomass), ("Other", Other), ("Solar", Solar), ("Storage", Storage),
            }
            .Where(p => p.Value is double d && double.IsFinite(d))
            .Select(p => (p.Label, p.Value!.Value))
            .OrderByDescending(p => p.Item2)
            .ToList();

        /// <summary>Total MW across every fuel the tick carries (finite values only).</summary>
        public double TotalMw() => Rows().Sum(r => r.Mw);

        /// <summary>Estimated behind-the-meter (embedded / distribution-connected) generation: rooftop + field solar
        /// plus embedded wind. NESO estimates these - they aren't transmission-metered - so they suppress the metered
        /// transmission demand without changing what was actually consumed. Used to reconstruct underlying demand.</summary>
        public double EmbeddedMw() => (Solar is double s && double.IsFinite(s) ? s : 0)
                                    + (WindEmb is double w && double.IsFinite(w) ? w : 0);
    }

    /// <summary>Per-tick raw WEATHER from the <c>source=raw</c> replay's weather lake row (national context).</summary>
    public sealed class WeatherBlock
    {
        public double? TemperatureC { get; set; }
        public double? WindSpeed100mKph { get; set; }
        public double? SolarRadiationWm2 { get; set; }

        /// <summary>True when at least one channel joined the tick (else the panel shows "no weather at this time").</summary>
        public bool HasAny =>
            (TemperatureC is double t && double.IsFinite(t)) ||
            (WindSpeed100mKph is double w && double.IsFinite(w)) ||
            (SolarRadiationWm2 is double s && double.IsFinite(s));
    }

    /// <summary>
    /// Per-tick raw INTERCONNECTOR flows (MW, import +ve) from the <c>source=raw</c> replay's demand lake row, plus
    /// the net. Named links so the raw GENERATION panel shows the real cross-border flows moving with the replay,
    /// instead of a static topology figure. All optional.
    /// </summary>
    public sealed class RawInterconnectorsBlock
    {
        public double? Ifa { get; set; }
        public double? Ifa2 { get; set; }
        public double? BritNed { get; set; }
        public double? Moyle { get; set; }
        public double? EastWest { get; set; }
        public double? Nemo { get; set; }
        public double? Nsl { get; set; }
        public double? ElecLink { get; set; }
        public double? Viking { get; set; }
        public double? Greenlink { get; set; }
        public double? Net { get; set; }

        /// <summary>Named (link, MW) rows the tick carries a non-zero finite flow for, largest magnitude first.</summary>
        public IReadOnlyList<(string Name, double Mw)> Rows() =>
            new (string Name, double? Mw)[]
            {
                ("IFA", Ifa), ("IFA2", Ifa2), ("BritNed", BritNed), ("Moyle", Moyle), ("East-West", EastWest),
                ("Nemo", Nemo), ("NSL", Nsl), ("ElecLink", ElecLink), ("Viking", Viking), ("Greenlink", Greenlink),
            }
            .Where(p => p.Mw is double d && double.IsFinite(d) && Math.Abs(d) > MinDisplayFlowMw)
            .Select(p => (p.Name, p.Mw!.Value))
            .OrderByDescending(p => Math.Abs(p.Item2))
            .ToList();
    }

    /// <summary>The <c>payload</c> of a query <c>result</c> message.</summary>
    public sealed class QueryPayload
    {
        /// <summary>A full <c>gridsim-solve-export/1</c> document, captured raw and fed to <see cref="SolveImport.Read(string)"/>.</summary>
        public JsonElement? SolveExport { get; set; }
        public string? Verdict { get; set; }
        public string? BindingConstraint { get; set; }
        public List<ViolationDto>? Violations { get; set; }

        // ---- op="asset" per-asset drill-down blocks (task T-J). Each is a list of raw
        // Derived-dataset rows (column name -> value); the client renders them generically. ----
        public string? BmUnit { get; set; }
        public int? BusId { get; set; }
        public List<Dictionary<string, JsonElement>>? Envelope { get; set; }
        public List<Dictionary<string, JsonElement>>? BessSoc { get; set; }
        public List<Dictionary<string, JsonElement>>? TapWear { get; set; }
        public List<Dictionary<string, JsonElement>>? Consumers { get; set; }

        // ---- op="propagation": GDA's propagation_delays lake product, streamed as corridor tau for the
        // distributed inertia model. `provenance` carries the honest real-vs-modelled note. ----
        public List<PropagationCorridorDto>? Corridors { get; set; }
        public string? Provenance { get; set; }
    }

    /// <summary>One corridor of the <c>op="propagation"</c> payload (GDA's <c>propagation_delays</c> row).</summary>
    public sealed class PropagationCorridorDto
    {
        public int From { get; set; }
        public int To { get; set; }
        public double? Tau { get; set; }      // seconds; null when the lake row has no tau
        public double? Km { get; set; }       // physical distance
        public double? VKmS { get; set; }     // wave speed km/s
        public string? Class { get; set; }    // element class (branch vs gen-pair)
    }

    public sealed class ViolationDto
    {
        public string? Kind { get; set; }
        public string? Element { get; set; }
        public double? Value { get; set; }
        public double? Limit { get; set; }
        public string? Message { get; set; }
    }
}

/// <summary>A single constraint violation reported by an on-demand physics query.</summary>
public sealed record Violation(string? Kind, string? Element, double? Value, double? Limit, string? Message);

/// <summary>One field of a per-asset row: the dataset column and its formatted value.</summary>
public sealed record AssetField(string Key, string Value);

/// <summary>One row of a per-asset block (e.g. one BM unit envelope, one tap changer, one consumer).</summary>
public sealed record AssetEntry(IReadOnlyList<AssetField> Fields);

/// <summary>
/// The projected <c>op="asset"</c> payload: the resolved unit ids and the four per-asset blocks
/// (generator envelope MSG/Pmax, BESS SoC ratings, tap-changer wear, consumer/demand placement),
/// each a list of raw Derived-dataset rows. Blocks the unit doesn't have arrive empty.
/// </summary>
public sealed record AssetData(
    string? BmUnit,
    int? BusId,
    IReadOnlyList<AssetEntry> Envelope,
    IReadOnlyList<AssetEntry> BessSoc,
    IReadOnlyList<AssetEntry> TapWear,
    IReadOnlyList<AssetEntry> Consumers)
{
    public bool IsEmpty => Envelope.Count == 0 && BessSoc.Count == 0
                        && TapWear.Count == 0 && Consumers.Count == 0;

    /// <summary>Project the raw payload's asset blocks into display rows, or <c>null</c> if the payload carried none.</summary>
    public static AssetData? From(BridgeProtocol.QueryPayload? p)
    {
        if (p is null) return null;
        var env = Rows(p.Envelope);
        var bess = Rows(p.BessSoc);
        var tap = Rows(p.TapWear);
        var cons = Rows(p.Consumers);
        if (p.BmUnit is null && p.BusId is null &&
            env.Count == 0 && bess.Count == 0 && tap.Count == 0 && cons.Count == 0)
            return null;
        return new AssetData(p.BmUnit, p.BusId, env, bess, tap, cons);
    }

    private static IReadOnlyList<AssetEntry> Rows(List<Dictionary<string, JsonElement>>? block)
    {
        if (block is null) return Array.Empty<AssetEntry>();
        var outp = new List<AssetEntry>(block.Count);
        foreach (var row in block)
        {
            var fields = new List<AssetField>(row.Count);
            foreach (var kv in row)
            {
                string? v = Format(kv.Value);
                if (v is not null) fields.Add(new AssetField(kv.Key, v));
            }
            if (fields.Count > 0) outp.Add(new AssetEntry(fields));
        }
        return outp;
    }

    private static string? Format(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Number => e.TryGetInt64(out long l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : e.GetDouble().ToString("0.####", CultureInfo.InvariantCulture),
        _ => e.GetRawText(),
    };
}

/// <summary>
/// The projected <c>op="propagation"</c> payload: GDA's per-corridor electromechanical delays as a
/// <c>(fromBus, toBus) -> tau seconds</c> map ready to drop into <c>DistributedConfig.ProvidedTauSeconds</c>,
/// plus the honest provenance note (distances/inertia real; wave speed + tau modelled, unvalidated at
/// scale). Corridors whose lake row carries no tau are omitted from the map.
/// </summary>
public sealed record PropagationData(
    IReadOnlyDictionary<(int From, int To), double> TauByBusPair,
    string? Provenance)
{
    /// <summary>Corridor tau beyond this many seconds is physically implausible (the GB span ~ 1,215 km at the
    /// >=500 km/s wave-speed floor => ~ 2.4 s max), so a larger value is treated as a bad row (e.g. a ms-as-s
    /// unit slip) and dropped rather than fed to the swing model, where it would freeze the coupled domain
    /// at its t=0 angle for the whole run.</summary>
    public const double MaxPlausibleTauSeconds = 10.0;

    public static PropagationData? From(BridgeProtocol.QueryPayload? p)
    {
        if (p?.Corridors is null) return null;
        var map = new Dictionary<(int, int), double>(p.Corridors.Count);
        int dropped = 0;
        foreach (BridgeProtocol.PropagationCorridorDto c in p.Corridors)
            if (c.Tau is double t)
            {
                // Validate at the protocol boundary: a negative, non-finite, or implausibly-large tau is silently
                // destructive downstream (zero delay, or a domain frozen at its t=0 angle), so reject it here.
                if (double.IsFinite(t) && t >= 0.0 && t <= MaxPlausibleTauSeconds)
                {
                    // A corridor given twice with different tau is ambiguous: refuse it rather than keep the last.
                    // An exact repeat is harmless and kept once.
                    if (map.TryGetValue((c.From, c.To), out double prev) && prev != t)
                        throw new InvalidDataException(
                            $"Propagation data: corridor {c.From}-{c.To} is given twice, with tau {prev} s and {t} s.");
                    map[(c.From, c.To)] = t;
                }
                else
                    dropped++;
            }
        string? provenance = dropped > 0
            ? $"{p.Provenance}{(string.IsNullOrEmpty(p.Provenance) ? "" : "; ")}" +
              $"dropped {dropped} implausible tau (negative/NaN/>{MaxPlausibleTauSeconds:0}s)"
            : p.Provenance;
        return new PropagationData(map, provenance);
    }
}

/// <summary>
/// The projected result of a <see cref="Net.GridBridgeClient.Query"/>: the reconstructed solve (from the payload's
/// <c>gridsim-solve-export/1</c> document, or <c>null</c> if none), the engine's verdict and binding constraint,
/// any reported violations, and  -  for <c>op="asset"</c> / <c>op="propagation"</c>  -  the drill-down blocks.
/// </summary>
public sealed record QueryResult(
    string Op,
    DateTime At,
    SolveSnapshot? Solve,
    string? Verdict,
    string? BindingConstraint,
    IReadOnlyList<Violation> Violations,
    AssetData? Asset = null,
    PropagationData? Propagation = null);

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the bridge messages GridSim reads (topology, state, result, note)
/// and the fixed requests it sends (<see cref="GridSim.Core.Net.BridgeRequests"/>), so the bridge needs no runtime
/// reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the reflection path used. The
/// runtime options are the bridge settings, passed in by <see cref="BridgeProtocol.Json"/>.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BridgeProtocol.TopologyMessage))]
[JsonSerializable(typeof(BridgeProtocol.StateMessage))]
[JsonSerializable(typeof(BridgeProtocol.ResultMessage))]
[JsonSerializable(typeof(BridgeProtocol.NoteMessage))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.StartRequest))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.StartLoopingRequest))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.StopRequest))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.SetSpeedRequest))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.SeekRequest))]
[JsonSerializable(typeof(GridSim.Core.Net.BridgeRequests.QueryRequest<GridSim.Core.Net.BridgeRequests.NoParams>), TypeInfoPropertyName = "QueryNoParams")]
internal sealed partial class BridgeProtocolJsonContext : JsonSerializerContext;
