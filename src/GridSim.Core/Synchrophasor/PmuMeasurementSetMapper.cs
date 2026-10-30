using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;

namespace GridSim.Core.Synchrophasor;

/// <summary>A PMU voltage-phasor channel: which phasor in the PMU's frame is the voltage at which bus, plus
/// the per-unit base and the measurement sigmas.</summary>
/// <param name="PhasorIndex">Index into <see cref="PmuSample.Phasors"/>.</param>
/// <param name="BusId">The bus the voltage phasor is measured at.</param>
/// <param name="BaseVolts">Line-to-neutral voltage base for per-unit conversion.</param>
/// <param name="VmSigmaPu">Sigma of the derived voltage-magnitude measurement (pu).</param>
/// <param name="VaSigmaDeg">Sigma of the derived voltage-angle measurement (degrees).</param>
public sealed record PmuVoltageChannel(int PhasorIndex, int BusId, double BaseVolts, double VmSigmaPu, double VaSigmaDeg);

/// <summary>A PMU current-phasor channel: which phasor is the current in which branch, its base, and sigmas.</summary>
/// <param name="PhasorIndex">Index into <see cref="PmuSample.Phasors"/>.</param>
/// <param name="FromBusId">Branch from-bus id.</param>
/// <param name="ToBusId">Branch to-bus id.</param>
/// <param name="Circuit">Circuit name/ordinal to disambiguate parallels (null if unique).</param>
/// <param name="BaseAmps">Current base for per-unit conversion.</param>
/// <param name="IMagSigmaPu">Sigma of the derived current-magnitude measurement (pu).</param>
/// <param name="IAngSigmaDeg">Sigma of the derived current-angle measurement (degrees).</param>
public sealed record PmuCurrentChannel(
    int PhasorIndex, int FromBusId, int ToBusId, string? Circuit, double BaseAmps, double IMagSigmaPu, double IAngSigmaDeg);

/// <summary>The crosswalk for one PMU: which of its phasors are voltages (at buses) and currents (in branches).</summary>
/// <param name="PmuId">The PMU's id (matches <see cref="PmuSample.PmuId"/>).</param>
/// <param name="Voltages">Voltage channels.</param>
/// <param name="Currents">Current channels.</param>
public sealed record PmuChannelMap(
    int PmuId, IReadOnlyList<PmuVoltageChannel> Voltages, IReadOnlyList<PmuCurrentChannel> Currents);

/// <summary>
/// The named angle-reference channel (R295): one VOLTAGE phasor channel whose angle every other PMU angle is
/// measured against, so that the emitted rows are in the estimator's slack frame rather than the PMUs' UTC frame.
/// </summary>
/// <param name="PmuId">The PMU that carries the reference channel.</param>
/// <param name="PhasorIndex">The reference phasor's index in that PMU's frame. It must be one of that PMU's
/// <see cref="PmuChannelMap.Voltages"/> channels.</param>
/// <param name="AnchorDeg">The slack-frame angle (degrees) of the bus the reference channel measures: 0 when the
/// reference channel is AT the slack bus (the normal choice); otherwise the estimator's current view of that bus's
/// angle (for example the warm-start prior), as in the design note's
/// <c>Va_row = angle_pmu(node) - angle_pmu(datum) + Va_prior(datum_bus)</c>.</param>
public sealed record PmuAngleReference(int PmuId, int PhasorIndex, double AnchorDeg = 0.0);

/// <summary>
/// D-DOC-008 (October 2026): a PMU channel map does not fit the samples it is applied to: a mapped channel names a phasor
/// its PMU did not report (index past the end of the reported phasors, or negative). An <see cref="ArgumentException"/>
/// (the map argument does not fit the superframe argument), deliberately NOT an <see cref="InvalidDataException"/>:
/// the mapper throws <see cref="InvalidDataException"/> when the reference PMU simply did not report at one instant
/// (that instant gives no set, and pmu-ingest counts it), while a map that does not fit the data is an input error to
/// be fixed, never to be counted as a missing reference. (InvalidDataException is sealed, so it could not be a
/// subtype of it either.)
/// </summary>
public sealed class PmuChannelMapMismatchException : ArgumentException
{
    /// <summary>Create the exception with a message naming every channel that does not fit.</summary>
    public PmuChannelMapMismatchException(string message) : base(message) { }
}

/// <summary>
/// Maps an aligned <see cref="PdcSuperframe"/> to a <see cref="MeasurementSet"/> the estimator consumes:
/// each voltage phasor becomes a per-unit <see cref="MeasurementKind.VmPu"/> + <see cref="MeasurementKind.VaDeg"/>
/// pair at its bus, and each current phasor an <see cref="MeasurementKind.IMagPu"/> +
/// <see cref="MeasurementKind.IAngDeg"/> pair on its branch. The superframe's aligned instant becomes the
/// set's <c>TakenUtc</c> and is checked against the horizon guard (invariant #1) - a PMU stream, like every
/// dated ingester, can only ever feed permitted-age or replayed data into the estimator.
///
/// <para><b>Experimental</b> (R062, October 2026). Its production caller is the CLI verb <c>pmu-ingest</c> with
/// <c>--channels</c> / <c>--measurements-dir</c> (D-DOC-107; D-DOC-203 closed): every aligned instant of a recorded capture
/// is mapped here. The decoder fixes it once waited on are done (FORMAT bit order R046, PHUNIT / FNOM scaling R293,
/// multi-PMU DATA frames N061) and the crosswalk has a file format (<see cref="PmuChannelMapFile"/>,
/// <c>gridsim-pmu-channels/1</c>). The sets it writes are read by <c>estimate</c> and, since 1.95.245, by
/// <c>pmu-estimate</c> (the linear estimate, <see cref="Estimation.LinearPmuStateEstimator"/>, through
/// <see cref="Estimation.PmuMeasurementSetAdapter"/>). Still outstanding: a live C37.118 transport (TCP / UDP) feeding the
/// concentrator, and running the linear estimate at PMU rate on a live stream.</para>
///
/// <para><b>Every channel is bounds-checked (D-DOC-008, October 2026).</b> Before any row is emitted, every mapped
/// channel's phasor index is checked against the phasor count its PMU actually reported in this superframe; a channel
/// that does not exist there refuses the whole superframe with a <see cref="PmuChannelMapMismatchException"/> naming the
/// PMU, the channel (bus or branch) and both numbers. Before, only the reference channel's index was checked, so any
/// other channel past the end surfaced as a raw <see cref="IndexOutOfRangeException"/> (for example when a capture's
/// configuration changed mid-stream to fewer phasors).</para>
///
/// <para><b>Angles are re-referenced (R295).</b> IEEE C37.118.1-2011 section 4.2 (and C37.118.2-2011, which carries
/// the values) defines a synchrophasor's angle against a cosine at nominal frequency synchronised to UTC: it is an
/// ABSOLUTE angle that rotates with any frequency deviation, not an angle measured from the estimator's slack bus.
/// The estimator's VaDeg and IAngDeg rows are slack-referenced. So every angle is emitted as its difference from a
/// NAMED reference voltage channel (<see cref="PmuAngleReference"/>), plus that channel's slack-frame anchor:
/// <c>row = angle(channel) - angle(reference) + AnchorDeg</c>. The common UTC-frame offset cancels exactly in the
/// difference. The mapper used to pass the absolute angles straight through, which put rows that rotate at the
/// frequency deviation (about 3.6 degrees per second at 50.01 Hz) into a slack-referenced estimator.</para>
///
/// <para>Fail loud: an angle row is never emitted without a reference. With no reference named, or the named
/// reference channel absent from the superframe (its PMU dropped, or its phasor missing), the map throws instead
/// of emitting unreferenced angles. Magnitude rows need no reference. A superframe that yields no angle rows at all
/// (every mapped PMU dropped) needs none either.</para>
/// </summary>
public static class PmuMeasurementSetMapper
{
    /// <summary>Build a guarded measurement set from an aligned superframe and the per-PMU crosswalk.
    /// <paramref name="angleReference"/> names the voltage channel that every angle is re-referenced against
    /// (R295); it is required whenever any angle row is produced.</summary>
    /// <exception cref="ArgumentException">The named reference is not a mapped voltage channel.</exception>
    /// <exception cref="InvalidOperationException">An angle row would be produced but no reference was named.</exception>
    /// <exception cref="InvalidDataException">The named reference channel is missing from this superframe.</exception>
    /// <exception cref="PmuChannelMapMismatchException">A mapped channel names a phasor its PMU did not report in this
    /// superframe (D-DOC-008); every such channel is named.</exception>
    public static MeasurementSet Map(
        PdcSuperframe frame, IReadOnlyList<PmuChannelMap> maps, double baseMva = 100.0,
        string caseName = "pmu", DateTime? utcNow = null, PmuAngleReference? angleReference = null)
    {
        // Invariant #1: the aligned instant must be historical before any PMU value is admitted.
        ForwardInferenceGuard.RejectFuture(frame.TimeUtc, utcNow, "PMU superframe");

        // The reference must name a mapped VOLTAGE channel: re-referencing against a current, or against a phasor
        // nobody mapped, is a configuration error, reported before any data is looked at.
        if (angleReference is not null && !maps.Any(m => m.PmuId == angleReference.PmuId
                && m.Voltages.Any(v => v.PhasorIndex == angleReference.PhasorIndex)))
            throw new ArgumentException(
                $"The angle reference (PMU {angleReference.PmuId}, phasor {angleReference.PhasorIndex}) is not one of the " +
                "mapped voltage channels; the reference must be a voltage phasor listed in the channel maps.",
                nameof(angleReference));

        // The reference phasor's own UTC-frame angle in this superframe, resolved the first time an angle row
        // needs it (so a superframe with no angle rows needs no reference). Throws, never guesses, when missing.
        double? referenceRad = null;
        double ReferenceAngleRad()
        {
            if (referenceRad is double known) return known;
            if (angleReference is null)
                throw new InvalidOperationException(
                    "PMU angles are UTC-referenced (IEEE C37.118.1-2011 section 4.2), but no angle-reference channel " +
                    "was named, so they cannot be expressed in the estimator's slack frame. Pass a PmuAngleReference " +
                    "naming the voltage channel at the slack bus (or at a bus with a known slack-frame anchor).");
            if (!frame.Samples.TryGetValue(angleReference.PmuId, out PmuSample? refSample))
                throw new InvalidDataException(
                    $"The angle-reference PMU {angleReference.PmuId} did not report at {frame.TimeUtc:O}, so none of " +
                    "this superframe's angles can be referenced to the slack frame. Refusing to emit absolute " +
                    "(UTC-referenced) angles as slack-referenced rows.");
            if (angleReference.PhasorIndex < 0 || angleReference.PhasorIndex >= refSample.Phasors.Length)
                throw new InvalidDataException(
                    $"The angle-reference PMU {angleReference.PmuId} reported {refSample.Phasors.Length} phasor(s) at " +
                    $"{frame.TimeUtc:O}; the reference phasor {angleReference.PhasorIndex} is missing.");
            Phasor refPhasor = refSample.Phasors[angleReference.PhasorIndex];
            if (!double.IsFinite(refPhasor.Real) || !double.IsFinite(refPhasor.Imag))
                throw new InvalidDataException(
                    $"The angle-reference phasor of PMU {angleReference.PmuId} carries no data at {frame.TimeUtc:O} (the " +
                    "missing-data marker), so none of this superframe's angles can be referenced to the slack frame.");
            referenceRad = refPhasor.AngleRad;
            return referenceRad.Value;
        }

        // One phasor's angle as a slack-referenced row value in degrees: the difference from the reference
        // channel, wrapped into (-180, 180] degrees, plus the reference bus's slack-frame anchor. Both angles come
        // from atan2 (each in [-pi, pi]), so their difference is within [-2 pi, 2 pi] and one 2 pi step wraps it.
        double SlackFrameDeg(Phasor ph)
        {
            double d = ph.AngleRad - ReferenceAngleRad();
            if (d > Math.PI) d -= 2.0 * Math.PI;
            else if (d <= -Math.PI) d += 2.0 * Math.PI;
            return d * 180.0 / Math.PI + angleReference!.AnchorDeg;
        }

        // D-DOC-008: every channel of every PMU that reported must exist in what it reported, checked for all of them
        // before any row is built, so a map that does not fit the data refuses the whole superframe, naming each bad
        // channel, instead of failing half-way with a raw IndexOutOfRangeException. (A PMU that did not report is not
        // checked: it gives no rows at this instant.)
        var misfits = new List<string>();
        foreach (PmuChannelMap map in maps)
        {
            if (!frame.Samples.TryGetValue(map.PmuId, out PmuSample? reported)) continue;
            int count = reported.Phasors.Length;
            foreach (PmuVoltageChannel v in map.Voltages)
                if (v.PhasorIndex < 0 || v.PhasorIndex >= count)
                    misfits.Add($"PMU {map.PmuId} reported {count} phasor(s), but its voltage channel at bus {v.BusId} uses phasor {v.PhasorIndex}");
            foreach (PmuCurrentChannel c in map.Currents)
            {
                if (c.PhasorIndex >= 0 && c.PhasorIndex < count) continue;
                string circuit = c.Circuit is null ? "" : $" circuit '{c.Circuit}'";
                misfits.Add($"PMU {map.PmuId} reported {count} phasor(s), but its current channel on branch {c.FromBusId}->{c.ToBusId}{circuit} uses phasor {c.PhasorIndex}");
            }
        }
        if (misfits.Count > 0)
            throw new PmuChannelMapMismatchException(
                $"The PMU channel map does not fit the superframe at {frame.TimeUtc:O}: " + string.Join("; ", misfits) +
                ". Check the map against the configuration (CONFIG frame) these samples were decoded with; no measurement set is built.");

        var measurements = new List<Measurement>();
        int missingChannels = 0;
        foreach (PmuChannelMap map in maps)
        {
            if (!frame.Samples.TryGetValue(map.PmuId, out PmuSample? sample)) continue; // dropped this instant

            foreach (PmuVoltageChannel v in map.Voltages)
            {
                Phasor ph = sample.Phasors[v.PhasorIndex];   // in range: checked above (D-DOC-008)
                // A channel that carries the missing-data marker (decoded as NaN) gives no rows; it is counted.
                if (!double.IsFinite(ph.Real) || !double.IsFinite(ph.Imag)) { missingChannels++; continue; }
                measurements.Add(new Measurement(MeasurementKind.VmPu, ph.Magnitude / v.BaseVolts, v.VmSigmaPu,
                    Bus: v.BusId, Source: "pmu"));
                measurements.Add(new Measurement(MeasurementKind.VaDeg, SlackFrameDeg(ph), v.VaSigmaDeg,
                    Bus: v.BusId, Source: "pmu"));
            }

            foreach (PmuCurrentChannel c in map.Currents)
            {
                Phasor ph = sample.Phasors[c.PhasorIndex];   // in range: checked above (D-DOC-008)
                if (!double.IsFinite(ph.Real) || !double.IsFinite(ph.Imag)) { missingChannels++; continue; }
                measurements.Add(new Measurement(MeasurementKind.IMagPu, ph.Magnitude / c.BaseAmps, c.IMagSigmaPu,
                    From: c.FromBusId, To: c.ToBusId, Circuit: c.Circuit, Source: "pmu"));
                // The current angle is re-referenced against the same VOLTAGE reference: the estimator's IAngDeg
                // model is Va[a] - atan2(Q, P), which lives in the slack frame like VaDeg.
                measurements.Add(new Measurement(MeasurementKind.IAngDeg, SlackFrameDeg(ph), c.IAngSigmaDeg,
                    From: c.FromBusId, To: c.ToBusId, Circuit: c.Circuit, Source: "pmu"));
            }
        }

        return new MeasurementSet
        {
            Case = caseName,
            BaseMva = baseMva,
            TakenUtc = frame.TimeUtc,
            Note = missingChannels == 0
                ? "mapped from C37.118 PDC superframe"
                : $"mapped from C37.118 PDC superframe; left out {missingChannels} channel(s) carrying no data",
            Measurements = measurements,
        };
    }
}
