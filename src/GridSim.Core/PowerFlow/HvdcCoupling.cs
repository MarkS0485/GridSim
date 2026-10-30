using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The AC power flow of a model with HVDC links (<see cref="GridModel.LccLinks"/>, <see cref="GridModel.VscLinks"/>):
/// the AC solve and the links' DC solves alternate until the AC solve's inputs stop changing.
/// <list type="bullet">
/// <item>Each LCC converter is a constant-power load at its bus (<see cref="LccDcLinks.SolveLink"/> from the last AC
/// voltages).</item>
/// <item>Each VSC converter injects its active power (<see cref="VscDcLinks.SolveDc"/>, with any capability reduction of
/// its order); a MODE 1 converter is a voltage-controlling source with its reactive limits (a power-flow-only unit, the
/// bus voltage-controlled), a MODE 2 converter a constant-power injection at its power factor. After each AC solve every
/// VSC converter's apparent power is checked against its rating at the solved voltage and reduced by its weighting
/// (<see cref="VscDcLinks.Reduce"/>) when it exceeds it; reductions only ever tighten.</item>
/// </list>
/// <item>FACTS devices and static var compensators (<see cref="FactsDevices"/>): each shunt element or compensator is a
/// voltage-controlling source at its bus; after each AC solve a shunt element over its current rating is fixed at the
/// reactive power the rating allows (re-taken at each solved voltage until it settles), a compensator whose bus has left
/// its band starts holding the band edge, and one beyond its susceptance range is fixed at the end of the range. A
/// UPFC's series element is the injections PDES + j QDES at its terminal end bus and -PDES at its sending end bus.</item>
/// <item>Induction machines (<see cref="InductionMachines"/>): each a constant-power load at its bus at the slip that
/// meets its schedule at the last AC voltage.</item>
/// Settled when the LCC and induction machine states repeat within 1e-9 (1 + |P|) and no VSC, FACTS or compensator input
/// changed; at most
/// <see cref="LccDcLinks.MaxCouplingIterations"/> AC solves. Failures (no DC state meets an order, the AC solve did not
/// converge, no settling) return the last result marked NOT converged with the reason in <see cref="PowerFlowResult.LccNote"/>
/// (LCC), <see cref="PowerFlowResult.VscNote"/> (VSC), <see cref="PowerFlowResult.FactsNote"/> (FACTS and compensators;
/// also a settled UPFC outside its terminal voltage band or its series or bridge ratings). A model with none of these
/// devices never reaches here.
/// </summary>
public static class HvdcCoupling
{
    /// <summary>Solve <paramref name="model"/>'s AC power flow with its HVDC links, through the AC engine <paramref name="ac"/>.</summary>
    public static PowerFlowResult Solve(GridModel model, PowerFlowOptions? options, Func<GridModel, PowerFlowOptions?, PowerFlowResult> ac)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(ac);
        var busIndex = new Dictionary<int, int>(model.Buses.Count);
        for (int b = 0; b < model.Buses.Count; b++) busIndex[model.Buses[b].Id] = b;
        foreach (LccDcLink l in model.LccLinks)
            foreach (int bus in new[] { l.Rectifier.Bus, l.Inverter.Bus })
                if (!busIndex.ContainsKey(bus)) throw new InvalidOperationException($"LCC link {l.Name}: converter bus {bus} is not in the model.");
        foreach (VscDcLink l in model.VscLinks)
            foreach (VscConverter c in new[] { l.First, l.Second })
            {
                if (!busIndex.TryGetValue(c.Bus, out int convBusIndex)) throw new InvalidOperationException($"VSC link {l.Name}: converter bus {c.Bus} is not in the model.");
                if (c.AcControl == VscAcControl.Voltage && model.Generators.Any(g => g.Bus == c.Bus))
                    throw new InvalidOperationException($"VSC link {l.Name}: converter bus {c.Bus} controls its voltage and also has generators; GridSim cannot share the bus's voltage control between them.");
                // The slack bus holds its own voltage, so a converter in AC voltage control there is refused like a FACTS
                // shunt element or compensator on the slack (CheckFactsBuses). On a generator-less slack it used to be
                // accepted: the converter's power-flow-only unit replaced the slack's set-point, its reactive limits and
                // rating were never applied (a slack is never reactive-limited), and the slack's whole reactive output
                // was reported as the converter's.
                if (c.AcControl == VscAcControl.Voltage && model.Buses[convBusIndex].Type == BusType.Slack)
                    throw new InvalidOperationException($"VSC link {l.Name}: converter bus {c.Bus} is the slack bus and the converter is in AC voltage control; it cannot share the slack bus's voltage control.");
            }
        Dictionary<int, VoltageHolders> holders = CheckFactsBuses(model, busIndex);

        int nv = model.VscLinks.Count;
        var orderScale = new double[nv];
        Array.Fill(orderScale, 1.0);
        var qCapHi = new double[2 * nv];   // per converter (2k first, 2k+1 second): a reduced upper reactive limit
        var qCapLo = new double[2 * nv];
        Array.Fill(qCapHi, double.PositiveInfinity);
        Array.Fill(qCapLo, double.NegativeInfinity);
        var limited = new bool[2 * nv];
        // FACTS shunt elements: limited by current (then a fixed reactive injection, Mvar, at the bus) or regulating.
        int nf = model.FactsDevices.Count, ns = model.StaticVarCompensators.Count;
        var factsLimited = new bool[nf];
        var factsQ = new double[nf];
        // Static var compensators: their state and the set-point they hold.
        var svcState = new SvcState[ns];
        var svcTarget = new double[ns];
        for (int k = 0; k < ns; k++)
        {
            StaticVarCompensator c = model.StaticVarCompensators[k];
            svcTarget[k] = double.NaN;
            if (c.VhiPu == c.VloPu) { svcState[k] = SvcState.Regulating; svcTarget[k] = c.VhiPu; }
        }

        var vm = model.Buses.ToDictionary(b => b.Id, b => b.Vm);
        LccLinkResult[]? prevLcc = null;
        MtdcLinkResult[]? prevMtdc = null;
        MtdcLinkResult[] mtdc = Array.Empty<MtdcLinkResult>();
        InductionMachineResult[]? prevIm = null;
        InductionMachineResult[] im = Array.Empty<InductionMachineResult>();
        foreach (InductionMachine x in model.InductionMachines)
            if (!busIndex.ContainsKey(x.Bus)) throw new InvalidOperationException($"induction machine {x.Name}: bus {x.Bus} is not in the model.");
        PowerFlowResult? r = null;
        LccLinkResult[] lcc = Array.Empty<LccLinkResult>();
        var vscDc = new (double FirstMw, double SecondMw, double CurrentKa, double FirstDcKv, double SecondDcKv, double FirstLossMw, double SecondLossMw)[nv];
        GridModel? acModel = null;
        double[] devQ = Array.Empty<double>();   // the devices' reactive draws per bus in the last AC model (Mvar)
        bool vscChanged = true;
        for (int it = 0; it < LccDcLinks.MaxCouplingIterations; it++)
        {
            try { lcc = model.LccLinks.Select(l => LccDcLinks.SolveLink(l, vm[l.Rectifier.Bus], vm[l.Inverter.Bus])).ToArray(); }
            catch (InvalidOperationException ex)
            {
                r ??= ac(Plain(model), options);
                return With(r, model, prevLcc ?? Array.Empty<LccLinkResult>(), null, ex.Message, null, notConverged: true);
            }
            try { mtdc = model.MtdcLinks.Select(l => MtdcLinks.Solve(l, vm)).ToArray(); }
            catch (InvalidOperationException ex)
            {
                r ??= ac(Plain(model), options);
                return With(r, model, lcc, null, null, null, notConverged: true, mtdc: prevMtdc ?? Array.Empty<MtdcLinkResult>(), mtdcNote: ex.Message);
            }
            try { im = model.InductionMachines.Select(x => InductionMachines.Solve(x, vm[x.Bus], model.Buses[busIndex[x.Bus]].BaseKv)).ToArray(); }
            catch (InvalidOperationException ex)
            {
                r ??= ac(Plain(model), options);
                return With(r, model, lcc, null, null, null, notConverged: true, mtdc: mtdc, im: prevIm, inductionNote: ex.Message);
            }
            if (prevLcc is not null && !vscChanged && LccSettled(prevLcc, lcc) && MtdcSettled(prevMtdc!, mtdc) && ImSettled(prevIm!, im))
            {
                // Settled. A UPFC must also be inside its terminal voltage band and its series and bridge ratings.
                (double[] fShare, double[] sShare) = Shares(model, holders, factsLimited, svcState);
                (FactsResult[] fr, string? why) = FactsResults(model, busIndex, r!, acModel!, devQ, factsLimited, factsQ, fShare);
                return With(r!, model, lcc, VscResults(model, r!, acModel!, vscDc, orderScale, limited, devQ, busIndex), null, null, mtdc: mtdc,
                    facts: fr, svcs: SvcResults(model, busIndex, r!, acModel!, devQ, svcState, svcTarget, sShare), factsNote: why, notConverged: why is not null,
                    im: im);
            }
            prevLcc = lcc;
            prevMtdc = mtdc;
            prevIm = im;

            try { for (int k = 0; k < nv; k++) vscDc[k] = VscDcLinks.SolveDc(model.VscLinks[k], orderScale[k]); }
            catch (InvalidOperationException ex)
            {
                r ??= ac(Plain(model), options);
                return With(r, model, lcc, null, null, ex.Message, notConverged: true);
            }

            acModel = AcModel(model, busIndex, lcc, vscDc, qCapHi, qCapLo, mtdc, factsLimited, factsQ, svcState, svcTarget, im, out devQ);
            r = ac(acModel, options);
            if (!r.Converged)
            {
                string why = $"the AC solve did not converge at AC/DC iteration {it + 1}";
                return With(r, model, lcc, null, model.LccLinks.Count > 0 ? why : null, nv > 0 ? why : null, notConverged: true,
                    mtdc: mtdc, mtdcNote: model.MtdcLinks.Count > 0 ? why : null, factsNote: nf + ns > 0 ? why : null,
                    im: im, inductionNote: model.InductionMachines.Count > 0 ? why : null);
            }
            for (int b = 0; b < r.BusIds.Count; b++) vm[r.BusIds[b]] = r.Vm[b];

            // VSC ratings at the solved voltages.
            vscChanged = false;
            for (int k = 0; k < nv; k++)
            {
                VscDcLink l = model.VscLinks[k];
                for (int side = 0; side < 2; side++)
                {
                    VscConverter c = side == 0 ? l.First : l.Second;
                    double p = side == 0 ? vscDc[k].FirstMw : vscDc[k].SecondMw;
                    double q = ConverterQ(acModel, r, c, p, devQ, busIndex);
                    double baseKv = model.Buses[busIndex[c.Bus]].BaseKv;
                    double lim = VscDcLinks.Limit(c, vm[c.Bus], baseKv);
                    if (!(p * p + q * q > lim * lim * (1 + 1e-12))) continue;
                    bool power = c.DcControl == VscDcControl.Power;
                    (double P, double Q)? red;
                    if (c.AcControl == VscAcControl.PowerFactor)
                    {
                        // At a fixed power factor Q follows P: both scale onto the rating together, whatever PWF says.
                        if (!power)
                            return With(r, model, lcc, null, null, $"VSC link {l.Name}: converter at bus {c.Bus} holds the DC voltage at a fixed power factor and its rating of {lim:G6} MVA is exceeded; its active power is set by the link and cannot be reduced", notConverged: true);
                        double f = lim / Math.Sqrt(p * p + q * q);
                        red = (p * f, q * f);
                    }
                    else red = VscDcLinks.Reduce(p, q, lim, power ? c.PowerWeight : 1.0);   // the DC voltage converter: reactive only
                    if (red is null)
                        return With(r, model, lcc, null, null, $"VSC link {l.Name}: converter at bus {c.Bus}: {Math.Abs(p):G6} MW alone exceeds its rating of {lim:G6} MVA and its weighting cannot reduce it enough", notConverged: true);
                    limited[2 * k + side] = true;
                    vscChanged = true;
                    if (power && p != 0) orderScale[k] *= red.Value.P / p;
                    if (c.AcControl == VscAcControl.Voltage)
                    {
                        if (q > 0) qCapHi[2 * k + side] = Math.Min(qCapHi[2 * k + side], red.Value.Q);
                        else qCapLo[2 * k + side] = Math.Max(qCapLo[2 * k + side], red.Value.Q);
                    }
                }
            }

            // FACTS shunt elements and static var compensators at the solved voltages.
            if (nf + ns > 0)
            {
                // Shares are taken from the state the AC solve used (before this round's updates), so a bus held by
                // several devices splits the reactive power it actually needed by RMPCT.
                (double[] fShare, double[] sShare) = Shares(model, holders, factsLimited, svcState);
                string? fail = UpdateFacts(model, busIndex, r, acModel, devQ, factsLimited, factsQ, fShare, ref vscChanged);
                fail ??= UpdateSvcs(model, busIndex, r, acModel, devQ, svcState, svcTarget, sShare, ref vscChanged);
                fail ??= SetpointConflict(model, holders, factsLimited, svcState, svcTarget);
                if (fail is not null)
                    return With(r, model, lcc, null, null, null, notConverged: true, mtdc: mtdc, factsNote: fail);
            }
        }
        string late = $"the AC/DC alternation did not settle within {LccDcLinks.MaxCouplingIterations} iterations (converter taps, modes or ratings alternating)";
        return With(r!, model, lcc, acModel is null ? null : VscResults(model, r!, acModel, vscDc, orderScale, limited, devQ, busIndex),
            model.LccLinks.Count > 0 ? late : null, nv > 0 ? late : null, notConverged: true,
            mtdc: mtdc, mtdcNote: model.MtdcLinks.Count > 0 ? late : null, factsNote: nf + ns > 0 ? late : null,
            im: im, inductionNote: model.InductionMachines.Count > 0 ? late : null);
    }

    private static GridModel Plain(GridModel model) =>
        model.CopyWith(lccLinks: Array.Empty<LccDcLink>(), vscLinks: Array.Empty<VscDcLink>(), mtdcLinks: Array.Empty<MtdcLink>(),
            factsDevices: Array.Empty<FactsDevice>(), staticVarCompensators: Array.Empty<StaticVarCompensator>(),
            inductionMachines: Array.Empty<InductionMachine>());

    /// <summary>The AC model: LCC converters as loads, VSC converters as injections (MODE 2) or voltage-controlling
    /// power-flow-only units (MODE 1), FACTS shunt elements and compensators as voltage-controlling units (or fixed
    /// injections and susceptances at their limits), no devices. A device's constant power at a bus whose load varies
    /// with voltage (a ZIP load) is a fixed injection of its own rather than part of the bus load, so the ZIP
    /// coefficients do not scale it; <paramref name="devQ"/> returns the devices' reactive draws per bus.</summary>
    private static GridModel AcModel(GridModel model, Dictionary<int, int> busIndex, LccLinkResult[] lcc,
        (double FirstMw, double SecondMw, double CurrentKa, double FirstDcKv, double SecondDcKv, double FirstLossMw, double SecondLossMw)[] vscDc,
        double[] qCapHi, double[] qCapLo, MtdcLinkResult[] mtdc, bool[] factsLimited, double[] factsQ, SvcState[] svcState, double[] svcTarget,
        InductionMachineResult[] im, out double[] devQ)
    {
        var pd = new double[model.Buses.Count];
        var qd = new double[model.Buses.Count];
        var vgBus = new Dictionary<int, double>();
        var extra = new List<Generator>();
        for (int k = 0; k < lcc.Length; k++)
        {
            LccDcLink l = model.LccLinks[k];
            pd[busIndex[l.Rectifier.Bus]] += lcc[k].RectifierMw;
            qd[busIndex[l.Rectifier.Bus]] += lcc[k].RectifierMvar;
            pd[busIndex[l.Inverter.Bus]] -= lcc[k].InverterMw;
            qd[busIndex[l.Inverter.Bus]] += lcc[k].InverterMvar;
        }
        foreach (MtdcLinkResult m in mtdc)
            foreach (MtdcConverterResult c in m.Converters)
            {
                pd[busIndex[c.Bus]] += c.DrawnMw;
                qd[busIndex[c.Bus]] += c.AbsorbedMvar;
            }
        for (int k = 0; k < vscDc.Length; k++)
        {
            VscDcLink l = model.VscLinks[k];
            for (int side = 0; side < 2; side++)
            {
                VscConverter c = side == 0 ? l.First : l.Second;
                double p = side == 0 ? vscDc[k].FirstMw : vscDc[k].SecondMw;
                if (c.AcControl == VscAcControl.PowerFactor)
                {
                    pd[busIndex[c.Bus]] -= p;
                    qd[busIndex[c.Bus]] -= VscDcLinks.PowerFactorQ(p, c.AcSetpoint);
                }
                else
                {
                    double qmax = Math.Min(c.QmaxMvar, qCapHi[2 * k + side]), qmin = Math.Max(c.QminMvar, qCapLo[2 * k + side]);
                    extra.Add(new Generator(c.Bus, p, Qg: 0, Vg: c.AcSetpoint, Qmin: qmin, Qmax: qmax, Pmin: p, Pmax: p,
                        Name: $"VSC {l.Name} converter at bus {c.Bus}", Type: "hvdc", PowerFlowOnly: true));
                    vgBus[c.Bus] = c.AcSetpoint;
                }
            }
        }
        foreach (InductionMachineResult x in im)
        {
            pd[busIndex[x.Bus]] += x.Mw;
            qd[busIndex[x.Bus]] += x.Mvar;
        }
        double[]? bs = null;   // susceptance added to a bus (Mvar at 1 pu): a compensator fixed at an end of its range
        for (int k = 0; k < factsLimited.Length; k++)
        {
            FactsDevice d = model.FactsDevices[k];
            double p = d.Mode == FactsMode.Upfc ? -d.PdesMw : 0;   // a UPFC's sending end bus gives up PDES
            if (d.Mode == FactsMode.Upfc)
            {
                pd[busIndex[d.TerminalBus]] -= d.PdesMw;
                qd[busIndex[d.TerminalBus]] -= d.QdesMvar;
            }
            if (!(d.ShuntMaxMva > 0)) continue;
            if (factsLimited[k])
            {
                pd[busIndex[d.SendingBus]] -= p;
                qd[busIndex[d.SendingBus]] -= factsQ[k];
            }
            else
            {
                extra.Add(new Generator(d.SendingBus, p, Qg: 0, Vg: d.VsetPu, Qmin: -FactsDevices.UnlimitedMvar, Qmax: FactsDevices.UnlimitedMvar,
                    Pmin: p, Pmax: p, Name: $"FACTS {d.Name} shunt element", Type: "facts", PowerFlowOnly: true));
                vgBus[d.SendingBus] = d.VsetPu;
            }
        }
        for (int k = 0; k < svcState.Length; k++)
        {
            StaticVarCompensator c = model.StaticVarCompensators[k];
            switch (svcState[k])
            {
                case SvcState.Regulating:
                    extra.Add(new Generator(c.Bus, 0, Qg: 0, Vg: svcTarget[k], Qmin: -FactsDevices.UnlimitedMvar, Qmax: FactsDevices.UnlimitedMvar,
                        Pmin: 0, Pmax: 0, Name: c.Name, Type: "svc", PowerFlowOnly: true));
                    vgBus[c.Bus] = svcTarget[k];
                    break;
                case SvcState.AtMaximum:
                case SvcState.AtMinimum:
                    bs ??= new double[model.Buses.Count];
                    bs[busIndex[c.Bus]] += (svcState[k] == SvcState.AtMaximum ? c.BmaxMvar : c.BminMvar) - c.BinitMvar;
                    break;
            }
        }
        var buses = new Bus[model.Buses.Count];
        for (int b = 0; b < buses.Length; b++)
        {
            Bus bus = model.Buses[b];
            Bus nb = pd[b] == 0 && qd[b] == 0 ? bus : bus with { Pd = bus.Pd + pd[b], Qd = bus.Qd + qd[b] };
            if (bus.Zip is { VariesWithVoltage: true } && (pd[b] != 0 || qd[b] != 0))
            {
                // The ZIP coefficients scale the whole bus load with voltage, so a device's constant power added to Pd
                // and Qd would scale too. Inject it as a fixed unit instead (its own Pg, Qg; limits equal, so it never
                // holds a voltage). The engines take the last unit's set-point at a bus, so this one carries the set-point
                // the bus already has: a device's, else the last generator's, else the bus's stored voltage.
                double vgHere = vgBus.TryGetValue(bus.Id, out double vd) ? vd
                    : model.Generators.LastOrDefault(g => g.Bus == bus.Id) is Generator lg ? lg.Vg : bus.Vm;
                extra.Add(new Generator(bus.Id, -pd[b], Qg: -qd[b], Vg: vgHere, Qmin: -qd[b], Qmax: -qd[b], Pmin: -pd[b], Pmax: -pd[b],
                    Name: $"device injections at bus {bus.Id}", Type: "device", PowerFlowOnly: true));
                nb = bus;
                // A bus typed PV with no generator of its own and no device holding it is solved as PQ, as before.
                if (bus.Type == BusType.Pv && !vgBus.ContainsKey(bus.Id) && !model.Generators.Any(g => g.Bus == bus.Id)) nb = nb with { Type = BusType.Pq };
            }
            if (bs is not null && bs[b] != 0) nb = nb with { Bs = nb.Bs + bs[b] };
            if (vgBus.TryGetValue(bus.Id, out double vg) && nb.Type == BusType.Pq) nb = nb with { Type = BusType.Pv, Vm = vg };
            buses[b] = nb;
        }
        devQ = qd;
        IReadOnlyList<Generator> gens = extra.Count == 0 ? model.Generators : model.AllGenerators.Concat(extra).ToList();
        return model.CopyWith(buses: buses, generators: extra.Count == 0 ? null : gens,
            lccLinks: Array.Empty<LccDcLink>(), vscLinks: Array.Empty<VscDcLink>(), mtdcLinks: Array.Empty<MtdcLink>(),
            factsDevices: Array.Empty<FactsDevice>(), staticVarCompensators: Array.Empty<StaticVarCompensator>(),
            inductionMachines: Array.Empty<InductionMachine>());
    }

    /// <summary>A converter's reactive injection: its power factor's for MODE 2; for MODE 1 the bus's voltage source's
    /// (<see cref="SourceQ"/>; the converter is the bus's only source).</summary>
    private static double ConverterQ(GridModel acModel, PowerFlowResult r, VscConverter c, double p, double[] devQ, Dictionary<int, int> busIndex)
    {
        if (c.AcControl == VscAcControl.PowerFactor) return VscDcLinks.PowerFactorQ(p, c.AcSetpoint);
        return SourceQ(acModel, r, c.Bus, devQ, busIndex);
    }

    /// <summary>The reactive power of the one voltage source at <paramref name="bus"/> in the AC model: the bus's solved
    /// net reactive injection plus everything else drawn there, the bus load (at the solved voltage for a ZIP load) and,
    /// at a ZIP bus, the other devices' draws (injected there as a fixed unit). For a constant-power bus this is the
    /// injection plus the AC model's bus Qd, exactly as before.</summary>
    private static double SourceQ(GridModel acModel, PowerFlowResult r, int bus, double[] devQ, Dictionary<int, int> busIndex)
    {
        int i = IndexOf(r, bus);
        Bus b = acModel.Buses[busIndex[bus]];
        if (b.Zip is { VariesWithVoltage: true } zip) return r.QInjMvar[i] + (b.Qd * zip.QMultiplier(r.Vm[i]) + devQ[busIndex[bus]]);
        return r.QInjMvar[i] + b.Qd;
    }

    /// <summary>The FACTS shunt elements (indices into <see cref="GridModel.FactsDevices"/>) and static var compensators
    /// (indices into <see cref="GridModel.StaticVarCompensators"/>) that hold one bus's voltage.</summary>
    private sealed class VoltageHolders
    {
        public readonly List<int> Facts = new();
        public readonly List<int> Svcs = new();
        public int Count => Facts.Count + Svcs.Count;
    }

    /// <summary>Every FACTS shunt element and compensator bus must exist, must not be the slack bus and must not also be
    /// held by a generator or a VSC converter; a UPFC's terminal end bus must exist and differ from its sending end bus.
    /// Several FACTS shunt elements and compensators may hold one bus together (PSS/E RMPCT sharing): each then needs a
    /// positive finite RMPCT, and the set-points known before the solve (every VSET, every fixed compensator set-point)
    /// must be one value, since the bus can be held at only one voltage. Returns the holders of each bus.</summary>
    private static Dictionary<int, VoltageHolders> CheckFactsBuses(GridModel model, Dictionary<int, int> busIndex)
    {
        var vscHeld = new Dictionary<int, string>();
        foreach (VscDcLink l in model.VscLinks)
            foreach (VscConverter c in new[] { l.First, l.Second })
                if (c.AcControl == VscAcControl.Voltage) vscHeld[c.Bus] = $"VSC link {l.Name}'s converter";
        var holders = new Dictionary<int, VoltageHolders>();
        VoltageHolders Hold(int bus, string what)
        {
            if (!busIndex.TryGetValue(bus, out int b)) throw new InvalidOperationException($"{what}: bus {bus} is not in the model.");
            if (model.Buses[b].Type == BusType.Slack)
                throw new InvalidOperationException($"{what} controls the voltage of bus {bus}, the slack bus; GridSim cannot share the slack bus's voltage control.");
            if (model.Generators.Any(g => g.Bus == bus))
                throw new InvalidOperationException($"{what} controls the voltage of bus {bus}, which also has generators; GridSim cannot share the bus's voltage control between them.");
            if (vscHeld.TryGetValue(bus, out string? other))
                throw new InvalidOperationException($"{what} controls the voltage of bus {bus}, which {other} also controls; GridSim cannot share the bus's voltage control between them.");
            if (!holders.TryGetValue(bus, out VoltageHolders? h)) holders[bus] = h = new VoltageHolders();
            return h;
        }
        for (int k = 0; k < model.FactsDevices.Count; k++)
        {
            FactsDevice d = model.FactsDevices[k];
            if (d.Mode == FactsMode.Upfc)
            {
                if (!busIndex.ContainsKey(d.TerminalBus)) throw new InvalidOperationException($"FACTS device {d.Name}: terminal end bus {d.TerminalBus} is not in the model.");
                if (d.TerminalBus == d.SendingBus) throw new InvalidOperationException($"FACTS device {d.Name}: its terminal end bus is its sending end bus {d.SendingBus}.");
                if (!(d.ShuntMaxMva > 0)) throw new InvalidOperationException($"FACTS device {d.Name}: a UPFC needs its shunt element (SHMX above 0) to supply its series element's active power.");
            }
            if (d.ShuntMaxMva > 0) Hold(d.SendingBus, $"FACTS device {d.Name}'s shunt element").Facts.Add(k);
            else if (!busIndex.ContainsKey(d.SendingBus)) throw new InvalidOperationException($"FACTS device {d.Name}: bus {d.SendingBus} is not in the model.");
        }
        for (int k = 0; k < model.StaticVarCompensators.Count; k++)
        {
            StaticVarCompensator c = model.StaticVarCompensators[k];
            Hold(c.Bus, $"static var compensator {c.Name}").Svcs.Add(k);
        }

        // Shared buses: RMPCT must make a share, and the set-points fixed before the solve must agree.
        foreach ((int bus, VoltageHolders h) in holders)
        {
            if (h.Count < 2) continue;
            string? firstWho = null;
            double firstV = double.NaN;
            void Setpoint(string who, double v, string what)
            {
                if (firstWho is null) { firstWho = who; firstV = v; return; }
                // Exact comparison on purpose: two set-points one ulp apart are still two set-points.
                if (v != firstV)
                    throw new InvalidOperationException($"bus {bus} is held by {firstWho} at {FactsDevices.F(firstV)} pu and by {who} at {FactsDevices.F(v)} pu ({what}); one bus can be held at only one voltage, and GridSim does not choose between conflicting set-points.");
            }
            void Rmpct(string who, double p)
            {
                if (!(p > 0) || !double.IsFinite(p))
                    throw new InvalidOperationException($"{who} shares the voltage control of bus {bus} with other devices, so its RMPCT must be a positive finite percentage; it is {FactsDevices.F(p)}.");
            }
            foreach (int k in h.Facts)
            {
                FactsDevice d = model.FactsDevices[k];
                Rmpct($"FACTS device {d.Name}'s shunt element", d.RmpctPercent);
                Setpoint($"FACTS device {d.Name}'s shunt element", d.VsetPu, "VSET");
            }
            foreach (int k in h.Svcs)
            {
                StaticVarCompensator c = model.StaticVarCompensators[k];
                Rmpct($"static var compensator {c.Name}", c.RmpctPercent);
                if (c.VhiPu == c.VloPu) Setpoint($"static var compensator {c.Name}", c.VhiPu, "its fixed set-point");
            }
        }
        return holders;
    }

    /// <summary>
    /// Each device's share of the reactive power its bus's voltage sources supply, from the state the AC model was built
    /// with: a device alone on its bus takes all of it (1, exactly, so a single device's numbers are unchanged); on a bus
    /// held by several, each device still holding the voltage (a FACTS shunt element not at its current rating, a
    /// compensator in <see cref="SvcState.Regulating"/>) takes its RMPCT over the sum of their RMPCTs, the PSS/E sharing
    /// rule (RMPCT: "percent of the total Mvar required to hold the voltage at the bus ... that are to be contributed" by
    /// the device). A device at a limit is a fixed injection, outside the bus's sources, and its share is 0.
    /// </summary>
    private static (double[] Facts, double[] Svcs) Shares(GridModel model, Dictionary<int, VoltageHolders> holders, bool[] factsLimited, SvcState[] svcState)
    {
        var f = new double[model.FactsDevices.Count];
        var s = new double[model.StaticVarCompensators.Count];
        foreach (VoltageHolders h in holders.Values)
        {
            if (h.Count == 1)
            {
                if (h.Facts.Count == 1) f[h.Facts[0]] = 1.0; else s[h.Svcs[0]] = 1.0;
                continue;
            }
            // Sum in a fixed order (FACTS in model order, then compensators in model order) so the shares are deterministic.
            double sum = 0;
            foreach (int k in h.Facts) if (!factsLimited[k]) sum += model.FactsDevices[k].RmpctPercent;
            foreach (int k in h.Svcs) if (svcState[k] == SvcState.Regulating) sum += model.StaticVarCompensators[k].RmpctPercent;
            if (sum == 0) continue;   // nothing holds the bus: every device is a fixed injection or at BINIT
            foreach (int k in h.Facts) if (!factsLimited[k]) f[k] = model.FactsDevices[k].RmpctPercent / sum;
            foreach (int k in h.Svcs) if (svcState[k] == SvcState.Regulating) s[k] = model.StaticVarCompensators[k].RmpctPercent / sum;
        }
        return (f, s);
    }

    /// <summary>After this round's updates: the devices still holding each shared bus must hold the same voltage. A
    /// compensator that has started holding its band edge while another device holds a different set-point is a conflict
    /// PSS/E's data does not resolve, so the solve fails loud naming both.</summary>
    private static string? SetpointConflict(GridModel model, Dictionary<int, VoltageHolders> holders, bool[] factsLimited, SvcState[] svcState, double[] svcTarget)
    {
        foreach ((int bus, VoltageHolders h) in holders)
        {
            if (h.Count < 2) continue;
            string? firstWho = null;
            double firstV = double.NaN;
            foreach (int k in h.Facts)
            {
                if (factsLimited[k]) continue;
                FactsDevice d = model.FactsDevices[k];
                if (firstWho is null) { firstWho = $"FACTS device {d.Name}'s shunt element"; firstV = d.VsetPu; }
            }
            foreach (int k in h.Svcs)
            {
                if (svcState[k] != SvcState.Regulating) continue;
                StaticVarCompensator c = model.StaticVarCompensators[k];
                if (firstWho is null) { firstWho = $"static var compensator {c.Name}"; firstV = svcTarget[k]; continue; }
                if (svcTarget[k] != firstV)
                    return $"bus {bus}: static var compensator {c.Name} has started holding {FactsDevices.F(svcTarget[k])} pu (the edge of its band) while {firstWho} holds {FactsDevices.F(firstV)} pu; one bus can be held at only one voltage, and GridSim does not choose between conflicting set-points";
            }
        }
        return null;
    }

    /// <summary>The reactive power a FACTS device's shunt element source gives its sending end bus: the fixed value when
    /// it is at its current rating, else its share of the bus's voltage sources (<see cref="SourceQ"/>).</summary>
    private static double FactsSourceQ(GridModel acModel, PowerFlowResult r, FactsDevice d, double[] devQ, Dictionary<int, int> busIndex,
        bool limited, double fixedQ, double share)
    {
        if (limited) return fixedQ;
        double q = SourceQ(acModel, r, d.SendingBus, devQ, busIndex);
        return share == 1.0 ? q : q * share;
    }

    /// <summary>After an AC solve: a regulating shunt element over its current rating is fixed at the reactive power the
    /// rating allows; a limited one has that value re-taken at the solved voltage. Sets <paramref name="changed"/> when
    /// any input of the next AC solve moves; returns a failure message when a shunt element's active power alone
    /// exceeds its rating.</summary>
    private static string? UpdateFacts(GridModel model, Dictionary<int, int> busIndex, PowerFlowResult r, GridModel acModel, double[] devQ,
        bool[] factsLimited, double[] factsQ, double[] share, ref bool changed)
    {
        for (int k = 0; k < factsLimited.Length; k++)
        {
            FactsDevice d = model.FactsDevices[k];
            if (!(d.ShuntMaxMva > 0)) continue;
            double qSource = FactsSourceQ(acModel, r, d, devQ, busIndex, factsLimited[k], factsQ[k], share[k]);
            (double pSh, double qSh, double vm, double siIm) = Shunt(model, r, d, qSource);
            double limit = d.ShuntMaxMva * vm;
            if (!factsLimited[k])
            {
                if (!(pSh * pSh + qSh * qSh > limit * limit * (1 + 1e-12))) continue;
            }
            double? qMax = FactsDevices.ShuntQMax(d, vm, pSh);
            if (qMax is null)
                return $"FACTS device {d.Name}: its shunt element carries {FactsDevices.F(Math.Abs(pSh))} MW, more than its current rating of {FactsDevices.F(d.ShuntMaxMva)} MVA at unity voltage allows at {FactsDevices.F(vm)} pu";
            double wantSh = qSh >= 0 ? qMax.Value : -qMax.Value;
            double wantSource = wantSh - siIm;   // the shunt element's output less what the series element draws at the bus
            if (!factsLimited[k])
            {
                factsLimited[k] = true;
                factsQ[k] = wantSource;
                changed = true;
            }
            else if (Math.Abs(wantSource - factsQ[k]) > LccDcLinks.CouplingTolerance * (1 + Math.Abs(factsQ[k])))
            {
                factsQ[k] = wantSource;
                changed = true;
            }
        }
        return null;
    }

    /// <summary>A shunt element at the solved state, given its source's reactive power <paramref name="qSource"/>: its
    /// active and reactive output into the sending end bus (MW, Mvar), the sending end voltage (pu) and the reactive power
    /// the series element draws at the bus (Mvar; 0 unless a UPFC).</summary>
    private static (double PSh, double QSh, double Vm, double SiIm) Shunt(GridModel model, PowerFlowResult r, FactsDevice d, double qSource)
    {
        int i = IndexOf(r, d.SendingBus);
        double vm = r.Vm[i];
        if (d.Mode != FactsMode.Upfc) return (0, qSource, vm, 0);
        int j = IndexOf(r, d.TerminalBus);
        (double siRe, double siIm, _, _, _) = FactsDevices.Series(d, FactsDevices.Phasor(vm, r.VaDeg[i]), FactsDevices.Phasor(r.Vm[j], r.VaDeg[j]), model.BaseMva);
        // The device gives -PDES + j Q_source to the bus; the series element draws S_I there; the shunt converter's output
        // is the difference.
        double pSh = -d.PdesMw + siRe * model.BaseMva, qSh = qSource + siIm * model.BaseMva;
        return (pSh, qSh, vm, siIm * model.BaseMva);
    }

    /// <summary>After an AC solve: a compensator at BINIT whose bus has left its band starts holding the band edge it
    /// crossed; a holding one whose share needs more than its range is fixed at the end of its range.</summary>
    private static string? UpdateSvcs(GridModel model, Dictionary<int, int> busIndex, PowerFlowResult r, GridModel acModel, double[] devQ,
        SvcState[] svcState, double[] svcTarget, double[] share, ref bool changed)
    {
        for (int k = 0; k < svcState.Length; k++)
        {
            StaticVarCompensator c = model.StaticVarCompensators[k];
            double vm = r.Vm[IndexOf(r, c.Bus)];
            switch (svcState[k])
            {
                case SvcState.Initial:
                    if (vm < c.VloPu) { svcState[k] = SvcState.Regulating; svcTarget[k] = c.VloPu; changed = true; }
                    else if (vm > c.VhiPu) { svcState[k] = SvcState.Regulating; svcTarget[k] = c.VhiPu; changed = true; }
                    break;
                case SvcState.Regulating:
                    double b = SvcSusceptance(c, SvcSourceQ(acModel, r, c, devQ, busIndex, share[k]), vm);
                    if (b > c.BmaxMvar) { svcState[k] = SvcState.AtMaximum; changed = true; }
                    else if (b < c.BminMvar) { svcState[k] = SvcState.AtMinimum; changed = true; }
                    break;
            }
        }
        return null;
    }

    /// <summary>A holding compensator's source reactive power: its share of the bus's voltage sources.</summary>
    private static double SvcSourceQ(GridModel acModel, PowerFlowResult r, StaticVarCompensator c, double[] devQ, Dictionary<int, int> busIndex, double share)
    {
        double q = SourceQ(acModel, r, c.Bus, devQ, busIndex);
        return share == 1.0 ? q : q * share;
    }

    /// <summary>A holding compensator's susceptance (Mvar at 1 pu): BINIT (in the bus shunt) plus its source's reactive
    /// power over V^2.</summary>
    private static double SvcSusceptance(StaticVarCompensator c, double qSource, double vm) => c.BinitMvar + qSource / (vm * vm);

    private static (FactsResult[] Results, string? Why) FactsResults(GridModel model, Dictionary<int, int> busIndex, PowerFlowResult r,
        GridModel acModel, double[] devQ, bool[] factsLimited, double[] factsQ, double[] share)
    {
        var res = new FactsResult[factsLimited.Length];
        string? why = null;
        for (int k = 0; k < res.Length; k++)
        {
            FactsDevice d = model.FactsDevices[k];
            double qSh = 0, shCur = 0, vSe = 0, iSe = 0, bridge = 0;
            int i = IndexOf(r, d.SendingBus);
            if (d.ShuntMaxMva > 0)
            {
                double qSource = FactsSourceQ(acModel, r, d, devQ, busIndex, factsLimited[k], factsQ[k], share[k]);
                (double pSh, double q, double vm, _) = Shunt(model, r, d, qSource);
                qSh = q;
                shCur = Math.Sqrt(pSh * pSh + q * q) / vm;
            }
            if (d.Mode == FactsMode.Upfc)
            {
                int j = IndexOf(r, d.TerminalBus);
                (_, _, double cur, double vs, double br) = FactsDevices.Series(d, FactsDevices.Phasor(r.Vm[i], r.VaDeg[i]), FactsDevices.Phasor(r.Vm[j], r.VaDeg[j]), model.BaseMva);
                vSe = vs;
                iSe = cur * model.BaseMva;
                bridge = br * model.BaseMva;
                why ??= FactsDevices.CheckUpfc(d, r.Vm[j], vs, cur, bridge, model.BaseMva);
            }
            res[k] = new FactsResult(d.Name, d.Mode, d.SendingBus, d.TerminalBus, qSh, shCur,
                d.Mode == FactsMode.Upfc ? d.PdesMw : 0, d.Mode == FactsMode.Upfc ? d.QdesMvar : 0, vSe, iSe, bridge, factsLimited[k]);
        }
        return (res, why);
    }

    private static SvcResult[] SvcResults(GridModel model, Dictionary<int, int> busIndex, PowerFlowResult r, GridModel acModel, double[] devQ,
        SvcState[] svcState, double[] svcTarget, double[] share)
    {
        var res = new SvcResult[svcState.Length];
        for (int k = 0; k < res.Length; k++)
        {
            StaticVarCompensator c = model.StaticVarCompensators[k];
            double vm = r.Vm[IndexOf(r, c.Bus)];
            double b = svcState[k] switch
            {
                SvcState.Regulating => SvcSusceptance(c, SvcSourceQ(acModel, r, c, devQ, busIndex, share[k]), vm),
                SvcState.AtMaximum => c.BmaxMvar,
                SvcState.AtMinimum => c.BminMvar,
                _ => c.BinitMvar,
            };
            res[k] = new SvcResult(c.Name, c.Bus, b, b * vm * vm, svcState[k] == SvcState.Initial ? double.NaN : svcTarget[k], svcState[k]);
        }
        return res;
    }

    private static int IndexOf(PowerFlowResult r, int bus)
    {
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == bus) return i;
        throw new InvalidOperationException($"bus {bus} is not in the solved result.");
    }

    private static VscLinkResult[] VscResults(GridModel model, PowerFlowResult r, GridModel acModel,
        (double FirstMw, double SecondMw, double CurrentKa, double FirstDcKv, double SecondDcKv, double FirstLossMw, double SecondLossMw)[] dc,
        double[] orderScale, bool[] limited, double[] devQ, Dictionary<int, int> busIndex)
    {
        var res = new VscLinkResult[model.VscLinks.Count];
        for (int k = 0; k < res.Length; k++)
        {
            VscDcLink l = model.VscLinks[k];
            VscConverterResult Side(VscConverter c, double p, double vdc, double loss, int s) => new(c.Bus, p, ConverterQ(acModel, r, c, p, devQ, busIndex), vdc, loss,
                c.DcControl == VscDcControl.Power ? c.DcSetpoint * orderScale[k] : double.NaN, limited[2 * k + s]);
            res[k] = new VscLinkResult(l.Name, dc[k].CurrentKa * 1000,
                Side(l.First, dc[k].FirstMw, dc[k].FirstDcKv, dc[k].FirstLossMw, 0),
                Side(l.Second, dc[k].SecondMw, dc[k].SecondDcKv, dc[k].SecondLossMw, 1));
        }
        return res;
    }

    private static bool LccSettled(LccLinkResult[] a, LccLinkResult[] b)
    {
        for (int k = 0; k < a.Length; k++)
        {
            if (a[k].Control != b[k].Control || a[k].RectifierTap != b[k].RectifierTap || a[k].InverterTap != b[k].InverterTap) return false;
            if (!Close(a[k].RectifierMw, b[k].RectifierMw) || !Close(a[k].RectifierMvar, b[k].RectifierMvar)
                || !Close(a[k].InverterMw, b[k].InverterMw) || !Close(a[k].InverterMvar, b[k].InverterMvar)) return false;
        }
        return true;
        static bool Close(double x, double y) => Math.Abs(x - y) <= LccDcLinks.CouplingTolerance * (1 + Math.Abs(x));
    }

    private static bool ImSettled(InductionMachineResult[] a, InductionMachineResult[] b)
    {
        for (int k = 0; k < a.Length; k++)
        {
            if (Math.Abs(a[k].Mw - b[k].Mw) > LccDcLinks.CouplingTolerance * (1 + Math.Abs(a[k].Mw))) return false;
            if (Math.Abs(a[k].Mvar - b[k].Mvar) > LccDcLinks.CouplingTolerance * (1 + Math.Abs(a[k].Mvar))) return false;
        }
        return true;
    }

    private static bool MtdcSettled(MtdcLinkResult[] a, MtdcLinkResult[] b)
    {
        for (int k = 0; k < a.Length; k++)
            for (int c = 0; c < a[k].Converters.Count; c++)
            {
                MtdcConverterResult x = a[k].Converters[c], y = b[k].Converters[c];
                if (x.Tap != y.Tap || x.Rectifier != y.Rectifier) return false;
                if (Math.Abs(x.DrawnMw - y.DrawnMw) > LccDcLinks.CouplingTolerance * (1 + Math.Abs(x.DrawnMw))) return false;
                if (Math.Abs(x.AbsorbedMvar - y.AbsorbedMvar) > LccDcLinks.CouplingTolerance * (1 + Math.Abs(x.AbsorbedMvar))) return false;
            }
        return true;
    }

    private static PowerFlowResult With(PowerFlowResult r, GridModel model, LccLinkResult[] lcc, VscLinkResult[]? vsc,
        string? lccNote, string? vscNote, bool notConverged = false, MtdcLinkResult[]? mtdc = null, string? mtdcNote = null,
        FactsResult[]? facts = null, SvcResult[]? svcs = null, string? factsNote = null,
        InductionMachineResult[]? im = null, string? inductionNote = null) => new()
    {
        Converged = r.Converged && !notConverged,
        Iterations = r.Iterations,
        MaxMismatch = r.MaxMismatch,
        Status = notConverged ? PowerFlowStatus.MaxIterations : r.Status,
        BusIds = r.BusIds,
        Vm = r.Vm,
        VaDeg = r.VaDeg,
        PInjMw = r.PInjMw,
        QInjMvar = r.QInjMvar,
        TotalLossMw = r.TotalLossMw,
        TotalLossMvar = r.TotalLossMvar,
        LineFlows = r.LineFlows,
        SolvedBusTypes = r.SolvedBusTypes,
        SweepVoltageStepPu = r.SweepVoltageStepPu,
        LccLinks = model.LccLinks.Count > 0 ? lcc : null,
        LccNote = lccNote,
        VscLinks = model.VscLinks.Count > 0 ? vsc : null,
        VscNote = vscNote,
        MtdcLinks = model.MtdcLinks.Count > 0 ? mtdc : null,
        MtdcNote = mtdcNote,
        FactsDevices = model.FactsDevices.Count > 0 ? facts : null,
        StaticVarCompensators = model.StaticVarCompensators.Count > 0 ? svcs : null,
        FactsNote = factsNote,
        InductionMachines = model.InductionMachines.Count > 0 ? im : null,
        InductionNote = inductionNote,
    };
}
