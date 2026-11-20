using GridSim.Core.Dynamics.Models;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// One running copy of a compiled declarative model (R4-08): parameter values bound, every block's state fresh. It is the
/// interpreter. A step is, in this order and no other: (1) write the inputs; (2) snapshot the pre-step output of every
/// block read with <c>.prev</c>; (3) evaluate the compiled operation list in order, each stateful block advancing by
/// its own <see cref="ControlBlocks"/> step operator (so the arithmetic is the coded models' by construction); (4) apply
/// deferred state updates (a washout with an advance input) in the same order; (5) read the outputs. No reflection, no
/// dynamic code, no I/O, no hash-ordered collection is on this path: determinism follows from the closed vocabulary and
/// the fixed order. One instance is one machine's model; instances share nothing.
/// </summary>
public sealed class ModelInstance
{
    private readonly CompiledModel _plan;
    private readonly double[] _s;
    private readonly Op[] _ops;
    private readonly Op[] _initOps;
    private readonly Op[] _deferred;
    private readonly Dictionary<string, Op> _byBlock;

    private ModelInstance(CompiledModel plan, double[] s, Op[] ops, Op[] initOps)
    {
        _plan = plan; _s = s; _ops = ops; _initOps = initOps;
        _deferred = ops.Where(o => o.HasUpdate).ToArray();
        _byBlock = new Dictionary<string, Op>(StringComparer.Ordinal);   // lookup only, never enumerated
        foreach (Op o in ops) _byBlock[o.Block.Id] = o;
    }

    /// <summary>The model's document (id, role, hash).</summary>
    public ModelDocument Document => _plan.Document;

    /// <summary>The compiled evaluation order (block ids).</summary>
    public IReadOnlyList<string> Order => _plan.Order;

    /// <summary>The bound parameter values by name (declared parameters, then derived constants), in document order.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> ParameterValues
        => _plan.Document.Parameters.Select(p => p.Name).Concat(_plan.Document.Derived.Select(d => d.Name))
            .Select(n => new KeyValuePair<string, double>(n, _s[_plan.ParamIndex[n]])).ToList();

    /// <summary>The steady regulator error computed at the last <see cref="Initialize"/> (exciter models), else NaN.</summary>
    public double EquilibriumError { get; private set; } = double.NaN;

    /// <summary>Output port <paramref name="index"/> after the last step.</summary>
    public double Output(int index) => _s[_plan.OutputSlots[index]];

    /// <summary>Bind parameter values (overriding the document defaults by name) and build a fresh instance.</summary>
    /// <exception cref="UserModelException">A non-finite value, a value outside its bounds, an unknown name, a violated sum constraint, or a block that refuses its values.</exception>
    public static ModelInstance Create(CompiledModel plan, IReadOnlyDictionary<string, double>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ModelDocument doc = plan.Document;
        var issues = new List<ModelIssue>();
        var s = new double[plan.SlotCount];
        if (overrides is not null)
            foreach (string name in overrides.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (!doc.Parameters.Any(p => p.Name == name))
                    issues.Add(new ModelIssue("unknown-parameter", name, "the model has no parameter of this name."));
        foreach (ModelParameter p in doc.Parameters)
        {
            double v = overrides is not null && overrides.TryGetValue(p.Name, out double ov) ? ov : p.Value;
            ModelCompiler.CheckParameterValue(p, v, issues);
            s[plan.ParamIndex[p.Name]] = v;
        }
        for (int i = 0; i < plan.ConstValues.Length; i++) s[plan.ConstBase + i] = plan.ConstValues[i];
        foreach (ModelDerived d in doc.Derived)
        {
            double a = Const(d.A, s, plan), b = Const(d.B, s, plan);
            if (d.Op == "div" && b == 0.0) issues.Add(new ModelIssue("division-by-zero", "derived " + d.Name, "the divisor is zero."));
            s[plan.ParamIndex[d.Name]] = d.Op switch { "add" => a + b, "sub" => a - b, "mul" => a * b, _ => a / b };
        }
        foreach ((IReadOnlyList<string> names, double target, double tol) in doc.SumConstraints)
        {
            double sum = 0.0;
            foreach (string n in names) sum += s[plan.ParamIndex[n]];
            if (!(Math.Abs(sum - target) <= tol))
                issues.Add(new ModelIssue("constraint", "constraint", $"the parameters {string.Join(", ", names)} must sum to "
                    + $"{ControlBlocks.Num(target)} (within {ControlBlocks.Num(tol)}); they sum to {ControlBlocks.Num(sum)}."));
        }
        if (issues.Count > 0) throw new UserModelException(issues);

        var ops = new Op[plan.Ops.Length];
        var initOps = new Op[plan.InitOps.Length];
        for (int i = 0; i < ops.Length; i++) ops[i] = Build(plan.Ops[i], s, "block ");
        for (int i = 0; i < initOps.Length; i++) initOps[i] = Build(plan.InitOps[i], s, "initialisation step ");
        return new ModelInstance(plan, s, ops, initOps);
    }

    private static Op Build(OpSpec spec, double[] s, string what)
    {
        try { return Op.Make(spec, s, spec.Block); }
        catch (ArgumentException ex)
        {
            throw new UserModelException("block-refused", what + spec.Block.Id, $"the {spec.Spec.Type} refuses its values: {ex.Message}");
        }
    }

    private static double Const(ModelRef r, double[] s, CompiledModel plan)
        => r.Kind == ModelRefKind.Literal ? r.Literal : s[plan.ParamIndex[r.Name]];

    /// <summary>
    /// Initialise at an operating point: write the initialisation inputs, run the initialisation steps in their listed
    /// order (computations, state presets, equilibrium-within-limits checks), and keep the equilibrium error.
    /// A value that cannot be an equilibrium is refused with <see cref="ArgumentOutOfRangeException"/>, as the coded
    /// models refuse it (R252).
    /// </summary>
    public void Initialize(ReadOnlySpan<double> initInputs)
    {
        if (initInputs.Length != _plan.InitInputSlots.Length)
            throw new ArgumentException($"{Document.Id}: expected {_plan.InitInputSlots.Length} initialisation input(s), got {initInputs.Length}.", nameof(initInputs));
        for (int i = 0; i < initInputs.Length; i++) _s[_plan.InitInputSlots[i]] = initInputs[i];
        foreach ((bool isAction, int idx) in _plan.InitSequence)
        {
            if (!isAction) { _initOps[idx].Eval(_s, 0.0); continue; }
            InitAction a = _plan.InitActions[idx];
            if (a.Kind == "initialise")
                _byBlock[_plan.Document.Blocks[a.OpIndex].Id].Initialize(_s, a.Values);
            else
            {
                ModelInitRequire rq = a.Require!;
                double min = a.MinSlot < 0 ? double.NegativeInfinity : _s[a.MinSlot];
                double max = a.MaxSlot < 0 ? double.PositiveInfinity : _s[a.MaxSlot];
                ControlBlocks.RequireEquilibriumWithinLimits(rq.Model, rq.Quantity, _s[a.ValueSlot], min, max, rq.Argument);
            }
        }
        EquilibriumError = _plan.EquilibriumErrorSlot >= 0 ? _s[_plan.EquilibriumErrorSlot] : double.NaN;
    }

    /// <summary>Initialise from one value.</summary>
    public void Initialize(double a) { Span<double> v = stackalloc double[1]; v[0] = a; Initialize(v); }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with the input ports in declaration order.</summary>
    public void Step(ReadOnlySpan<double> inputs, double dtSeconds)
    {
        if (inputs.Length != _plan.InputSlots.Length)
            throw new ArgumentException($"{Document.Id}: expected {_plan.InputSlots.Length} input(s), got {inputs.Length}.", nameof(inputs));
        for (int i = 0; i < inputs.Length; i++) _s[_plan.InputSlots[i]] = inputs[i];
        foreach ((int slot, int op) in _plan.PrevSnapshots) _s[slot] = _ops[op].Current;
        for (int i = 0; i < _ops.Length; i++) _ops[i].Eval(_s, dtSeconds);
        for (int i = 0; i < _deferred.Length; i++) _deferred[i].Update(_s, dtSeconds);
    }

    /// <summary>One-input step returning output port 0.</summary>
    public double Step(double a, double dtSeconds)
    {
        Span<double> v = stackalloc double[1]; v[0] = a;
        Step(v, dtSeconds);
        return Output(0);
    }

    /// <summary>Two-input step returning output port 0.</summary>
    public double Step(double a, double b, double dtSeconds)
    {
        Span<double> v = stackalloc double[2]; v[0] = a; v[1] = b;
        Step(v, dtSeconds);
        return Output(0);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // The operations. Each does exactly the arithmetic of the coded class it mirrors, in the same operand order.
    // ---------------------------------------------------------------------------------------------------------------------

    private abstract class Op
    {
        public ModelBlock Block = null!;
        public int Out;
        public abstract void Eval(double[] s, double dt);
        public virtual bool HasUpdate => false;
        public virtual void Update(double[] s, double dt) { }
        public virtual double Current => double.NaN;
        public virtual void Initialize(double[] s, int[] vals) => throw new InvalidOperationException("not stateful");

        public static Op Make(OpSpec sp, double[] s, ModelBlock block)
        {
            double P(string n, double dflt = double.NaN)
            {
                int i = Array.IndexOf(sp.ParamNames, n);
                return i < 0 ? dflt : s[sp.ParamSlots[i]];
            }
            int a = sp.In.Length > 0 ? sp.In[0] : -1;
            Op op = sp.Spec.Type switch
            {
                "gain" => new GainOp(a, P("k")),
                "div" => new DivOp(a, P("d")),
                "sum" => new SumOp(sp.In, block.Signs!),
                "product" => new ProductOp(sp.In),
                "lag" => new LagOp(a, new FirstOrderLag(P("gain"), P("time"), P("min", double.NegativeInfinity), P("max", double.PositiveInfinity))),
                "leadlag" => new LeadLagOp(a, new LeadLag(P("lead"), P("lag"))),
                "washout" => new WashoutOp(a, sp.AdvanceSlot, new Washout(P("time"))),
                "integrator" => new IntegratorOp(a, new Integrator(P("gain"), P("min", double.NegativeInfinity), P("max", double.PositiveInfinity))),
                "pi" => new PiOp(a, new PiController(P("kp"), P("ki"), P("min", double.NegativeInfinity), P("max", double.PositiveInfinity))),
                "limit" => new LimitOp(a, P("min"), P("max")),
                "ratelimit" => new RateLimitOp(a, new RateLimiter(P("rise"), P("fall"))),
                "deadband" => new DeadBandOp(a, P("lower"), P("upper")),
                "table1d" => new TableOp(a, block.Table!.X.ToArray(), block.Table.Y.ToArray()),
                "expsat" => new ExpSatOp(a, P("a"), P("b")),
                "min" => new MinMaxOp(sp.In, true),
                "max" => new MinMaxOp(sp.In, false),
                "switch" => new SwitchOp(sp.In, P("threshold")),
                "const" => new ConstOp(P("value")),
                "hold" => new HoldOp(),
                "delay" => new DelayOp(a, new StepDelay((int)P("steps"))),
                "satpole" => new SatPoleOp(a, new SaturatedExciterPole(P("te"), P("ke"), P("a"), P("b"))),
                _ => throw new InvalidOperationException("catalogue/instance mismatch: " + sp.Spec.Type),
            };
            op.Block = block;
            op.Out = sp.Out;
            return op;
        }
    }

    private sealed class GainOp(int a, double k) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = k * s[a];
    }

    private sealed class DivOp : Op
    {
        private readonly int _a; private readonly double _d;
        public DivOp(int a, double d)
        {
            if (d == 0.0 || double.IsNaN(d)) throw new ArgumentException("the divisor must be non-zero.");
            _a = a; _d = d;
        }
        public override void Eval(double[] s, double dt) => s[Out] = s[_a] / _d;
    }

    private sealed class SumOp(int[] ins, string signs) : Op
    {
        public override void Eval(double[] s, double dt)
        {
            double acc = signs[0] == '+' ? s[ins[0]] : -s[ins[0]];
            for (int i = 1; i < ins.Length; i++)
                acc = signs[i] == '+' ? acc + s[ins[i]] : acc - s[ins[i]];
            s[Out] = acc;
        }
    }

    private sealed class ProductOp(int[] ins) : Op
    {
        public override void Eval(double[] s, double dt)
        {
            double acc = s[ins[0]];
            for (int i = 1; i < ins.Length; i++) acc = acc * s[ins[i]];
            s[Out] = acc;
        }
    }

    private sealed class LagOp(int a, FirstOrderLag lag) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = lag.Step(s[a], dt);
        public override double Current => lag.Output;
        public override void Initialize(double[] s, int[] vals) => lag.Initialize(s[vals[0]]);
    }

    private sealed class LeadLagOp(int a, LeadLag ll) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = ll.Step(s[a], dt);
        public override void Initialize(double[] s, int[] vals) => ll.Initialize(s[vals[0]]);
    }

    private sealed class WashoutOp(int a, int advance, Washout w) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = advance < 0 ? w.Step(s[a], dt) : w.OutputFor(s[a]);
        public override bool HasUpdate => advance >= 0;
        public override void Update(double[] s, double dt) => w.Step(s[advance], dt);
        public override void Initialize(double[] s, int[] vals) => w.Initialize(s[vals[0]]);
    }

    private sealed class IntegratorOp(int a, Integrator i) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = i.Step(s[a], dt);
        public override double Current => i.Output;
        public override void Initialize(double[] s, int[] vals) => i.Initialize(s[vals[0]]);
    }

    private sealed class PiOp(int a, PiController p) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = p.Step(s[a], dt);
        public override void Initialize(double[] s, int[] vals) => p.Initialize(s[vals[0]], s[vals[1]]);
    }

    private sealed class LimitOp : Op
    {
        private readonly int _a; private readonly double _min, _max;
        public LimitOp(int a, double min, double max)
        {
            if (!(max >= min)) throw new ArgumentException("a limit needs max >= min.");
            _a = a; _min = min; _max = max;
        }
        public override void Eval(double[] s, double dt) => s[Out] = Math.Clamp(s[_a], _min, _max);
    }

    private sealed class RateLimitOp(int a, RateLimiter r) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = r.Step(s[a], dt);
        public override double Current => r.Output;
        public override void Initialize(double[] s, int[] vals) => r.Initialize(s[vals[0]]);
    }

    private sealed class DeadBandOp : Op
    {
        private readonly int _a; private readonly double _lo, _hi;
        public DeadBandOp(int a, double lo, double hi)
        {
            if (!(hi >= lo)) throw new ArgumentException("a dead band needs upper >= lower.");
            _a = a; _lo = lo; _hi = hi;
        }
        public override void Eval(double[] s, double dt) => s[Out] = UserBlocks.DeadBand(s[_a], _lo, _hi);
    }

    private sealed class TableOp(int a, double[] x, double[] y) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = UserBlocks.Table(s[a], x, y);
    }

    private sealed class ExpSatOp(int a, double ca, double cb) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = UserBlocks.ExpSat(s[a], ca, cb);
    }

    private sealed class MinMaxOp(int[] ins, bool min) : Op
    {
        public override void Eval(double[] s, double dt)
        {
            double acc = s[ins[0]];
            for (int i = 1; i < ins.Length; i++) acc = min ? Math.Min(acc, s[ins[i]]) : Math.Max(acc, s[ins[i]]);
            s[Out] = acc;
        }
    }

    private sealed class SwitchOp(int[] ins, double threshold) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = s[ins[0]] >= threshold ? s[ins[1]] : s[ins[2]];
    }

    private sealed class ConstOp(double v) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = v;
    }

    private sealed class HoldOp : Op
    {
        private double _v;
        public override void Eval(double[] s, double dt) => s[Out] = _v;
        public override double Current => _v;
        public override void Initialize(double[] s, int[] vals) => _v = s[vals[0]];
    }

    private sealed class DelayOp(int a, StepDelay d) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = d.Step(s[a]);
        public override double Current => d.Output;
        public override void Initialize(double[] s, int[] vals) => d.Initialize(s[vals[0]]);
    }

    private sealed class SatPoleOp(int a, SaturatedExciterPole p) : Op
    {
        public override void Eval(double[] s, double dt) => s[Out] = p.Step(s[a], dt);
        public override double Current => p.Output;
        public override void Initialize(double[] s, int[] vals) => p.Initialize(s[vals[0]]);
    }
}
