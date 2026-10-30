using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO.Cim;

/// <summary>
/// N295 (CIM3, 2026-10): one remote voltage control, as the bridge resolved it onto GridModel buses: the machines at
/// <see cref="GeneratorBusIds"/> hold bus <see cref="RegulatedBusId"/> at <see cref="TargetPu"/>.
/// <see cref="CimControlledPowerFlow"/> applies it; a plain solve of the bridged model holds those machines at their q.
/// </summary>
/// <param name="RegulatedNode">The CIM bus (mRID) whose voltage is regulated.</param>
/// <param name="RegulatedBusId">Its GridModel bus id.</param>
/// <param name="TargetPu">The target on the regulated bus's base voltage (RegulatingControl.targetValue kV / base kV).</param>
/// <param name="GeneratorBusIds">The GridModel buses of the machines that regulate it, ascending. N307 (LF3, October
/// 2026; Ruling R1): when there are several (a shared control), the reactive output is shared between their buses in
/// proportion to <see cref="ReactiveShareKeys"/> (each bus's machines' reactive range), the powsybl rule and the PSS/E
/// RMPCT default; it used to be divided by the network (one common per-unit voltage at every machine bus).</param>
/// <param name="Machines">The names of the regulating machines, in the network's generator order.</param>
/// <param name="ReactiveShareKeys">N307: one key per entry of <see cref="GeneratorBusIds"/>, same order: the sum of
/// (maxQ - minQ) MVAr over the control's machines at that bus. When any of them has no finite positive range every key is
/// 1.0 (equal shares, the PSS/E RMPCT default of 100 % each), and the bridge says so. Null (a control built in code
/// without keys) is read as equal shares. Not used for a control with one machine bus (nothing to share).</param>
public sealed record CimRemoteVoltageControl(
    string RegulatedNode, int RegulatedBusId, double TargetPu, IReadOnlyList<int> GeneratorBusIds, IReadOnlyList<string> Machines,
    IReadOnlyList<double>? ReactiveShareKeys = null);

/// <summary>
/// N295 (CIM3, 2026-10): one variable-susceptance voltage control (a StaticVarCompensator; N296 adds a shunt compensator
/// under continuous control), as the bridge resolved it onto GridModel buses. The device is a susceptance B at bus
/// <see cref="BusId"/> between <see cref="BMinMvar"/> and <see cref="BMaxMvar"/>, chosen so that the regulated bus's
/// voltage V (pu) satisfies V = <see cref="TargetPu"/> - <see cref="SlopePu"/> x Q / baseMVA, with Q = B x Vbus^2 its
/// capacitive output (MVAr); at a limit it stays a fixed susceptance (its output then follows V^2, which is what an SVC
/// at its limit does, and what the CGMES 3.0 MicroGrid BE's published SV shows: q = -9.88107 MVAr = 223.658^2 / 5062.5).
/// </summary>
/// <param name="MRID">The device's mRID.</param>
/// <param name="Name">The device's name.</param>
/// <param name="Class">The CIM class ("StaticVarCompensator", or a shunt class under N296).</param>
/// <param name="BusId">The GridModel bus it is connected to.</param>
/// <param name="RegulatedBusId">The GridModel bus whose voltage it regulates.</param>
/// <param name="BMinMvar">The smallest susceptance, as MVAr at 1.0 pu of its bus's base (negative = inductive). For a
/// shunt (N296) relative to the sections the bridged model already holds.</param>
/// <param name="BMaxMvar">The largest susceptance, MVAr at 1.0 pu (positive = capacitive), relative likewise.</param>
/// <param name="InitialBMvar">Where the controlled solve starts (its SSH output at 1.0 pu, within the range).</param>
/// <param name="TargetPu">The voltage target on the regulated bus's base.</param>
/// <param name="SlopePu">The droop: StaticVarCompensator.slope (kV / MVAr) x baseMVA / regulated base kV; 0 = none.</param>
/// <param name="HeldQdMvar">The load-sign q the plain model holds it at (added to its bus's Qd); the controlled solve
/// takes it back out. 0 for a shunt.</param>
public sealed record CimSusceptanceControl(
    string MRID, string Name, string Class, int BusId, int RegulatedBusId, double BMinMvar, double BMaxMvar,
    double InitialBMvar, double TargetPu, double SlopePu, double HeldQdMvar);

/// <summary>
/// N296 (CIM3, 2026-10): one voltage-regulating linear shunt as the bridge resolved it onto GridModel buses, in both
/// forms GridSim can solve: <see cref="Discrete"/>, a <see cref="SwitchedShunt"/> for <see cref="SwitchedShuntPowerFlow"/>
/// (whole sections, relative to the sections the bridged model already holds, inside the band target +/- deadband / 2), and
/// <see cref="Continuous"/>, a susceptance control for <see cref="CimControlledPowerFlow"/> (any value between 0 and
/// maximumSections sections, holding the target exactly). A form that cannot be built is null and <see cref="Basis"/> says
/// why.
/// </summary>
/// <param name="MRID">The shunt's mRID.</param>
/// <param name="Name">The shunt's name.</param>
/// <param name="BusId">Its GridModel bus.</param>
/// <param name="RegulatedBusId">The GridModel bus it regulates.</param>
/// <param name="DataSaysDiscrete">The CGMES RegulatingControl.discrete flag (true when absent).</param>
/// <param name="Discrete">The switched-shunt form; null when it cannot be built.</param>
/// <param name="Continuous">The continuous form; null when it cannot be built.</param>
/// <param name="SectionsInModel">The sections the bridged model holds (Bs); the switched steps are relative to it.</param>
/// <param name="StepMvar">One section, MVAr at 1.0 pu of the bus base.</param>
/// <param name="Basis">How each form was built, or why it was not.</param>
public sealed record CimShuntRegulation(
    string MRID, string Name, int BusId, int RegulatedBusId, bool DataSaysDiscrete, SwitchedShunt? Discrete,
    CimSusceptanceControl? Continuous, double SectionsInModel, double StepMvar, string Basis);

/// <summary>The controls a bridged model carries for <see cref="CimControlledPowerFlow"/> (N295 / N296).</summary>
internal sealed record CimControls(
    IReadOnlyList<CimRemoteVoltageControl> Remote, IReadOnlyList<CimSusceptanceControl> Susceptances,
    IReadOnlyList<CimShuntRegulation> Shunts);

public static partial class CimGridModelBridge
{
    /// <summary>
    /// N295 (CIM3, 2026-10): resolve the network's remote voltage controls and regulating SVCs onto GridModel buses. Every
    /// choice is a note; a control that cannot be applied is said and left out (the plain model holds the device at its
    /// q, as before), and contradictory data is refused. The rules:
    /// <list type="bullet">
    /// <item>A regulated bus already held by a machine's own voltage control (a PV or slack bus) is not regulated a
    /// second time: two controllers of one voltage fight (the controlled solve would have no solution, or many). The later
    /// claimant is said and left at its q, as PSS/E and powsybl give one controller per bus priority.</item>
    /// <item>A machine bus that already holds its own voltage (another machine there controls it locally) cannot also
    /// follow a remote target: said, left out.</item>
    /// <item>Two machines at one bus regulating two different buses, or one bus regulated to two targets, are refused
    /// (one bus has one voltage; the D-DOC-104 rule for local targets).</item>
    /// <item>The regulated bus must be in the solved network (not a de-energised island) and have a base voltage.</item>
    /// </list>
    /// Controls are listed in GridModel bus order of the regulated bus, then the SVCs in network order, so the controlled
    /// solve is the same whatever the document order.
    /// </summary>
    private static CimControls BuildControls(
        CimNetwork net, double baseMva, ICollection<string> log, Dictionary<string, int> nodeToId, int[] genBus,
        BusType[] busType, bool[] controlsVoltage, double[] baseKv, string[] busName)
    {
        // Buses whose voltage is held: every PV and slack bus (a machine's own control, or the slack's reference
        // voltage), then each control accepted below.
        var held = new HashSet<int>();
        for (int i = 0; i < busType.Length; i++)
            if (busType[i] is BusType.Pv or BusType.Slack) held.Add(i + 1);

        // ---- remote voltage control (machines, ExternalNetworkInjection, PowerElectronicsConnection) ----------
        var byRegulated = new SortedDictionary<int, (double TargetPu, SortedSet<int> GenBuses, List<string> Machines, string Node)>();
        var regulatedOfGenBus = new Dictionary<int, int>();
        // N307: the reactive range (maxQ - minQ, MVAr) of each control's machines, summed per machine bus, and whether every
        // machine of the control had a finite positive one (else the shares fall back to equal keys, said below).
        var rangeOf = new Dictionary<(int Rb, int Gb), double>();
        var rangeFinite = new Dictionary<int, bool>();
        for (int k = 0; k < net.Generators.Count; k++)
        {
            CimGenerator g = net.Generators[k];
            if (g.RegulatedNode is null || g.RemoteTargetKv is not double targetKv) continue;
            int gb = genBus[k];
            if (!nodeToId.TryGetValue(g.RegulatedNode, out int rb))
            {
                log.Add($"Remote voltage control of {g.Name}: the bus it regulates ({g.RegulatedNode}) is not in the solved network " +
                        "(a de-energised island, or not in the document); it is held at its q.");
                continue;
            }
            if (controlsVoltage[gb - 1])
            {
                log.Add($"Remote voltage control of {g.Name}: its bus '{busName[gb - 1]}' already holds its own voltage (another machine " +
                        "there controls it locally), so it cannot also follow a remote target; not applied.");
                continue;
            }
            if (held.Contains(rb))
            {
                log.Add($"Remote voltage control of {g.Name}: bus '{busName[rb - 1]}' is already held by a machine's own voltage " +
                        "control; one bus has one controller, so this one is not applied (it is held at its q).");
                continue;
            }
            if (!(baseKv[rb - 1] > 0))
                throw new InvalidDataException(
                    $"CIM generator {g.Name}: its remote voltage target {F(targetKv)} kV cannot be put in per-unit, because the " +
                    $"bus it regulates ('{busName[rb - 1]}') has no base voltage.");
            double pu = targetKv / baseKv[rb - 1];
            if (regulatedOfGenBus.TryGetValue(gb, out int otherRb) && otherRb != rb)
                throw new InvalidDataException(
                    $"CIM bus '{busName[gb - 1]}': its machines regulate two different buses ('{busName[otherRb - 1]}' and " +
                    $"'{busName[rb - 1]}'); one bus's machines hold one voltage.");
            regulatedOfGenBus[gb] = rb;
            // N307: this machine's reactive range, for the sharing keys of a shared control.
            double range = (g.MaxQ ?? double.PositiveInfinity) - (g.MinQ ?? double.NegativeInfinity);
            bool finite = double.IsFinite(range) && range > 0.0;
            rangeFinite[rb] = (!rangeFinite.TryGetValue(rb, out bool soFar) || soFar) && finite;
            rangeOf[(rb, gb)] = (rangeOf.TryGetValue((rb, gb), out double r0) ? r0 : 0.0) + (finite ? range : 0.0);
            if (byRegulated.TryGetValue(rb, out var entry))
            {
                if (entry.TargetPu != pu)
                    throw new InvalidDataException(
                        $"CIM bus '{busName[rb - 1]}': machines regulate it to different targets ({F(entry.TargetPu)} and {F(pu)} pu); " +
                        "one bus has one voltage.");
                entry.GenBuses.Add(gb);
                entry.Machines.Add(g.Name);
            }
            else
            {
                byRegulated[rb] = (pu, new SortedSet<int> { gb }, new List<string> { g.Name }, g.RegulatedNode);
            }
        }
        var remote = new List<CimRemoteVoltageControl>();
        foreach ((int rb, var e) in byRegulated)
        {
            held.Add(rb);
            // N307 (LF3, October 2026; ruled by main under Ruling R1): a shared control (machines at several buses) shares
            // its reactive output in proportion to each bus's machines' reactive range (maxQ - minQ): powsybl's rule for a
            // shared remote control, and the PSS/E RMPCT default (a percentage per machine; with no data each machine
            // 100 %, i.e. equal shares, which is the fallback here when a range is missing or not positive). It used to
            // hold one common per-unit voltage at every machine bus, leaving the split to the network.
            List<int> genBuses = e.GenBuses.ToList();
            bool useRanges = rangeFinite.TryGetValue(rb, out bool allFinite) && allFinite;
            List<double> keys = genBuses.Select(gb => useRanges ? rangeOf[(rb, gb)] : 1.0).ToList();
            remote.Add(new CimRemoteVoltageControl(e.Node, rb, e.TargetPu, genBuses, e.Machines, keys));
            string sharing = genBuses.Count <= 1 ? ""
                : useRanges
                    ? $", sharing their reactive output in proportion to their reactive ranges ({string.Join(" / ", keys.Select(F))} MVAr)"
                    : ", sharing their reactive output equally (a machine there has no finite reactive range)";
            log.Add($"Remote voltage control: bus {rb} '{busName[rb - 1]}' is regulated to {F(e.TargetPu)} pu by {string.Join(", ", e.Machines)} " +
                    $"(at bus(es) {string.Join(", ", e.GenBuses)}{sharing}); " +
                    "CimControlledPowerFlow applies it, a plain solve holds them at their q.");
        }

        // ---- static var compensators --------------------------------------------------------------------------
        var susceptances = new List<CimSusceptanceControl>();
        foreach (CimStaticVarCompensator svc in net.Svcs)
        {
            if (!svc.Regulating || svc.TargetKv is not double targetKv) continue;
            int sb = nodeToId[svc.Node];   // resolved by the plain-model loop already (it refuses an unknown node)
            if (!nodeToId.TryGetValue(svc.RegulatedNode ?? svc.Node, out int rb))
            {
                log.Add($"Static var compensator {svc.Name}: the bus it regulates ({svc.RegulatedNode}) is not in the solved network; " +
                        "it is held at its q.");
                continue;
            }
            if (held.Contains(rb))
            {
                log.Add($"Static var compensator {svc.Name}: bus '{busName[rb - 1]}' is already held by a machine's voltage control; " +
                        "one bus has one controller, so the SVC is held at its q.");
                continue;
            }
            double kv = baseKv[sb - 1] > 0 ? baseKv[sb - 1] : svc.BaseKv;
            if (!(kv > 0) || !(baseKv[rb - 1] > 0))
                throw new InvalidDataException(
                    $"CIM static var compensator {svc.Name}: no base voltage on its bus or on the bus it regulates, so its ratings " +
                    "(ohm) and its target (kV) cannot be put in per-unit.");
            // Susceptance limits as MVAr at 1.0 pu of the bus base: (1 / |X|) siemens x kV^2. A rating not given is a
            // limit of 0 on that side (it cannot go that way).
            double bMax = svc.CapacitiveRatingOhm is double c ? 1.0 / Math.Abs(c) * (kv * kv) : 0.0;
            double bMin = svc.InductiveRatingOhm is double ind ? 0.0 - 1.0 / Math.Abs(ind) * (kv * kv) : 0.0;
            double start = Math.Clamp(0.0 - svc.Q, bMin, bMax);
            // GR-658 (October 2026): an SSH q outside the SVC's own ratings is inconsistent data. Only the controlled
            // solve's STARTING susceptance is moved inside the ratings (the plain model keeps the SVC at its q, and the
            // controlled result is the regulated one), so the import goes on, but the move is now said instead of silent.
            if (start != 0.0 - svc.Q)
                log.Add($"Static var compensator {svc.Name}: its SSH q {F(svc.Q)} MVAr is outside its ratings ({F(bMin)} .. {F(bMax)} MVAr " +
                        $"susceptance at 1.0 pu); the controlled solve starts it at {F(start)} MVAr, inside them.");
            double rkv = baseKv[rb - 1];
            var control = new CimSusceptanceControl(svc.MRID, svc.Name, "StaticVarCompensator", sb, rb, bMin, bMax, start,
                targetKv / rkv, svc.SlopeKvPerMvar * baseMva / rkv, svc.Q);
            susceptances.Add(control);
            held.Add(rb);
            log.Add($"Static var compensator {svc.Name}: regulates bus '{busName[rb - 1]}' to {F(control.TargetPu)} pu with slope " +
                    $"{F(control.SlopePu)} pu (V = target - slope x Q), susceptance {F(bMin)} .. {F(bMax)} MVAr at 1.0 pu; " +
                    "CimControlledPowerFlow applies it.");
        }

        // ---- N296: voltage-regulating linear shunts -------------------------------------------------------------
        // Each regulating linear shunt is offered in both forms (see CimShuntRegulation); CimControlledPowerFlow picks one
        // by its options (by default the form the data asks for: RegulatingControl.discrete). The plain model keeps the
        // shunt at its sections in either case (its Bs), so the switched steps and the continuous susceptance are both
        // RELATIVE to those sections. Not regulated, and said: a bus already held (a machine, an SVC, a remote control, an
        // earlier shunt: one controller per bus), a per-section conductance (it would have to switch with B, which
        // SwitchedShuntPowerFlow does not do), or no base voltage on the regulated bus.
        var shuntRegs = new List<CimShuntRegulation>();
        foreach (CimShunt sh in net.Shunts)
        {
            if (sh.Control is not CimShuntControl c) continue;
            int sb = nodeToId[sh.Node];   // resolved by the plain-model shunt loop already
            if (!nodeToId.TryGetValue(c.RegulatedNode, out int rb))
            {
                log.Add($"Shunt {sh.Name}: the bus it regulates ({c.RegulatedNode}) is not in the solved network; it is held at its sections.");
                continue;
            }
            if (held.Contains(rb))
            {
                log.Add($"Shunt {sh.Name}: bus '{busName[rb - 1]}' is already held (a machine, an SVC or another control); one bus has " +
                        "one controller, so the shunt is held at its sections.");
                continue;
            }
            if (c.GPerSection != 0.0)
            {
                log.Add($"Shunt {sh.Name}: it has a per-section conductance, which would have to switch with its susceptance; its " +
                        "control is not applied (held at its sections).");
                continue;
            }
            if (!(baseKv[rb - 1] > 0))
            {
                log.Add($"Shunt {sh.Name}: the bus it regulates ('{busName[rb - 1]}') has no base voltage, so its target cannot be " +
                        "put in per-unit; held at its sections.");
                continue;
            }
            // One section in MVAr at 1.0 pu, on the same scale as the plain shunt loop used for its Bs.
            double kv = baseKv[sb - 1] > 0 ? baseKv[sb - 1] : sh.BaseKv;
            double scale = kv > 0 ? kv * kv : baseMva;
            double step = c.BPerSection * scale;
            if (step == 0.0)
            {
                log.Add($"Shunt {sh.Name}: a section of zero susceptance cannot regulate; held at its sections.");
                continue;
            }
            double rkv = baseKv[rb - 1];
            double max = c.MaximumSections ?? c.Sections;
            double targetPu = c.TargetKv / rkv;

            // Continuous: any susceptance between 0 and maximumSections sections, relative to the sections held.
            double lowEnd = (0.0 - c.Sections) * step, highEnd = (max - c.Sections) * step;
            var continuous = new CimSusceptanceControl(sh.MRID, sh.Name, sh.Class, sb, rb,
                Math.Min(lowEnd, highEnd), Math.Max(lowEnd, highEnd), 0.0, targetPu, 0.0, 0.0);

            // Discrete: whole sections inside the band target +/- deadband / 2 on the shunt's own bus.
            SwitchedShunt? discrete = null;
            string discreteBasis;
            if (rb != sb)
                discreteBasis = "no discrete form: it regulates another bus, and SwitchedShuntPowerFlow switches a bank on its own bus's voltage";
            else if (c.DeadbandKv is not double db || !(db > 0))
                discreteBasis = "no discrete form: no deadband (a band of zero width cannot be kept by whole sections)";
            else if (c.Sections != Math.Floor(c.Sections) || max != Math.Floor(max))
                discreteBasis = "no discrete form: its sections are not whole numbers";
            else if (!(c.TargetKv - db / 2.0 > 0))
                discreteBasis = "no discrete form: its deadband is wider than twice its target";
            else
            {
                discrete = new SwitchedShunt(sb, step, MinSteps: 0 - (int)c.Sections, MaxSteps: (int)(max - c.Sections), InitialSteps: 0,
                    VminPu: (c.TargetKv - db / 2.0) / rkv, VmaxPu: (c.TargetKv + db / 2.0) / rkv, Name: sh.Name);
                discreteBasis = $"discrete: {F(step)} MVAr per section, {F(c.Sections)} of {F(max)} in service, band " +
                                $"{F(discrete.VminPu)} .. {F(discrete.VmaxPu)} pu";
            }
            string basis = $"{discreteBasis}; continuous: {F(continuous.BMinMvar)} .. {F(continuous.BMaxMvar)} MVAr at 1.0 pu about the " +
                           $"sections held, target {F(targetPu)} pu";
            shuntRegs.Add(new CimShuntRegulation(sh.MRID, sh.Name, sb, rb, c.Discrete, discrete, continuous, c.Sections, step, basis));
            held.Add(rb);
            log.Add($"Shunt {sh.Name}: regulates bus '{busName[rb - 1]}' ({(c.Discrete ? "discrete" : "continuous")} by its data); {basis}.");
        }
        return new CimControls(remote, susceptances, shuntRegs);
    }
}
