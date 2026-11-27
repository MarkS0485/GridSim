using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Api;

/// <summary>
/// RC5 WP15: adding and deleting elements. Both work on a draft of the model and change nothing unless every rule holds and
/// the edited model is as valid as the case as loaded; a refusal leaves the model, the keys, the log and the revision as they
/// were. One accepted call is one revision; every change it makes is logged under that revision, in the order made.
///
/// <para>Keys. A new element gets the key it was added under (and that key as its model name, so a fresh
/// <see cref="CaseEditor"/> over the edited model, which keys an element with no PSS/E identity by its name, keys it the same
/// way). Every existing key keeps its element. A bus, unit and branch is appended, so an add never moves an index. A delete
/// moves the <see cref="ElementKey.Index"/> of the elements after the deleted one down by one (for a bus: every later bus,
/// load and shunt), and of no other element. Loads and shunts stay listed in bus order.</para>
///
/// <para>The validity check. The edited model's errors (<see cref="GridModel.Validate"/>) must be among those of the case as
/// loaded, with one exception: an island that has no slack bus. Building or taking apart a network passes through such
/// states (a new bus has no branch yet; deleting the branch to a bus leaves it alone), so a structural edit accepts them and
/// any study of the model as it then stands refuses the unsupplied island by name, as the loaders do. The islands an
/// accepted structural edit leaves are then accepted until an edit closes them: after every accepted edit the accepted set is
/// the islands of the model as it then stands, so a later <see cref="Set"/> is checked against the case as it stands (a Set
/// that makes an island is refused, even one an earlier edit had made and a later edit closed).</para>
///
/// <para>Scope. One load and one shunt per bus (the model folds them onto the bus). No three-winding transformer, HVDC, FACTS
/// or switched shunt is added or deleted. A bus is not deleted from a model that carries HVDC, FACTS, SVC or induction
/// machine records, and nothing is deleted from a model that carries authored protection or asset dynamics, because those
/// records name elements this editor does not re-map.</para>
/// </summary>
public sealed partial class CaseEditor
{
    /// <summary>The message prefix of <see cref="GridModel.Validate"/>'s island-without-slack error (D-DOC-082).</summary>
    private const string IslandErrorPrefix = "An island of ";

    /// <summary>The refusal of a second load (or shunt) on a bus.</summary>
    public const string OneLoadPerBus = "GridSim holds one load per bus; use set to change it";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Add an element of <paramref name="kind"/> under <paramref name="key"/> with the attributes
    /// <paramref name="values"/> (<see cref="ElementAttributes.AddFieldsOf"/>) and return the new revision.
    /// <list type="bullet">
    /// <item>bus: the key is the new bus id (an integer, unused). A slack is refused when the model has one. Appended.</item>
    /// <item>generator: the key is <c>bus:id</c>; the bus must exist and the key be new. Appended to the fleet. A PQ bus
    /// that gets an in-service unit becomes PV, logged as its own edit.</item>
    /// <item>load: the key is the bus id of a bus with no demand (<see cref="OneLoadPerBus"/> otherwise).</item>
    /// <item>branch: the key is <c>from-to:circuit</c>; both buses must exist and the key be new. Appended.</item>
    /// <item>shunt: the key is the bus id of a bus with no fixed shunt.</item>
    /// </list>
    /// </summary>
    public int Add(ElementKind kind, string key, IReadOnlyList<KeyValuePair<string, double>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrEmpty(key)) throw new CaseEditException($"a new {ElementKinds.Name(kind)} needs a key.");
        Dictionary<string, double> v = AddValues(kind, key, values);
        var d = new Draft(this);
        switch (kind)
        {
            case ElementKind.Bus: d.AddBus(key, v); break;
            case ElementKind.Generator: d.AddGenerator(key, v); break;
            case ElementKind.Load: d.AddLoad(key, v); break;
            case ElementKind.Branch: d.AddBranch(key, v, transformer: values.Any(kv => kv.Key is "tap" or "shift_deg")); break;
            default: d.AddShunt(key, v); break;
        }
        return Commit(d, $"adding {ElementKinds.Name(kind)} '{key}'");
    }

    /// <summary>
    /// Delete the element of <paramref name="kind"/> and <paramref name="key"/> and return the new revision.
    /// <list type="bullet">
    /// <item>bus: refused while a branch, unit, load or shunt refers to it, unless <paramref name="cascade"/>, which deletes
    /// them first, in this order: its branches (ascending index), its units, its load, its shunt; each is logged.</item>
    /// <item>generator: removed from the fleet. If no in-service unit is left at a PV bus, the bus becomes PQ (logged).
    /// Deleting the last unit, or the last in-service unit, at a slack bus is refused.</item>
    /// <item>load, shunt: the bus's demand (or Gs and Bs) is zeroed and the key removed.</item>
    /// <item>branch: removed. An OLTC, transformer flow control or zero-sequence mutual record that names it makes the delete
    /// refused, unless <paramref name="cascade"/>, which removes those records too.</item>
    /// </list>
    /// </summary>
    public int Delete(ElementKind kind, string key, bool cascade = false)
    {
        ElementKey k = Find(kind, key);
        if (Model.Protections is not null || Model.AssetDynamics is not null)
            throw new CaseEditException($"{ElementKinds.Name(kind)} '{key}' cannot be deleted: the case carries authored protection or asset dynamics, which name elements the object model does not re-map; nothing was changed.");
        var d = new Draft(this);
        switch (kind)
        {
            case ElementKind.Bus: d.DeleteBus(k.Key, cascade); break;
            case ElementKind.Generator: d.DeleteGenerator(k.Key, busGoing: false); break;
            case ElementKind.Load: d.DeleteLoad(k.Key); break;
            case ElementKind.Branch: d.DeleteBranch(k.Key, cascade); break;
            default: d.DeleteShunt(k.Key); break;
        }
        return Commit(d, $"deleting {ElementKinds.Name(kind)} '{key}'");
    }

    /// <summary>The attributes given to <see cref="Add"/>, checked against the kind's add table, with the defaults of
    /// those not given (an omitted attribute with no default is absent).</summary>
    private static Dictionary<string, double> AddValues(ElementKind kind, string key, IReadOnlyList<KeyValuePair<string, double>> values)
    {
        IReadOnlyList<AddField> table = ElementAttributes.AddFieldsOf(kind);
        var given = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string name, double x) in values)
        {
            if (table.All(a => a.Name != name))
                throw new CaseEditException($"a new {ElementKinds.Name(kind)} takes no attribute '{name}'; it takes: {ElementAttributes.AddList(kind)}.");
            if (!given.TryAdd(name, x)) throw new CaseEditException($"'{name}' is given twice.");
            if (!double.IsFinite(x)) throw new CaseEditException($"'{name}' must be a finite number.");
            try
            {
                if (name == "status") ElementAttributes.StatusOf(x);
                if (name == "type") ElementAttributes.BusTypeOf(x);
            }
            catch (ArgumentException ex) { throw new CaseEditException($"{ElementKinds.Name(kind)} '{key}': {ex.Message}; got {x.ToString("R", Inv)}."); }
        }
        foreach (AddField a in table)
        {
            if (given.ContainsKey(a.Name)) continue;
            if (a.Required) throw new CaseEditException($"a new {ElementKinds.Name(kind)} needs '{a.Name}'; it takes: {ElementAttributes.AddList(kind)}.");
            if (a.Default is { } dv) given[a.Name] = dv;
        }
        return given;
    }

    /// <summary>Check the draft's model and, when it is accepted, make it the model.</summary>
    private int Commit(Draft d, string what)
    {
        GridModel view = d.Build(Model);
        HashSet<string> all = Errors(view);
        var errors = new HashSet<string>(all, StringComparer.Ordinal);
        errors.ExceptWith(_originalErrors);
        errors.RemoveWhere(e => e.StartsWith(IslandErrorPrefix, StringComparison.Ordinal));
        if (errors.Count > 0)
            throw new CaseEditException($"{what} is refused: the model would not be valid ({errors.Order(StringComparer.Ordinal).First()}); nothing was changed.");
        // The islands without a slack of the model as it now stands are the accepted ones, replacing (not adding to) those
        // of earlier edits, so a later Set is checked against the case as it now stands: a Set that makes an island, even
        // one an earlier edit had and a later edit closed, is refused.
        _acceptedIslands = Islands(all);
        Model = view;
        _tripped = d.Tripped;
        Replace(_buses, d.Buses);
        Replace(_generators, d.Generators);
        Replace(_loads, d.Loads);
        Replace(_branches, d.Branches);
        Replace(_shunts, d.Shunts);
        _byKey.Clear();
        foreach (List<ElementKey> list in new[] { _buses, _generators, _loads, _branches, _shunts })
            foreach (ElementKey k in list) _byKey[(k.Kind, k.Key)] = k;
        Revision++;
        _edits.AddRange(d.Log);
        return Revision;
    }

    private static void Replace(List<ElementKey> target, List<ElementKey> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    /// <summary>A copy of everything an add or delete changes; nothing of the editor changes until <see cref="Commit"/>.</summary>
    private sealed class Draft
    {
        private readonly CaseEditor _ed;
        public readonly List<Bus> BusList;
        public readonly List<Generator> Fleet;
        public readonly List<Branch> BranchList;
        public readonly List<Oltc> Oltcs;
        public readonly List<TransformerFlowControl> FlowControls;
        public readonly List<ZeroSequenceMutual> Mutuals;
        public readonly Dictionary<int, (double Pd, double Qd)> Tripped;
        public readonly List<ElementKey> Buses, Generators, Loads, Branches, Shunts;
        public readonly List<CaseEdit> Log = [];
        private bool _buses, _fleet, _branches, _oltcs, _flows, _mutuals;

        public Draft(CaseEditor ed)
        {
            _ed = ed;
            GridModel m = ed.Model;
            BusList = [.. m.Buses];
            Fleet = [.. m.AllGenerators];
            BranchList = [.. m.Branches];
            Oltcs = [.. m.Oltcs];
            FlowControls = [.. m.TransformerFlowControls];
            Mutuals = [.. m.ZeroSequenceMutuals];
            Tripped = new Dictionary<int, (double Pd, double Qd)>(ed._tripped);
            Buses = [.. ed._buses];
            Generators = [.. ed._generators];
            Loads = [.. ed._loads];
            Branches = [.. ed._branches];
            Shunts = [.. ed._shunts];
        }

        public GridModel Build(GridModel m) => m.CopyWith(
            buses: _buses ? BusList.ToArray() : null,
            generators: _fleet ? Fleet.ToArray() : null,
            branches: _branches ? BranchList.ToArray() : null,
            oltcs: _oltcs ? Oltcs.ToArray() : null,
            transformerFlowControls: _flows ? FlowControls.ToArray() : null,
            zeroSequenceMutuals: _mutuals ? Mutuals.ToArray() : null,
            generatorsAreWholeFleet: _fleet);

        private int Rev => _ed.Revision + 1;

        private void Logged(ElementKind kind, string key, string attribute, object? before, double after) =>
            Log.Add(new CaseEdit(Rev, kind, key, attribute, before, after));

        private static ElementKey? In(List<ElementKey> list, string key) => list.FirstOrDefault(k => k.Key == key);

        private int BusIndex(int id)
        {
            for (int i = 0; i < BusList.Count; i++) if (BusList[i].Id == id) return i;
            return -1;
        }

        private static int ParseBusId(string text, string what)
        {
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, Inv, out int id) || id.ToString(Inv) != text || id <= 0)
                throw new CaseEditException($"{what} '{text}' is not a bus id (a whole number above 0, written plainly).");
            return id;
        }

        private int ExistingBus(string text, string what)
        {
            int id = ParseBusId(text, what);
            int i = BusIndex(id);
            if (i < 0) throw new CaseEditException($"{what}: there is no bus {text} in the case.");
            return i;
        }

        // ---- add ----------------------------------------------------------------------------------------------------

        public void AddBus(string key, Dictionary<string, double> v)
        {
            int id = ParseBusId(key, "a new bus key");
            if (BusIndex(id) >= 0) throw new CaseEditException($"bus {key} already exists; a new bus needs an unused id.");
            double kv = v["base_kv"];
            if (!(kv > 0)) throw new CaseEditException($"a new bus needs base_kv above 0 kV; got {kv.ToString("R", Inv)}.");
            BusType type = ElementAttributes.BusTypeOf(v["type"]);
            if (type == BusType.Slack && BusList.Any(b => b.Type == BusType.Slack))
                throw new CaseEditException($"bus {key} cannot be added as a slack bus: the case has one (bus {BusList.First(b => b.Type == BusType.Slack).Id.ToString(Inv)}).");
            BusList.Add(new Bus(id, type, BaseKv: kv, Vm: v["vm"], VaDeg: v["va_deg"],
                VminPu: v.TryGetValue("vmin", out double lo) ? lo : null, VmaxPu: v.TryGetValue("vmax", out double hi) ? hi : null));
            _buses = true;
            Buses.Add(new ElementKey(ElementKind.Bus, key, BusList.Count - 1));
            Logged(ElementKind.Bus, key, "add", null, 1);
        }

        public void AddGenerator(string key, Dictionary<string, double> v)
        {
            int colon = key.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == key.Length - 1 || key.AsSpan(colon + 1).ContainsAny(" \t:#"))
                throw new CaseEditException($"a new generator key is 'bus:id' (as 5:1); got '{key}'.");
            int bi = ExistingBus(key[..colon], "generator '" + key + "'");
            if (In(Generators, key) is not null) throw new CaseEditException($"generator '{key}' already exists; a new unit needs an id that is new at its bus.");
            bool on = ElementAttributes.StatusOf(v["status"]);
            Bus bus = BusList[bi];
            Fleet.Add(new Generator(bus.Id, v["pg"], Qg: v["qg"], Vg: v["vg"], Qmin: v["qmin"], Qmax: v["qmax"], Pmin: v["pmin"], Pmax: v["pmax"],
                MBase: v["mbase"], Name: key, InService: on));
            _fleet = true;
            Generators.Add(new ElementKey(ElementKind.Generator, key, Fleet.Count - 1));
            Logged(ElementKind.Generator, key, "add", null, 1);
            if (on && bus.Type == BusType.Pq)
            {
                BusList[bi] = bus with { Type = BusType.Pv };
                _buses = true;
                Logged(ElementKind.Bus, bus.Id.ToString(Inv), "type", 1.0, 2);
            }
        }

        public void AddLoad(string key, Dictionary<string, double> v)
        {
            int bi = ExistingBus(key, "load '" + key + "'");
            Bus b = BusList[bi];
            if (In(Loads, key) is not null || b.Pd != 0 || b.Qd != 0)
                throw new CaseEditException($"bus {key} already has a load: {OneLoadPerBus}.");
            double pd = v["pd"], qd = v["qd"];
            if (pd == 0 && qd == 0) throw new CaseEditException($"a new load needs pd or qd not zero (a bus with no demand has no load).");
            if (ElementAttributes.StatusOf(v["status"])) BusList[bi] = b with { Pd = pd, Qd = qd };
            else Tripped[bi] = (pd, qd);
            _buses = true;
            InsertInBusOrder(Loads, new ElementKey(ElementKind.Load, key, bi));
            Logged(ElementKind.Load, key, "add", null, 1);
        }

        public void AddShunt(string key, Dictionary<string, double> v)
        {
            int bi = ExistingBus(key, "shunt '" + key + "'");
            Bus b = BusList[bi];
            if (In(Shunts, key) is not null || b.Gs != 0 || b.Bs != 0)
                throw new CaseEditException($"bus {key} already has a fixed shunt: GridSim holds one shunt per bus; use set to change it.");
            double gs = v["gs"], bs = v["bs"];
            if (gs == 0 && bs == 0) throw new CaseEditException("a new shunt needs gs or bs not zero (a bus with neither has no shunt).");
            BusList[bi] = b with { Gs = gs, Bs = bs };
            _buses = true;
            InsertInBusOrder(Shunts, new ElementKey(ElementKind.Shunt, key, bi));
            Logged(ElementKind.Shunt, key, "add", null, 1);
        }

        private static void InsertInBusOrder(List<ElementKey> list, ElementKey k)
        {
            int at = 0;
            while (at < list.Count && list[at].Index < k.Index) at++;
            list.Insert(at, k);
        }

        public void AddBranch(string key, Dictionary<string, double> v, bool transformer)
        {
            int colon = key.IndexOf(':', StringComparison.Ordinal);
            string ends = colon < 0 ? "" : key[..colon];
            int dash = ends.IndexOf('-', StringComparison.Ordinal);
            if (colon <= 0 || dash <= 0 || colon == key.Length - 1 || key.AsSpan(colon + 1).ContainsAny(" \t:#"))
                throw new CaseEditException($"a new branch key is 'from-to:circuit' (as 4-5:1); got '{key}'.");
            int fi = ExistingBus(ends[..dash], "branch '" + key + "' from bus");
            int ti = ExistingBus(ends[(dash + 1)..], "branch '" + key + "' to bus");
            if (In(Branches, key) is not null) throw new CaseEditException($"branch '{key}' already exists; a new branch needs a new circuit id.");
            BranchList.Add(new Branch(BusList[fi].Id, BusList[ti].Id, v["r"], v["x"], B: v["b"], TapRatio: v["tap"], PhaseShiftDeg: v["shift_deg"],
                RateAMva: v["rate_a"], Name: key, IsTransformer: transformer, InService: ElementAttributes.StatusOf(v["status"]),
                RateBMva: v["rate_b"], RateCMva: v["rate_c"]));
            _branches = true;
            Branches.Add(new ElementKey(ElementKind.Branch, key, BranchList.Count - 1));
            Logged(ElementKind.Branch, key, "add", null, 1);
        }

        // ---- delete -------------------------------------------------------------------------------------------------

        public void DeleteBus(string key, bool cascade)
        {
            ElementKey k = In(Buses, key)!;
            int id = BusList[k.Index].Id;
            GridModel m = _ed.Model;
            if (m.HasCoupledDevices)
                throw new CaseEditException($"bus {key} cannot be deleted: the case carries HVDC, FACTS, SVC or induction machine records, which name buses the object model does not re-map; nothing was changed.");
            if (Oltcs.Any(o => o.ControlsBus == id) || FlowControls.Any(f => f.FromBus == id))
                throw new CaseEditException($"bus {key} cannot be deleted: a tap changer or transformer flow control record names it; nothing was changed.");
            var branches = Branches.Where(b => BranchList[b.Index].From == id || BranchList[b.Index].To == id).OrderBy(b => b.Index).Select(b => b.Key).ToList();
            var units = Generators.Where(g => Fleet[g.Index].Bus == id).Select(g => g.Key).ToList();
            bool load = In(Loads, key) is not null, shunt = In(Shunts, key) is not null;
            if (!cascade && (branches.Count > 0 || units.Count > 0 || load || shunt))
            {
                var what = new List<string>();
                if (branches.Count > 0) what.Add($"{branches.Count} branch(es)");
                if (units.Count > 0) what.Add($"{units.Count} unit(s)");
                if (load) what.Add("a load");
                if (shunt) what.Add("a shunt");
                throw new CaseEditException($"bus {key} cannot be deleted: {string.Join(", ", what)} refer to it; delete them first or delete with cascade; nothing was changed.");
            }
            foreach (string b in branches) DeleteBranch(b, cascade: true);
            foreach (string g in units) DeleteGenerator(g, busGoing: true);
            if (load) DeleteLoad(key);
            if (shunt) DeleteShunt(key);

            int at = In(Buses, key)!.Index;
            BusList.RemoveAt(at);
            _buses = true;
            Buses.RemoveAll(x => x.Key == key);
            Shift(Buses, at);
            Shift(Loads, at);
            Shift(Shunts, at);
            var tripped = Tripped.ToList();
            Tripped.Clear();
            foreach (KeyValuePair<int, (double Pd, double Qd)> t in tripped)
                if (t.Key != at) Tripped[t.Key > at ? t.Key - 1 : t.Key] = t.Value;
            Logged(ElementKind.Bus, key, "delete", null, 0);
        }

        /// <summary>Every key of <paramref name="list"/> whose index is after <paramref name="removed"/> moves down by one.</summary>
        private static void Shift(List<ElementKey> list, int removed)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Index > removed) list[i] = list[i] with { Index = list[i].Index - 1 };
        }

        public void DeleteGenerator(string key, bool busGoing)
        {
            ElementKey k = In(Generators, key)!;
            Generator g = Fleet[k.Index];
            int bi = BusIndex(g.Bus);
            Bus bus = BusList[bi];
            bool othersAtBus = Generators.Any(o => o.Key != key && Fleet[o.Index].Bus == g.Bus);
            bool othersOn = Generators.Any(o => o.Key != key && Fleet[o.Index].Bus == g.Bus && Fleet[o.Index].InService);
            if (!busGoing && bus.Type == BusType.Slack && (!othersAtBus || (g.InService && !othersOn)))
                throw new CaseEditException($"generator '{key}' cannot be deleted: it is the last {(othersAtBus ? "in-service " : "")}unit at the slack bus {bus.Id.ToString(Inv)}; nothing was changed.");
            if (g.Name is { } n && _ed.Model.UnitProtection?.For(n) is not null)
                throw new CaseEditException($"generator '{key}' cannot be deleted: unit protection is set for it by name; nothing was changed.");
            Fleet.RemoveAt(k.Index);
            _fleet = true;
            Generators.RemoveAll(x => x.Key == key);
            Shift(Generators, k.Index);
            Logged(ElementKind.Generator, key, "delete", null, 0);
            if (!busGoing && bus.Type == BusType.Pv && !othersOn)
            {
                BusList[bi] = bus with { Type = BusType.Pq };
                _buses = true;
                Logged(ElementKind.Bus, bus.Id.ToString(Inv), "type", 2.0, 1);
            }
        }

        public void DeleteLoad(string key)
        {
            ElementKey k = In(Loads, key)!;
            BusList[k.Index] = BusList[k.Index] with { Pd = 0, Qd = 0 };
            Tripped.Remove(k.Index);
            _buses = true;
            Loads.RemoveAll(x => x.Key == key);
            Logged(ElementKind.Load, key, "delete", null, 0);
        }

        public void DeleteShunt(string key)
        {
            ElementKey k = In(Shunts, key)!;
            BusList[k.Index] = BusList[k.Index] with { Gs = 0, Bs = 0 };
            _buses = true;
            Shunts.RemoveAll(x => x.Key == key);
            Logged(ElementKind.Shunt, key, "delete", null, 0);
        }

        public void DeleteBranch(string key, bool cascade)
        {
            ElementKey k = In(Branches, key)!;
            int at = k.Index;
            GridModel before = Snapshot();
            int[] oltc = PowerFlow.SteadyStateSolver.MapOltcRecordsToBranches(before);
            int[] flows = MapFlowControls(before);
            bool namedByOltc = oltc.Contains(at), namedByFlow = flows.Contains(at);
            bool namedByMutual = Mutuals.Any(z => z.BranchA == at || z.BranchB == at);
            if (!cascade && (namedByOltc || namedByFlow || namedByMutual))
                throw new CaseEditException($"branch '{key}' cannot be deleted: {(namedByOltc ? "a tap changer record" : namedByFlow ? "a transformer flow control record" : "a zero-sequence mutual record")} names it; delete with cascade to remove those records too; nothing was changed.");

            var keepOltc = new List<Oltc>();
            var keepOltcBranch = new List<int>();
            for (int i = 0; i < Oltcs.Count; i++)
                if (oltc[i] != at) { keepOltc.Add(Oltcs[i]); keepOltcBranch.Add(oltc[i]); }
            var keepFlows = new List<TransformerFlowControl>();
            var keepFlowBranch = new List<int>();
            for (int i = 0; i < FlowControls.Count; i++)
                if (flows[i] != at) { keepFlows.Add(FlowControls[i]); keepFlowBranch.Add(flows[i]); }
            if (keepOltc.Count != Oltcs.Count) { Oltcs.Clear(); Oltcs.AddRange(keepOltc); _oltcs = true; }
            if (keepFlows.Count != FlowControls.Count) { FlowControls.Clear(); FlowControls.AddRange(keepFlows); _flows = true; }
            if (Mutuals.Count > 0 && Mutuals.Any(z => z.BranchA >= at || z.BranchB >= at))
            {
                var kept = Mutuals.Where(z => z.BranchA != at && z.BranchB != at)
                    .Select(z => z with { BranchA = z.BranchA > at ? z.BranchA - 1 : z.BranchA, BranchB = z.BranchB > at ? z.BranchB - 1 : z.BranchB }).ToList();
                Mutuals.Clear();
                Mutuals.AddRange(kept);
                _mutuals = true;
            }

            BranchList.RemoveAt(at);
            _branches = true;
            Branches.RemoveAll(x => x.Key == key);
            Shift(Branches, at);

            // Every record kept must still drive the branch it drove (they find their branch by name).
            GridModel after = Snapshot();
            int[] oltcAfter = PowerFlow.SteadyStateSolver.MapOltcRecordsToBranches(after);
            int[] flowsAfter = MapFlowControls(after);
            for (int i = 0; i < oltcAfter.Length; i++)
                if (oltcAfter[i] != (keepOltcBranch[i] > at ? keepOltcBranch[i] - 1 : keepOltcBranch[i]))
                    throw new CaseEditException($"branch '{key}' cannot be deleted: tap changer record {i.ToString(Inv)} would then drive another branch; nothing was changed.");
            for (int i = 0; i < flowsAfter.Length; i++)
                if (flowsAfter[i] != (keepFlowBranch[i] > at ? keepFlowBranch[i] - 1 : keepFlowBranch[i]))
                    throw new CaseEditException($"branch '{key}' cannot be deleted: transformer flow control record {i.ToString(Inv)} would then drive another branch; nothing was changed.");
            Logged(ElementKind.Branch, key, "delete", null, 0);
        }

        /// <summary>The draft as a model, for the record mappings (no validation).</summary>
        private GridModel Snapshot() => _ed.Model.CopyWith(buses: BusList.ToArray(), generators: Fleet.ToArray(), branches: BranchList.ToArray(),
            oltcs: Oltcs.ToArray(), transformerFlowControls: FlowControls.ToArray(), zeroSequenceMutuals: Mutuals.ToArray(), generatorsAreWholeFleet: true);

        /// <summary>The branch each transformer flow control record drives: the first branch whose name is the record's
        /// transformer and whose From bus is the record's; -1 when none (the steady-state solve's rule).</summary>
        private static int[] MapFlowControls(GridModel model)
        {
            var map = new int[model.TransformerFlowControls.Count];
            for (int k = 0; k < map.Length; k++)
            {
                TransformerFlowControl fc = model.TransformerFlowControls[k];
                map[k] = -1;
                for (int i = 0; i < model.Branches.Count; i++)
                    if (model.Branches[i].Name == fc.Transformer && model.Branches[i].From == fc.FromBus) { map[k] = i; break; }
            }
            return map;
        }
    }
}
