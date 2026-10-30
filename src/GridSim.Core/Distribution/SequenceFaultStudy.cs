using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>The result of a single-line-to-ground fault study at a bus.</summary>
/// <param name="FaultBusId">The faulted bus.</param>
/// <param name="FaultCurrentPu">The faulted-phase current magnitude |3 I1| (per-unit).</param>
/// <param name="I0">Zero-sequence current.</param>
/// <param name="I1">Positive-sequence current.</param>
/// <param name="I2">Negative-sequence current.</param>
/// <param name="Z0">Thevenin zero-sequence impedance at the fault bus.</param>
/// <param name="Z1">Thevenin positive-sequence impedance at the fault bus.</param>
/// <param name="Z2">Thevenin negative-sequence impedance at the fault bus.</param>
public sealed record SlgFaultResult(
    int FaultBusId, double FaultCurrentPu, Complex I0, Complex I1, Complex I2, Complex Z0, Complex Z1, Complex Z2);

/// <summary>
/// Sequence-network fault study on a radial three-phase model - the earth-fault analysis a positive-sequence
/// phasor model declines. It derives each line's sequence impedances (z1 = z2 = z_self - z_mutual,
/// z0 = z_self + 2 z_mutual) from its 3x3 phase impedance, accumulates the Thevenin sequence impedances along
/// the radial path from the (infinite-bus) source to the fault, and applies the classic single-line-to-ground
/// connection I0 = I1 = I2 = E1 / (Z0 + Z1 + Z2 + 3 Zf), fault current = |3 I1|.
/// <para>N167 (October 2026): shunt elements are neglected - loads of every kind (constant power, constant current,
/// constant impedance, wye or delta), shunt capacitors and line charging - so the fault current is that of the series
/// path alone, from the given prefault E1. That was always the study's treatment of loads; it now also applies to the
/// N062 shunt elements instead of refusing them. (The matching rule of IEC 60909-0, which neglects line capacitances and
/// non-rotating loads in the positive- and negative-sequence networks, is [VERIFY]: the standard is not in
/// docs/reference, N136.) A closed switch is a zero series impedance. A single- or two-phase branch or a step-voltage
/// regulator on the path to the fault is refused, naming the branch.</para>
/// </summary>
public static class SequenceFaultStudy
{
    /// <summary>Zero-, positive-, negative-sequence impedances of a (transposed-equivalent) line from its
    /// 3x3 phase impedance: the symmetric-component reduction z1 = z2 = z_self - z_mutual, z0 = z_self + 2 z_mutual.</summary>
    public static (Complex Z0, Complex Z1, Complex Z2) LineSequenceImpedances(PhaseImpedance z)
    {
        Complex zSelf = (z[0, 0] + z[1, 1] + z[2, 2]) / 3.0;
        Complex zMutual = (z[0, 1] + z[0, 2] + z[1, 0] + z[1, 2] + z[2, 0] + z[2, 1]) / 6.0;
        Complex z1 = zSelf - zMutual;
        Complex z0 = zSelf + 2.0 * zMutual;
        return (z0, z1, z1);
    }

    /// <summary>Single-line-to-ground fault at <paramref name="faultBusId"/> with fault impedance
    /// <paramref name="faultZ"/> (per-unit), from a prefault positive-sequence voltage <paramref name="prefaultE1"/>
    /// (default 1.0). The source is treated as an infinite bus (zero source impedance).</summary>
    public static SlgFaultResult SingleLineToGround(
        MultiPhaseModel model, int faultBusId, Complex faultZ = default, Complex? prefaultE1 = null)
    {
        RequirePhysicalFaultImpedance(faultZ);
        (Complex z0, Complex z1, Complex z2) = Thevenin(model, faultBusId);
        Complex e1 = prefaultE1 ?? Complex.One;
        Complex zLoop = z0 + z1 + z2 + 3.0 * faultZ;
        RequireFiniteLoop(zLoop, faultBusId, "single-line-to-ground");   // R234: was a silent NaN at the source
        Complex iSeq = e1 / zLoop; // I0 = I1 = I2 for SLG
        Complex faultCurrent = 3.0 * iSeq;
        // The same final check as every other fault type: a zero loop impedance gives an infinite current, refused.
        double currentPu = RequireFiniteCurrent(faultCurrent.Magnitude, faultBusId, "single-line-to-ground");
        return new SlgFaultResult(faultBusId, currentPu, iSeq, iSeq, iSeq, z0, z1, z2);
    }

    /// <summary>Line-to-line fault between two phases at <paramref name="faultBusId"/>. Only the positive and
    /// negative sequence networks are involved (no ground): I1 = E1/(Z1 + Z2 + Zf), I2 = -I1, I0 = 0; the
    /// faulted-phase current magnitude is sqrt(3)|I1|.</summary>
    public static double LineToLineFaultCurrentPu(
        MultiPhaseModel model, int faultBusId, Complex faultZ = default, Complex? prefaultE1 = null)
    {
        RequirePhysicalFaultImpedance(faultZ);
        (Complex _, Complex z1, Complex z2) = Thevenin(model, faultBusId);
        Complex e1 = prefaultE1 ?? Complex.One;
        // D-DOC-116 (October 2026): the same loop-impedance check the single-line-to-ground study has had since R234.
        // A bolted line-to-line fault at the source (or at a bus joined to it only by closed switches) has
        // Z1 + Z2 + Zf = 0, and the division below used to return a non-finite current to a Core caller without a word.
        // The check only reads the loop impedance, so every finite answer is unchanged to the bit.
        Complex zLoop = z1 + z2 + faultZ;
        RequireFiniteLoop(zLoop, faultBusId, "line-to-line");
        Complex i1 = e1 / zLoop;
        return RequireFiniteCurrent(Math.Sqrt(3.0) * i1.Magnitude, faultBusId, "line-to-line");
    }

    /// <summary>All four shunt-fault current magnitudes (per-unit) at a bus, for a bolted (or Zf) fault.</summary>
    /// <param name="ThreePhase">Balanced three-phase fault current.</param>
    /// <param name="SingleLineGround">Single-line-to-ground fault current.</param>
    /// <param name="LineLine">Line-to-line fault current.</param>
    /// <param name="LineLineGround">Double-line-to-ground fault current: the GROUND (neutral) current |3 I0|, not the
    /// faulted-phase current the other three fields give. Bug hunt 2026-10-29, finding 66 (decided, documented): the
    /// record mixes the two definitions on purpose (the ground current is what earth-fault protection sees), but a caller
    /// sizing phase protection from a double-line-to-ground fault needs the larger phase current |a^2 I1 + a I2 + I0|,
    /// which this field is not.</param>
    public sealed record FaultSummary(double ThreePhase, double SingleLineGround, double LineLine, double LineLineGround);

    /// <summary>Compute every supported fault type at <paramref name="faultBusId"/> in one call.</summary>
    public static FaultSummary AllFaults(MultiPhaseModel model, int faultBusId, Complex faultZ = default)
        => new(
            ThreePhaseFaultCurrentPu(model, faultBusId, faultZ),
            SingleLineToGround(model, faultBusId, faultZ).FaultCurrentPu,
            LineToLineFaultCurrentPu(model, faultBusId, faultZ),
            LineToLineGroundFaultCurrentPu(model, faultBusId, faultZ));

    /// <summary>Balanced three-phase fault at <paramref name="faultBusId"/>: only the positive sequence is
    /// involved, I_fault = E1 / (Z1 + Zf).</summary>
    public static double ThreePhaseFaultCurrentPu(
        MultiPhaseModel model, int faultBusId, Complex faultZ = default, Complex? prefaultE1 = null)
    {
        RequirePhysicalFaultImpedance(faultZ);
        (Complex _, Complex z1, Complex _) = Thevenin(model, faultBusId);
        Complex e1 = prefaultE1 ?? Complex.One;
        // D-DOC-116 (October 2026): refuse a zero loop impedance (Z1 + Zf = 0: a bolted fault at the source) instead of
        // returning Infinity or NaN. Read-only check: a finite answer is unchanged to the bit.
        Complex zLoop = z1 + faultZ;
        RequireFiniteLoop(zLoop, faultBusId, "three-phase");
        return RequireFiniteCurrent((e1 / zLoop).Magnitude, faultBusId, "three-phase");
    }

    /// <summary>Double-line-to-ground fault at <paramref name="faultBusId"/>: two phases to ground through
    /// <paramref name="groundZ"/>. All three sequence networks connect (Z2 in parallel with Z0+3Zg, in series
    /// with Z1); returns the ground fault current magnitude |3 I0| (the neutral current, not the faulted-phase current;
    /// see <see cref="FaultSummary.LineLineGround"/>).</summary>
    public static double LineToLineGroundFaultCurrentPu(
        MultiPhaseModel model, int faultBusId, Complex groundZ = default, Complex? prefaultE1 = null)
    {
        RequirePhysicalFaultImpedance(groundZ);
        (Complex z0, Complex z1, Complex z2) = Thevenin(model, faultBusId);
        Complex e1 = prefaultE1 ?? Complex.One;
        Complex zg = z0 + 3.0 * groundZ;
        // D-DOC-116 (October 2026): this study divides twice, by Z2 + Zg (the parallel combination) and by Z1 + Z2 || Zg
        // (the loop). A bolted fault at the source makes both zero (0 / 0 = NaN), so both are checked by name before
        // they are used, as the single-line-to-ground study checks its loop. The checks only read the sums, so every
        // finite answer is unchanged to the bit.
        Complex zParallelSum = z2 + zg;
        RequireFiniteLoop(zParallelSum, faultBusId, "double-line-to-ground (Z2 + Z0 + 3 Zg)");
        Complex zPar = z2 * zg / zParallelSum;
        Complex zLoop = z1 + zPar;
        RequireFiniteLoop(zLoop, faultBusId, "double-line-to-ground");
        Complex i1 = e1 / zLoop;
        Complex i0 = -i1 * z2 / zParallelSum;
        return RequireFiniteCurrent((3.0 * i0).Magnitude, faultBusId, "double-line-to-ground");
    }

    /// <summary>Thevenin sequence impedances (Z0, Z1, Z2) at a bus: the sum of the per-line sequence impedances
    /// along the radial path from the (infinite-bus) source to the fault bus.</summary>
    public static (Complex Z0, Complex Z1, Complex Z2) Thevenin(MultiPhaseModel model, int faultBusId)
    {
        // N167 (October 2026): this used to refuse every model with an N062 element (RequireOnlyV1Elements). The study is
        // a series-path Thevenin from an infinite source, so what it can model is decided by the branches ON THE PATH to
        // the fault (checked below, by name), not by the rest of the feeder: shunt capacitors, line charging and loads
        // of every kind are shunt elements, neglected exactly as the constant-power loads always were; a closed switch
        // adds zero series impedance. Only a single- or two-phase branch (no symmetrical components) or a step-voltage
        // regulator (an off-nominal ratio the sequence sum does not hold) on the path is refused.
        int n = model.Buses.Count;
        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idToIndex[model.Buses[i].Id] = i;
        if (!idToIndex.TryGetValue(faultBusId, out int faultIdx))
            throw new ArgumentException($"fault bus {faultBusId} is not in the model.");
        int source = -1;
        for (int i = 0; i < n; i++) if (model.Buses[i].IsSource) { source = i; break; }

        // Radial tree (BFS from source) so we can walk the unique path to the fault bus.
        var parentBus = new int[n];
        var parentBranch = new int[n];
        for (int i = 0; i < n; i++) { parentBus[i] = -1; parentBranch[i] = -1; }
        var adjacency = new List<(int Other, int Branch)>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<(int, int)>();
        for (int b = 0; b < model.Branches.Count; b++)
        {
            PhaseBranch br = model.Branches[b];
            if (idToIndex.TryGetValue(br.From, out int f) && idToIndex.TryGetValue(br.To, out int t))
            {
                adjacency[f].Add((t, b));
                adjacency[t].Add((f, b));
            }
        }
        var visited = new bool[n];
        var queue = new Queue<int>();
        queue.Enqueue(source);
        visited[source] = true;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach ((int other, int branch) in adjacency[u])
            {
                if (visited[other]) continue;
                visited[other] = true;
                parentBus[other] = u;
                parentBranch[other] = branch;
                queue.Enqueue(other);
            }
        }
        if (!visited[faultIdx]) throw new ArgumentException($"fault bus {faultBusId} is not connected to the source.");

        // The accumulation below sums impedances along the SINGLE spanning-tree path, so any parallel
        // cable or mesh tie in the source's component is silently dropped and the Thevenin impedance
        // (and every fault current derived from it) is overstated/understated with no warning. A radial
        // component has exactly (buses - 1) branches; refuse a meshed one rather than answer wrongly.
        int visitedCount = 0;
        for (int i = 0; i < n; i++) if (visited[i]) visitedCount++;
        int branchesAmongVisited = 0;
        for (int b = 0; b < model.Branches.Count; b++)
        {
            PhaseBranch br = model.Branches[b];
            if (idToIndex.TryGetValue(br.From, out int f) && idToIndex.TryGetValue(br.To, out int t)
                && visited[f] && visited[t]) branchesAmongVisited++;
        }
        if (branchesAmongVisited > visitedCount - 1)
            throw new ArgumentException(
                $"SequenceFaultStudy requires a radial network; the source component has {branchesAmongVisited} " +
                $"branches among {visitedCount} buses (parallel/loop branches present). Thevenin accumulation " +
                "along one tree path would misstate the fault current.");

        // Accumulate Thevenin sequence impedances along the radial path source -> fault bus.
        // N167: first refuse, by name, a path branch the sequence sum cannot hold (nothing is accumulated before this, so a
        // refused model never produces a number).
        for (int bus = faultIdx; bus != source; bus = parentBus[bus])
        {
            PhaseBranch br = model.Branches[parentBranch[bus]];
            if (br.Phases != Phase.Abc)
                throw new ArgumentException(
                    $"the sequence fault study cannot model the path to bus {faultBusId}: branch {br.From}-{br.To} carries phase(s) " +
                    $"{PhaseIndex.Name(br.Phases)} only, and a single- or two-phase branch has no symmetrical-component (sequence) " +
                    "impedances. It models three-phase branches on the path; shunt capacitors, line charging and loads anywhere are neglected.");
            if (br.IsTransformerElement)
                throw new ArgumentException(
                    $"the sequence fault study cannot model the path to bus {faultBusId}: branch {br.From}-{br.To} is a {br.TransformerName} " +
                    "transformer (a phase shift or a zero-sequence break the series sum of sequence impedances does not hold). Use the three-phase sweep.");
            if (br.RegulatorRatio is not null)
                throw new ArgumentException(
                    $"the sequence fault study cannot model the path to bus {faultBusId}: branch {br.From}-{br.To} has a step-voltage " +
                    "regulator, whose off-nominal (per-phase) ratio the series sum of sequence impedances does not hold. Use the three-phase sweep.");
        }

        Complex z0 = Complex.Zero, z1 = Complex.Zero, z2 = Complex.Zero;
        for (int bus = faultIdx; bus != source; bus = parentBus[bus])
        {
            (Complex b0, Complex b1, Complex b2) = LineSequenceImpedances(model.Branches[parentBranch[bus]].Z);
            z0 += b0; z1 += b1; z2 += b2;
        }

        return (z0, z1, z2);
    }

    /// <summary>
    /// The fault loop impedance must be non-zero: the source is an infinite bus (zero source impedance), so a
    /// bolted fault AT the source bus has no finite current in this model. It used to divide by zero and return
    /// NaN currents silently (R234); now it fails loudly naming the bus.
    /// </summary>
    // GR-594: a fault impedance must be finite with a resistance of 0 or above; a negative resistance is an active
    // source, not a fault, and used to be accepted.
    private static void RequirePhysicalFaultImpedance(Complex z)
    {
        if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary) || z.Real < 0)
            throw new ArgumentOutOfRangeException(nameof(z), z, "A fault impedance must be finite with a resistance of 0 or above.");
    }

    private static void RequireFiniteLoop(Complex zLoop, int faultBusId, string kind)
    {
        if (zLoop == Complex.Zero || !double.IsFinite(zLoop.Real) || !double.IsFinite(zLoop.Imaginary))
            throw new ArgumentException(
                $"A bolted {kind} fault at bus {faultBusId} has zero loop impedance: the source is modelled as an infinite " +
                "bus, so a fault at the source has no finite current. Fault a downstream bus, or give the fault a non-zero impedance.",
                nameof(faultBusId));
    }

    /// <summary>
    /// D-DOC-116 (October 2026): the last line of defence for every fault type. A loop impedance can be non-zero and
    /// finite and still give a non-finite current (a prefault E1 that is not finite, or a sum that overflows), and a
    /// Core caller must never receive Infinity or NaN as a fault current. Returns <paramref name="currentPu"/> unchanged
    /// when it is finite, so a valid answer is untouched to the bit.
    /// </summary>
    private static double RequireFiniteCurrent(double currentPu, int faultBusId, string kind)
    {
        if (!double.IsFinite(currentPu))
            throw new ArgumentException(
                $"The {kind} fault current at bus {faultBusId} is not finite ({currentPu.ToString(System.Globalization.CultureInfo.InvariantCulture)}); " +
                "check the prefault voltage and the fault impedance.", nameof(faultBusId));
        return currentPu;
    }
}
