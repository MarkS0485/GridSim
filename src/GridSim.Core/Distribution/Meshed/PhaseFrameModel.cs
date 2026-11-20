using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

// RC4 R4-15: the phase-frame model of the meshed unbalanced load flow. Every conductor is an explicit wire: a node has a
// list of named wires (for example a, b, c, n, or l1, l2, n for a split-phase service), a branch carries 1 to 4 (or more)
// of them with its FULL primitive impedance matrix, and earth is the reference (0 V). Units are SI throughout: volts
// (wire to earth), ohms, siemens, volt-amperes, amperes. Nothing is eliminated implicitly: a Kron reduction happens only
// on a branch that declares KronWire (a multi-grounded neutral), and it is recorded in the result notes.

/// <summary>A node and the wires that exist at it. Wire names are free text ("a", "n", "l1"); a wire is a separate unknown
/// at every node it is listed on.</summary>
public sealed record FrameNode(string Name, IReadOnlyList<string> Wires);

/// <summary>
/// An ideal voltage source that fixes the wires it names at their node (voltage to earth). With <paramref name="SeriesZ"/>
/// (one impedance per wire, ohm) the source sits behind that impedance instead: the EMF is fixed and the node wire is
/// reached through it (a source impedance or an earth electrode at the source end).
/// </summary>
public sealed record FrameSource(string Node, IReadOnlyList<string> Wires, IReadOnlyList<Complex> Voltage,
    IReadOnlyList<Complex>? SeriesZ = null);

/// <summary>
/// A series element between two nodes: the conductors <paramref name="Wires"/> (the same names exist at both nodes) with the
/// full primitive impedance matrix <paramref name="Z"/> (ohm, total for the branch, rows and columns in wire order, so a
/// four-wire line is 4x4 with the neutral included) and an optional shunt admittance matrix to earth
/// <paramref name="ShuntY"/> (siemens, total, split half at each end as a pi section).
/// <paramref name="KronWire"/> declares that wire a multi-grounded neutral: its voltage is taken as zero along the branch and
/// it is eliminated from the branch (a recorded Kron reduction), and it need not exist at the nodes; null (default) keeps every wire.
/// </summary>
public sealed record FrameBranch(string Name, string From, string To, IReadOnlyList<string> Wires, Complex[,] Z,
    Complex[,]? ShuntY = null, string? KronWire = null);

/// <summary>How a load is connected. The legs are listed in <see cref="FrameLoad.Legs"/>.</summary>
public enum FrameConnection
{
    /// <summary>Each leg from a phase wire to a named return wire at the node (usually the neutral n).</summary>
    Wye,
    /// <summary>Each leg from a phase wire straight to earth (a locally earthed star point).</summary>
    WyeEarthed,
    /// <summary>Each leg from a phase wire to a floating star point (an internal unknown; no neutral).</summary>
    WyeFloating,
    /// <summary>Legs a-b, b-c, c-a between phase wires.</summary>
    Delta,
    /// <summary>One leg between two named wires (line-line, or a single phase to neutral).</summary>
    LineLine,
}

/// <summary>One leg of a load: current flows From to To through it. <c>To</c> null means earth for
/// <see cref="FrameConnection.WyeEarthed"/> and the floating star for <see cref="FrameConnection.WyeFloating"/>.</summary>
public readonly record struct FrameLeg(string From, string? To);

/// <summary>ZIP fractions of a load's nominal power: constant power, constant current, constant impedance. They must be
/// finite, not negative and sum to 1.</summary>
public readonly record struct Zip(double P, double I, double Z)
{
    /// <summary>All constant power (the default).</summary>
    public static Zip ConstantPower => new(1.0, 0.0, 0.0);
    /// <summary>All constant impedance.</summary>
    public static Zip ConstantImpedance => new(0.0, 0.0, 1.0);
    /// <summary>All constant current.</summary>
    public static Zip ConstantCurrent => new(0.0, 1.0, 0.0);
}

/// <summary>
/// A load of one or more legs. <paramref name="S"/> is the complex power each leg draws at its nominal voltage
/// <paramref name="NominalV"/> (the leg voltage magnitude the rating refers to: the line-to-neutral voltage for a wye leg, the
/// line-to-line voltage for a delta or line-line leg). A leg's current is the ZIP mix:
/// P part I = conj(S/u); I part |I| = |S|/V0 keeping the power-factor angle to u; Z part I = u conj(S)/V0^2.
/// </summary>
public sealed record FrameLoad(string Name, string Node, FrameConnection Connection, IReadOnlyList<FrameLeg> Legs,
    IReadOnlyList<Complex> S, double NominalV, Zip Zip)
{
    private static readonly string[] Abc = ["a", "b", "c"];

    /// <summary>A wye load: legs from each of the phase wires <paramref name="phases"/> to <paramref name="returnWire"/>.
    /// <paramref name="s"/> has one power per phase; <paramref name="vln"/> is the line-to-neutral nominal voltage.</summary>
    public static FrameLoad Wye(string name, string node, IReadOnlyList<Complex> s, double vln, Zip? zip = null,
        string returnWire = "n", IReadOnlyList<string>? phases = null)
    {
        IReadOnlyList<string> p = phases ?? Abc;
        return new FrameLoad(name, node, FrameConnection.Wye, p.Select(w => new FrameLeg(w, returnWire)).ToArray(), s, vln, zip ?? Zip.ConstantPower);
    }

    /// <summary>A wye load whose star point is solidly connected to local earth.</summary>
    public static FrameLoad WyeEarthed(string name, string node, IReadOnlyList<Complex> s, double vln, Zip? zip = null,
        IReadOnlyList<string>? phases = null)
    {
        IReadOnlyList<string> p = phases ?? Abc;
        return new FrameLoad(name, node, FrameConnection.WyeEarthed, p.Select(w => new FrameLeg(w, null)).ToArray(), s, vln, zip ?? Zip.ConstantPower);
    }

    /// <summary>A wye load with a floating star point (no neutral connection).</summary>
    public static FrameLoad WyeFloating(string name, string node, IReadOnlyList<Complex> s, double vln, Zip? zip = null,
        IReadOnlyList<string>? phases = null)
    {
        IReadOnlyList<string> p = phases ?? Abc;
        return new FrameLoad(name, node, FrameConnection.WyeFloating, p.Select(w => new FrameLeg(w, null)).ToArray(), s, vln, zip ?? Zip.ConstantPower);
    }

    /// <summary>A delta load: legs a-b, b-c, c-a; <paramref name="vll"/> is the line-to-line nominal voltage.</summary>
    public static FrameLoad Delta(string name, string node, IReadOnlyList<Complex> s, double vll, Zip? zip = null)
        => new(name, node, FrameConnection.Delta, [new FrameLeg("a", "b"), new FrameLeg("b", "c"), new FrameLeg("c", "a")], s, vll, zip ?? Zip.ConstantPower);

    /// <summary>One leg between two named wires (line-line, or one phase to neutral).</summary>
    public static FrameLoad LineLine(string name, string node, string from, string to, Complex s, double nominalV, Zip? zip = null)
        => new(name, node, FrameConnection.LineLine, [new FrameLeg(from, to)], [s], nominalV, zip ?? Zip.ConstantPower);
}

/// <summary>One winding of a <see cref="FrameTransformerUnit"/>: it sits between wire <paramref name="Wire"/> and
/// <paramref name="ReturnWire"/> (null: earth) of node <paramref name="Node"/>, has <paramref name="Turns"/> (a signed
/// number proportional to its rated voltage; the sign is the polarity) and a leakage impedance <paramref name="Z"/> (ohm,
/// on this winding's own side; must not be zero). R4-15b: <paramref name="ReturnNode"/> (null: the same node) puts the
/// winding's return terminal at another node, which is how a series winding (a regulator's, between its output and input
/// nodes) and an internal star point are written.</summary>
public sealed record FrameWinding(string Node, string Wire, string? ReturnWire, double Turns, Complex Z, string? ReturnNode = null);

/// <summary>
/// A single-core transformer unit with any number of windings (two, three, or a centre-tapped secondary as two
/// windings). All windings share one flux with no magnetising current: each winding obeys v = t E + z i and the
/// ampere-turns balance sum(t i) = 0. A three-phase bank is three units. Windings of different units are galvanically
/// separate, so an ungrounded secondary has no reference (reported INCOMPLETE unless the caller grounds it).
/// R4-15b: the optional <paramref name="MagnetisingY"/> (siemens, referred to the FIRST winding's voltage level; zero by
/// default: none) is a magnetising branch across the common flux: the ampere-turn balance becomes
/// sum(t i) = MagnetisingY t1^2 E, so the winding admittance is D - (Dt)(Dt)^T / (t^T D t + MagnetisingY t1^2). With one
/// winding open the first winding then sees its leakage in series with the magnetising branch (exactly).
/// </summary>
public sealed record FrameTransformerUnit(string Name, IReadOnlyList<FrameWinding> Windings, Complex MagnetisingY = default);

/// <summary>The explicit phase-frame network: nodes, sources, branches, loads, earthing, grids and transformer units.
/// The constructor refuses structurally impossible input (it throws <see cref="ArgumentException"/>); input that is merely
/// incomplete (a neutral with no reference, a delta load with a missing wire) is reported by the solver as INCOMPLETE with
/// the offending names.</summary>
public sealed class PhaseFrameModel
{
    /// <summary>Case name.</summary>
    public string Name { get; }
    /// <summary>Nodes in declaration order.</summary>
    public IReadOnlyList<FrameNode> Nodes { get; }
    /// <summary>Sources.</summary>
    public IReadOnlyList<FrameSource> Sources { get; }
    /// <summary>Branches.</summary>
    public IReadOnlyList<FrameBranch> Branches { get; }
    /// <summary>Loads.</summary>
    public IReadOnlyList<FrameLoad> Loads { get; }
    /// <summary>Earthing elements.</summary>
    public IReadOnlyList<EarthingElement> Earthing { get; }
    /// <summary>Shared earth grids.</summary>
    public IReadOnlyList<EarthGrid> Grids { get; }
    /// <summary>Transformer units.</summary>
    public IReadOnlyList<FrameTransformerUnit> Transformers { get; }

    /// <summary>Build and validate a model.</summary>
    public PhaseFrameModel(string name, IReadOnlyList<FrameNode> nodes, IReadOnlyList<FrameSource> sources,
        IReadOnlyList<FrameBranch> branches, IReadOnlyList<FrameLoad>? loads = null,
        IReadOnlyList<EarthingElement>? earthing = null, IReadOnlyList<EarthGrid>? grids = null,
        IReadOnlyList<FrameTransformerUnit>? transformers = null)
    {
        Name = name;
        Nodes = nodes;
        Sources = sources;
        Branches = branches;
        Loads = loads ?? Array.Empty<FrameLoad>();
        Earthing = earthing ?? Array.Empty<EarthingElement>();
        Grids = grids ?? Array.Empty<EarthGrid>();
        Transformers = transformers ?? Array.Empty<FrameTransformerUnit>();
        Validate();
    }

    private static bool Finite(Complex c) => double.IsFinite(c.Real) && double.IsFinite(c.Imaginary);

    private void Validate()
    {
        var nodeWires = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (FrameNode n in Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Name)) throw new ArgumentException("a node needs a name.");
            if (n.Wires is null || n.Wires.Count == 0) throw new ArgumentException($"node '{n.Name}' needs at least one wire.");
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string w in n.Wires)
            {
                if (string.IsNullOrWhiteSpace(w)) throw new ArgumentException($"node '{n.Name}' has an unnamed wire.");
                if (!set.Add(w)) throw new ArgumentException($"node '{n.Name}' lists wire '{w}' twice.");
            }
            if (!nodeWires.TryAdd(n.Name, set)) throw new ArgumentException($"node '{n.Name}' is declared twice.");
        }
        if (Nodes.Count == 0) throw new ArgumentException("the model has no nodes.");

        HashSet<string> NodeOf(string node, string what)
            => nodeWires.TryGetValue(node, out HashSet<string>? s) ? s : throw new ArgumentException($"{what} refers to node '{node}', which is not declared.");

        if (Sources.Count == 0) throw new ArgumentException("the model needs at least one source.");
        foreach (FrameSource s in Sources)
        {
            NodeOf(s.Node, "a source");
            if (s.Wires.Count == 0 || s.Voltage.Count != s.Wires.Count)
                throw new ArgumentException($"source at '{s.Node}' needs one voltage per wire.");
            if (s.Voltage.Any(v => !Finite(v))) throw new ArgumentException($"source at '{s.Node}' has a non-finite voltage.");
            if (s.SeriesZ is { } sz)
            {
                if (sz.Count != s.Wires.Count) throw new ArgumentException($"source at '{s.Node}' needs one series impedance per wire.");
                if (sz.Any(z => !Finite(z))) throw new ArgumentException($"source at '{s.Node}' has a non-finite series impedance.");
            }
        }

        var branchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameBranch b in Branches)
        {
            if (!branchNames.Add(b.Name)) throw new ArgumentException($"branch name '{b.Name}' is used twice.");
            HashSet<string> f = NodeOf(b.From, $"branch '{b.Name}'"), t = NodeOf(b.To, $"branch '{b.Name}'");
            if (b.From == b.To) throw new ArgumentException($"branch '{b.Name}' joins node '{b.From}' to itself.");
            int k = b.Wires.Count;
            if (k == 0) throw new ArgumentException($"branch '{b.Name}' carries no wire.");
            if (b.Wires.Distinct(StringComparer.Ordinal).Count() != k) throw new ArgumentException($"branch '{b.Name}' lists a wire twice.");
            foreach (string w in b.Wires)
                if (w != b.KronWire && (!f.Contains(w) || !t.Contains(w)))
                    throw new ArgumentException($"branch '{b.Name}' carries wire '{w}', which is not present at both '{b.From}' and '{b.To}'.");
            if (b.Z.GetLength(0) != k || b.Z.GetLength(1) != k)
                throw new ArgumentException($"branch '{b.Name}' needs a {k}x{k} impedance matrix (one row and column per wire).");
            for (int i = 0; i < k; i++)
                for (int j = 0; j < k; j++)
                    if (!Finite(b.Z[i, j])) throw new ArgumentException($"branch '{b.Name}' has a non-finite impedance entry [{i},{j}].");
            if (b.ShuntY is { } y)
            {
                if (y.GetLength(0) != k || y.GetLength(1) != k)
                    throw new ArgumentException($"branch '{b.Name}' needs a {k}x{k} shunt admittance matrix.");
                for (int i = 0; i < k; i++)
                    for (int j = 0; j < k; j++)
                        if (!Finite(y[i, j])) throw new ArgumentException($"branch '{b.Name}' has a non-finite shunt entry [{i},{j}].");
            }
            if (b.KronWire is not null)
            {
                if (!b.Wires.Contains(b.KronWire)) throw new ArgumentException($"branch '{b.Name}' declares Kron wire '{b.KronWire}', which it does not carry.");
                if (k < 2) throw new ArgumentException($"branch '{b.Name}' would have no wire left after the Kron reduction.");
            }
        }

        var loadNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameLoad l in Loads)
        {
            if (!loadNames.Add(l.Name)) throw new ArgumentException($"load name '{l.Name}' is used twice.");
            NodeOf(l.Node, $"load '{l.Name}'");
            if (l.Legs.Count == 0) throw new ArgumentException($"load '{l.Name}' has no legs.");
            if (l.S.Count != l.Legs.Count)
                throw new ArgumentException($"load '{l.Name}' needs one power per leg ({l.Legs.Count}) but has {l.S.Count}.");
            if (l.S.Any(v => !Finite(v))) throw new ArgumentException($"load '{l.Name}' has a non-finite power.");
            if (!(double.IsFinite(l.NominalV) && l.NominalV > 0)) throw new ArgumentException($"load '{l.Name}' needs a nominal voltage above 0.");
            Zip z = l.Zip;
            if (!(double.IsFinite(z.P) && double.IsFinite(z.I) && double.IsFinite(z.Z)) || z.P < 0 || z.I < 0 || z.Z < 0 || Math.Abs(z.P + z.I + z.Z - 1.0) > 1e-9)
                throw new ArgumentException($"load '{l.Name}' ZIP fractions must be finite, not negative, and sum to 1.");
        }

        var gridNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (EarthGrid g in Grids)
        {
            if (!gridNames.Add(g.Name)) throw new ArgumentException($"earth grid '{g.Name}' is declared twice.");
            if (!Finite(g.Z) || g.Z == Complex.Zero) throw new ArgumentException($"earth grid '{g.Name}' needs a finite non-zero impedance to remote earth.");
        }
        foreach (EarthingElement e in Earthing)
        {
            HashSet<string> w = NodeOf(e.Node, "an earthing element");
            if (!w.Contains(e.Wire)) throw new ArgumentException($"an earthing element is on wire '{e.Wire}', which node '{e.Node}' does not have.");
            if (!Finite(e.Z)) throw new ArgumentException($"earthing at '{e.Node}'/'{e.Wire}' has a non-finite impedance.");
            if (e.Grid is not null)
            {
                if (!gridNames.Contains(e.Grid)) throw new ArgumentException($"earthing at '{e.Node}'/'{e.Wire}' names grid '{e.Grid}', which is not declared.");
                if (e.Z == Complex.Zero) throw new ArgumentException($"earthing at '{e.Node}'/'{e.Wire}' to grid '{e.Grid}' needs a non-zero impedance.");
            }
        }

        var unitNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameTransformerUnit u in Transformers)
        {
            if (!unitNames.Add(u.Name)) throw new ArgumentException($"transformer unit '{u.Name}' is declared twice.");
            if (u.Windings.Count < 2) throw new ArgumentException($"transformer unit '{u.Name}' needs at least two windings.");
            if (!Finite(u.MagnetisingY)) throw new ArgumentException($"transformer unit '{u.Name}' has a non-finite magnetising admittance.");
            foreach (FrameWinding w in u.Windings)
            {
                NodeOf(w.Node, $"transformer unit '{u.Name}'");
                if (w.ReturnNode is not null) NodeOf(w.ReturnNode, $"transformer unit '{u.Name}'");
                if (!double.IsFinite(w.Turns) || w.Turns == 0) throw new ArgumentException($"transformer unit '{u.Name}' has a winding with zero or non-finite turns.");
                if (!Finite(w.Z) || w.Z == Complex.Zero) throw new ArgumentException($"transformer unit '{u.Name}' has a winding with zero or non-finite leakage impedance (use a small non-zero value).");
            }
        }
    }
}
