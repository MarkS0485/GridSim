using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>The closed block catalogue: the only operations a document may use.</summary>
public static class ModelCatalogue
{
    // RC5 wp32b: AddedForRelays marks the blocks added with the relay role (ontimer, latch). The refusal of an unknown block type
    // lists the whole catalogue, these included (a refusal must be true).
    internal sealed record Spec(string Type, int MinIn, int MaxIn, string[] Required, string[] Optional, bool Stateful,
        bool Combinational, int InitValues, bool PrevAllowed, string Summary, bool AddedForRelays = false);

    private static readonly string[] None = Array.Empty<string>();

    internal static readonly IReadOnlyList<Spec> All = new Spec[]
    {
        new("gain", 1, 1, new[] { "k" }, None, false, true, 0, false, "y = k*u"),
        new("div", 1, 1, new[] { "d" }, None, false, true, 0, false, "y = u/d"),
        new("sum", 1, 8, None, None, false, true, 0, false, "left-to-right signed sum; the 'signs' member gives + or - per input"),
        new("product", 2, 8, None, None, false, true, 0, false, "left-to-right product"),
        new("lag", 1, 1, new[] { "gain", "time" }, new[] { "min", "max" }, true, false, 1, true, "gain/(1+sT) with non-windup limits (ControlBlocks.FirstOrderLag); initialised with its OUTPUT"),
        new("leadlag", 1, 1, new[] { "lead", "lag" }, None, true, false, 1, false, "(1+sTa)/(1+sTb) (ControlBlocks.LeadLag); initialised with its INPUT"),
        new("washout", 1, 2, new[] { "time" }, None, true, false, 1, false, "sT/(1+sT) (ControlBlocks.Washout); initialised with its INPUT; a second input defers the state update to that value"),
        new("integrator", 1, 1, new[] { "gain" }, new[] { "min", "max" }, true, false, 1, true, "gain/s with windup-limited state (ControlBlocks.Integrator); initialised with its OUTPUT"),
        new("pi", 1, 1, new[] { "kp", "ki" }, new[] { "min", "max" }, true, false, 2, false, "kp + ki/s with the integral clamped to the limits (ControlBlocks.PiController); initialised with (input, output)"),
        new("limit", 1, 1, new[] { "min", "max" }, None, false, true, 0, false, "y = clamp(u, min, max)"),
        new("ratelimit", 1, 1, new[] { "rise", "fall" }, None, true, false, 1, true, "output follows the input at no more than rise (>0) and fall (<0) per second; initialised with its OUTPUT"),
        new("deadband", 1, 1, new[] { "lower", "upper" }, None, false, true, 0, false, "zero inside [lower, upper], the excess outside"),
        new("table1d", 1, 1, None, None, false, true, 0, false, "piecewise-linear table (the 'table' member), end values held outside the range"),
        new("expsat", 1, 1, new[] { "a", "b" }, None, false, true, 0, false, "y = a*exp(b*u) (DetMath.Exp), the exponential saturation"),
        new("min", 2, 8, None, None, false, true, 0, false, "smallest input (Math.Min, left to right)"),
        new("max", 2, 8, None, None, false, true, 0, false, "largest input (Math.Max, left to right)"),
        new("switch", 3, 3, new[] { "threshold" }, None, false, true, 0, false, "inputs [sel, a, b]: a when sel >= threshold, else b"),
        new("const", 0, 0, new[] { "value" }, None, false, true, 0, false, "a constant"),
        new("hold", 0, 0, None, None, true, false, 1, true, "a value fixed at initialisation (for example the load reference); initialised with its OUTPUT"),
        new("delay", 1, 1, new[] { "steps" }, None, true, false, 1, true, "fixed delay of a whole number of steps; initialised with its OUTPUT"),
        new("satpole", 1, 1, new[] { "te", "ke", "a", "b" }, None, true, false, 1, true, "IEEE Type-1 saturated exciter pole Te dE/dt = u - (ke + a*exp(b*E))*E, exact with the saturation frozen over the step; initialised with its OUTPUT"),
        new("expr", 1, 8, None, None, false, true, 0, false, "an expression of in0..in7 and @parameters (+ - * /, unary -, brackets, min, max, abs, sqrt, exp, clamp) evaluated in its written order; 'expr' gives the text and 'unit' the output unit; a non-finite result refuses the step"),
        new("ontimer", 1, 1, new[] { "time" }, None, true, false, 1, false, "on-delay timer counted in whole steps: picks up at the first step its input is non-zero and gives 1 from the first step k with (k - k_pickup) h + h 1e-6 >= time (the built-in unit relay stages' rule), 0 before; a step with the input 0 resets it; one fixed step size; initialised with its OUTPUT, which must be 0", AddedForRelays: true),
        new("latch", 1, 1, None, None, true, false, 1, false, "seal-in: gives 1 from the first step its input is non-zero to the end of the run, 0 before; initialised with its OUTPUT (0 or 1)", AddedForRelays: true),
    };

    internal static Spec? Find(string type)
    {
        foreach (Spec s in All) if (s.Type == type) return s;
        return null;
    }

    /// <summary>The catalogue as (type, summary) rows, in a fixed order, for help and reports.</summary>
    public static IReadOnlyList<(string Type, string Summary)> Rows => All.Select(s => (s.Type, s.Summary)).ToList();
}

/// <summary>One compiled operation (a block or an initialisation computation) with every reference resolved to a slot.</summary>
internal sealed record OpSpec(ModelBlock Block, ModelCatalogue.Spec Spec, int[] In, int[] ParamSlots, string[] ParamNames, int Out,
    bool DeferredUpdate, int AdvanceSlot);

/// <summary>An initialisation action with its references resolved to slots.</summary>
internal sealed record InitAction(string Kind, int OpIndex, int[] Values, int ValueSlot, int MinSlot, int MaxSlot, ModelInitRequire? Require);

/// <summary>
/// The result of compiling a document (R4-08): the flat, topologically sorted operation list with every reference
/// resolved, independent of parameter values. A <see cref="ModelInstance"/> is made from it with bound parameter values.
/// </summary>
public sealed class CompiledModel
{
    internal CompiledModel(ModelDocument doc) { Document = doc; }

    /// <summary>The document.</summary>
    public ModelDocument Document { get; }

    /// <summary>Block ids in compiled evaluation order. Part of the provenance; equal for equal documents.</summary>
    public IReadOnlyList<string> Order { get; internal set; } = Array.Empty<string>();

    /// <summary>SHA-256 of the document hash and the compiled order: changes if either does.</summary>
    public string OrderDigest { get; internal set; } = "";

    internal int SlotCount;
    internal int ParamBase, ConstBase;                     // consts region: declared params and derived first, then literals
    internal double[] ConstValues = Array.Empty<double>();  // literal values at ConstBase + k
    internal int[] InputSlots = Array.Empty<int>();
    internal int[] OutputSlots = Array.Empty<int>();
    internal int[] InitInputSlots = Array.Empty<int>();
    internal OpSpec[] Ops = Array.Empty<OpSpec>();          // step ops, compiled order
    internal OpSpec[] InitOps = Array.Empty<OpSpec>();      // initialisation computations, listed order
    internal InitAction[] InitActions = Array.Empty<InitAction>();   // in document order, interleaved by InitOrder
    internal (bool IsAction, int Index)[] InitSequence = Array.Empty<(bool, int)>();
    internal (int Slot, int OpIndex)[] PrevSnapshots = Array.Empty<(int, int)>();
    internal int EquilibriumErrorSlot = -1;
    internal Dictionary<string, int> ParamIndex = new(StringComparer.Ordinal);   // declared + derived name -> slot

    /// <summary>The declared parameter names, then derived names, in document order.</summary>
    public IReadOnlyList<string> ParameterNames => Document.Parameters.Select(p => p.Name).ToList();

    /// <summary>RC5 wp32b: true when a block or an output port reads the input port named <paramref name="port"/>.</summary>
    internal bool ReadsInput(string port)
    {
        for (int i = 0; i < Document.Inputs.Count && i < InputSlots.Length; i++)
        {
            if (Document.Inputs[i].Name != port) continue;
            int slot = InputSlots[i];
            foreach (OpSpec o in Ops) if (Array.IndexOf(o.In, slot) >= 0) return true;
            return Array.IndexOf(OutputSlots, slot) >= 0;
        }
        return false;
    }
}

/// <summary>
/// Validates and compiles a <see cref="ModelDocument"/> (R4-08): vocabulary (closed catalogue), ports, references, units,
/// initialisation, then a deterministic topological sort. An algebraic loop without a declared break (a <c>.prev</c>
/// reference to a stateful block) is refused, naming the loop. Every refusal names the offending object.
/// </summary>
public static class ModelCompiler
{
    /// <summary>Compile; throws <see cref="UserModelException"/> listing every issue.</summary>
    public static CompiledModel Compile(ModelDocument doc)
    {
        (CompiledModel? plan, List<ModelIssue> issues) = Analyse(doc);
        if (issues.Count > 0 || plan is null) throw new UserModelException(issues);
        return plan;
    }

    internal static (CompiledModel? Plan, List<ModelIssue> Issues) Analyse(ModelDocument doc)
    {
        var issues = new List<ModelIssue>();
        void Issue(string code, string obj, string msg) => issues.Add(new ModelIssue(code, obj, msg));
        var plan = new CompiledModel(doc);

        // ---- namespaces ---------------------------------------------------------------------------------------------
        var paramDim = new Dictionary<string, ModelUnit>(StringComparer.Ordinal);
        var slotOfConst = plan.ParamIndex;
        int slot = 0;
        foreach (ModelParameter p in doc.Parameters)
        {
            if (!paramDim.TryAdd(p.Name, p.Unit)) { Issue("duplicate-name", "parameter " + p.Name, "the parameter is declared twice."); continue; }
            slotOfConst[p.Name] = slot++;
            if (p.Min is double lo && p.Max is double hi && lo > hi)
                Issue("bad-bounds", "parameter " + p.Name, $"min {lo.ToString("R", CultureInfo.InvariantCulture)} is above max {hi.ToString("R", CultureInfo.InvariantCulture)}.");
            if (!p.AllowInfinite && double.IsInfinity(p.Value)) Issue("non-finite", "parameter " + p.Name, "the value is infinite; set allowInfinite to permit it.");
            CheckBounds(p, p.Value, Issue);
        }
        foreach (ModelDerived d in doc.Derived)
        {
            if (paramDim.ContainsKey(d.Name)) { Issue("duplicate-name", "derived " + d.Name, "the name is already a parameter or derived constant."); continue; }
            ModelUnit? da = RefDim(d.A, paramDim), db = RefDim(d.B, paramDim);
            if (d.A.Kind == ModelRefKind.Parameter && da is null) Issue("undeclared-parameter", "derived " + d.Name, $"'@{d.A.Name}' is not a declared parameter or an earlier derived constant.");
            if (d.B.Kind == ModelRefKind.Parameter && db is null) Issue("undeclared-parameter", "derived " + d.Name, $"'@{d.B.Name}' is not a declared parameter or an earlier derived constant.");
            ModelUnit ua = da ?? ModelUnit.One, ub = db ?? ModelUnit.One;
            ModelUnit outDim = d.Op switch { "mul" => ua.Times(ub), "div" => ua.Over(ub), _ => ua };
            if (d.Op is "add" or "sub" && da is { } x && db is { } y && x != y)
                Issue("unit-mismatch", "derived " + d.Name, $"{d.Op} of {x} and {y}: the operands must have the same unit.");
            paramDim[d.Name] = outDim;
            slotOfConst[d.Name] = slot++;
        }
        foreach ((IReadOnlyList<string> names, double _, double _) in doc.SumConstraints)
            foreach (string n in names)
                if (!paramDim.ContainsKey(n)) Issue("undeclared-parameter", "constraint", $"the constraint names '{n}', which is not a declared parameter.");
        plan.ParamBase = 0;
        plan.ConstBase = slot;

        var literals = new List<double>();
        int LiteralSlot(double v)
        {
            for (int i = 0; i < literals.Count; i++)
                if (BitConverter.DoubleToInt64Bits(literals[i]) == BitConverter.DoubleToInt64Bits(v)) return plan.ConstBase + i;
            literals.Add(v);
            return plan.ConstBase + literals.Count - 1;
        }

        // ---- ports and blocks: names -------------------------------------------------------------------------------
        var stepNames = new Dictionary<string, int>(StringComparer.Ordinal);   // name -> kind: 0 input, 1 block
        foreach (ModelPort p in doc.Inputs)
            if (!stepNames.TryAdd(p.Name, 0)) Issue("duplicate-name", "input port " + p.Name, "the port name is used twice.");
        var blockIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            ModelBlock b = doc.Blocks[i];
            if (!stepNames.TryAdd(b.Id, 1)) { Issue("duplicate-name", "block " + b.Id, "the id is also an input port or another block."); continue; }
            blockIndex[b.Id] = i;
        }
        if (doc.Blocks.Count == 0) Issue("empty", "blocks", "the model has no blocks.");

        // ---- catalogue, arity, params -----------------------------------------------------------------------------------
        var specs = new ModelCatalogue.Spec?[doc.Blocks.Count];
        for (int i = 0; i < doc.Blocks.Count; i++)
            specs[i] = CheckBlockShape(doc.Blocks[i], "block", issues, paramDim);

        // ---- slots: params and literals are assigned lazily; signals after the const region is known (patched below) ------
        // First pass collects literals (so ConstBase + literals.Count is known), then signals are numbered after them.
        // To keep slot numbers stable and simple, resolve literals now into a map and number signals afterwards.
        var litMap = new Dictionary<ModelRef, int>(ReferenceEqualityComparer.Instance);
        void CollectLiterals(ModelBlock b)
        {
            foreach (ModelRef r in b.Inputs) if (r.Kind == ModelRefKind.Literal) litMap[r] = LiteralSlot(r.Literal);
            foreach (var kv in b.Params) if (kv.Value.Kind == ModelRefKind.Literal) litMap[kv.Value] = LiteralSlot(kv.Value.Literal);
        }
        foreach (ModelBlock b in doc.Blocks) CollectLiterals(b);
        foreach (ModelInitStep st in doc.Initialisation.Steps)
        {
            switch (st)
            {
                case ModelInitCompute c: CollectLiterals(c.Block); break;
                case ModelInitInitialise ii: foreach (ModelRef r in ii.Values) if (r.Kind == ModelRefKind.Literal) litMap[r] = LiteralSlot(r.Literal); break;
                case ModelInitRequire rq:
                    foreach (ModelRef? r in new[] { rq.Value, rq.Min, rq.Max })
                        if (r is { Kind: ModelRefKind.Literal }) litMap[r] = LiteralSlot(r.Literal);
                    break;
            }
        }
        plan.ConstValues = literals.ToArray();
        int next = plan.ConstBase + literals.Count;
        var inputSlot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ModelPort p in doc.Inputs) if (!inputSlot.ContainsKey(p.Name)) inputSlot[p.Name] = next++;
        var blockSlot = new int[doc.Blocks.Count];
        for (int i = 0; i < blockSlot.Length; i++) blockSlot[i] = next++;
        // init scope
        var initSlot = new Dictionary<string, int>(StringComparer.Ordinal);
        var initDim = new Dictionary<string, ModelUnit?>(StringComparer.Ordinal);
        foreach (ModelPort p in doc.Initialisation.Inputs)
        {
            if (initSlot.ContainsKey(p.Name)) { Issue("duplicate-name", "initialisation input " + p.Name, "the name is used twice."); continue; }
            initSlot[p.Name] = next++;
            initDim[p.Name] = p.Unit;
        }

        int QuietConst(ModelRef r) => r.Kind == ModelRefKind.Literal ? litMap[r] : slotOfConst.TryGetValue(r.Name, out int qs) ? qs : 0;

        int ResolveConst(ModelRef r, string obj)
        {
            if (r.Kind == ModelRefKind.Literal) return litMap[r];
            if (slotOfConst.TryGetValue(r.Name, out int s)) return s;
            Issue("undeclared-parameter", obj, $"'@{r.Name}' is not a declared parameter or derived constant.");
            return 0;
        }

        // Each parameter reference of each block is resolved (and an undeclared one reported) exactly once, here.
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            foreach (var kv in doc.Blocks[i].Params)
                if (kv.Value.Kind == ModelRefKind.Parameter) ResolveConst(kv.Value, "block " + doc.Blocks[i].Id);
            // RC5 WP12: the @parameters an expr block's text names.
            if (doc.Blocks[i].Type == "expr" && ExprProgram.Parse(doc.Blocks[i].Expr).Program is { } xp)
                foreach (string pn in xp.Parameters) ResolveConst(new ModelRef(ModelRefKind.Parameter, pn, false, 0), "block " + doc.Blocks[i].Id);
        }

        // ---- initialisation block ------------------------------------------------------------------------------------
        var initOps = new List<OpSpec>();
        var initActions = new List<InitAction>();
        var initSeq = new List<(bool, int)>();
        var initialisedBlocks = new Dictionary<string, int>(StringComparer.Ordinal);
        var holdDimFromInit = new Dictionary<string, ModelUnit?>(StringComparer.Ordinal);
        var initDimOfSlot = new Dictionary<int, ModelUnit?>();
        foreach (ModelPort p in doc.Initialisation.Inputs) if (initSlot.TryGetValue(p.Name, out int sl)) initDimOfSlot[sl] = p.Unit;

        (int Slot, ModelUnit? Dim) InitRef(ModelRef r, string obj)
        {
            switch (r.Kind)
            {
                case ModelRefKind.Literal: return (litMap[r], null);
                case ModelRefKind.Parameter:
                    return (ResolveConst(r, obj), paramDim.TryGetValue(r.Name, out ModelUnit pd) ? pd : null);
                default:
                    if (r.Prev) { Issue("bad-reference", obj, $"'{r}': .prev has no meaning in the initialisation block."); return (0, null); }
                    if (initSlot.TryGetValue(r.Name, out int s)) return (s, initDim.TryGetValue(r.Name, out ModelUnit? d) ? d : null);
                    Issue("undeclared-port", obj, $"'{r.Name}' is not an initialisation input or an EARLIER initialisation step.");
                    return (0, null);
            }
        }

        foreach (ModelInitStep st in doc.Initialisation.Steps)
        {
            switch (st)
            {
                case ModelInitCompute c:
                {
                    ModelBlock b = c.Block;
                    ModelCatalogue.Spec? sp = CheckBlockShape(b, "initialisation step", issues, paramDim);
                    if (sp is not null && !sp.Combinational)
                        Issue("not-combinational", "initialisation step " + b.Id, $"'{b.Type}' has state; the initialisation block computes with combinational blocks only.");
                    if (initSlot.ContainsKey(b.Id))
                    {
                        Issue("duplicate-name", "initialisation step " + b.Id, "the name is already an initialisation input or step.");
                        break;
                    }
                    var ins = new int[b.Inputs.Count];
                    var dims = new ModelUnit?[b.Inputs.Count];
                    for (int k = 0; k < ins.Length; k++) (ins[k], dims[k]) = InitRef(b.Inputs[k], "initialisation step " + b.Id);
                    (int[] ps, string[] pn, ModelUnit? od) = sp is null ? (Array.Empty<int>(), Array.Empty<string>(), (ModelUnit?)null)
                        : ParamsAndDim(b, sp, dims, "initialisation step " + b.Id, paramDim, ResolveConst, Issue);
                    int outSlot = next++;
                    initSlot[b.Id] = outSlot;
                    initDim[b.Id] = od;
                    initDimOfSlot[outSlot] = od;
                    if (sp is not null)
                    {
                        initSeq.Add((false, initOps.Count));
                        initOps.Add(new OpSpec(b, sp, ins, ps, pn, outSlot, false, -1));
                    }
                    break;
                }
                case ModelInitInitialise ii:
                {
                    string obj = $"initialise {ii.BlockId}";
                    if (!blockIndex.TryGetValue(ii.BlockId, out int bi)) { Issue("undeclared-block", obj, $"'{ii.BlockId}' is not a block of this model."); break; }
                    ModelCatalogue.Spec? sp = specs[bi];
                    if (sp is null) break;
                    if (!sp.Stateful) { Issue("not-stateful", obj, $"'{ii.BlockId}' ({sp.Type}) has no state to initialise."); break; }
                    if (initialisedBlocks.ContainsKey(ii.BlockId)) { Issue("duplicate-init", obj, "the block is initialised twice."); break; }
                    if (ii.Values.Count != sp.InitValues) { Issue("bad-arity", obj, $"a {sp.Type} is initialised with {sp.InitValues} value(s), got {ii.Values.Count}."); break; }
                    var vs = new int[ii.Values.Count];
                    for (int k = 0; k < vs.Length; k++)
                    {
                        (vs[k], ModelUnit? vd) = InitRef(ii.Values[k], obj);
                        if (sp.Type == "hold") holdDimFromInit[ii.BlockId] = vd;
                        initDimOfSlot[vs[k]] = vd;
                    }
                    initialisedBlocks[ii.BlockId] = initActions.Count;
                    initSeq.Add((true, initActions.Count));
                    initActions.Add(new InitAction("initialise", bi, vs, -1, -1, -1, null));
                    // the unit check of the values against the block's dimension is done once dimensions are known (below)
                    break;
                }
                case ModelInitRequire rq:
                {
                    (int vslot, ModelUnit? vd) = InitRef(rq.Value, "require " + rq.Argument);
                    int mn = rq.Min is null ? -1 : ResolveConst(rq.Min, "require " + rq.Argument);
                    int mx = rq.Max is null ? -1 : ResolveConst(rq.Max, "require " + rq.Argument);
                    foreach (ModelRef? lim in new[] { rq.Min, rq.Max })
                        if (lim is { Kind: ModelRefKind.Parameter } && vd is { } v && paramDim.TryGetValue(lim.Name, out ModelUnit ld) && ld != v)
                            Issue("unit-mismatch", "require " + rq.Argument, $"the value is in {v} but the limit '@{lim.Name}' is in {ld}.");
                    initSeq.Add((true, initActions.Count));
                    initActions.Add(new InitAction("require", -1, Array.Empty<int>(), vslot, mn, mx, rq));
                    break;
                }
            }
        }
        foreach (var kv in blockIndex)
            if (specs[kv.Value] is { Stateful: true } && !initialisedBlocks.ContainsKey(kv.Key))
                Issue("not-initialised", "block " + kv.Key, $"the {specs[kv.Value]!.Type} has state but the initialisation block does not initialise it.");
        if (doc.Initialisation.EquilibriumError is string eq)
        {
            if (initSlot.TryGetValue(eq, out int es)) plan.EquilibriumErrorSlot = es;
            else Issue("undeclared-port", "equilibriumError", $"'{eq}' is not an initialisation input or step.");
        }

        // ---- step-scope references, ordering, dimensions ------------------------------------------------------------------
        var inDimOf = new Dictionary<string, ModelUnit>(StringComparer.Ordinal);
        foreach (ModelPort p in doc.Inputs) inDimOf[p.Name] = p.Unit;

        var deps = new List<int>[doc.Blocks.Count];
        var prevTargets = new SortedSet<int>();
        var resolvedIn = new int[doc.Blocks.Count][];
        var advanceSlot = new int[doc.Blocks.Count];
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            ModelBlock b = doc.Blocks[i];
            deps[i] = new List<int>();
            resolvedIn[i] = new int[b.Inputs.Count];
            advanceSlot[i] = -1;
            for (int k = 0; k < b.Inputs.Count; k++)
            {
                ModelRef r = b.Inputs[k];
                string obj = $"block {b.Id} input {k}";
                switch (r.Kind)
                {
                    case ModelRefKind.Literal: resolvedIn[i][k] = litMap[r]; break;
                    case ModelRefKind.Parameter: resolvedIn[i][k] = ResolveConst(r, obj); break;
                    default:
                        if (inputSlot.TryGetValue(r.Name, out int isl))
                        {
                            if (r.Prev) Issue("bad-reference", obj, $"'{r}': .prev applies to a stateful block, not to the input port '{r.Name}'.");
                            resolvedIn[i][k] = isl;
                        }
                        else if (blockIndex.TryGetValue(r.Name, out int tj))
                        {
                            if (r.Prev)
                            {
                                if (specs[tj] is { PrevAllowed: false } ps)
                                    Issue("bad-reference", obj, $"'{r}': a {ps.Type} block has no declared pre-step output; .prev is allowed on lag, integrator, ratelimit, hold, delay and satpole.");
                                prevTargets.Add(tj);
                                resolvedIn[i][k] = -2 - tj;   // patched to the snapshot slot below
                            }
                            else
                            {
                                resolvedIn[i][k] = blockSlot[tj];
                                bool isAdvance = b.Type == "washout" && k == 1;
                                if (!isAdvance) deps[i].Add(tj);
                            }
                        }
                        else Issue("undeclared-port", obj, $"'{r.Name}' is not an input port or a block of this model (an undeclared port).");
                        break;
                }
            }
        }

        // snapshot slots for .prev targets, in block order
        var snapSlot = new Dictionary<int, int>();
        var snaps = new List<(int, int)>();
        foreach (int tj in prevTargets) { int s = next++; snapSlot[tj] = s; snaps.Add((s, tj)); }
        for (int i = 0; i < resolvedIn.Length; i++)
            for (int k = 0; k < resolvedIn[i].Length; k++)
                if (resolvedIn[i][k] <= -2) resolvedIn[i][k] = snapSlot[-2 - resolvedIn[i][k]];
        plan.SlotCount = next;

        // topological sort, lowest document index first among ready blocks
        var done = new bool[doc.Blocks.Count];
        var order = new List<int>();
        for (int round = 0; round < doc.Blocks.Count; round++)
        {
            int pick = -1;
            for (int i = 0; i < doc.Blocks.Count && pick < 0; i++)
                if (!done[i] && deps[i].All(j => done[j])) pick = i;
            if (pick < 0) break;
            done[pick] = true;
            order.Add(pick);
        }
        if (order.Count < doc.Blocks.Count)
        {
            string loop = FindLoop(doc, deps, done);
            Issue("algebraic-loop", "blocks", $"algebraic loop: {loop}. A loop through blocks with no delay has no defined value; break it by reading a stateful block's pre-step output with '.prev' (for example 'efd.prev').");
        }

        // dimensions: iterate to a fixed point (a .prev feedback gives a block's dimension only after it is known)
        var blockDim = new ModelUnit?[doc.Blocks.Count];
        ModelUnit? RefDimStep(ModelRef r)
        {
            switch (r.Kind)
            {
                case ModelRefKind.Literal: return null;
                case ModelRefKind.Parameter: return paramDim.TryGetValue(r.Name, out ModelUnit d) ? d : null;
                default:
                    if (inDimOf.TryGetValue(r.Name, out ModelUnit pd)) return pd;
                    if (blockIndex.TryGetValue(r.Name, out int j)) return blockDim[j];
                    return null;
            }
        }
        var opSpecs = new OpSpec[doc.Blocks.Count];
        List<ModelIssue>? finalIssues = null;
        for (int pass = 0; pass <= doc.Blocks.Count + 1; pass++)
        {
            bool changed = false;
            var local = new List<ModelIssue>();
            void LIssue(string c, string o, string m) => local.Add(new ModelIssue(c, o, m));
            foreach (int i in order)
            {
                ModelBlock b = doc.Blocks[i];
                ModelCatalogue.Spec? sp = specs[i];
                if (sp is null) continue;
                var dims = b.Inputs.Select(RefDimStep).ToArray();
                (int[] ps, string[] pn, ModelUnit? od) = ParamsAndDim(b, sp, dims, "block " + b.Id, paramDim, (r, _) => QuietConst(r), LIssue, holdDimFromInit);
                if (!Nullable.Equals(od, blockDim[i])) { blockDim[i] = od; changed = true; }
                int adv = b.Type == "washout" && b.Inputs.Count == 2 ? resolvedIn[i][1] : -1;
                opSpecs[i] = new OpSpec(b, sp, resolvedIn[i], ps, pn, blockSlot[i], adv >= 0, adv);
                if (b.Type == "washout" && b.Inputs.Count == 2 && dims[0] is { } d0 && dims[1] is { } d1 && d0 != d1)
                    LIssue("unit-mismatch", "block " + b.Id, $"the advance input is in {d1} but the washout input is in {d0}.");
            }
            finalIssues = local;
            if (!changed) break;
        }
        issues.AddRange(finalIssues ?? new List<ModelIssue>());
        // an unresolved dimension that a block needs is a wiring problem already reported; check init values vs blocks:
        foreach (InitAction a in initActions)
        {
            if (a.Kind != "initialise") continue;
            ModelBlock b = doc.Blocks[a.OpIndex];
            ModelCatalogue.Spec sp = specs[a.OpIndex]!;
            ModelUnit? expected = blockDim[a.OpIndex];
            for (int k = 0; k < a.Values.Length; k++)
            {
                ModelUnit? vd = initDimOfSlot.TryGetValue(a.Values[k], out ModelUnit? q) ? q : null;
                if (sp.Type == "hold") continue;   // a hold takes its dimension from the value it is initialised with
                bool isInput = sp.Type is "leadlag" or "washout" || (sp.Type == "pi" && k == 0);
                ModelUnit? want = isInput ? (b.Inputs.Count > 0 ? RefDimStep(b.Inputs[0]) : null) : expected;
                if (vd is { } v && want is { } w && v != w)
                    issues.Add(new ModelIssue("unit-mismatch", $"initialise {b.Id}", $"value {k} is in {v} but the block's {(isInput ? "input" : "output")} is in {w}."));
            }
        }

        // ---- ports -----------------------------------------------------------------------------------------------------
        var outSlots = new List<int>();
        foreach (ModelPort p in doc.Outputs)
        {
            ModelRef from = p.From!;
            if (from.Kind != ModelRefKind.Signal) { Issue("bad-reference", "output port " + p.Name, "an output port is bound to a block or input, not a constant."); outSlots.Add(0); continue; }
            if (blockIndex.TryGetValue(from.Name, out int bj) && !from.Prev)
            {
                outSlots.Add(blockSlot[bj]);
                if (blockDim[bj] is { } bd && bd != p.Unit)
                    Issue("unit-mismatch", "output port " + p.Name, $"declared in {p.Unit} but block '{from.Name}' produces {bd}.");
            }
            else if (!from.Prev && inputSlot.TryGetValue(from.Name, out int ps2)) outSlots.Add(ps2);
            else { Issue("undeclared-port", "output port " + p.Name, $"'{from}' is not a block output or input port of this model."); outSlots.Add(0); }
        }
        CheckRole(doc, Issue);

        // ---- finish ------------------------------------------------------------------------------------------------------
        plan.InputSlots = doc.Inputs.Select(p => inputSlot.TryGetValue(p.Name, out int s) ? s : 0).ToArray();
        plan.OutputSlots = outSlots.ToArray();
        plan.InitInputSlots = doc.Initialisation.Inputs.Select(p => initSlot.TryGetValue(p.Name, out int s) ? s : 0).ToArray();
        plan.Ops = order.Where(i => opSpecs[i] is not null).Select(i => opSpecs[i]).ToArray();
        plan.InitOps = initOps.ToArray();
        plan.InitActions = initActions.ToArray();
        plan.InitSequence = initSeq.ToArray();
        plan.PrevSnapshots = snaps.Select(s => (s.Item1, Array.IndexOf(plan.Ops.Select(o => o.Block.Id).ToArray(), doc.Blocks[s.Item2].Id))).ToArray();
        plan.Order = order.Select(i => doc.Blocks[i].Id).ToList();
        plan.OrderDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Sha256 + "|" + string.Join(",", plan.Order)))).ToLowerInvariant();
        return (issues.Count == 0 ? plan : null, issues);
    }

    private static void CheckBounds(ModelParameter p, double v, Action<string, string, string> issue)
    {
        string obj = "parameter " + p.Name;
        if (double.IsNaN(v)) { issue("non-finite", obj, "the value is not a number."); return; }
        if (double.IsInfinity(v) && !p.AllowInfinite) { issue("non-finite", obj, "the value is infinite."); return; }
        if (p.Min is double lo && (p.MinExclusive ? !(v > lo) : !(v >= lo)))
            issue("out-of-bounds", obj, $"{v.ToString("R", CultureInfo.InvariantCulture)} {p.UnitText} is outside the bound ({(p.MinExclusive ? ">" : ">=")} {lo.ToString("R", CultureInfo.InvariantCulture)}).");
        if (p.Max is double hi && (p.MaxExclusive ? !(v < hi) : !(v <= hi)))
            issue("out-of-bounds", obj, $"{v.ToString("R", CultureInfo.InvariantCulture)} {p.UnitText} is outside the bound ({(p.MaxExclusive ? "<" : "<=")} {hi.ToString("R", CultureInfo.InvariantCulture)}).");
    }

    internal static void CheckParameterValue(ModelParameter p, double v, List<ModelIssue> issues)
        => CheckBounds(p, v, (c, o, m) => issues.Add(new ModelIssue(c, o, m)));

    private static ModelUnit? RefDim(ModelRef r, Dictionary<string, ModelUnit> paramDim)
        => r.Kind == ModelRefKind.Parameter && paramDim.TryGetValue(r.Name, out ModelUnit d) ? d : null;

    private static ModelCatalogue.Spec? CheckBlockShape(ModelBlock b, string what, List<ModelIssue> issues, Dictionary<string, ModelUnit> paramDim)
    {
        string obj = $"{what} {b.Id}";
        ModelCatalogue.Spec? sp = ModelCatalogue.Find(b.Type);
        if (sp is null)
        {
            issues.Add(new ModelIssue("unknown-block", obj,
                $"the block type '{b.Type}' is not in the closed catalogue ({string.Join(", ", ModelCatalogue.All.Select(s => s.Type))}); "
                + "it is refused rather than approximated."));
            return null;
        }
        if (b.Inputs.Count < sp.MinIn || b.Inputs.Count > sp.MaxIn)
            issues.Add(new ModelIssue("bad-arity", obj, $"a {sp.Type} takes {(sp.MinIn == sp.MaxIn ? sp.MinIn.ToString(CultureInfo.InvariantCulture) : $"{sp.MinIn} to {sp.MaxIn}")} input(s), got {b.Inputs.Count}."));
        foreach (string req in sp.Required)
            if (!b.Params.Any(p => p.Key == req)) issues.Add(new ModelIssue("missing-param", obj, $"a {sp.Type} needs the parameter '{req}'."));
        foreach (var kv in b.Params)
            if (Array.IndexOf(sp.Required, kv.Key) < 0 && Array.IndexOf(sp.Optional, kv.Key) < 0)
                issues.Add(new ModelIssue("unknown-param", obj, $"'{kv.Key}' is not a parameter of a {sp.Type} (it takes: {string.Join(", ", sp.Required.Concat(sp.Optional))})."));
        if (sp.Type == "sum")
        {
            if (b.Signs is null || b.Signs.Length != b.Inputs.Count || b.Signs.Any(c => c != '+' && c != '-'))
                issues.Add(new ModelIssue("bad-signs", obj, "a sum needs a 'signs' text of '+' and '-' with one sign per input."));
        }
        else if (b.Signs is not null) issues.Add(new ModelIssue("unknown-param", obj, "'signs' belongs to a sum block only."));
        if (sp.Type == "table1d")
        {
            ModelTable? t = b.Table;
            if (t is null) issues.Add(new ModelIssue("missing-param", obj, "a table1d needs the 'table' member."));
            else
            {
                if (t.X.Count < 2 || t.X.Count != t.Y.Count) issues.Add(new ModelIssue("bad-table", obj, "the table needs at least two points and equal x and y counts."));
                for (int i = 1; i < t.X.Count; i++)
                    if (!(t.X[i] > t.X[i - 1])) { issues.Add(new ModelIssue("bad-table", obj, "the x values must be strictly increasing.")); break; }
                if (ModelUnit.Parse(t.XUnitText) is null || ModelUnit.Parse(t.YUnitText) is null)
                    issues.Add(new ModelIssue("bad-table", obj, "xUnit and yUnit must be units in the grammar."));
            }
        }
        else if (b.Table is not null) issues.Add(new ModelIssue("unknown-param", obj, "'table' belongs to a table1d block only."));
        if (sp.Type == "expr")
        {
            (ExprProgram? xp, string? err) = ExprProgram.Parse(b.Expr);
            if (b.Expr is null) issues.Add(new ModelIssue("missing-param", obj, "an expr block needs the 'expr' member (the expression text)."));
            else if (xp is null) issues.Add(new ModelIssue("bad-expr", obj, err!));
            else if (xp.MaxInput >= b.Inputs.Count)
                issues.Add(new ModelIssue("bad-expr", obj, $"the expression reads in{xp.MaxInput.ToString(CultureInfo.InvariantCulture)} but the block has {b.Inputs.Count.ToString(CultureInfo.InvariantCulture)} input(s)."));
            if (b.UnitText is null) issues.Add(new ModelIssue("missing-param", obj, "an expr block declares its output unit with the 'unit' member (it is not inferred)."));
            else if (ModelUnit.Parse(b.UnitText) is null) issues.Add(new ModelIssue("unit-mismatch", obj, $"'{b.UnitText}' is not a unit in the grammar (pu, 1, s combined with * and /)."));
        }
        else if (b.Expr is not null || b.UnitText is not null)
            issues.Add(new ModelIssue("unknown-param", obj, "'expr' and 'unit' belong to an expr block only."));
        if (sp.Type == "delay")
        {
            ModelRef? st = b.Params.FirstOrDefault(p => p.Key == "steps").Value;
            if (st is not null && (st.Kind != ModelRefKind.Literal || st.Literal < 1 || st.Literal != Math.Floor(st.Literal) || st.Literal > 100000))
                issues.Add(new ModelIssue("bad-param", obj, "'steps' of a delay must be a literal whole number from 1 to 100000."));
        }
        return sp;
    }

    /// <summary>Resolve a block's parameter slots (in catalogue order) and infer its output dimension; checks parameter units.</summary>
    private static (int[] Slots, string[] Names, ModelUnit? Out) ParamsAndDim(ModelBlock b, ModelCatalogue.Spec sp, ModelUnit?[] inDims, string obj,
        Dictionary<string, ModelUnit> paramDim, Func<ModelRef, string, int> resolve, Action<string, string, string> issue,
        Dictionary<string, ModelUnit?>? holdDims = null)
    {
        var names = new List<string>();
        var slots = new List<int>();
        foreach (string n in sp.Required.Concat(sp.Optional))
        {
            ModelRef? r = b.Params.FirstOrDefault(p => p.Key == n).Value;
            if (r is null) continue;
            names.Add(n);
            slots.Add(resolve(r, obj));
        }
        ModelUnit? PD(string n)
        {
            ModelRef? r = b.Params.FirstOrDefault(p => p.Key == n).Value;
            return r is not null && r.Kind == ModelRefKind.Parameter && paramDim.TryGetValue(r.Name, out ModelUnit d) ? d : null;
        }
        void Need(string n, ModelUnit expected)
        {
            if (PD(n) is { } d && d != expected)
                issue("unit-mismatch", obj, $"the parameter '{n}' of a {sp.Type} must be in {expected}, but '@{b.Params.First(p => p.Key == n).Value.Name}' is declared in {d}.");
        }
        ModelUnit? d0 = inDims.Length > 0 ? inDims[0] : null;
        ModelUnit? Same(IEnumerable<ModelUnit?> ds, string what)
        {
            ModelUnit? first = null;
            foreach (ModelUnit? d in ds)
            {
                if (d is null) continue;
                if (first is null) first = d;
                else if (first != d)
                {
                    issue("unit-mismatch", obj, $"the inputs of this {what} have different units ({first} and {d}); a {what} combines like with like.");
                    return first;
                }
            }
            return first;
        }
        ModelUnit One = ModelUnit.One, S = ModelUnit.Seconds;
        switch (sp.Type)
        {
            case "gain": return (slots.ToArray(), names.ToArray(), d0 is { } g ? g.Times(PD("k") ?? One) : null);
            case "div": return (slots.ToArray(), names.ToArray(), d0 is { } v ? v.Over(PD("d") ?? One) : null);
            case "sum": return (slots.ToArray(), names.ToArray(), Same(inDims, "sum"));
            case "product":
            {
                ModelUnit acc = One;
                foreach (ModelUnit? d in inDims) { if (d is null) return (slots.ToArray(), names.ToArray(), null); acc = acc.Times(d.Value); }
                return (slots.ToArray(), names.ToArray(), acc);
            }
            case "lag":
                Need("gain", One); Need("time", S);
                if (d0 is { } l) { Need("min", l); Need("max", l); }
                return (slots.ToArray(), names.ToArray(), d0);
            case "leadlag": Need("lead", S); Need("lag", S); return (slots.ToArray(), names.ToArray(), d0);
            case "washout": Need("time", S); return (slots.ToArray(), names.ToArray(), d0);
            case "integrator":
            {
                Need("gain", One.Over(S));
                ModelUnit? o = d0 is { } w ? w.Times(S) : null;
                if (o is { } ow) { Need("min", ow); Need("max", ow); }
                return (slots.ToArray(), names.ToArray(), o);
            }
            case "pi":
                Need("kp", One); Need("ki", One.Over(S));
                if (d0 is { } p) { Need("min", p); Need("max", p); }
                return (slots.ToArray(), names.ToArray(), d0);
            case "limit": if (d0 is { } c) { Need("min", c); Need("max", c); } return (slots.ToArray(), names.ToArray(), d0);
            case "ratelimit": if (d0 is { } r) { Need("rise", r.Over(S)); Need("fall", r.Over(S)); } return (slots.ToArray(), names.ToArray(), d0);
            case "deadband": if (d0 is { } db) { Need("lower", db); Need("upper", db); } return (slots.ToArray(), names.ToArray(), d0);
            case "table1d":
            {
                ModelUnit? xu = ModelUnit.Parse(b.Table?.XUnitText), yu = ModelUnit.Parse(b.Table?.YUnitText);
                if (xu is { } xx && d0 is { } dd && xx != dd)
                    issue("unit-mismatch", obj, $"the table is indexed in {xx} but its input is in {dd}.");
                return (slots.ToArray(), names.ToArray(), yu);
            }
            case "expsat":
                Need("a", One);
                if (d0 is { } e) Need("b", One.Over(e));
                return (slots.ToArray(), names.ToArray(), PD("a") ?? One);
            case "min":
            case "max": return (slots.ToArray(), names.ToArray(), Same(inDims, sp.Type));
            case "switch":
            {
                if (d0 is { } sd) Need("threshold", sd);
                return (slots.ToArray(), names.ToArray(), Same(new[] { inDims.Length > 1 ? inDims[1] : null, inDims.Length > 2 ? inDims[2] : null }, "switch"));
            }
            case "const": return (slots.ToArray(), names.ToArray(), PD("value") ?? One);
            case "hold": return (slots.ToArray(), names.ToArray(), holdDims is not null && holdDims.TryGetValue(b.Id, out ModelUnit? h) ? h : null);
            case "delay": return (slots.ToArray(), names.ToArray(), d0);
            case "satpole":
                Need("te", S); Need("ke", One); Need("a", One);
                if (d0 is { } sp0) Need("b", One.Over(sp0));
                return (slots.ToArray(), names.ToArray(), d0);
            case "expr":
            {
                // RC5 WP12: the parameters in first-appearance order (the program's parameter numbers); the output unit is the
                // declared one, never inferred.
                if (ExprProgram.Parse(b.Expr).Program is { } xp)
                    foreach (string pn in xp.Parameters)
                    {
                        names.Add("@" + pn);
                        slots.Add(resolve(new ModelRef(ModelRefKind.Parameter, pn, false, 0), obj));
                    }
                return (slots.ToArray(), names.ToArray(), ModelUnit.Parse(b.UnitText));
            }
            // RC5 wp32b: a timer and a seal-in give a 0 / 1 signal; the timer's time is in seconds.
            case "ontimer": Need("time", S); return (slots.ToArray(), names.ToArray(), One);
            case "latch": return (slots.ToArray(), names.ToArray(), One);
        }
        return (slots.ToArray(), names.ToArray(), null);
    }

    private static string FindLoop(ModelDocument doc, List<int>[] deps, bool[] done)
    {
        // follow unresolved dependencies from the lowest-index stuck block until a block repeats
        int cur = Array.FindIndex(done, d => !d);
        var seen = new List<int>();
        while (cur >= 0 && !seen.Contains(cur))
        {
            seen.Add(cur);
            int nxt = deps[cur].FirstOrDefault(j => !done[j], -1);
            cur = nxt;
        }
        if (cur < 0) return string.Join(" -> ", seen.Select(i => doc.Blocks[i].Id));
        int start = seen.IndexOf(cur);
        return string.Join(" -> ", seen.Skip(start).Select(i => doc.Blocks[i].Id).Append(doc.Blocks[cur].Id));
    }

    private static void CheckRole(ModelDocument doc, Action<string, string, string> issue)
    {
        (string[] ins, string[] outs, string[] init, bool eq)? want = doc.Role switch
        {
            "exciter" => (new[] { "verr", "speed" }, new[] { "efd" }, new[] { "efd0" }, true),
            "governor" => (new[] { "speed" }, new[] { "pm" }, new[] { "pm0" }, false),
            "stabiliser" => (new[] { "u" }, new[] { "vs" }, new[] { "u0" }, false),
            // RC5 WP12: terminal |V| in; active and reactive current exporting positive on the unit's MBASE (injection), or
            // drawn P and Q as multiples of the load's PMw and QMvar (load); started from p0, q0 and |V0|.
            "injection" => (new[] { "vm" }, new[] { "ip", "iq" }, new[] { "p0", "q0", "v0" }, false),
            "load" => (new[] { "vm" }, new[] { "p", "q" }, new[] { "p0", "q0", "v0" }, false),
            // RC5 wp32b: the fields of RelayInputs in, the trip (0, or the reason code) out; no initialisation inputs.
            "relay" => (new[] { "vm", "f", "p", "q", "dfdt", "speed", "i" }, new[] { "trip" }, Array.Empty<string>(), false),
            _ => null,
        };
        if (want is not { } w) return;
        if (!doc.Inputs.Select(p => p.Name).SequenceEqual(w.ins))
            issue("role-ports", "ports.inputs", $"a {doc.Role} has the input ports [{string.Join(", ", w.ins)}] in that order.");
        if (!doc.Outputs.Select(p => p.Name).SequenceEqual(w.outs))
            issue("role-ports", "ports.outputs", w.outs.Length == 1 ? $"a {doc.Role} has the output port [{string.Join(", ", w.outs)}]."
                : $"a {doc.Role} has the output ports [{string.Join(", ", w.outs)}] in that order.");
        if (!doc.Initialisation.Inputs.Select(p => p.Name).SequenceEqual(w.init))
            issue("role-ports", "initialisation.inputs", $"a {doc.Role} is initialised from [{string.Join(", ", w.init)}].");
        if (w.eq && doc.Initialisation.EquilibriumError is null)
            issue("role-ports", "initialisation.equilibriumError", "an exciter must name the initialisation value that is its steady regulator error.");
        foreach (ModelPort p in doc.Inputs.Concat(doc.Outputs).Concat(doc.Initialisation.Inputs))
        {
            // RC5 wp32b: a relay's dfdt is a rate (Hz/s), declared 1/s; every other relay port is dimensionless (f in Hz, 1).
            if (doc.Role == "relay" && p.Name == "dfdt" && doc.Inputs.Contains(p))
            {
                if (p.Unit != ModelUnit.One.Over(ModelUnit.Seconds)) issue("unit-mismatch", "port " + p.Name, $"a relay's 'dfdt' port is a rate in 1/s (Hz/s); it is declared in {p.Unit}.");
                continue;
            }
            if (p.Unit != ModelUnit.One) issue("unit-mismatch", "port " + p.Name, $"a {doc.Role} port is per-unit; '{p.Name}' is declared in {p.Unit}.");
        }
    }
}

/// <summary>Validate a document without compiling it for use: returns every issue (empty when valid).</summary>
public static class ModelValidator
{
    /// <summary>All issues in document order; empty for a valid, compilable document.</summary>
    public static IReadOnlyList<ModelIssue> Validate(ModelDocument doc) => ModelCompiler.Analyse(doc).Issues;
}
