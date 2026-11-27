using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): binds the network device models of an RMS study (<see cref="RmsLoadedMachineData.Devices"/>) to the devices of
/// the case by name and initialises each on its power-flow state. Refused, naming the device: an unknown or ambiguous name,
/// a device bound twice, a power flow without that device's results or whose device solve did not settle, a FACTS device
/// that is not a STATCOM (a UPFC or series-impedance device stays at its power-flow injection), and any operating point the
/// model cannot hold.
/// </summary>
internal static class RmsDeviceBinding
{
    /// <summary>One terminal of a bound device: the unit the RMS network gets at its bus.</summary>
    internal sealed record DeviceTerminal(int Bus, string Name, double RatingPu, double P0, double Q0, Complex V0, RmsDevicePlan Plan);

    /// <summary>Largest active power (system pu) a generator-held SVC or STATCOM may carry in the power flow: its share of
    /// a bus's solved injection beyond the power-flow mismatch is refused.</summary>
    internal const double MaxFactsGeneratorPowerPu = 1e-6;

    /// <summary>The plan of an SVC or STATCOM held as a generator (no susceptance of it in the network; P must be 0).</summary>
    internal static RmsDevicePlan GeneratorFacts(RmsFactsData facts, string name, double baseMva, double p0, double q0, Complex v0)
    {
        if (!(Math.Abs(p0) <= MaxFactsGeneratorPowerPu))
            throw new ArgumentException($"{facts.Model} '{name}': the unit carries {ControlBlocks.Num(p0 * baseMva)} MW in the power flow; an SVC or STATCOM held as a generator injects reactive power only.");
        double v2 = v0.Real * v0.Real + v0.Imaginary * v0.Imaginary;
        return facts switch
        {
            Csvgn1Data c => new Csvgn1Plan(c, name, baseMva, q0 / Math.Max(v2, SubtransientInit.MinVoltageSquaredPu), 0.0, v0),
            Statcom1Data s => new Statcom1Plan(s, name, baseMva, q0, v0),
            _ => throw new ArgumentException($"'{name}': unknown device model {facts.GetType().Name}."),
        };
    }

    internal static (IReadOnlyList<DeviceTerminal> Terminals, Dictionary<int, (double P, double Q)> DrawnByBus) Bind(GridModel model,
        PowerFlowResult result, IReadOnlyList<RmsNetworkDeviceData> devices, Dictionary<int, Complex> vByBus, double baseMva)
    {
        var terminals = new List<DeviceTerminal>();
        var drawn = new Dictionary<int, (double P, double Q)>();
        var seen = new HashSet<(string Kind, string Name)>();
        Complex V(int bus) => vByBus.TryGetValue(bus, out Complex v) ? v : Complex.One;
        void Draw(int bus, double p, double q)
        {
            (double P, double Q) acc = drawn.GetValueOrDefault(bus);
            drawn[bus] = (acc.P + p, acc.Q + q);
        }
        for (int i = 0; i < devices.Count; i++)
        {
            RmsNetworkDeviceData d = devices[i] ?? throw new ArgumentException($"devices[{i}] is null.");
            ArgumentNullException.ThrowIfNull(d.DeviceName);
            if (!seen.Add((d.Kind, d.DeviceName)))
                throw new ArgumentException($"{d.Label}: the {d.Kind} is given a model twice.");
            switch (d)
            {
                case SvcDeviceData svc:
                {
                    ArgumentNullException.ThrowIfNull(svc.Svc);
                    int k = Find(model.StaticVarCompensators, x => x.Name, d);
                    StaticVarCompensator c = model.StaticVarCompensators[k];
                    if (result.StaticVarCompensators is not { } sr || sr.Count != model.StaticVarCompensators.Count || result.FactsNote is not null)
                        throw new ArgumentException($"{d.Label}: the power flow gives no settled compensator results" + Note(result.FactsNote));
                    Complex v0 = V(c.Bus);
                    var plan = new Csvgn1Plan(svc.Svc, c.Name, baseMva, sr[k].SusceptanceMvar / baseMva, c.BinitMvar / baseMva, v0);
                    double q0 = (sr[k].SusceptanceMvar - c.BinitMvar) / baseMva * (v0.Real * v0.Real + v0.Imaginary * v0.Imaginary);
                    terminals.Add(new DeviceTerminal(c.Bus, c.Name, svc.Svc.MbaseMva / baseMva, 0.0, q0, v0, plan));
                    Draw(c.Bus, 0.0, -q0 * baseMva);
                    break;
                }
                case StatcomDeviceData st:
                {
                    ArgumentNullException.ThrowIfNull(st.Statcom);
                    int k = Find(model.FactsDevices, x => x.Name, d);
                    FactsDevice f = model.FactsDevices[k];
                    if (f.Mode != FactsMode.Statcom)
                        throw new ArgumentException($"{d.Label}: the FACTS device is a {f.Mode} device; only a STATCOM (shunt element only) has an RMS model, so a UPFC or series-impedance device stays at its power-flow injection and cannot be given one.");
                    if (!(f.ShuntMaxMva > 0))
                        throw new ArgumentException($"{d.Label}: the FACTS device has no shunt element (SHMX 0).");
                    if (result.FactsDevices is not { } fr || fr.Count != model.FactsDevices.Count || result.FactsNote is not null)
                        throw new ArgumentException($"{d.Label}: the power flow gives no settled FACTS results" + Note(result.FactsNote));
                    Complex v0 = V(f.SendingBus);
                    var plan = new Statcom1Plan(st.Statcom, f.Name, baseMva, fr[k].ShuntMvar / baseMva, v0);
                    terminals.Add(new DeviceTerminal(f.SendingBus, f.Name, st.Statcom.MbaseMva / baseMva, 0.0, fr[k].ShuntMvar / baseMva, v0, plan));
                    Draw(f.SendingBus, 0.0, -fr[k].ShuntMvar);
                    break;
                }
                case LccLinkDynamicsData lcc:
                {
                    ArgumentNullException.ThrowIfNull(lcc.Lcc);
                    int k = Find(model.LccLinks, x => x.Name, d);
                    LccDcLink l = model.LccLinks[k];
                    if (result.LccLinks is not { } lr || lr.Count != model.LccLinks.Count || result.LccNote is not null)
                        throw new ArgumentException($"{d.Label}: the power flow gives no settled LCC results" + Note(result.LccNote));
                    LccLinkResult r = lr[k];
                    var link = new LccLinkPlan(l, r, lcc.Lcc, baseMva, V(l.Rectifier.Bus), V(l.Inverter.Bus));
                    double rating = Math.Abs(r.RectifierMw) / baseMva;
                    terminals.Add(new DeviceTerminal(l.Rectifier.Bus, $"{l.Name} rectifier", rating, -r.RectifierMw / baseMva, -r.RectifierMvar / baseMva,
                        V(l.Rectifier.Bus), new LccTerminalPlan(link, 0)));
                    terminals.Add(new DeviceTerminal(l.Inverter.Bus, $"{l.Name} inverter", rating, r.InverterMw / baseMva, -r.InverterMvar / baseMva,
                        V(l.Inverter.Bus), new LccTerminalPlan(link, 1)));
                    // What the power flow's AC model drew at each bus for this link (HvdcCoupling: the converters as loads).
                    Draw(l.Rectifier.Bus, r.RectifierMw, r.RectifierMvar);
                    Draw(l.Inverter.Bus, -r.InverterMw, r.InverterMvar);
                    break;
                }
                case VscLinkDynamicsData vsc:
                {
                    ArgumentNullException.ThrowIfNull(vsc.Vsc);
                    int k = Find(model.VscLinks, x => x.Name, d);
                    VscDcLink l = model.VscLinks[k];
                    if (result.VscLinks is not { } vr || vr.Count != model.VscLinks.Count || result.VscNote is not null)
                        throw new ArgumentException($"{d.Label}: the power flow gives no settled VSC results" + Note(result.VscNote));
                    VscLinkResult r = vr[k];
                    var link = new VscLinkPlan(l, r, vsc.Vsc, baseMva, V(l.First.Bus), V(l.Second.Bus));
                    double rating = vsc.Vsc.MbaseMva / baseMva;
                    terminals.Add(new DeviceTerminal(l.First.Bus, $"{l.Name} converter 1", rating, r.First.Mw / baseMva, r.First.Mvar / baseMva,
                        V(l.First.Bus), new VscTerminalPlan(link, 0)));
                    terminals.Add(new DeviceTerminal(l.Second.Bus, $"{l.Name} converter 2", rating, r.Second.Mw / baseMva, r.Second.Mvar / baseMva,
                        V(l.Second.Bus), new VscTerminalPlan(link, 1)));
                    Draw(l.First.Bus, -r.First.Mw, -r.First.Mvar);
                    Draw(l.Second.Bus, -r.Second.Mw, -r.Second.Mvar);
                    break;
                }
                case MtdcGridDynamicsData mtdc:
                {
                    // WP16 (RC5): a multi-terminal DC link, one terminal unit per converter in file order.
                    int k = Find(model.MtdcLinks, x => x.Name, d);
                    MtdcLink l = model.MtdcLinks[k];
                    if (result.MtdcLinks is not { } mr || mr.Count != model.MtdcLinks.Count || result.MtdcNote is not null)
                        throw new ArgumentException($"{d.Label}: the power flow gives no settled multi-terminal DC results" + Note(result.MtdcNote));
                    MtdcLinkResult r = mr[k];
                    var acKv = new double[l.Converters.Count];
                    for (int c = 0; c < acKv.Length; c++)
                    {
                        int bus = l.Converters[c].Bus;
                        Bus? b = model.Buses.FirstOrDefault(x => x.Id == bus);
                        acKv[c] = b is not null && b.BaseKv > 0 ? b.BaseKv : l.Converters[c].BaseKv;
                    }
                    var grid = new MtdcGridPlan(l, r, mtdc, baseMva, l.Converters.Select(c => V(c.Bus)).ToArray(), model.FrequencyHz, acKv);
                    for (int c = 0; c < l.Converters.Count; c++)
                    {
                        MtdcConverterResult cr = r.Converters[c];
                        int bus = l.Converters[c].Bus;
                        terminals.Add(new DeviceTerminal(bus, $"{l.Name} converter {bus.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            grid.Controls[c].MbaseMva / baseMva, -cr.DrawnMw / baseMva, -cr.AbsorbedMvar / baseMva, V(bus), new MtdcTerminalPlan(grid, c)));
                        // What the power flow's AC model drew at the bus for this converter (HvdcCoupling: the converters as loads).
                        Draw(bus, cr.DrawnMw, cr.AbsorbedMvar);
                    }
                    break;
                }
                default:
                    throw new ArgumentException($"devices[{i}]: unknown device kind {d.GetType().Name}.");
            }
        }
        return (terminals, drawn);
    }

    private static string Note(string? note) => note is null ? "." : $" ({note}).";

    private static int Find<T>(IReadOnlyList<T> list, Func<T, string> name, RmsNetworkDeviceData d)
    {
        int found = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.Equals(name(list[i]), d.DeviceName, StringComparison.Ordinal)) continue;
            if (found >= 0) throw new ArgumentException($"{d.Label}: the model has more than one {d.Kind} named '{d.DeviceName}'.");
            found = i;
        }
        if (found < 0) throw new ArgumentException($"{d.Label}: the model has no {d.Kind} named '{d.DeviceName}'.");
        return found;
    }
}
