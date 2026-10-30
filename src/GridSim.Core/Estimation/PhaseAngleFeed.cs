using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>How the angles handed to <see cref="PhaseAngleFeed.ToMeasurements"/> are interpreted.</summary>
public enum PhaseAngleReference
{
    /// <summary>Treat the angles as absolute: time-project each to the common instant, then re-reference to
    /// the model slack via differences at a datum bus (the C37.118 §10.1 form). The robust default.</summary>
    AutoReference,
    /// <summary>The angles are already in the slack frame; only time-project them to the common instant.</summary>
    AlreadyReferenced,
    /// <summary>Inject the angles exactly as given (already slack-referenced AND at the target instant);
    /// frequency is kept only as provenance in the row's <c>Source</c>.</summary>
    Raw,
}

/// <summary>One per-bus phase-angle observation: a bus, its voltage angle (degrees), the instant it was
/// stated at, and the local system frequency (Hz) - with optional RoCoF for a second-order time projection.</summary>
public sealed record PhaseAngleObs(int Bus, double AngleDeg, DateTime Utc, double FreqHz, double RocofHzPerS = 0);

/// <summary>Knobs for turning <see cref="PhaseAngleObs"/> into estimator <see cref="Measurement"/>s.</summary>
public sealed record PhaseAngleOptions
{
    /// <summary>How to interpret / condition the input angles.</summary>
    public PhaseAngleReference Mode { get; init; } = PhaseAngleReference.AutoReference;

    /// <summary>1-σ of each authored angle measurement, degrees.</summary>
    public double SigmaDeg { get; init; } = 2.0;

    /// <summary>The common instant every angle is projected to. Null = the latest observation's timestamp.</summary>
    public DateTime? TargetUtc { get; init; }

    /// <summary>Re-reference datum bus id (<see cref="PhaseAngleReference.AutoReference"/> only). Null = the
    /// slack bus if it appears in the observations, else the first observation's bus (a non-slack datum then
    /// needs <see cref="DatumAnchorDeg"/>).</summary>
    public int? DatumBus { get; init; }

    /// <summary>
    /// The datum bus's PRESENT-STATE angle in the slack frame, degrees (<see cref="PhaseAngleReference.AutoReference"/>
    /// only), for example from a solved prior or warm start. Required when the datum is not the slack (R028): the
    /// whole re-referenced angle set is shifted onto this anchor, so it must describe the operating point the
    /// angles were measured at, never the case file's static angle. Must be left null when the datum IS the slack
    /// (the slack angle is the frame's own reference) and in the other modes.
    /// </summary>
    public double? DatumAnchorDeg { get; init; }

    /// <summary>Nominal frequency the angle rotates against (Hz).</summary>
    public double NominalHz { get; init; } = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;   // GB PMU feed nominal (named, R196 / D2)

    /// <summary>
    /// A system common-mode phase (degrees) subtracted from every observation after time-projection - the GDA
    /// <c>PHASEANGLE_INFERED</c> system angle θ_sys(T). Use it in <see cref="PhaseAngleReference.AlreadyReferenced"/>
    /// mode to bring an absolute (inertial-frame) per-bus feed into the network frame before injection. Note it
    /// is redundant under <see cref="PhaseAngleReference.AutoReference"/> - the datum difference already removes
    /// any common mode - and it is ignored under <see cref="PhaseAngleReference.Raw"/>.
    /// </summary>
    public double CommonModeDeg { get; init; } = 0.0;

    /// <summary>Provenance tag written to each measurement's <see cref="Measurement.Source"/> (frequency and
    /// instant are appended).</summary>
    public string Source { get; init; } = "phaseangle";
}

/// <summary>
/// Turns per-bus voltage phase-angle observations into slack-referenced <see cref="MeasurementKind.VaDeg"/>
/// measurements the WLS estimator consumes. This is the estimator-improving path: a genuine per-bus angle
/// (one that carries real inter-bus spread) becomes a direct <c>1.0</c>-pivot row on that bus's angle column,
/// converting a weakly/indirectly-observed angle direction into a directly-observed one.
/// <para>
/// It does NOT and cannot manufacture spatial detail from a system common-mode angle: the estimator solves
/// angles relative to a fixed slack, so a common offset applied to every bus cancels on re-referencing. Feed
/// this real per-bus differences; use a system COI angle (GDA <c>PHASEANGLE_INFERED</c>) only as the absolute
/// reference frame / projection frequency, never as a per-bus row.
/// </para>
/// </summary>
public static class PhaseAngleFeed
{
    /// <summary>
    /// Parse a whitespace- and/or comma-separated list of <c>BUS:ANGLE_DEG:UTC:FREQ_HZ</c> tuples. The UTC is
    /// an ISO-8601 instant and itself contains colons, so each tuple is split by its FIRST two colons (BUS,
    /// ANGLE) and its LAST colon (FREQ), with everything in between the timestamp. Throws
    /// <see cref="FormatException"/> naming the offending token on any malformed tuple.
    /// </summary>
    public static IReadOnlyList<PhaseAngleObs> Parse(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var obs = new List<PhaseAngleObs>();
        foreach (string token in spec.Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int i1 = token.IndexOf(':');
            int i2 = i1 < 0 ? -1 : token.IndexOf(':', i1 + 1);
            int iLast = token.LastIndexOf(':');
            if (i1 <= 0 || i2 < 0 || iLast <= i2)
                throw new FormatException($"Bad --phaseangle tuple '{token}': expected BUS:ANGLE_DEG:UTC:FREQ_HZ (UTC in ISO-8601 'Z' form).");

            string busS = token[..i1];
            string angS = token[(i1 + 1)..i2];
            string utcS = token[(i2 + 1)..iLast];
            string freqS = token[(iLast + 1)..];

            if (!int.TryParse(busS, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus))
                throw new FormatException($"Bad --phaseangle tuple '{token}': bus '{busS}' is not an integer id.");
            if (!double.TryParse(angS, NumberStyles.Float, CultureInfo.InvariantCulture, out double ang))
                throw new FormatException($"Bad --phaseangle tuple '{token}': angle '{angS}' is not a number.");
            if (!double.TryParse(freqS, NumberStyles.Float, CultureInfo.InvariantCulture, out double freq))
                throw new FormatException($"Bad --phaseangle tuple '{token}': frequency '{freqS}' is not a number (is the UTC in 'Z' form, not a ':'-offset?).");
            // D-DOC-063 (same class as --utc-now / --phaseangle-at): the instant parses strictly as ISO 8601 with the
            // invariant culture. The lenient DateTime.TryParse accepted ambiguous shapes such as 01/02/2100 (2 January
            // to the invariant culture, 1 February to a UK reader), and this instant decides the horizon check.
            if (!GridSim.Core.Diagnostics.UtcTime.TryParseIso8601(utcS, out DateTime utc))
                throw new FormatException($"Bad --phaseangle tuple '{token}': UTC '{utcS}' is not an ISO-8601 instant.");

            obs.Add(new PhaseAngleObs(bus, ang, DateTime.SpecifyKind(utc, DateTimeKind.Utc), freq));
        }
        return obs;
    }

    /// <summary>
    /// Convert observations to <see cref="MeasurementKind.VaDeg"/> rows and report the common instant they
    /// were projected to (use it as the set's <see cref="MeasurementSet.TakenUtc"/>). Rows on the slack bus
    /// are dropped (the reference angle is fixed, so a slack-angle row is structurally uninformative).
    /// </summary>
    public static (IReadOnlyList<Measurement> Measurements, DateTime TargetUtc) ToMeasurements(
        GridModel model, IReadOnlyList<PhaseAngleObs> obs, PhaseAngleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(obs);
        PhaseAngleOptions opt = options ?? new PhaseAngleOptions();
        if (opt.SigmaDeg <= 0 || !double.IsFinite(opt.SigmaDeg))
            throw new ArgumentException("PhaseAngleOptions.SigmaDeg must be finite and > 0.", nameof(options));

        var byId = new Dictionary<int, Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) byId[b.Id] = b;
        Bus? slack = model.Buses.FirstOrDefault(b => b.Type == BusType.Slack);

        // GR-530: with no observations and no given target there is no instant: the set used to be dated 1970-01-01,
        // a fabricated stamp that passes the horizon guard trivially. Refused instead.
        if (opt.TargetUtc is null && obs.Count == 0)
            throw new ArgumentException("A phase-angle set with no observations has no instant; give PhaseAngleOptions.TargetUtc or at least one observation.", nameof(obs));
        DateTime target = opt.TargetUtc ?? obs.Max(o => o.Utc);
        bool project = opt.Mode != PhaseAngleReference.Raw;

        // Time-projected angle of each observation at the common instant T (Raw: the value as given).
        double Projected(PhaseAngleObs o)
        {
            if (!project) return o.AngleDeg;
            double dt = (target - o.Utc).TotalSeconds;
            // theta(T) = theta(t) + 360*(f - f0)*dt + 180*rocof*dt^2  (degrees; f0 = NominalHz)
            return o.AngleDeg + 360.0 * (o.FreqHz - opt.NominalHz) * dt + 180.0 * o.RocofHzPerS * dt * dt;
        }

        if (opt.DatumAnchorDeg is double anchorIn)
        {
            if (opt.Mode != PhaseAngleReference.AutoReference)
                throw new ArgumentException(
                    $"PhaseAngleOptions.DatumAnchorDeg applies only to AutoReference; it would be ignored in {opt.Mode} mode.", nameof(options));
            if (!double.IsFinite(anchorIn))
                throw new ArgumentException($"PhaseAngleOptions.DatumAnchorDeg is {anchorIn}; it must be a finite angle.", nameof(options));
        }

        // Re-reference datum + its anchor angle in the slack frame.
        int datumBus = opt.DatumBus
            ?? (slack is not null && obs.Any(o => o.Bus == slack.Id) ? slack.Id : (obs.Count > 0 ? obs[0].Bus : 0));
        bool datumIsSlack = slack is not null && datumBus == slack.Id;
        double datumAngleT = 0, anchorDeg = 0;
        bool skipDatumRow = false;
        if (opt.Mode == PhaseAngleReference.AutoReference && obs.Count > 0)
        {
            PhaseAngleObs? datumObs = obs.FirstOrDefault(o => o.Bus == datumBus);
            if (datumObs is null)
                throw new InvalidOperationException(
                    $"--phaseangle re-referencing needs the datum bus {datumBus} among the observations; supply it or set the datum to a bus that is present.");
            if (!byId.TryGetValue(datumBus, out Bus? datumModel))
                throw new InvalidOperationException($"--phaseangle datum bus {datumBus} does not exist in the model.");
            datumAngleT = Projected(datumObs);
            if (datumIsSlack)
            {
                // The slack's angle IS the estimator's reference frame, so the slack datum anchors exactly.
                if (opt.DatumAnchorDeg is not null)
                    throw new ArgumentException(
                        $"PhaseAngleOptions.DatumAnchorDeg was given but the datum is the slack bus {datumBus}, whose angle is the reference itself.", nameof(options));
                anchorDeg = datumModel.VaDeg;
            }
            else
            {
                // R028: a non-slack datum's anchor used to be the CASE FILE's static VaDeg, shifting every
                // injected angle by (true datum angle - case-file angle) and emitting the datum's own row as a
                // "measurement" whose value was purely that stale prior. Demand the present-state anchor.
                anchorDeg = opt.DatumAnchorDeg ?? throw new InvalidOperationException(
                    $"--phaseangle AutoReference: the datum bus {datumBus} is not the slack, so the re-referenced angles need the " +
                    "datum's PRESENT-STATE angle as an anchor (PhaseAngleOptions.DatumAnchorDeg, e.g. from a solved prior). " +
                    "Anchoring to the case file's static angle would bias every row. Include the slack bus among the " +
                    "observations or supply the anchor.");
                // The datum's own re-referenced value is exactly the anchor - a prior, not an observation.
                skipDatumRow = true;
            }
        }

        var rows = new List<Measurement>(obs.Count);
        foreach (PhaseAngleObs o in obs)
        {
            if (slack is not null && o.Bus == slack.Id) continue;   // slack-angle row: fixed reference, uninformative
            if (skipDatumRow && o.Bus == datumBus) continue;        // datum row = the anchor itself (R028)

            double z = opt.Mode switch
            {
                // AutoReference: the datum difference already removes any common mode, so CommonModeDeg is moot.
                PhaseAngleReference.AutoReference => Projected(o) - datumAngleT + anchorDeg,
                // AlreadyReferenced: subtract the supplied system common-mode (θ_sys) to de-rotate an absolute feed.
                PhaseAngleReference.AlreadyReferenced => Projected(o) - opt.CommonModeDeg,
                _ => o.AngleDeg,   // Raw
            };

            rows.Add(new Measurement(
                MeasurementKind.VaDeg, z, opt.SigmaDeg, Bus: o.Bus,
                Source: $"{opt.Source} f={o.FreqHz.ToString("0.###", CultureInfo.InvariantCulture)}Hz@{o.Utc:o}"));
        }

        return (rows, target);
    }
}
