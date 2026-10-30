using GridSim.Ui.Schematic;

namespace GridSim.Ui.Diagrams;

/// <summary>
/// A connection between two ports: its end points and its orthogonal route (every segment horizontal or vertical), and
/// its own live state (a connection is a conductor: energised or not, with an optional loading reading).
/// </summary>
/// <param name="Id">A unique id within the diagram.</param>
/// <param name="From">The port it starts at.</param>
/// <param name="To">The port it ends at.</param>
/// <param name="Points">The route, from <paramref name="From"/> to <paramref name="To"/>, at least two points.</param>
public sealed record DiagramConnection(string Id, DiagramPort From, DiagramPort To, IReadOnlyList<ScreenPoint> Points)
{
    /// <summary>The live state of the conductor.</summary>
    public DiagramState State { get; init; } = DiagramState.Normal;
}

/// <summary>
/// The orthogonal router: a route between two ports made only of horizontal and vertical segments, worked out from the
/// two positions and the start port's direction alone (so it is deterministic and exact: no search, no rounding).
/// <list type="bullet">
/// <item>Ports in line (same x or same y): one straight segment.</item>
/// <item>A start port facing up or down: down (or up) to half way, across, then on to the end (a "Z" on its side).</item>
/// <item>A start port facing left or right: across to half way, down (or up), then on to the end.</item>
/// </list>
/// </summary>
public static class OrthogonalRouter
{
    /// <summary>The route from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IReadOnlyList<ScreenPoint> Route(DiagramPort from, DiagramPort to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ScreenPoint a = from.Position, b = to.Position;
        if (a.X == b.X || a.Y == b.Y) return [a, b];
        if (DiagramGeometry.IsVertical(from.Direction))
        {
            double midY = (a.Y + b.Y) / 2;
            return [a, new ScreenPoint(a.X, midY), new ScreenPoint(b.X, midY), b];
        }
        double midX = (a.X + b.X) / 2;
        return [a, new ScreenPoint(midX, a.Y), new ScreenPoint(midX, b.Y), b];
    }
}

/// <summary>
/// The diagram model (UI programme ASSETS A1b): placed symbols, the connections between their ports, and free labels,
/// plus a version number that goes up on every change (a renderer caches its drawing per version). Every mistake is
/// refused loudly where it is made: a duplicate or empty id, a port that does not exist, a busbar without a length or
/// with taps off the bar, a length or taps on a symbol that is not a bar, a position that is not a finite number.
///
/// <para>Symbols do not move once placed (a connection's route is worked out when it is made); their live state
/// changes through <see cref="SetState"/>, which keeps the position and bumps the version.</para>
/// </summary>
public sealed class DiagramModel
{
    private readonly List<DiagramSymbol> _symbols = new();
    private readonly Dictionary<string, int> _symbolIndex = new(StringComparer.Ordinal);
    private readonly List<DiagramConnection> _connections = new();
    private readonly Dictionary<string, int> _connectionIndex = new(StringComparer.Ordinal);
    private readonly List<DiagramLabel> _labels = new();

    /// <summary>The symbols in drawing order (a later symbol is drawn over, and hit before, an earlier one).</summary>
    public IReadOnlyList<DiagramSymbol> Symbols => _symbols;

    /// <summary>The connections in drawing order (all drawn under the symbols).</summary>
    public IReadOnlyList<DiagramConnection> Connections => _connections;

    /// <summary>The free labels (drawn over everything).</summary>
    public IReadOnlyList<DiagramLabel> Labels => _labels;

    /// <summary>Goes up by one on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change (a front end redraws).</summary>
    public event EventHandler? Changed;

    private void Bump()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Place a symbol. Returns it.</summary>
    public DiagramSymbol Add(DiagramSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (string.IsNullOrEmpty(symbol.Id)) throw new ArgumentException("a symbol needs an id", nameof(symbol));
        if (_symbolIndex.ContainsKey(symbol.Id)) throw new ArgumentException($"duplicate symbol id '{symbol.Id}'", nameof(symbol));
        if (!double.IsFinite(symbol.Position.X) || !double.IsFinite(symbol.Position.Y))
            throw new ArgumentException($"symbol '{symbol.Id}' has a position that is not a finite number", nameof(symbol));
        ArgumentNullException.ThrowIfNull(symbol.Label, nameof(symbol));
        if (symbol.IsBar)
        {
            if (!(symbol.Length > 0) || !double.IsFinite(symbol.Length))
                throw new ArgumentException($"bar '{symbol.Id}' needs a finite length above 0 (got {symbol.Length})", nameof(symbol));
            foreach (double t in symbol.Taps ?? [])
                if (!(t >= 0 && t <= symbol.Length))
                    throw new ArgumentException($"bar '{symbol.Id}' has a tap at {t}, off the bar (0 to {symbol.Length})", nameof(symbol));
        }
        else if (symbol.Length != 0 || symbol.Taps is { Count: > 0 })
        {
            throw new ArgumentException($"symbol '{symbol.Id}' is a {symbol.Kind}: only a bar has a length and taps", nameof(symbol));
        }
        _symbolIndex[symbol.Id] = _symbols.Count;
        _symbols.Add(symbol);
        Bump();
        return symbol;
    }

    /// <summary>The symbol with this id (throws when there is none).</summary>
    public DiagramSymbol Symbol(string id) =>
        _symbolIndex.TryGetValue(id, out int i) ? _symbols[i] : throw new KeyNotFoundException($"no symbol '{id}' in the diagram");

    /// <summary>The connection with this id (throws when there is none).</summary>
    public DiagramConnection Connection(string id) =>
        _connectionIndex.TryGetValue(id, out int i) ? _connections[i] : throw new KeyNotFoundException($"no connection '{id}' in the diagram");

    /// <summary>A port of a placed symbol (throws when the symbol or the port does not exist).</summary>
    public DiagramPort Port(string symbolId, string port)
    {
        DiagramSymbol s = Symbol(symbolId);
        foreach (DiagramPort p in SymbolLibrary.Ports(s))
            if (string.Equals(p.Name, port, StringComparison.Ordinal)) return p;
        throw new KeyNotFoundException($"symbol '{symbolId}' ({s.Kind}) has no port '{port}'");
    }

    /// <summary>
    /// Join two ports with an orthogonal route (<see cref="OrthogonalRouter"/>). The id defaults to
    /// "from.port-to.port". Returns the connection.
    /// </summary>
    public DiagramConnection Connect(string fromSymbol, string fromPort, string toSymbol, string toPort, string? id = null)
    {
        DiagramPort a = Port(fromSymbol, fromPort), b = Port(toSymbol, toPort);
        string cid = id ?? $"{fromSymbol}.{fromPort}-{toSymbol}.{toPort}";
        if (cid.Length == 0) throw new ArgumentException("a connection needs an id", nameof(id));
        if (_connectionIndex.ContainsKey(cid)) throw new ArgumentException($"duplicate connection id '{cid}'", nameof(id));
        var c = new DiagramConnection(cid, a, b, OrthogonalRouter.Route(a, b));
        _connectionIndex[cid] = _connections.Count;
        _connections.Add(c);
        Bump();
        return c;
    }

    /// <summary>Add a free label.</summary>
    public void AddLabel(DiagramLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        _labels.Add(label);
        Bump();
    }

    /// <summary>Set a symbol's live state (its position, label and shape stay).</summary>
    public void SetState(string symbolId, DiagramState state)
    {
        int i = _symbolIndex.TryGetValue(symbolId, out int at) ? at : throw new KeyNotFoundException($"no symbol '{symbolId}' in the diagram");
        _symbols[i] = _symbols[i] with { State = state };
        Bump();
    }

    /// <summary>Set a connection's live state.</summary>
    public void SetConnectionState(string connectionId, DiagramState state)
    {
        int i = _connectionIndex.TryGetValue(connectionId, out int at) ? at : throw new KeyNotFoundException($"no connection '{connectionId}' in the diagram");
        _connections[i] = _connections[i] with { State = state };
        Bump();
    }

    /// <summary>
    /// The extent of the drawing: every symbol's bounds and every route point (labels are not measured: a front end
    /// adds a margin). Null for an empty diagram.
    /// </summary>
    public DiagramRect? Extent()
    {
        DiagramRect? all = null;
        foreach (DiagramSymbol s in _symbols)
        {
            DiagramRect b = SymbolLibrary.Bounds(s);
            all = all is DiagramRect a ? a.Union(b) : b;
        }
        foreach (DiagramConnection c in _connections)
            foreach (ScreenPoint p in c.Points)
            {
                var b = new DiagramRect(p.X, p.Y, 0, 0);
                all = all is DiagramRect a ? a.Union(b) : b;
            }
        return all;
    }
}
