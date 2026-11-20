using System.Collections.Immutable;
using GridSim.Core.Model;

namespace GridSim.Scripting;

/// <summary>The network as the script has left it: the case's model with the script's trips and set-points applied. Immutable.</summary>
public sealed record Plant(GridModel Model, ImmutableDictionary<string, (double Pd, double Qd)> TrippedLoads, int Version)
{
    public static Plant From(GridModel model) =>
        new(model, ImmutableDictionary<string, (double, double)>.Empty.WithComparers(StringComparer.Ordinal), 0);
}

/// <summary>The result of one edit: the new plant and the change in the system's net infeed it makes, in MW (positive = more
/// generation than demand, negative = a loss of infeed). The live engine takes it as a trip of that size.</summary>
public readonly record struct EditOutcome(Plant Plant, double NetInfeedDeltaMw);

/// <summary>TRIP, CLOSE and SET as pure functions of the plant. They refuse a state the edit cannot apply to.</summary>
public static class ModelEdit
{
    public static EditOutcome Apply(Statement s, CaseInfo ci, Plant p) => s switch
    {
        TripStatement t => Trip(t, ci, p),
        CloseStatement c => Close(c, ci, p),
        SetStatement st => Set(st, ci, p),
        AtStatement a => Apply(a.Inner, ci, p),
        _ => throw new ScriptException(ErrorPhase.Execution, s.Pos, "this statement does not edit the model"),
    };

    private static ScriptException Refuse(SourcePos pos, string msg) => new(ErrorPhase.Execution, pos, msg);

    private static EditOutcome Trip(TripStatement t, CaseInfo ci, Plant p)
    {
        GridModel m = p.Model;
        switch (t.Noun)
        {
            case Noun.Generator:
            {
                int i = ci.GeneratorIndex[t.Name.Value];
                Generator g = m.AllGenerators[i];
                if (!g.InService) throw Refuse(t.Name.Pos, $"generator \"{t.Name.Value}\" is already tripped");
                return new EditOutcome(Bump(p, ReplaceGenerator(m, i, g with { InService = false })), -g.Pg);
            }
            case Noun.Line:
            {
                int i = ci.LineIndex[t.Name.Value];
                Branch b = m.Branches[i];
                if (!b.InService) throw Refuse(t.Name.Pos, $"line \"{t.Name.Value}\" is already tripped");
                return new EditOutcome(Bump(p, ReplaceBranch(m, i, b with { InService = false })), 0);
            }
            default:
            {
                int k = ci.LoadIndex[t.Name.Value];
                if (p.TrippedLoads.ContainsKey(t.Name.Value)) throw Refuse(t.Name.Pos, $"load \"{t.Name.Value}\" is already tripped");
                Bus b = m.Buses[ci.LoadBusIndex[k]];
                Plant q = Bump(p, ReplaceBus(m, ci.LoadBusIndex[k], b with { Pd = 0, Qd = 0 }));
                return new EditOutcome(q with { TrippedLoads = q.TrippedLoads.SetItem(t.Name.Value, (b.Pd, b.Qd)) }, b.Pd);
            }
        }
    }

    private static EditOutcome Close(CloseStatement c, CaseInfo ci, Plant p)
    {
        GridModel m = p.Model;
        switch (c.Noun)
        {
            case Noun.Generator:
            {
                int i = ci.GeneratorIndex[c.Name.Value];
                Generator g = m.AllGenerators[i];
                if (g.InService) throw Refuse(c.Name.Pos, $"generator \"{c.Name.Value}\" is already in service");
                return new EditOutcome(Bump(p, ReplaceGenerator(m, i, g with { InService = true })), g.Pg);
            }
            case Noun.Line:
            {
                int i = ci.LineIndex[c.Name.Value];
                Branch b = m.Branches[i];
                if (b.InService) throw Refuse(c.Name.Pos, $"line \"{c.Name.Value}\" is already in service");
                return new EditOutcome(Bump(p, ReplaceBranch(m, i, b with { InService = true })), 0);
            }
            default:
            {
                int k = ci.LoadIndex[c.Name.Value];
                if (!p.TrippedLoads.TryGetValue(c.Name.Value, out (double Pd, double Qd) orig))
                    throw Refuse(c.Name.Pos, $"load \"{c.Name.Value}\" is already in service");
                Bus b = m.Buses[ci.LoadBusIndex[k]];
                Plant q = Bump(p, ReplaceBus(m, ci.LoadBusIndex[k], b with { Pd = orig.Pd, Qd = orig.Qd }));
                return new EditOutcome(q with { TrippedLoads = q.TrippedLoads.Remove(c.Name.Value) }, -orig.Pd);
            }
        }
    }

    private static EditOutcome Set(SetStatement s, CaseInfo ci, Plant p)
    {
        GridModel m = p.Model;
        Quantity qv = Binder.Q(s.Value).Value;
        double v = (double)qv.Value;
        if (s.Noun == Noun.Generator)
        {
            int i = ci.GeneratorIndex[s.Name.Value];
            Generator g = m.AllGenerators[i];
            if (!g.InService) throw Refuse(s.Name.Pos, $"generator \"{s.Name.Value}\" is tripped; CLOSE it before SET");
            switch (s.Property.Value)
            {
                case Property.P:
                    return new EditOutcome(Bump(p, ReplaceGenerator(m, i, g with { Pg = v })), v - g.Pg);
                case Property.Q:
                    return new EditOutcome(Bump(p, ReplaceGenerator(m, i, g with { Qg = v })), 0);
                default:
                {
                    double pu = qv.Unit == Unit.Kv ? v / m.Buses.First(b => b.Id == g.Bus).BaseKv : v;
                    return new EditOutcome(Bump(p, ReplaceGenerator(m, i, g with { Vg = pu })), 0);
                }
            }
        }
        else
        {
            int k = ci.LoadIndex[s.Name.Value];
            if (p.TrippedLoads.ContainsKey(s.Name.Value)) throw Refuse(s.Name.Pos, $"load \"{s.Name.Value}\" is tripped; CLOSE it before SET");
            int bi = ci.LoadBusIndex[k];
            Bus b = m.Buses[bi];
            return s.Property.Value == Property.P
                ? new EditOutcome(Bump(p, ReplaceBus(m, bi, b with { Pd = v })), -(v - b.Pd))
                : new EditOutcome(Bump(p, ReplaceBus(m, bi, b with { Qd = v })), 0);
        }
    }

    private static Plant Bump(Plant p, GridModel m) => p with { Model = m, Version = p.Version + 1 };

    private static GridModel ReplaceGenerator(GridModel m, int i, Generator g)
    {
        var fleet = m.AllGenerators.ToArray();
        fleet[i] = g;
        return m.CopyWith(generators: fleet, generatorsAreWholeFleet: true);
    }

    private static GridModel ReplaceBranch(GridModel m, int i, Branch b)
    {
        var all = m.Branches.ToArray();
        all[i] = b;
        return m.CopyWith(branches: all);
    }

    private static GridModel ReplaceBus(GridModel m, int i, Bus b)
    {
        var all = m.Buses.ToArray();
        all[i] = b;
        return m.CopyWith(buses: all);
    }
}
