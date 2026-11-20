namespace GridSim.Core.Dynamics.Emt.Nodal;

/// <summary>
/// A network of named nodes and <see cref="NodalElement"/>s in SI units. Node ids are assigned in first-use order and
/// elements are kept in insertion order; both orders are fixed, so the matrix layout and every sum are deterministic.
/// A three-phase bus is simply three nodes.
/// </summary>
public sealed class NodalNetwork
{
    /// <summary>The reference node.</summary>
    public const int Ground = -1;

    private readonly List<string> _nodeNames = new();
    private readonly Dictionary<string, int> _nodeIds = new(StringComparer.Ordinal);
    private readonly List<NodalElement> _elements = new();
    private readonly Dictionary<string, NodalElement> _byName = new(StringComparer.Ordinal);

    /// <summary>Number of non-ground nodes.</summary>
    public int NodeCount => _nodeNames.Count;

    /// <summary>Node names by id.</summary>
    public IReadOnlyList<string> NodeNames => _nodeNames;

    /// <summary>The elements in insertion order.</summary>
    public IReadOnlyList<NodalElement> Elements => _elements;

    /// <summary>The id of a node, created on first use. The names <c>gnd</c> and <c>ground</c> are the reference node.</summary>
    public int Node(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A node needs a name.", nameof(name));
        if (name is "gnd" or "ground") return Ground;
        if (_nodeIds.TryGetValue(name, out int id)) return id;
        id = _nodeNames.Count;
        _nodeNames.Add(name);
        _nodeIds[name] = id;
        return id;
    }

    /// <summary>The id of an existing node, or -2 when the name is not a node of this network.</summary>
    public int FindNode(string name)
    {
        if (name is "gnd" or "ground") return Ground;
        return _nodeIds.TryGetValue(name, out int id) ? id : -2;
    }

    /// <summary>Add an element; its name must be unique.</summary>
    public T Add<T>(T element) where T : NodalElement
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_byName.TryAdd(element.Name, element)) throw new ArgumentException($"Duplicate element name '{element.Name}'.", nameof(element));
        _elements.Add(element);
        return element;
    }

    /// <summary>The element with this name, or null.</summary>
    public NodalElement? Find(string name) => _byName.TryGetValue(name, out NodalElement? e) ? e : null;
}
