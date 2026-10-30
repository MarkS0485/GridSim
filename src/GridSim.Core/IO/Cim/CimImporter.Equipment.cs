namespace GridSim.Core.IO.Cim;

/// <summary>
/// N286 (CIM2, 2026-10): the equipment the CIM importer reads besides lines, transformers, loads and synchronous
/// machines, all needed to reproduce the published solved state (SV) of the ENTSO-E CGMES conformity models:
/// <list type="bullet">
/// <item>shunt compensators (LinearShuntCompensator, NonlinearShuntCompensator) and EquivalentShunt, with their
/// sections in service from the SSH / SV / EQ profiles (<see cref="CimShunt"/>, mapped by the bridge to the bus shunt
/// Gs / Bs on the system base);</item>
/// <item>ExternalNetworkInjection and EquivalentInjection: generators, regulating or not, as powsybl's CGMES import
/// maps them;</item>
/// <item>CGMES 3.0 PowerElectronicsConnection (wind, photovoltaic and battery units behind a converter): generators
/// flagged converter-interfaced plant;</item>
/// <item>AsynchronousMachine: a fixed P / Q load (in <see cref="CimImporter"/>'s load loop).</item>
/// </list>
/// Before N286 each of these was listed as "conducting equipment of a class the importer does not represent" and left
/// out, so a model carrying them could not reproduce its own solved voltages. What cannot be mapped (an abstract class,
/// a unit kind GridSim has no word for, contradictory data) is refused by name, never guessed.
/// </summary>
public static partial class CimImporter
{
    /// <summary>
    /// The voltage control of a regulating conducting equipment (a SynchronousMachine since D-DOC-104; also an
    /// ExternalNetworkInjection or a PowerElectronicsConnection since N286): <c>RegulatingCondEq.controlEnabled</c>
    /// (SSH) false = it does not regulate; otherwise its RegulatingControl decides: a voltage-mode, enabled control at
    /// the equipment's own node gives the set-point (targetValue, kV). Remote regulation (the control's terminal on
    /// another node) is not represented by GridSim's PV bus, so such equipment is held at its SSH q instead and
    /// listed. Returns the voltage-control flag (null = the document says nothing) and the target (kV), and adds the
    /// notes. N286 moved this code out of the machine reader unchanged; the same notes in the same order. N287: the
    /// mode and unit multiplier are read in either enumeration form (an rdf:resource URI, or element text such as
    /// "RegulatingControlModeKind.voltage" or "voltage"); a mode ending in ".voltage" reads exactly as before.
    /// </summary>
    /// <remarks>N295 (CIM3, 2026-10): remote voltage control. A voltage control whose terminal is on another bus used to be
    /// listed and the equipment held at its q, with nothing kept. Now the regulated bus and the target are returned too
    /// (<see cref="CimGenerator.RegulatedNode"/>, <see cref="CimGenerator.RemoteTargetKv"/>), checked exactly as a local
    /// target is (kV multiplier, positive): a plain solve still holds the equipment at its q (the flag stays false, so the
    /// slack rule and every existing number are unchanged), and <see cref="CimControlledPowerFlow"/> regulates the remote
    /// bus. A remote control with no target, or on an unconnected terminal, is listed and holds q as before.</remarks>
    private static (bool? VoltageControl, double? TargetKv, string? RegulatedNode, double? RemoteTargetKv) ReadVoltageControl(
        CimObject o, string node, double qGen, Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode,
        List<string> notes)
    {
        bool? controlEnabled = o.Boolean("RegulatingCondEq.controlEnabled");
        bool? voltageControl = controlEnabled;
        double? targetKv = null;
        string? remoteNode = null;
        double? remoteKv = null;
        if (controlEnabled == false)
        {
            notes.Add($"{o.Describe}: voltage control is disabled (RegulatingCondEq.controlEnabled = false); it is held at " +
                      $"its q = {F(qGen)} MVAr (generator sense).");
        }
        else if (o.Resource("RegulatingCondEq.RegulatingControl") is string rcRef)
        {
            if (!byId.TryGetValue(rcRef, out CimObject? rc))
            {
                notes.Add($"{o.Describe} names RegulatingControl {rcRef}, which is not in this document; it has no voltage target.");
            }
            else
            {
                // N287 (CIM2): the mode and the unit multiplier may be written as element text as well as rdf:resource.
                string mode = rc.RawEnum("RegulatingControl.mode")
                    ?? throw new InvalidDataException($"CIM {rc.Describe} has no RegulatingControl.mode.");
                bool? enabled = rc.Boolean("RegulatingControl.enabled");
                string? rcTerminal = rc.Resource("RegulatingControl.Terminal");
                string? regulatedNode = rcTerminal is null ? node : terminalNode.GetValueOrDefault(rcTerminal);
                if (EnumLeaf(mode) != "voltage")
                {
                    voltageControl = false;
                    notes.Add($"{o.Describe}: its RegulatingControl mode is {mode}; only voltage control is represented, " +
                              $"so it is held at its q = {F(qGen)} MVAr.");
                }
                else if (enabled == false)
                {
                    voltageControl = false;
                    notes.Add($"{o.Describe}: its RegulatingControl is disabled (RegulatingControl.enabled = false); it is " +
                              $"held at its q = {F(qGen)} MVAr.");
                }
                else if (!string.Equals(regulatedNode, node, StringComparison.Ordinal))
                {
                    voltageControl = false;
                    double? remote = regulatedNode is null ? null : VoltageTarget(rc);
                    if (regulatedNode is not null && remote is double kv)
                    {
                        // N295 (CIM3): kept, so the controlled solve can regulate the remote bus.
                        remoteNode = regulatedNode;
                        remoteKv = kv;
                        notes.Add($"{o.Describe} regulates the voltage at {regulatedNode} ({F(kv)} kV), not at its own node {node} " +
                                  "(remote voltage control). A GridSim PV bus holds the voltage of its own bus, so a plain solve " +
                                  $"holds it at its q = {F(qGen)} MVAr; CimControlledPowerFlow regulates the remote bus.");
                    }
                    else
                    {
                        notes.Add($"{o.Describe} regulates the voltage at {(regulatedNode ?? "an unconnected terminal")}, not at " +
                                  $"its own node {node}; {(regulatedNode is null ? "" : "its control gives no target, so ")}remote voltage " +
                                  $"control cannot be applied and it is held at its q = {F(qGen)} MVAr.");
                    }
                }
                else
                {
                    targetKv = VoltageTarget(rc);
                    voltageControl = true;
                }
            }
        }
        return (voltageControl, targetKv, remoteNode, remoteKv);
    }

    /// <summary>A voltage RegulatingControl's target (kV): <c>RegulatingControl.targetValue</c>, read in kV only (a
    /// targetValueUnitMultiplier other than k is refused) and positive (refused otherwise); null when absent. The checks
    /// D-DOC-104 applied to a local target, shared since N295 with the remote one.</summary>
    private static double? VoltageTarget(CimObject rc)
    {
        string? multiplier = rc.RawEnum("RegulatingControl.targetValueUnitMultiplier");
        if (multiplier is not null && EnumLeaf(multiplier) != "k")
            throw new InvalidDataException(
                $"CIM {rc.Describe}: targetValueUnitMultiplier is {multiplier}; a voltage target is read in kV " +
                "(UnitMultiplier.k) only.");
        double? targetKv = rc.Number("RegulatingControl.targetValue");
        if (targetKv is <= 0)
            throw new InvalidDataException(
                $"CIM {rc.Describe}: RegulatingControl.targetValue = {F(targetKv.GetValueOrDefault())} kV is not a voltage.");
        return targetKv;
    }

    /// <summary>A pair of optional limits read from <paramref name="o"/>; refused when the minimum is above the maximum.</summary>
    private static (double? Min, double? Max) Limits(CimObject o, string minProp, string maxProp, string unit)
    {
        double? min = o.Number(minProp), max = o.Number(maxProp);
        if (min is not null && max is not null && min > max)
            throw new InvalidDataException(
                $"CIM {o.Describe}: {minProp} = {F(min.Value)} is above {maxProp} = {F(max.Value)} {unit}.");
        return (min, max);
    }

    /// <summary>
    /// N286 (CIM2, 2026-10): read one ExternalNetworkInjection, EquivalentInjection or PowerElectronicsConnection
    /// (already known to be connected at <paramref name="node"/>) as a generator. The mappings, and why:
    /// <list type="bullet">
    /// <item><b>Sign.</b> The SSH <c>p</c> / <c>q</c> of all three are in the LOAD sign convention (CGMES 2.4.15 and
    /// 3.0 SSH, on each class: "Load sign convention is used, i.e. positive sign means flow out from a node"), so both
    /// are negated into the generator sense exactly as a machine's are (R267), '0.0 - x' so a zero stays +0.0. powsybl
    /// does the same (targetP = -p, targetQ = -q). An absent value is 0 (an EQ-only document has no SSH).</item>
    /// <item><b>ExternalNetworkInjection</b> (the equivalent of a neighbouring network): minP / maxP, minQ / maxQ and
    /// referencePriority as written (powsybl takes ENI.minP / maxP as the generator's limits unchanged); voltage
    /// control through its RegulatingControl exactly as a machine's. Without a RegulatingControl it does NOT
    /// regulate (powsybl: no regulating control, no voltage regulation), whereas a machine that says nothing is
    /// read as regulating (the D-DOC-104 rule kept for older documents); said in a note.</item>
    /// <item><b>EquivalentInjection</b> (an equivalent of an external network or a boundary flow): it regulates the
    /// voltage only when <c>regulationCapability</c> (EQ) AND <c>regulationStatus</c> (SSH) are true, at
    /// <c>regulationTarget</c> (kV), as powsybl (regulating = regulationCapability and a valid target, when
    /// regulationStatus is on); otherwise it is held at its q. minP / maxP / minQ / maxQ as written. Its r / x (the
    /// equivalent's internal impedance, a short-circuit quantity) are not used by a power flow, as in powsybl. An
    /// EquivalentInjection on a boundary node is a generator at that node (powsybl's convertBoundary = true view;
    /// with the boundary node as a bus this is the same network as powsybl's boundary line).</item>
    /// <item><b>PowerElectronicsConnection</b> (CGMES 3.0: the converter of PV, wind or battery units; powsybl-core has
    /// no conversion for it, so the CGMES 3.0 text and PowerFactory's static-generator export are the benchmark):
    /// minQ / maxQ and ratedS from the connection; minP / maxP the sums of its units' PowerElectronicsUnit.minP /
    /// maxP (when every unit gives them); Type "solar" (PhotoVoltaicUnit), "wind" (PowerElectronicsWindUnit) or
    /// "battery" (BatteryUnit) when its units are all of one kind, else "" (listed); always converter plant
    /// (<see cref="CimGenerator.ConverterPlant"/>). Voltage control as an ExternalNetworkInjection's. A unit of
    /// another class (an abstract PowerElectronicsUnit, a CGMES extension) is refused by name: what it is cannot be
    /// mapped.</item>
    /// </list>
    /// </summary>
    private static CimGenerator ReadInjection(
        CimObject o, string node, Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode,
        ILookup<string, CimObject> unitsOf, ILookup<string, CimObject> curvePointsOf, List<string> notes)
    {
        string cls = o.Class;
        double pGen = 0.0 - (o.Number(cls + ".p") ?? 0.0);   // load sign -> generator sign
        double qGen = 0.0 - (o.Number(cls + ".q") ?? 0.0);
        bool? voltageControl;
        double? targetKv;
        string? regulatedNode = null;   // N295: a remote regulated bus (ExternalNetworkInjection, PowerElectronicsConnection)
        double? remoteKv = null;
        double? minP, maxP, minQ, maxQ, ratedS = null;
        int priority = 0;
        string type = "";
        bool converter = false;
        switch (cls)
        {
            case "ExternalNetworkInjection":
            {
                (minP, maxP) = Limits(o, "ExternalNetworkInjection.minP", "ExternalNetworkInjection.maxP", "MW");
                (minQ, maxQ) = Limits(o, "ExternalNetworkInjection.minQ", "ExternalNetworkInjection.maxQ", "MVAr");
                priority = o.Integer("ExternalNetworkInjection.referencePriority") ?? 0;
                if (priority < 0)
                    throw new InvalidDataException($"CIM {o.Describe}: ExternalNetworkInjection.referencePriority = {priority} is negative.");
                (voltageControl, targetKv, regulatedNode, remoteKv) = InjectionVoltageControl(o, node, qGen, byId, terminalNode, notes);
                break;
            }
            case "EquivalentInjection":
            {
                (minP, maxP) = Limits(o, "EquivalentInjection.minP", "EquivalentInjection.maxP", "MW");
                (minQ, maxQ) = Limits(o, "EquivalentInjection.minQ", "EquivalentInjection.maxQ", "MVAr");
                bool capable = o.Boolean("EquivalentInjection.regulationCapability") == true;
                bool status = o.Boolean("EquivalentInjection.regulationStatus") == true;
                targetKv = null;
                voltageControl = false;
                if (capable && status)
                {
                    targetKv = o.Number("EquivalentInjection.regulationTarget");
                    if (targetKv is not > 0)
                        throw new InvalidDataException(
                            $"CIM {o.Describe}: it regulates its voltage (regulationCapability and regulationStatus true) but " +
                            $"EquivalentInjection.regulationTarget is {(targetKv is null ? "missing" : F(targetKv.Value) + " kV")}; " +
                            "a voltage target must be a positive number of kV.");
                    voltageControl = true;
                }
                else if (status)
                {
                    notes.Add($"{o.Describe}: regulationStatus is true but regulationCapability is not; it cannot regulate, so it " +
                              $"is held at its q = {F(qGen)} MVAr (generator sense).");
                }
                // N306 (LF3, October 2026): the injection's ReactiveCapabilityCurve, evaluated at its active power by the
                // same rule as a machine's InitialReactiveCapabilityCurve (CapabilityAt, N295: the curve's own style, its
                // end values held outside its points, the curve winning over minQ / maxQ, as powsybl uses the curve when
                // the injection has one). It used to be listed as not represented, the limits coming from minQ / maxQ only.
                if (o.Resource("EquivalentInjection.ReactiveCapabilityCurve") is string curveRef)
                {
                    if (!byId.TryGetValue(curveRef, out CimObject? curve))
                    {
                        notes.Add($"{o.Describe} names reactive capability curve {curveRef}, which is not in this document; the " +
                                  "reactive limits come from EquivalentInjection.minQ / maxQ.");
                    }
                    else
                    {
                        (double cMin, double cMax) = CapabilityAt(curve, curvePointsOf[curve.Mrid], pGen);
                        string written = minQ is null && maxQ is null
                            ? "."
                            : $" instead of EquivalentInjection.minQ / maxQ ({(minQ is null ? "-" : F(minQ.Value))} / " +
                              $"{(maxQ is null ? "-" : F(maxQ.Value))}): the curve is the injection's capability at its operating point.";
                        notes.Add($"{o.Describe}: its reactive capability curve {curve.Describe} at its P = {F(pGen)} MW gives " +
                                  $"{F(cMin)} .. {F(cMax)} MVAr, used as its reactive limits{written}");
                        minQ = cMin;
                        maxQ = cMax;
                    }
                }
                break;
            }
            default: // PowerElectronicsConnection (CGMES 3.0)
            {
                (minQ, maxQ) = Limits(o, "PowerElectronicsConnection.minQ", "PowerElectronicsConnection.maxQ", "MVAr");
                ratedS = o.Number("PowerElectronicsConnection.ratedS");
                if (ratedS is <= 0)
                    throw new InvalidDataException(
                        $"CIM {o.Describe}: PowerElectronicsConnection.ratedS = {F(ratedS.GetValueOrDefault())} MVA is not positive.");
                (voltageControl, targetKv, regulatedNode, remoteKv) = InjectionVoltageControl(o, node, qGen, byId, terminalNode, notes);
                converter = true;
                List<CimObject> units = unitsOf[o.Mrid].OrderBy(u => u.Mrid, StringComparer.Ordinal).ToList();
                var kinds = new SortedSet<string>(StringComparer.Ordinal);
                double sumMin = 0.0, sumMax = 0.0;
                bool allMin = units.Count > 0, allMax = units.Count > 0;
                foreach (CimObject u in units)
                {
                    kinds.Add(u.Class switch
                    {
                        "PhotoVoltaicUnit" => "solar",
                        "PowerElectronicsWindUnit" => "wind",
                        "BatteryUnit" => "battery",
                        _ => throw new InvalidDataException(
                            $"CIM {u.Describe}: a power electronics unit of class {u.Class} on {o.Describe} cannot be mapped; " +
                            "the importer reads PhotoVoltaicUnit, PowerElectronicsWindUnit and BatteryUnit."),
                    });
                    (double? uMin, double? uMax) = Limits(u, "PowerElectronicsUnit.minP", "PowerElectronicsUnit.maxP", "MW");
                    if (uMin is double a) sumMin += a; else allMin = false;
                    if (uMax is double b) sumMax += b; else allMax = false;
                }
                minP = allMin ? sumMin : null;
                maxP = allMax ? sumMax : null;
                if (kinds.Count == 1)
                {
                    type = kinds.Min!;
                }
                else
                {
                    notes.Add(kinds.Count == 0
                        ? $"{o.Describe} names no PowerElectronicsUnit; it is converter plant of no stated kind (type left empty)."
                        : $"{o.Describe} has units of several kinds ({string.Join(", ", kinds)}); it is converter plant, with its " +
                          "type left empty.");
                }
                break;
            }
        }
        return new CimGenerator(o.Mrid, o.Name, node, pGen, qGen,
            TargetKv: targetKv, VoltageControl: voltageControl, MinQ: minQ, MaxQ: maxQ, MinP: minP, MaxP: maxP,
            RatedSMva: ratedS, H: null, ReferencePriority: priority, Type: type, Class: cls, ConverterPlant: converter,
            RegulatedNode: regulatedNode, RemoteTargetKv: remoteKv);
    }

    /// <summary>
    /// N286: the voltage control of an ExternalNetworkInjection or a PowerElectronicsConnection: the machine rule
    /// (<see cref="ReadVoltageControl"/>), except that equipment with NO RegulatingControl does not regulate (there is
    /// no control to follow; powsybl maps it without voltage regulation). A machine that says nothing keeps the older
    /// D-DOC-104 reading (regulating), which this does not change.
    /// </summary>
    private static (bool? VoltageControl, double? TargetKv, string? RegulatedNode, double? RemoteTargetKv) InjectionVoltageControl(
        CimObject o, string node, double qGen, Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode,
        List<string> notes)
    {
        if (o.Boolean("RegulatingCondEq.controlEnabled") != false && o.Resource("RegulatingCondEq.RegulatingControl") is null)
        {
            notes.Add($"{o.Describe} has no RegulatingControl, so it does not regulate a voltage; it is held at its " +
                      $"q = {F(qGen)} MVAr (generator sense).");
            return (false, null, null, null);
        }
        return ReadVoltageControl(o, node, qGen, byId, terminalNode, notes);
    }

    /// <summary>
    /// N286 (CIM2, 2026-10): read every shunt (LinearShuntCompensator, NonlinearShuntCompensator, EquivalentShunt) in
    /// document order, with its total admittance in service. The rules, and why:
    /// <list type="bullet">
    /// <item><b>Sections in service.</b> By <see cref="CimImportOptions.ShuntSections"/>: the SSH
    /// <c>ShuntCompensator.sections</c> (this case), and the SV <c>SvShuntCompensatorSections.sections</c> (the published
    /// solve) when there is no SSH value, or always with <see cref="CimTapPositions.StateVariables"/>; a difference
    /// between the two is listed. With neither, the EQ <c>ShuntCompensator.normalSections</c> is used and listed; with
    /// none of the three the state is unknown and refused. powsybl reads the same profiles (SSH sections, else normal
    /// sections; SV as the solved count). Negative sections, or more than <c>maximumSections</c>, are refused.</item>
    /// <item><b>Linear</b>: B = bPerSection x sections, G = gPerSection x sections (CGMES 2.4.15 / 3.0 EQ:
    /// "positive sequence shunt (charging) susceptance per section"; continuous, non-integer sections are allowed,
    /// as the SSH text says). An absent gPerSection is 0. A shunt with no bPerSection (or a non-linear one with no
    /// point) gives no admittance at all: it is left out and listed by name, as every shunt was before N286 (the
    /// D-DOC-099 listing); its sections are then not needed.</item>
    /// <item><b>Non-linear</b>: the sum of the b (and g) of its NonlinearShuntCompensatorPoints numbered 1 up to the
    /// sections in service, as powsybl (each point is the susceptance "per section" of that section). The sections must
    /// be a whole number and every point 1..sections must exist; a duplicate section number is refused.</item>
    /// <item><b>EquivalentShunt</b>: its g and b (EQ), always in service as written.</item>
    /// <item>The zero-sequence b0 / g0 (per section, or per point) are a fault-study quantity and play no part in the
    /// positive-sequence power flow; a non-zero one is listed as not represented.</item>
    /// <item>A shunt under voltage control (controlEnabled with a RegulatingControl) is held at its sections in a plain
    /// solve, and listed. N296 (CIM3, 2026-10): a LINEAR shunt's control is now kept (<see cref="CimShuntControl"/>: the
    /// target, the deadband, the regulated bus, the discrete flag, the sections and the per-section admittance) when its
    /// RegulatingControl is a voltage control that is not disabled and gives a target; the bridge then offers it to
    /// SwitchedShuntPowerFlow (discrete sections) and as a continuous susceptance (CimControlledPowerFlow). A non-linear
    /// shunt's sections are not uniform steps, so its control is listed and not kept.</item>
    /// <item>An element of the abstract class ShuntCompensator (a CIM14-style document) is refused by name: whether it
    /// is linear or not cannot be known.</item>
    /// </list>
    /// </summary>
    private static List<CimShunt> ReadShunts(
        List<CimObject> objects, Func<string, List<string>> nodesOf, Func<CimObject, bool> outOfService,
        Func<CimObject, double> ownKv, CimImportOptions options, List<string> notes,
        Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode)
    {
        // SV sections, shunt mRID -> count; one per shunt.
        var svSections = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            if (o.Class != "SvShuntCompensatorSections") continue;
            string shunt = o.Resource("SvShuntCompensatorSections.ShuntCompensator")
                ?? throw new InvalidDataException($"CIM {o.Describe} names no shunt compensator (SvShuntCompensatorSections.ShuntCompensator).");
            double sections = o.Number("SvShuntCompensatorSections.sections")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no SvShuntCompensatorSections.sections.");
            if (!svSections.TryAdd(shunt, sections))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: shunt compensator {shunt} has two SV section counts; which one applies is ambiguous.");
        }

        // Non-linear points, shunt mRID -> section number -> point.
        var points = new Dictionary<string, SortedDictionary<int, CimObject>>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            if (o.Class != "NonlinearShuntCompensatorPoint") continue;
            string shunt = o.Resource("NonlinearShuntCompensatorPoint.NonlinearShuntCompensator")
                ?? throw new InvalidDataException($"CIM {o.Describe} names no NonlinearShuntCompensator.");
            int section = o.Integer("NonlinearShuntCompensatorPoint.sectionNumber")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no NonlinearShuntCompensatorPoint.sectionNumber.");
            if (!points.TryGetValue(shunt, out SortedDictionary<int, CimObject>? bySection)) points[shunt] = bySection = new();
            if (!bySection.TryAdd(section, o))
                throw new InvalidDataException(
                    $"CIM {o.Describe}: shunt {shunt} has two points for section {section} ({bySection[section].Mrid} and {o.Mrid}).");
        }

        var shunts = new List<CimShunt>();
        int regulating = 0;
        foreach (CimObject o in objects)
        {
            if (!ShuntClasses.Contains(o.Class)) continue;
            if (o.Class == "ShuntCompensator")
                throw new InvalidDataException(
                    $"CIM {o.Describe}: ShuntCompensator is an abstract class; whether this shunt is linear or non-linear cannot " +
                    "be known, so it cannot be mapped. Write it as a LinearShuntCompensator or a NonlinearShuntCompensator.");
            if (outOfService(o)) continue;
            string? node = nodesOf(o.Mrid).FirstOrDefault();
            if (node is null) { notes.Add($"{o.Class} {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }

            double kv = ownKv(o);
            double g, b;
            CimShuntControl? control = null;   // N296: a linear shunt's voltage control, when it can be applied
            if (o.Class == "EquivalentShunt")
            {
                g = o.Number("EquivalentShunt.g") ?? 0.0;
                b = o.Number("EquivalentShunt.b") ?? 0.0;
            }
            else
            {
                if (!(kv > 0)) kv = o.Number("ShuntCompensator.nomU") is double nomU && nomU > 0 ? nomU : 0.0;
                // A shunt that gives no admittance at all (no bPerSection, or no point) says nothing GridSim could
                // represent: it is left out and listed by name (as every shunt was before N286, D-DOC-099), not
                // refused. Once it gives an admittance, everything else it needs must be there (refused otherwise).
                double? bPer = o.Number("LinearShuntCompensator.bPerSection");
                if ((o.Class == "LinearShuntCompensator" && bPer is null)
                    || (o.Class == "NonlinearShuntCompensator" && !points.ContainsKey(o.Mrid)))
                {
                    notes.Add($"{o.Describe} gives no admittance ({(o.Class == "LinearShuntCompensator" ? "no LinearShuntCompensator.bPerSection" : "no NonlinearShuntCompensatorPoint")}); " +
                              "what it would add is unknown, so it is left out.");
                    continue;
                }
                double sections = Sections(o, svSections, options, notes);
                if (o.Class == "LinearShuntCompensator")
                {
                    double gPer = o.Number("LinearShuntCompensator.gPerSection") ?? 0.0;
                    b = bPer.GetValueOrDefault() * sections;   // bPer is present: a shunt without it was left out above
                    g = gPer * sections;
                    if ((o.Number("LinearShuntCompensator.b0PerSection") ?? 0.0) != 0.0
                        || (o.Number("LinearShuntCompensator.g0PerSection") ?? 0.0) != 0.0)
                        notes.Add($"{o.Describe}: its zero-sequence b0PerSection / g0PerSection are not represented (they do " +
                                  "not enter a positive-sequence power flow).");
                }
                else
                {
                    if (sections != Math.Floor(sections))
                        throw new InvalidDataException(
                            $"CIM {o.Describe}: {F(sections)} sections in service is not a whole number; a non-linear shunt's " +
                            "admittance is defined section by section.");
                    points.TryGetValue(o.Mrid, out SortedDictionary<int, CimObject>? bySection);
                    g = 0.0;
                    b = 0.0;
                    bool zeroSequence = false;
                    for (int k = 1; k <= (int)sections; k++)
                    {
                        if (bySection is null || !bySection.TryGetValue(k, out CimObject? p))
                            throw new InvalidDataException(
                                $"CIM {o.Describe}: {F(sections)} sections are in service but there is no " +
                                $"NonlinearShuntCompensatorPoint for section {k}.");
                        b += p.Number("NonlinearShuntCompensatorPoint.b")
                             ?? throw new InvalidDataException($"CIM {p.Describe} has no NonlinearShuntCompensatorPoint.b.");
                        g += p.Number("NonlinearShuntCompensatorPoint.g") ?? 0.0;
                        zeroSequence |= (p.Number("NonlinearShuntCompensatorPoint.b0") ?? 0.0) != 0.0
                                        || (p.Number("NonlinearShuntCompensatorPoint.g0") ?? 0.0) != 0.0;
                    }
                    if (zeroSequence)
                        notes.Add($"{o.Describe}: the zero-sequence b0 / g0 of its points are not represented (they do not " +
                                  "enter a positive-sequence power flow).");
                }
                if (o.Boolean("RegulatingCondEq.controlEnabled") == true && o.Resource("RegulatingCondEq.RegulatingControl") is string rcRef)
                {
                    regulating++;
                    control = o.Class == "LinearShuntCompensator"
                        ? ShuntControl(o, node, rcRef, sections, bPer.GetValueOrDefault(), byId, terminalNode, notes)
                        : null;
                    notes.Add($"{o.Describe} is under voltage control (controlEnabled = true): in a plain solve it is held at " +
                              $"{F(sections)} section(s); " +
                              (control is not null
                                  ? "N296: the bridge offers its control to SwitchedShuntPowerFlow (discrete sections) and to CimControlledPowerFlow (continuous)."
                                  : o.Class == "NonlinearShuntCompensator"
                                      ? "a non-linear shunt's sections are not uniform steps, so its control is not applied."
                                      : "its control cannot be applied (see the note on its RegulatingControl)."));
                }
            }
            shunts.Add(new CimShunt(o.Mrid, o.Name, node, g, b, o.Class, kv, control));
        }
        if (regulating > 0)
            notes.Add($"{regulating} shunt compensator(s) under voltage control are held at their sections (see above) in a plain " +
                      "solve; a solve that switches or varies them (CimControlledPowerFlow, N296; or the tool that wrote an SV " +
                      "profile) can show a voltage difference at the buses they regulate.");
        return shunts;
    }

    /// <summary>The sections in service of a shunt compensator (see <see cref="ReadShunts"/>): SSH / SV by the option,
    /// else the EQ normal sections; checked against 0 and maximumSections.</summary>
    private static double Sections(CimObject o, Dictionary<string, double> svSections, CimImportOptions options, List<string> notes)
    {
        double? ssh = o.Number("ShuntCompensator.sections");
        double? sv = svSections.TryGetValue(o.Mrid, out double s) ? s : null;
        double? sections = ssh;
        if (sv is double svCount && (ssh is null || options.ShuntSections == CimTapPositions.StateVariables))
        {
            sections = svCount;
            if (ssh is null)
                notes.Add($"{o.Describe} has no ShuntCompensator.sections (SSH); its SV count SvShuntCompensatorSections.sections " +
                          $"{F(svCount)} is used.");
            else if (ssh.Value != svCount)
                notes.Add($"{o.Describe}: its SV section count {F(svCount)} is used (ShuntSections = StateVariables), not its SSH " +
                          $"count {F(ssh.Value)}.");
        }
        else if (sv is double other && ssh is double sshCount && other != sshCount)
        {
            notes.Add($"{o.Describe}: its SSH section count {F(sshCount)} is used; the published solved state (SV) has " +
                      $"{F(other)} (a shunt under automatic control switched in that solve). Import with ShuntSections = " +
                      "StateVariables to reproduce the SV state.");
        }
        if (sections is null)
        {
            sections = o.Number("ShuntCompensator.normalSections")
                ?? throw new InvalidDataException(
                    $"CIM {o.Describe} has no ShuntCompensator.sections (SSH), no SvShuntCompensatorSections (SV) and no " +
                    "ShuntCompensator.normalSections (EQ); the sections in service are unknown.");
            notes.Add($"{o.Describe} has no ShuntCompensator.sections (no SSH profile); its normalSections {F(sections.Value)} is used.");
        }
        double? max = o.Number("ShuntCompensator.maximumSections");
        if (sections < 0 || (max is not null && sections > max))
            throw new InvalidDataException(
                $"CIM {o.Describe}: {F(sections.Value)} sections in service is outside 0..maximumSections " +
                $"({(max is null ? "-" : F(max.Value))}).");
        return sections.Value;
    }
}
