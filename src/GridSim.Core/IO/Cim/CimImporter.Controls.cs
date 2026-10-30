namespace GridSim.Core.IO.Cim;

/// <summary>
/// N295 (CIM3, 2026-10): the voltage-control equipment and data the CIM importer reads so that the ENTSO-E CGMES
/// conformity models (MicroGrid BE, CGMES 2.4.15 and 3.0) reproduce their published solved state:
/// <list type="bullet">
/// <item>a synchronous machine's <c>InitialReactiveCapabilityCurve</c>, evaluated at the machine's active power
/// (<see cref="CapabilityAt"/>);</item>
/// <item>the <c>StaticVarCompensator</c> (<see cref="ReadStaticVarCompensators"/>): a variable susceptance between its
/// ratings, regulating a voltage with a slope. A plain solve holds it at its SSH q; <see cref="CimControlledPowerFlow"/>
/// regulates it exactly.</item>
/// </list>
/// The SeriesCompensator (a series branch) is read with the lines, remote voltage control with the voltage control of
/// each equipment, and the CGMES 3.0 boundary with the bus-branch view (all in CimImporter.cs / .Equipment.cs).
/// </summary>
public static partial class CimImporter
{
    /// <summary>
    /// N295 (CIM3, 2026-10): the reactive limits (min, max MVAr, generator sense) a ReactiveCapabilityCurve gives at the
    /// active power <paramref name="p"/> (MW, generator sense). The rules, and why:
    /// <list type="bullet">
    /// <item>Each <c>CurveData</c> is one point: xvalue = active power, y1value = the minimum and y2value = the maximum
    /// reactive power (CGMES 2.4.15 / 3.0 EQ, ReactiveCapabilityCurve: "Reactive power rating envelope versus the
    /// synchronous machine's active power, in both the generating and motoring modes. For each active power value there
    /// is a corresponding high and low reactive power limit value"). Read in MW / MVAr: a Curve.xMultiplier /
    /// y1Multiplier / y2Multiplier other than M (mega) is refused, as is a point missing a value, a point whose y1 is
    /// above its y2, two points at one x, or a curve with no point.</item>
    /// <item><c>Curve.curveStyle</c> decides between the points (IEC 61970-301 CurveStyle): constantYValue = "the Y-axis
    /// values are assumed constant until the next curve point and prior to the first curve point" (a step: the value of
    /// the last point at or below p); straightLineYValues = "the Y-axis values are assumed to be a straight line between
    /// values" (linear interpolation). Outside the points' range the end point's values are held (powsybl does the
    /// same: a capability curve is not extrapolated). No style given: a straight line (powsybl's only reading). Another
    /// style (formula, rampYValue) is refused.</item>
    /// <item>The machine's P is its SSH set-point; for the slack machine, whose P the solve decides, the curve is still
    /// evaluated at the set-point (the limits do not follow the slack's solved P; the note names the P used).</item>
    /// </list>
    /// </summary>
    private static (double Min, double Max) CapabilityAt(CimObject curve, IEnumerable<CimObject> points, double p)
    {
        foreach (string mult in new[] { "Curve.xMultiplier", "Curve.y1Multiplier", "Curve.y2Multiplier" })
        {
            string? m = EnumLeaf(curve.RawEnum(mult));
            if (m is not null && m != "M")
                throw new InvalidDataException(
                    $"CIM {curve.Describe}: {mult} is {m}; a reactive capability curve is read in MW and MVAr (UnitMultiplier.M) only.");
        }
        string? style = EnumLeaf(curve.RawEnum("Curve.curveStyle"));
        bool step = style switch
        {
            "constantYValue" => true,
            "straightLineYValues" or null => false,
            _ => throw new InvalidDataException(
                $"CIM {curve.Describe}: Curve.curveStyle {style} is not one a capability curve can be evaluated with " +
                "(constantYValue or straightLineYValues)."),
        };
        var rows = new List<(double X, double Y1, double Y2)>();
        foreach (CimObject pt in points.OrderBy(q => q.Mrid, StringComparer.Ordinal))
        {
            double x = pt.Number("CurveData.xvalue") ?? throw new InvalidDataException($"CIM {pt.Describe} has no CurveData.xvalue.");
            double y1 = pt.Number("CurveData.y1value") ?? throw new InvalidDataException($"CIM {pt.Describe} has no CurveData.y1value (the minimum reactive power).");
            double y2 = pt.Number("CurveData.y2value") ?? throw new InvalidDataException($"CIM {pt.Describe} has no CurveData.y2value (the maximum reactive power).");
            if (y1 > y2)
                throw new InvalidDataException(
                    $"CIM {pt.Describe}: at P = {F(x)} MW its minimum reactive power y1value = {F(y1)} is above its maximum y2value = {F(y2)} MVAr.");
            rows.Add((x, y1, y2));
        }
        if (rows.Count == 0)
            throw new InvalidDataException($"CIM {curve.Describe} has no CurveData point; a capability curve with no point gives no limit.");
        // By active power; OrderBy is stable and the mRID order above breaks nothing, as two points at one x are refused.
        rows = rows.OrderBy(r => r.X).ToList();
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].X == rows[i - 1].X)
                throw new InvalidDataException(
                    $"CIM {curve.Describe}: two points at P = {F(rows[i].X)} MW; which limits apply there is ambiguous.");

        if (p <= rows[0].X) return (rows[0].Y1, rows[0].Y2);
        if (p >= rows[^1].X) return (rows[^1].Y1, rows[^1].Y2);
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            (double xa, double a1, double a2) = rows[i];
            (double xb, double b1, double b2) = rows[i + 1];
            if (p < xa || p >= xb) continue;
            if (step) return (a1, a2);
            // A straight line between the two points (p strictly inside, so xb - xa > 0).
            return (a1 + (p - xa) * (b1 - a1) / (xb - xa), a2 + (p - xa) * (b2 - a2) / (xb - xa));
        }
        // Unreachable: p is strictly between the first and the last x, so one interval holds it.
        throw new InvalidOperationException($"Capability curve {curve.Describe}: P = {F(p)} MW fell in no interval.");
    }

    /// <summary>
    /// N296 (CIM3, 2026-10): the voltage control of a linear shunt under control, read from its RegulatingControl: a
    /// voltage-mode control that is not disabled (RegulatingControl.enabled false), with a target (kV, checked as a
    /// machine's), the regulated bus (its terminal's, else the shunt's own), the deadband (kV, not negative) and the
    /// discrete flag (absent = discrete, as a shunt's sections are). Null, with a note, when the control cannot be applied
    /// (another mode, disabled, an unconnected terminal, no target, or a control not in the document).
    /// </summary>
    private static CimShuntControl? ShuntControl(
        CimObject o, string node, string rcRef, double sections, double bPer, Dictionary<string, CimObject> byId,
        Dictionary<string, string> terminalNode, List<string> notes)
    {
        if (!byId.TryGetValue(rcRef, out CimObject? rc))
        {
            notes.Add($"{o.Describe} names RegulatingControl {rcRef}, which is not in this document; its control cannot be applied.");
            return null;
        }
        string? mode = EnumLeaf(rc.RawEnum("RegulatingControl.mode"));
        if (mode != "voltage")
        {
            notes.Add($"{o.Describe}: its RegulatingControl mode is {mode ?? "not given"}; only a voltage control is applied to a shunt.");
            return null;
        }
        if (rc.Boolean("RegulatingControl.enabled") == false)
        {
            notes.Add($"{o.Describe}: its RegulatingControl is disabled (RegulatingControl.enabled = false); it is not applied.");
            return null;
        }
        string? terminal = rc.Resource("RegulatingControl.Terminal");
        string? regulated = terminal is null ? node : terminalNode.GetValueOrDefault(terminal);
        if (regulated is null)
        {
            notes.Add($"{o.Describe}: its RegulatingControl names an unconnected terminal; it is not applied.");
            return null;
        }
        if (VoltageTarget(rc) is not double target)
        {
            notes.Add($"{o.Describe}: its RegulatingControl gives no target (RegulatingControl.targetValue); it is not applied.");
            return null;
        }
        double? deadband = rc.Number("RegulatingControl.targetDeadband");
        if (deadband < 0)
            throw new InvalidDataException(
                $"CIM {rc.Describe}: RegulatingControl.targetDeadband = {F(deadband.GetValueOrDefault())} kV is negative.");
        bool discrete = rc.Boolean("RegulatingControl.discrete") != false;
        return new CimShuntControl(target, deadband, regulated, discrete, sections, o.Number("ShuntCompensator.maximumSections"),
            bPer, o.Number("LinearShuntCompensator.gPerSection") ?? 0.0);
    }

    /// <summary>
    /// N295 (CIM3, 2026-10): read every StaticVarCompensator (in document order). The mapping, and why:
    /// <list type="bullet">
    /// <item>Ratings as written (<see cref="CimStaticVarCompensator"/>): capacitiveRating and inductiveRating are the
    /// reactances (ohm) at full capacitive / inductive output (CGMES: "Maximum available capacitive reactance",
    /// "Maximum available inductive reactance", the inductive one negative), so the susceptance range is
    /// -1 / |inductiveRating| .. +1 / |capacitiveRating| siemens (powsybl: bMin = 1 / inductiveRating, bMax =
    /// 1 / capacitiveRating; the magnitudes are taken so a rating written with the other sign still means what its name
    /// says). A rating of 0 ohm is refused (it is no reactance limit). A regulating SVC with neither rating is refused:
    /// its range is unknown.</item>
    /// <item><c>slope</c> (kV / MVAr, "shall be a positive value or zero"): the droop V = Vset - slope x Q, Q the
    /// capacitive output; a negative slope is refused.</item>
    /// <item>Voltage control: <c>RegulatingCondEq.controlEnabled</c> false = not regulating; otherwise its
    /// RegulatingControl, read exactly as a machine's (voltage mode, enabled, target in kV; local or remote). With no
    /// RegulatingControl, the CGMES 2.4.15 SSH attributes: <c>sVCControlMode</c> voltage with a
    /// <c>voltageSetPoint</c> (kV) regulates its own node; reactivePower or off holds its q; another mode is refused.</item>
    /// <item>The SSH <c>q</c> (load sign) is what a plain solve holds it at (the bridge adds it to the bus load), as every
    /// other non-regulating injection; the controlled solve regulates it (CimControlledPowerFlow).</item>
    /// </list>
    /// Before N295 an SVC was listed as conducting equipment the importer does not represent and left out entirely.
    /// </summary>
    private static List<CimStaticVarCompensator> ReadStaticVarCompensators(
        List<CimObject> objects, Func<string, List<string>> nodesOf, Func<CimObject, bool> outOfService,
        Func<CimObject, double> ownKv, Dictionary<string, CimObject> byId, Dictionary<string, string> terminalNode,
        List<string> notes)
    {
        var svcs = new List<CimStaticVarCompensator>();
        foreach (CimObject o in objects)
        {
            if (o.Class != "StaticVarCompensator") continue;
            if (outOfService(o)) continue;
            string? node = nodesOf(o.Mrid).FirstOrDefault();
            if (node is null) { notes.Add($"StaticVarCompensator {o.Name} ({o.Mrid}) has no connected terminal; left out."); continue; }

            double? cap = o.Number("StaticVarCompensator.capacitiveRating");
            double? ind = o.Number("StaticVarCompensator.inductiveRating");
            if (cap == 0.0 || ind == 0.0)
                throw new InvalidDataException(
                    $"CIM {o.Describe}: a {(cap == 0.0 ? "capacitiveRating" : "inductiveRating")} of 0 ohm is no reactance limit " +
                    "(the susceptance would be infinite).");
            double slope = o.Number("StaticVarCompensator.slope") ?? 0.0;
            if (slope < 0.0)
                throw new InvalidDataException(
                    $"CIM {o.Describe}: StaticVarCompensator.slope = {F(slope)} kV/MVAr is negative; CGMES says it is positive or zero.");
            double q = o.Number("StaticVarCompensator.q") ?? 0.0;   // SSH, load sign; absent = 0 (an EQ-only document)

            bool regulating = false;
            double? targetKv = null;
            string? regulatedNode = null;
            if (o.Boolean("RegulatingCondEq.controlEnabled") == false)
            {
                notes.Add($"{o.Describe}: voltage control is disabled (RegulatingCondEq.controlEnabled = false); it is held at its " +
                          $"q = {F(q)} MVAr (load sign).");
            }
            else if (o.Resource("RegulatingCondEq.RegulatingControl") is not null)
            {
                // The machine rule (mode, enabled, local or remote, kV target), with the SVC's capacitive output as "q".
                (bool? vc, double? local, string? remote, double? remoteKv) =
                    ReadVoltageControl(o, node, 0.0 - q, byId, terminalNode, notes);
                if (vc == true && local is double t)
                {
                    regulating = true;
                    targetKv = t;
                    regulatedNode = node;
                }
                else if (remote is not null && remoteKv is double rt)
                {
                    regulating = true;
                    targetKv = rt;
                    regulatedNode = remote;
                }
                else if (vc == true)
                {
                    notes.Add($"{o.Describe}: its voltage control gives no target (RegulatingControl.targetValue); it is held at " +
                              $"its q = {F(q)} MVAr (load sign).");
                }
            }
            else
            {
                // CGMES 2.4.15 SSH: StaticVarCompensator.sVCControlMode and voltageSetPoint (removed in CGMES 3.0, where the
                // RegulatingControl carries the mode and the target).
                string? mode = EnumLeaf(o.RawEnum("StaticVarCompensator.sVCControlMode"));
                double? setPoint = o.Number("StaticVarCompensator.voltageSetPoint");
                switch (mode)
                {
                    case "voltage" when setPoint is > 0:
                        regulating = true;
                        targetKv = setPoint;
                        regulatedNode = node;
                        break;
                    case "voltage":
                        throw new InvalidDataException(
                            $"CIM {o.Describe}: sVCControlMode is voltage but StaticVarCompensator.voltageSetPoint is " +
                            $"{(setPoint is null ? "missing" : F(setPoint.Value) + " kV")}; a voltage set-point must be a positive number of kV.");
                    case "reactivePower" or "off" or null:
                        notes.Add($"{o.Describe}: it does not regulate a voltage ({(mode is null ? "no RegulatingControl and no sVCControlMode" : $"sVCControlMode {mode}")}); " +
                                  $"it is held at its q = {F(q)} MVAr (load sign).");
                        break;
                    default:
                        throw new InvalidDataException(
                            $"CIM {o.Describe}: StaticVarCompensator.sVCControlMode {mode} is not voltage, reactivePower or off.");
                }
            }
            if (regulating && cap is null && ind is null)
                throw new InvalidDataException(
                    $"CIM {o.Describe} regulates a voltage but gives neither capacitiveRating nor inductiveRating; its range is unknown.");
            svcs.Add(new CimStaticVarCompensator(o.Mrid, o.Name, node, cap, ind, slope, q, regulating, targetKv, regulatedNode, ownKv(o)));
        }
        if (svcs.Count > 0)
            notes.Add($"{svcs.Count} StaticVarCompensator(s) read ({svcs.Count(s => s.Regulating)} regulating): a plain solve holds each at its " +
                      "SSH q; CimControlledPowerFlow regulates the regulating ones (a susceptance between their ratings, with their slope).");
        return svcs;
    }
}
