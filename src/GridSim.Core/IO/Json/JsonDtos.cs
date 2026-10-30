namespace GridSim.Core.IO.Json;

// Plain data carriers matching the JSON files. Optional fields have sensible defaults so the files stay
// easy to hand-edit, but an unknown or misspelt key is refused at load (JsonGridLoader, R268): a typo must
// never silently fall back to a default.

/// <summary>
/// The physical defaults a JSON grid file falls back to when an optional field is absent (October 2026 review,
/// WS13 R179). Named here so they are visible and pinned; values unchanged.
///
/// <para>A bus's baseKv is NOT here any more (R179 / Q13, October 2026): it is REQUIRED. A bus with no baseKv used
/// to become 400 kV silently, which scales every kA figure of a fault study; the loader now refuses it by name.
/// Every bundled buses.json carries baseKv on every bus (gb-full 3539, gb-synthetic 2227, continental-europe 5824,
/// france 1976, iberia 1497, ireland-all-island 60, uk 20), so the change is number-neutral on them. The untyped
/// generator still becomes <see cref="GeneratorType"/> (not part of this ruling).</para>
/// </summary>
internal static class JsonDefaults
{
    /// <summary>System base (MVA) of a file whose system block omits it.</summary>
    public const double BaseMva = GridSim.Core.Model.GridModel.DefaultBaseMva;

    /// <summary>Nominal frequency (Hz) of a file whose system block omits it.</summary>
    public const double FrequencyHz = GridSim.Core.Dynamics.FrequencyLimits.NominalHz;

    /// <summary>Technology of a generator that omits its type.</summary>
    public const string GeneratorType = "ccgt";

    /// <summary>Power factor of a demand that gives neither QMvar nor a power factor (0.97 lagging).</summary>
    public const double LoadPowerFactor = 0.97;

    /// <summary>The smallest power factor a demand may give; one outside [MinPowerFactor, 1] is refused by the
    /// loader (it used to be clamped), so a zero or negative entry cannot produce an infinite, NaN or huge Q.</summary>
    public const double MinPowerFactor = 0.01;
}

internal sealed class SystemDto
{
    public string? Name { get; set; }
    public double BaseMva { get; set; } = JsonDefaults.BaseMva;
    public double FrequencyHz { get; set; } = JsonDefaults.FrequencyHz;

    /// <summary>F24: false = load the generator outputs exactly as written, without the capacity-based dispatch
    /// balance (<c>Dispatcher.Balance</c>). The <c>convert</c> verb writes false, because a converted MATPOWER case
    /// carries a solved operating point that the balance would overwrite at the slack. Absent (null) = true: the
    /// historical behaviour, so every existing case is dispatched exactly as before.</summary>
    public bool? Dispatch { get; set; }
}

/// <summary>
/// F24: reads a double that may also be written as the JSON string "Infinity" or "-Infinity" (standard JSON has no
/// infinite number). Used only on the generator limits that MATPOWER allows to be unlimited (qMinMvar, qMaxMvar,
/// pMaxMw), so a converted case keeps an "Inf" limit exactly. Any other string ("NaN" included) is refused, naming it.
/// Writes infinities the same way and finite values as plain numbers.
/// </summary>
internal sealed class InfinityTolerantDoubleConverter : System.Text.Json.Serialization.JsonConverter<double?>
{
    public override double? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.Null: return null;
            case System.Text.Json.JsonTokenType.Number: return reader.GetDouble();
            case System.Text.Json.JsonTokenType.String:
                string? s = reader.GetString();
                // N139: exactly the two spellings the v1 / v2 schemas admit (N101 rule) and that JsonCaseWriter writes.
                // "+Infinity" used to be accepted here too, so a file the SchemaEditor save gate refuses still loaded.
                if (s == "Infinity") return double.PositiveInfinity;
                if (s == "-Infinity") return double.NegativeInfinity;
                throw new System.Text.Json.JsonException($"'{s}' is not a number; the only strings allowed here are \"Infinity\" and \"-Infinity\" (an unlimited generator limit; no other spelling, such as \"+Infinity\").");
            default:
                throw new System.Text.Json.JsonException($"expected a number, \"Infinity\" or \"-Infinity\", found {reader.TokenType}.");
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, double? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is not double v) writer.WriteNullValue();
        else if (double.IsPositiveInfinity(v)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(v)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(v);
    }
}

internal sealed class BusDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    /// <summary>Base voltage (kV). REQUIRED (R179 / Q13): null means the key was absent, and
    /// <see cref="JsonGridLoader"/> refuses the case naming the bus. There is no default any more.</summary>
    public double? BaseKv { get; set; }
    public string Type { get; set; } = "pq";  // pq | pv | slack (usually derived from generators)
    public double Vm { get; set; } = 1.0;
    public double VaDeg { get; set; }
    public double Gs { get; set; }
    public double Bs { get; set; }
    public double X { get; set; }   // layout coordinate (0..1) for the visualiser
    public double Y { get; set; }

    /// <summary>Optional voltage-dependent (ZIP) load model for the demand on this bus (N014 / R065). Absent or
    /// null = constant power, the historical behaviour. Validated fail-loud at load by
    /// <see cref="JsonGridLoader"/> (all six coefficients present and finite, each triple summing to 1).</summary>
    public BusZipDto? Zip { get; set; }

    /// <summary>F17: this bus's own lower / upper voltage limit (pu). Absent = the global band applies. Used only by
    /// checks asked for per-bus limits (the CLI --bus-vlimits flag); validated fail-loud at load by
    /// <see cref="JsonGridLoader"/> (finite, above 0, the lower not above the upper).</summary>
    public double? VminPu { get; set; }

    /// <summary>F17: see <see cref="VminPu"/>.</summary>
    public double? VmaxPu { get; set; }
}

/// <summary>
/// A bus ZIP load as written in buses.json: <c>"zip": { "ap": .., "bp": .., "cp": .., "aq": .., "bq": .., "cq": .. }</c>.
/// The keys are the six <see cref="GridSim.Core.Model.ZipLoad"/> coefficients, spelt exactly as the solve export
/// (<c>SolveExport</c>) writes them, so an exported case's zip block reads straight back in. The load drawn is
/// <c>P(V) = Pd * (ap*V^2 + bp*V + cp)</c> and <c>Q(V) = Qd * (aq*V^2 + bq*V + cq)</c>, V in pu.
/// Every coefficient is nullable so a MISSING one is caught and refused, rather than silently defaulting
/// (a forgotten "cp" must not quietly become 0 or 1).
/// </summary>
internal sealed class BusZipDto
{
    public double? Ap { get; set; }   // constant-impedance share of P (scales with V^2)
    public double? Bp { get; set; }   // constant-current share of P (scales with V)
    public double? Cp { get; set; }   // constant-power share of P
    public double? Aq { get; set; }   // constant-impedance share of Q
    public double? Bq { get; set; }   // constant-current share of Q
    public double? Cq { get; set; }   // constant-power share of Q
}

internal sealed class GenDto
{
    public string? Name { get; set; }
    public int Bus { get; set; }
    public string Type { get; set; } = JsonDefaults.GeneratorType;
    public double CapacityMva { get; set; }      // the "GVA" rating (here in MVA)
    public double? OutputMw { get; set; }        // explicit dispatch, else CapacityMva x CapacityFactor
    public double? CapacityFactor { get; set; }
    public double VoltageSetpoint { get; set; } = 1.0;
    [System.Text.Json.Serialization.JsonConverter(typeof(InfinityTolerantDoubleConverter))]
    public double? QMinMvar { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(InfinityTolerantDoubleConverter))]
    public double? QMaxMvar { get; set; }
    public double InertiaH { get; set; }
    public string Role { get; set; } = "pv";     // pv | slack
    public string? DynamicsClass { get; set; }   // optional inline asset-dynamics class (wins over a binding rule)
    public bool? ConverterInterfaced { get; set; }        // fault studies: full-converter plant; null = inferred from Type
    public double? ConverterFaultCurrentPu { get; set; }  // fault studies: converter k (x rated); 0 = no fault current
    public bool PowerFlowOnly { get; set; }                // power-flow device only (slack balancer, voltage control)
    public double UnitTransformerXPu { get; set; }         // step-up transformer reactance on the unit rating
    public bool GridForming { get; set; }                  // converter plant with grid-forming control
    /// <summary>F16: false = the unit is out of service. It is kept in the model (GridModel.AllGenerators) but never
    /// solved, and it does not make its bus PV or slack. Absent = true (in service), the historical behaviour.</summary>
    public bool InService { get; set; } = true;

    /// <summary>F24: the unit's minimum real output (MW). Absent = 0, the historical value.</summary>
    public double? PMinMw { get; set; }

    /// <summary>F24: the unit's maximum real output (MW), or "Infinity". Absent = capacityMva, the historical value.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(InfinityTolerantDoubleConverter))]
    public double? PMaxMw { get; set; }

    /// <summary>F24: the unit's scheduled reactive output (MVAr): the fixed injection of a unit on a PQ bus, the start
    /// value elsewhere. Absent = 0, the historical value.</summary>
    public double? QOutputMvar { get; set; }
}

internal sealed class DemandDto
{
    public string? Name { get; set; }
    public int Bus { get; set; }
    public double PMw { get; set; }
    public double? QMvar { get; set; }
    public double PowerFactor { get; set; } = JsonDefaults.LoadPowerFactor;
}

internal sealed class LineDto
{
    public string? Name { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public double RPu { get; set; }
    public double XPu { get; set; }
    public double BPu { get; set; }
    public double RatingMva { get; set; }
    public int Circuits { get; set; } = 1;
    public string? DynamicsClass { get; set; }   // optional inline passive-plant dynamics class
    public double LengthKm { get; set; }          // circuit length (km), for frequency-dependent line modelling
    public string? Equipment { get; set; }        // "ohl" | "cable" - conductor type (skin-effect model)
    public double RPu0 { get; set; }              // zero-sequence series resistance (pu on the case base)
    public double XPu0 { get; set; }              // zero-sequence series reactance (pu on the case base)

    // F24: an off-nominal branch written in transmission.json. A MATPOWER case interleaves its transformers with its
    // lines, and the solve sums branch admittances in branch order, so a converted case must keep that order to solve
    // bit-identically; a transformer can therefore be written here, in place, instead of in transformer.json. Absent
    // = a plain line (tap 1, no phase shift, not a transformer), exactly as before.
    public double TapRatio { get; set; } = 1.0;   // off-nominal turns ratio
    public double PhaseShiftDeg { get; set; }     // phase shift (degrees)
    public bool IsTransformer { get; set; }       // a transformer (labels, fault studies) rather than a line

    // F20: the short-term (B) and emergency (C) ratings of ONE circuit, MVA (MATPOWER RATE_B / RATE_C). Absent = 0 = not
    // given. Read only by a contingency screen asked to judge post-fault loading against them; nothing else uses them.
    public double RatingBMva { get; set; }
    public double RatingCMva { get; set; }
}

internal sealed class TransformerDto
{
    public string? Name { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public double RPu { get; set; }
    public double XPu { get; set; }
    public double TapRatio { get; set; } = 1.0;
    public double PhaseShiftDeg { get; set; }
    public double RatingMva { get; set; }
    public string? DynamicsClass { get; set; }   // optional inline passive-plant dynamics class

    // N119 (October 2026): the short-term (B) and emergency (C) ratings, MVA (MATPOWER RATE_B / RATE_C), as
    // transmission.json has carried them since F20. Absent = 0 = not given. Read only by a contingency screen asked to
    // judge post-fault loading against them (it falls back to ratingMva where they are 0).
    public double RatingBMva { get; set; }
    public double RatingCMva { get; set; }
}

internal sealed class BoundaryDto
{
    public string Name { get; set; } = "";
    public double LimitMw { get; set; }
    public List<BoundaryBranchDto>? Branches { get; set; }
}

internal sealed class BoundaryBranchDto
{
    public string Branch { get; set; } = "";
    public int Direction { get; set; } = 1;
}

internal sealed class OltcDto
{
    public string Transformer { get; set; } = "";
    public int ControlsBus { get; set; }
    public double TargetVpu { get; set; } = 1.0;
    public double MinTap { get; set; } = 0.9;
    public double MaxTap { get; set; } = 1.1;
    public double StepPu { get; set; } = 0.0125;
    public double DeadbandPu { get; set; } = 0.01;
}
