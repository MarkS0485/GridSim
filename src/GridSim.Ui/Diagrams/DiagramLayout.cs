using System.Globalization;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Diagrams;

/// <summary>Which way a bay runs from its busbar.</summary>
public enum BayDirection
{
    /// <summary>Below the busbar (symbols as drawn in the library).</summary>
    Down,
    /// <summary>Above the busbar (symbols turned half way round, so their "top" port still faces the busbar).</summary>
    Up,
}

/// <summary>What a feeder bay ends in.</summary>
public enum FeederEnd
{
    /// <summary>Nothing: the bay stops after its switchgear.</summary>
    None,
    /// <summary>An overhead line end.</summary>
    Line,
    /// <summary>A cable end.</summary>
    Cable,
    /// <summary>A load.</summary>
    Load,
    /// <summary>A generator.</summary>
    Generator,
}

/// <summary>A busbar row made by <see cref="DiagramLayout.BusbarRow"/>.</summary>
/// <param name="Id">The bar's symbol id.</param>
/// <param name="TapPorts">The tap port names, left to right ("T0", "T1" ...).</param>
/// <param name="TapPositions">Where each tap is on the diagram.</param>
public sealed record BusbarRowResult(string Id, IReadOnlyList<string> TapPorts, IReadOnlyList<ScreenPoint> TapPositions);

/// <summary>A feeder bay made by <see cref="DiagramLayout.FeederBay"/>: its symbol ids, busbar side first.</summary>
/// <param name="SymbolIds">The bay's symbols from the busbar outwards.</param>
/// <param name="ConnectionIds">The bay's connections from the busbar outwards.</param>
public sealed record FeederBayResult(IReadOnlyList<string> SymbolIds, IReadOnlyList<string> ConnectionIds);

/// <summary>A transformer bay made by <see cref="DiagramLayout.TransformerBetween"/>.</summary>
/// <param name="UpperBreakerId">The breaker under the upper busbar.</param>
/// <param name="TransformerId">The transformer.</param>
/// <param name="LowerBreakerId">The breaker over the lower busbar.</param>
/// <param name="ConnectionIds">The four connections, upper busbar first.</param>
public sealed record TransformerBayResult(string UpperBreakerId, string TransformerId, string LowerBreakerId, IReadOnlyList<string> ConnectionIds);

/// <summary>
/// Layout helpers that place standard arrangements on a diagram (UI programme ASSETS A1b). Every position is plain
/// arithmetic on the inputs (no search, no measuring), so the same call always places the same symbols at the same
/// points: a busbar row with evenly spaced bay taps, a feeder bay hanging from a tap (disconnector, breaker, optional
/// current transformer, then the feeder end), and a transformer bay between two busbars (breaker, transformer,
/// breaker).
/// </summary>
public static class DiagramLayout
{
    /// <summary>The default distance between neighbouring bay taps on a busbar row.</summary>
    public const double DefaultBayPitch = 80;

    /// <summary>The distance from a busbar to the centre of the first symbol of a bay.</summary>
    public const double BayStart = 40;

    /// <summary>The distance between the centres of neighbouring symbols in a bay.</summary>
    public const double BayPitch = 50;

    /// <summary>
    /// A horizontal busbar (or bus section) starting at <paramref name="leftEnd"/>, <paramref name="bays"/> pitches long,
    /// with one tap in the middle of each pitch: tap i at x = leftEnd.X + pitch * (i + 0.5).
    /// </summary>
    public static BusbarRowResult BusbarRow(DiagramModel model, string id, ScreenPoint leftEnd, int bays,
        double pitch = DefaultBayPitch, string label = "", DiagramSymbolKind kind = DiagramSymbolKind.Busbar, DiagramState? state = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (bays < 1) throw new ArgumentOutOfRangeException(nameof(bays), bays, "a busbar row needs at least one bay");
        if (!(pitch > 0) || !double.IsFinite(pitch)) throw new ArgumentOutOfRangeException(nameof(pitch), pitch, "the bay pitch must be a finite number above 0");
        if (kind is not (DiagramSymbolKind.Busbar or DiagramSymbolKind.BusSection))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "a busbar row is a busbar or a bus section");
        var taps = new double[bays];
        for (int i = 0; i < bays; i++) taps[i] = pitch * (i + 0.5);
        model.Add(new DiagramSymbol(id, kind, leftEnd, DiagramRotation.None, label, pitch * bays, taps) { State = state ?? DiagramState.Normal });
        return new BusbarRowResult(id,
            Enumerable.Range(0, bays).Select(i => "T" + i.ToString(CultureInfo.InvariantCulture)).ToList(),
            taps.Select(t => new ScreenPoint(leftEnd.X + t, leftEnd.Y)).ToList());
    }

    /// <summary>
    /// A feeder bay hanging from tap <paramref name="tap"/> of busbar <paramref name="busbarId"/>, running
    /// <paramref name="direction"/>: optionally a disconnector, then a circuit breaker, optionally a current
    /// transformer, then the feeder end. Symbol k (from 0) is centred <see cref="BayStart"/> + k * <see cref="BayPitch"/>
    /// from the busbar; ids are "{bayId}/DS", "/CB", "/CT", "/END"; the end symbol carries <paramref name="endLabel"/>.
    /// </summary>
    public static FeederBayResult FeederBay(DiagramModel model, string busbarId, int tap, string bayId, FeederEnd end,
        BayDirection direction = BayDirection.Down, bool disconnector = true, bool currentTransformer = false, string endLabel = "")
    {
        ArgumentNullException.ThrowIfNull(model);
        DiagramPort tapPort = model.Port(busbarId, "T" + tap.ToString(CultureInfo.InvariantCulture));
        double sign = direction == BayDirection.Down ? 1 : -1;
        DiagramRotation rot = direction == BayDirection.Down ? DiagramRotation.None : DiagramRotation.Half;

        var chain = new List<(string Id, DiagramSymbolKind Kind, string Near, string? Far, string Label)>();
        if (disconnector) chain.Add((bayId + "/DS", DiagramSymbolKind.Disconnector, "A", "B", ""));
        chain.Add((bayId + "/CB", DiagramSymbolKind.CircuitBreaker, "A", "B", ""));
        if (currentTransformer) chain.Add((bayId + "/CT", DiagramSymbolKind.CurrentTransformer, "A", "B", ""));
        switch (end)
        {
            case FeederEnd.None: break;
            case FeederEnd.Line: chain.Add((bayId + "/END", DiagramSymbolKind.LineEnd, "T", null, endLabel)); break;
            case FeederEnd.Cable: chain.Add((bayId + "/END", DiagramSymbolKind.CableEnd, "T", "C", endLabel)); break;
            case FeederEnd.Load: chain.Add((bayId + "/END", DiagramSymbolKind.Load, "T", null, endLabel)); break;
            case FeederEnd.Generator: chain.Add((bayId + "/END", DiagramSymbolKind.Generator, "T", null, endLabel)); break;
            default: throw new ArgumentOutOfRangeException(nameof(end), end, "not a feeder end");
        }

        var ids = new List<string>(chain.Count);
        var conns = new List<string>(chain.Count);
        string prevId = busbarId, prevPort = tapPort.Name;
        for (int k = 0; k < chain.Count; k++)
        {
            (string id, DiagramSymbolKind kind, string near, string? far, string label) = chain[k];
            var at = new ScreenPoint(tapPort.Position.X, tapPort.Position.Y + sign * (BayStart + k * BayPitch));
            model.Add(new DiagramSymbol(id, kind, at, rot, label));
            conns.Add(model.Connect(prevId, prevPort, id, near).Id);
            ids.Add(id);
            prevId = id;
            prevPort = far ?? "";
        }
        return new FeederBayResult(ids, conns);
    }

    /// <summary>
    /// A transformer bay between tap <paramref name="upperTap"/> of <paramref name="upperBusbarId"/> and tap
    /// <paramref name="lowerTap"/> of <paramref name="lowerBusbarId"/>, which must sit straight below it at least 160
    /// units lower: a breaker <see cref="BayStart"/> under the upper bar, the transformer half way, a breaker
    /// <see cref="BayStart"/> over the lower bar. Ids "{id}/CB1", "{id}/TX", "{id}/CB2".
    /// </summary>
    public static TransformerBayResult TransformerBetween(DiagramModel model, string upperBusbarId, int upperTap,
        string lowerBusbarId, int lowerTap, string id, string label = "")
    {
        ArgumentNullException.ThrowIfNull(model);
        DiagramPort up = model.Port(upperBusbarId, "T" + upperTap.ToString(CultureInfo.InvariantCulture));
        DiagramPort lo = model.Port(lowerBusbarId, "T" + lowerTap.ToString(CultureInfo.InvariantCulture));
        if (up.Position.X != lo.Position.X)
            throw new ArgumentException($"the two taps are not in line (x {up.Position.X} and {lo.Position.X})", nameof(lowerTap));
        if (!(lo.Position.Y - up.Position.Y >= 4 * BayStart))
            throw new ArgumentException($"the lower busbar must be at least {4 * BayStart} below the upper (gap {lo.Position.Y - up.Position.Y})", nameof(lowerBusbarId));
        double x = up.Position.X;
        string cb1 = id + "/CB1", tx = id + "/TX", cb2 = id + "/CB2";
        model.Add(new DiagramSymbol(cb1, DiagramSymbolKind.CircuitBreaker, new ScreenPoint(x, up.Position.Y + BayStart)));
        model.Add(new DiagramSymbol(tx, DiagramSymbolKind.Transformer2W, new ScreenPoint(x, (up.Position.Y + lo.Position.Y) / 2), DiagramRotation.None, label));
        model.Add(new DiagramSymbol(cb2, DiagramSymbolKind.CircuitBreaker, new ScreenPoint(x, lo.Position.Y - BayStart)));
        var conns = new List<string>
        {
            model.Connect(upperBusbarId, up.Name, cb1, "A").Id,
            model.Connect(cb1, "B", tx, "HV").Id,
            model.Connect(tx, "LV", cb2, "A").Id,
            model.Connect(cb2, "B", lowerBusbarId, lo.Name).Id,
        };
        return new TransformerBayResult(cb1, tx, cb2, conns);
    }
}
