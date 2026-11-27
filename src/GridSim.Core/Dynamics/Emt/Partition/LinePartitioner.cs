using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.Partition;

/// <summary>
/// A network described as named partitions joined by <see cref="NodalBergeronLine"/>s. The same description builds both forms of the
/// study: <see cref="Create"/> with <c>singlePartition: false</c> gives one <see cref="NodalNetwork"/> per partition; with
/// <c>singlePartition: true</c> every partition name maps to ONE shared network (the monolithic, incumbent-solver form).
/// Node names are prefixed by the partition name (<c>partition/name</c>) in both forms, so a probe name means the same thing in
/// both, and element names must be unique across the whole model (a duplicate throws in the monolithic form).
/// Build order is part of the contract: elements are added to a partition in the order given, and that order is the order of
/// every history and source sum, so both forms add elements to each node in the same order.
/// </summary>
public sealed class PartitionedModel
{
    private readonly List<string> _names = new();
    private readonly Dictionary<string, NodalNetwork> _nets = new(StringComparer.Ordinal);
    private readonly List<NodalBergeronLine> _lines = new();

    private PartitionedModel(bool single) { SinglePartition = single; }

    /// <summary>True for the monolithic form (one network for every partition name).</summary>
    public bool SinglePartition { get; }

    /// <summary>Partition names in declaration order (the fixed partition order of every exchange and every merge).</summary>
    public IReadOnlyList<string> PartitionNames => _names;

    /// <summary>The distinct networks, in partition order (one network in the monolithic form).</summary>
    public IReadOnlyList<NodalNetwork> Networks => _names.Select(n => _nets[n]).Distinct().ToArray();

    /// <summary>The lines joining or inside partitions, in creation order.</summary>
    public IReadOnlyList<NodalBergeronLine> Lines => _lines;

    /// <summary>Create a model with the named partitions.</summary>
    public static PartitionedModel Create(IReadOnlyList<string> partitionNames, bool singlePartition)
    {
        ArgumentNullException.ThrowIfNull(partitionNames);
        if (partitionNames.Count == 0) throw new ArgumentException("At least one partition is needed.", nameof(partitionNames));
        var m = new PartitionedModel(singlePartition);
        NodalNetwork? shared = singlePartition ? new NodalNetwork() : null;
        foreach (string p in partitionNames)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Contains('/')) throw new ArgumentException($"Bad partition name '{p}' (not empty, no '/').", nameof(partitionNames));
            if (m._nets.ContainsKey(p)) throw new ArgumentException($"Duplicate partition '{p}'.", nameof(partitionNames));
            m._names.Add(p);
            m._nets[p] = shared ?? new NodalNetwork();
        }
        return m;
    }

    /// <summary>The network of a partition.</summary>
    public NodalNetwork Net(string partition) =>
        _nets.TryGetValue(partition, out NodalNetwork? n) ? n : throw new ArgumentException($"No partition '{partition}'.", nameof(partition));

    /// <summary>The id of node <paramref name="name"/> of <paramref name="partition"/> (named <c>partition/name</c> in the network;
    /// <c>gnd</c> is the reference node).</summary>
    public int Node(string partition, string name) =>
        name is "gnd" or "ground" ? NodalNetwork.Ground : Net(partition).Node(partition + "/" + name);

    /// <summary>Add an element to a partition.</summary>
    public T Add<T>(string partition, T element) where T : NodalElement => Net(partition).Add(element);

    /// <summary>Join <paramref name="nodeA"/> of <paramref name="partA"/> to <paramref name="nodeB"/> of <paramref name="partB"/> by a
    /// Bergeron line (both to ground return). The partitions may be the same.</summary>
    public NodalBergeronLine AddLine(string name, string partA, string nodeA, string partB, string nodeB, double surgeImpedanceOhm,
        double travelSeconds, double attenuation = 1.0)
    {
        NodalBergeronLine line = NodalBergeronLine.Add(Net(partA), Node(partA, nodeA), NodalNetwork.Ground, Net(partB), Node(partB, nodeB), NodalNetwork.Ground,
            name, surgeImpedanceOhm, travelSeconds, attenuation);
        line.PartitionA = partA; line.PartitionB = partB;
        _lines.Add(line);
        return line;
    }
}

/// <summary>The partition plan for one step size.</summary>
/// <param name="Partitions">Number of distinct networks.</param>
/// <param name="CutLines">Names of the lines whose ends are in different networks.</param>
/// <param name="WindowSteps">Steps every partition may run before the next exchange: the smallest whole-step travel time over the cut
/// lines (a step reads history at least that many steps old), or <see cref="LinePartitioner.MaxWindow"/> when nothing is cut.</param>
/// <param name="Description">The exchange schedule in words (printed with a result).</param>
public sealed record PartitionPlan(int Partitions, IReadOnlyList<string> CutLines, int WindowSteps, string Description);

/// <summary>
/// Checks that a <see cref="PartitionedModel"/> is cut only where a partition's inputs are history values, and fixes the exchange
/// schedule. A cut is legal only at a Bergeron line whose travel time is at least the step: then the value an end reads at step
/// <c>s</c> was written at step <c>s - q</c> or earlier (<c>q = floor(travel / step) &gt;= 1</c>), so partitions can all run
/// <c>q_min</c> steps between exchanges, each in its own thread, and the result does not depend on the schedule of the threads.
/// </summary>
public static class LinePartitioner
{
    /// <summary>The window used when no line is cut (the partitions are independent), steps.</summary>
    public const int MaxWindow = 4096;

    /// <summary>Check the model at step <paramref name="dt"/> and return the plan.</summary>
    /// <exception cref="ArgumentException">A line crossing partitions has a travel time shorter than the step (a domain refusal).</exception>
    public static PartitionPlan Plan(PartitionedModel model, double dt)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!(dt > 0 && double.IsFinite(dt))) throw DomainRefusal.Mark(new ArgumentException("The time step must be finite and positive."));
        int window = MaxWindow;
        var cut = new List<string>();
        foreach (NodalBergeronLine l in model.Lines)
        {
            if (ReferenceEquals(model.Net(l.PartitionA), model.Net(l.PartitionB))) continue;
            if (l.TravelSeconds < dt * (1.0 - 1e-12))
                throw DomainRefusal.Mark(new ArgumentException(
                    FormattableString.Invariant($"line '{l.Name}' joins partitions '{l.PartitionA}' and '{l.PartitionB}' but its travel time {l.TravelSeconds:R} s is shorter than the step {dt:R} s: a partition may only be cut at a line the step does not exceed")));
            double q = l.TravelSeconds / dt;
            double rq = Math.Round(q);
            if (Math.Abs(q - rq) < 1e-9) q = rq;
            window = Math.Min(window, (int)Math.Floor(q));
            cut.Add(l.Name);
        }
        int parts = model.Networks.Count;
        string text = cut.Count == 0
            ? $"{parts} independent partition(s); no line is cut"
            : $"{parts} partitions cut at {cut.Count} Bergeron line(s) [{string.Join(", ", cut)}]; every partition runs {window} step(s) between exchanges (the smallest travel time in whole steps); "
              + "exchange order is fixed: partitions in declaration order, then barrier";
        return new PartitionPlan(parts, cut, window, text);
    }
}
