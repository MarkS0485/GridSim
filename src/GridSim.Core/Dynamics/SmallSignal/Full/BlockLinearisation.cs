using System.Globalization;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.SmallSignal.Full;

/// <summary>
/// R4-12: a controller (or any declarative model) that cannot be linearised, with the reason. A refusal is a reported
/// outcome (the model is listed as NOT LINEARISED), never an approximation made silently.
/// </summary>
public sealed class NotLinearisableException : Exception
{
    /// <summary>Short machine-readable code (for example <c>fixed-delay</c>, <c>not-at-equilibrium</c>).</summary>
    public string Code { get; }

    /// <summary>The model id.</summary>
    public string ModelId { get; }

    /// <summary>The offending block or object.</summary>
    public string Object { get; }

    /// <summary>Make the exception.</summary>
    public NotLinearisableException(string code, string modelId, string obj, string message)
        : base($"{modelId}: {obj}: {message}")
    {
        Code = code; ModelId = modelId; Object = obj;
    }
}

/// <summary>
/// The linearised state-space of one declarative model about its initial (equilibrium) state, in the continuous-time
/// limit of its blocks: <c>dx/dt = A x + B u</c>, <c>y = C x + D u</c>, all in per-unit deviations from the operating point.
/// </summary>
public sealed class LinearBlockModel
{
    /// <summary>Model id.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Model role (exciter, governor, stabiliser, generic).</summary>
    public string Role { get; init; } = "";

    /// <summary>Document SHA-256.</summary>
    public string Hash { get; init; } = "";

    /// <summary>State names (<c>block</c>, <c>block.xL</c> or <c>block.xI</c>), in state order.</summary>
    public IReadOnlyList<string> StateNames { get; init; } = Array.Empty<string>();

    /// <summary>State matrix (nS x nS).</summary>
    public double[,] A { get; init; } = new double[0, 0];

    /// <summary>Input matrix (nS x nI).</summary>
    public double[,] B { get; init; } = new double[0, 0];

    /// <summary>Output-from-state matrix (nO x nS).</summary>
    public double[,] C { get; init; } = new double[0, 0];

    /// <summary>Feedthrough matrix (nO x nI).</summary>
    public double[,] D { get; init; } = new double[0, 0];

    /// <summary>Operating state values.</summary>
    public IReadOnlyList<double> X0 { get; init; } = Array.Empty<double>();

    /// <summary>Operating input values (the model's input ports in order).</summary>
    public IReadOnlyList<double> U0 { get; init; } = Array.Empty<double>();

    /// <summary>Operating output values (the model's output ports in order).</summary>
    public IReadOnlyList<double> Y0 { get; init; } = Array.Empty<double>();

    /// <summary>Statements about the operating point: limits that are active (state frozen), non-differentiable points,
    /// rate limiters taken as inactive. Empty when nothing needs saying.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>State count.</summary>
    public int StateCount => StateNames.Count;

    /// <summary>Input count.</summary>
    public int InputCount => U0.Count;

    /// <summary>Output count.</summary>
    public int OutputCount => Y0.Count;
}

/// <summary>
/// R4-12: analytic linearisation of a compiled R4-08 declarative model about its initial state. Every catalogue block has
/// a closed-form Jacobian (no finite differences): gain, div, sum, product, limit, dead band, table, exponential
/// saturation, min, max, switch and constant by their partial derivatives at the operating point; lag, lead-lag, washout,
/// integrator, PI and the saturated exciter pole by their continuous-time state equations; hold as a constant.
/// A fixed delay has no finite-order state space and is refused (a Pade approximation would be an approximation made on the
/// user's behalf). A rate limiter is taken as inactive (its small-signal gain is one) and a limit that is active at the
/// operating point freezes the limited state; both are recorded in <see cref="LinearBlockModel.Notes"/>.
/// <para>Evaluation follows the compiled order. A <c>.prev</c> reference reads the block's state (in the continuous limit the
/// pre-step output of a lag, integrator or saturated pole is its state); a washout advance input enters its state equation.
/// The operating point is checked twice: every state must have zero derivative (the model is at equilibrium), and the
/// operating outputs must equal what the model's own interpreter holds after a step at that equilibrium.</para>
/// </summary>
public static class BlockLinearisation
{
    /// <summary>Largest allowed state derivative numerator at the operating point (relative to 1 + scale).</summary>
    public const double EquilibriumTolerance = 1e-9;

    /// <summary>Linearise a model. <paramref name="initInputs"/> are the initialisation inputs (efd0, pm0 or u0);
    /// <paramref name="stepInputs"/> (optional) are the operating values of the input ports, which default by role:
    /// exciter (equilibrium error, 0), governor (0), stabiliser (u0).</summary>
    /// <exception cref="NotLinearisableException">The model cannot be linearised, with the reason.</exception>
    public static LinearBlockModel Linearise(UserModel model, ReadOnlySpan<double> initInputs, ReadOnlySpan<double> stepInputs = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new Builder(model, initInputs.ToArray(), stepInputs.ToArray()).Build();
    }

    private sealed class Builder
    {
        private readonly UserModel _model;
        private readonly CompiledModel _plan;
        private readonly ModelDocument _doc;
        private readonly double[] _init;
        private readonly double[] _stepIn;
        private readonly double[] _s;
        private readonly List<string> _notes = new();
        private string Id => _doc.Id;

        // state bookkeeping
        private readonly List<string> _stateNames = new();
        private readonly List<double> _x0 = new();
        private readonly List<(int State, double[] Row, double Num, string Obj)> _derivs = new();

        public Builder(UserModel model, double[] init, double[] stepIn)
        {
            _model = model; _plan = model.Plan; _doc = _plan.Document; _init = init; _stepIn = stepIn;
            _s = new double[_plan.SlotCount];
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private NotLinearisableException Refuse(string code, string obj, string msg) => new(code, Id, obj, msg);

        private double P(OpSpec op, string name, double dflt = double.NaN)
        {
            int i = Array.IndexOf(op.ParamNames, name);
            return i < 0 ? dflt : _s[op.ParamSlots[i]];
        }

        public LinearBlockModel Build()
        {
            // 1. The interpreter validates the initialisation (limits, R252) and gives the steady regulator error.
            ModelInstance probe = _model.NewInstance();
            probe.Initialize(_init);

            double[] u0 = _stepIn.Length > 0 ? _stepIn : DefaultStepInputs(probe);
            if (u0.Length != _plan.InputSlots.Length)
                throw Refuse("bad-inputs", "input ports", $"expected {_plan.InputSlots.Length} operating input value(s), got {u0.Length}.");

            // 2. Parameters and literals into the slot vector.
            foreach (KeyValuePair<string, double> kv in probe.ParameterValues) _s[_plan.ParamIndex[kv.Key]] = kv.Value;
            for (int i = 0; i < _plan.ConstValues.Length; i++) _s[_plan.ConstBase + i] = _plan.ConstValues[i];

            // 3. Initialisation: combinational computations and state presets, in document order.
            for (int i = 0; i < _init.Length; i++) _s[_plan.InitInputSlots[i]] = _init[i];
            var presets = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var scratch = new double[16];
            foreach ((bool isAction, int idx) in _plan.InitSequence)
            {
                if (!isAction)
                {
                    OpSpec io = _plan.InitOps[idx];
                    _s[io.Out] = Comb(io, scratch);
                    continue;
                }
                InitAction a = _plan.InitActions[idx];
                if (a.Kind != "initialise") continue;
                presets[_doc.Blocks[a.OpIndex].Id] = a.Values.Select(v => _s[v]).ToArray();
            }

            // 4. Inputs at the operating point.
            for (int i = 0; i < u0.Length; i++) _s[_plan.InputSlots[i]] = u0[i];

            // 5. State numbering (pre-pass, from the presets and limits only) so that .prev can name its state.
            var ops = _plan.Ops;
            var stateOf = new Dictionary<string, int>(StringComparer.Ordinal);      // block id -> state index of its OUTPUT state (or -1)
            var kinds = new Dictionary<string, BlockKind>(StringComparer.Ordinal);
            foreach (OpSpec op in ops)
            {
                string t = op.Spec.Type;
                if (!op.Spec.Stateful) continue;
                double[] pv = presets[op.Block.Id];
                switch (t)
                {
                    case "delay":
                        throw Refuse("fixed-delay", "block " + op.Block.Id,
                            $"a fixed delay of {R(P(op, "steps"))} step(s) has no finite-order continuous-time state space; it is not linearised (a Pade approximation would be an approximation made for you).");
                    case "lag":
                    {
                        double tc = P(op, "time"), mn = P(op, "min", double.NegativeInfinity), mx = P(op, "max", double.PositiveInfinity);
                        double y0 = Math.Clamp(pv[0], mn, mx);
                        if (tc <= ControlBlocks.InstantThresholdSeconds) { kinds[op.Block.Id] = BlockKind.Algebraic; break; }
                        bool frozen = y0 <= mn || y0 >= mx;
                        if (frozen) _notes.Add($"block {op.Block.Id}: lag output {R(y0)} is at its limit [{R(mn)}, {R(mx)}]; its state is frozen (no dynamics) in the linearisation.");
                        kinds[op.Block.Id] = frozen ? BlockKind.Frozen : BlockKind.OutputState;
                        if (!frozen) stateOf[op.Block.Id] = AddState(op.Block.Id, y0);
                        break;
                    }
                    case "integrator":
                    {
                        double mn = P(op, "min", double.NegativeInfinity), mx = P(op, "max", double.PositiveInfinity);
                        double x0 = Math.Clamp(pv[0], mn, mx);
                        bool frozen = x0 <= mn || x0 >= mx;
                        if (frozen) _notes.Add($"block {op.Block.Id}: integrator output {R(x0)} is at its limit [{R(mn)}, {R(mx)}]; its state is frozen in the linearisation.");
                        kinds[op.Block.Id] = frozen ? BlockKind.Frozen : BlockKind.OutputState;
                        if (!frozen) stateOf[op.Block.Id] = AddState(op.Block.Id, x0);
                        break;
                    }
                    case "satpole":
                        kinds[op.Block.Id] = BlockKind.OutputState;
                        stateOf[op.Block.Id] = AddState(op.Block.Id, pv[0]);
                        break;
                    case "hold":
                        kinds[op.Block.Id] = BlockKind.Constant;
                        break;
                    case "ratelimit":
                        kinds[op.Block.Id] = BlockKind.PassThrough;
                        _notes.Add($"block {op.Block.Id}: rate limiter taken as inactive (small-signal gain 1); it is not a small-signal element.");
                        break;
                    case "leadlag":
                    {
                        double c = P(op, "lead") / P(op, "lag");
                        kinds[op.Block.Id] = c == 1.0 ? BlockKind.PassThrough : BlockKind.Auxiliary;
                        if (c != 1.0) stateOf[op.Block.Id] = AddState(op.Block.Id + ".xL", (1.0 - c) * pv[0]);
                        break;
                    }
                    case "washout":
                        kinds[op.Block.Id] = BlockKind.Auxiliary;
                        stateOf[op.Block.Id] = AddState(op.Block.Id + ".xL", pv[0]);
                        break;
                    case "pi":
                    {
                        double mn = P(op, "min", double.NegativeInfinity), mx = P(op, "max", double.PositiveInfinity);
                        double xi = Math.Clamp(pv[1] - P(op, "kp") * pv[0], mn, mx);
                        bool frozen = xi <= mn || xi >= mx;
                        if (frozen) _notes.Add($"block {op.Block.Id}: PI integral {R(xi)} is at its limit [{R(mn)}, {R(mx)}]; the integral state is frozen in the linearisation.");
                        kinds[op.Block.Id] = frozen ? BlockKind.Frozen : BlockKind.Auxiliary;
                        if (!frozen) stateOf[op.Block.Id] = AddState(op.Block.Id + ".xI", xi);
                        break;
                    }
                    default:
                        throw Refuse("unknown-block", "block " + op.Block.Id, $"no linearisation is defined for '{t}'.");
                }
            }
            int nS = _stateNames.Count, nI = u0.Length, w = nS + nI;

            // 6. Snapshot slots (.prev): the block's pre-step output is its state in the continuous limit.
            var lin = new double[_plan.SlotCount][];
            foreach ((int slot, int opIdx) in _plan.PrevSnapshots)
            {
                OpSpec tgt = ops[opIdx];
                string id = tgt.Block.Id;
                double[] row = new double[w];
                switch (kinds[id])
                {
                    case BlockKind.OutputState: row[stateOf[id]] = 1.0; _s[slot] = _x0[stateOf[id]]; break;
                    case BlockKind.Frozen:
                    case BlockKind.Constant:
                        _s[slot] = ConstantOutput(tgt, presets[id]); break;
                    default:
                        throw Refuse("prev-of-algebraic", "block " + id,
                            $"'{id}.prev' reads a {tgt.Spec.Type} whose output is algebraic in the continuous limit, so a feedback through it is an algebraic loop; not linearised.");
                }
                lin[slot] = row;
            }
            for (int i = 0; i < nI; i++)
            {
                var row = new double[w];
                row[nS + i] = 1.0;
                lin[_plan.InputSlots[i]] = row;
            }

            // 7. Step pass in compiled order.
            var partials = new double[16];
            foreach (OpSpec op in ops)
            {
                string t = op.Spec.Type;
                if (!op.Spec.Stateful)
                {
                    double val = Comb(op, partials);
                    _s[op.Out] = val;
                    double[] f = new double[w];
                    for (int k = 0; k < op.In.Length; k++)
                    {
                        double[]? fi = lin[op.In[k]];
                        if (fi is null || partials[k] == 0.0) continue;
                        for (int j = 0; j < w; j++) f[j] += partials[k] * fi[j];
                    }
                    lin[op.Out] = f;
                    continue;
                }
                string id = op.Block.Id;
                double[] pv = presets[id];
                double[] u = op.In.Length > 0 ? Form(lin, op.In[0], w) : new double[w];
                double uv = op.In.Length > 0 ? _s[op.In[0]] : 0.0;
                double[] outForm = new double[w];
                double outVal;
                switch (t)
                {
                    case "lag":
                    {
                        double k = P(op, "gain"), tc = P(op, "time"), mn = P(op, "min", double.NegativeInfinity), mx = P(op, "max", double.PositiveInfinity);
                        if (kinds[id] == BlockKind.Algebraic)
                        {
                            double y = k * uv;
                            bool act = y <= mn || y >= mx;
                            outVal = Math.Clamp(y, mn, mx);
                            if (act) _notes.Add($"block {id}: instantaneous lag output {R(outVal)} is at its limit; its gain is taken as 0.");
                            else for (int j = 0; j < w; j++) outForm[j] = k * u[j];
                        }
                        else if (kinds[id] == BlockKind.Frozen) outVal = Math.Clamp(pv[0], mn, mx);
                        else
                        {
                            int si = stateOf[id];
                            outVal = _x0[si];
                            outForm[si] = 1.0;
                            var d = new double[w];
                            for (int j = 0; j < w; j++) d[j] = k * u[j] / tc;
                            d[si] -= 1.0 / tc;
                            _derivs.Add((si, d, k * uv - outVal, id));
                        }
                        break;
                    }
                    case "leadlag":
                    {
                        double c = P(op, "lead") / P(op, "lag"), tb = P(op, "lag");
                        if (kinds[id] == BlockKind.PassThrough) { outVal = uv; Array.Copy(u, outForm, w); break; }
                        int si = stateOf[id];
                        outVal = c * uv + _x0[si];
                        for (int j = 0; j < w; j++) outForm[j] = c * u[j];
                        outForm[si] += 1.0;
                        var d = new double[w];
                        for (int j = 0; j < w; j++) d[j] = (1.0 - c) * u[j] / tb;
                        d[si] -= 1.0 / tb;
                        _derivs.Add((si, d, (1.0 - c) * uv - _x0[si], id));
                        break;
                    }
                    case "washout":
                    {
                        double tw = P(op, "time");
                        int si = stateOf[id];
                        outVal = uv - _x0[si];
                        Array.Copy(u, outForm, w);
                        outForm[si] -= 1.0;
                        // the state follows the advance input when there is one (read after all blocks, below)
                        _pendingWashouts.Add((si, op.In.Length == 2 ? op.In[1] : op.In[0], tw, id));
                        break;
                    }
                    case "integrator":
                    {
                        double k = P(op, "gain");
                        if (kinds[id] == BlockKind.Frozen) { outVal = Math.Clamp(pv[0], P(op, "min", double.NegativeInfinity), P(op, "max", double.PositiveInfinity)); break; }
                        int si = stateOf[id];
                        outVal = _x0[si];
                        outForm[si] = 1.0;
                        var d = new double[w];
                        for (int j = 0; j < w; j++) d[j] = k * u[j];
                        _derivs.Add((si, d, k * uv, id));
                        break;
                    }
                    case "pi":
                    {
                        double kp = P(op, "kp"), ki = P(op, "ki"), mn = P(op, "min", double.NegativeInfinity), mx = P(op, "max", double.PositiveInfinity);
                        double xi = kinds[id] == BlockKind.Frozen ? Math.Clamp(pv[1] - kp * pv[0], mn, mx) : _x0[stateOf[id]];
                        double y = kp * uv + xi;
                        outVal = Math.Clamp(y, mn, mx);
                        bool act = y <= mn || y >= mx;
                        if (act) _notes.Add($"block {id}: PI output {R(outVal)} is at its limit; its gain is taken as 0.");
                        else
                        {
                            for (int j = 0; j < w; j++) outForm[j] = kp * u[j];
                            if (kinds[id] != BlockKind.Frozen) outForm[stateOf[id]] += 1.0;
                        }
                        if (kinds[id] != BlockKind.Frozen)
                        {
                            var d = new double[w];
                            for (int j = 0; j < w; j++) d[j] = ki * u[j];
                            _derivs.Add((stateOf[id], d, ki * uv, id));
                        }
                        break;
                    }
                    case "ratelimit":
                        outVal = uv; Array.Copy(u, outForm, w);
                        if (Math.Abs(uv - pv[0]) > EquilibriumTolerance * (1.0 + Math.Abs(uv)))
                            throw Refuse("not-at-equilibrium", "block " + id, $"the rate limiter holds {R(pv[0])} but its input is {R(uv)}.");
                        break;
                    case "hold":
                        outVal = pv[0]; break;
                    case "satpole":
                    {
                        double te = P(op, "te"), ke = P(op, "ke"), a = P(op, "a"), b = P(op, "b");
                        int si = stateOf[id];
                        double e0 = _x0[si];
                        double ex = DetMath.Exp(b * e0);
                        double f0 = (ke + a * ex) * e0;
                        double df = ke + a * ex * (1.0 + b * e0);
                        outVal = e0;
                        outForm[si] = 1.0;
                        var d = new double[w];
                        for (int j = 0; j < w; j++) d[j] = u[j] / te;
                        d[si] -= df / te;
                        _derivs.Add((si, d, uv - f0, id));
                        break;
                    }
                    default:
                        throw Refuse("unknown-block", "block " + id, $"no linearisation is defined for '{t}'.");
                }
                _s[op.Out] = outVal;
                lin[op.Out] = outForm;
            }
            // washout state equations: T dxL/dt = adv - xL (the advance input may be computed after the washout in order)
            foreach ((int si, int advSlot, double tw, string id) in _pendingWashouts)
            {
                double[] adv = Form(lin, advSlot, w);
                var d = new double[w];
                for (int j = 0; j < w; j++) d[j] = adv[j] / tw;
                d[si] -= 1.0 / tw;
                _derivs.Add((si, d, _s[advSlot] - _x0[si], id));
            }

            // 8. Equilibrium: every state must have a zero derivative.
            foreach ((int si, double[] _, double num, string obj) in _derivs)
                if (!(Math.Abs(num) <= EquilibriumTolerance * (1.0 + Math.Abs(_x0[si]))))
                    throw Refuse("not-at-equilibrium", "block " + obj,
                        $"the initial state is not an equilibrium at the operating inputs (residual {R(num)} in the state equation of '{_stateNames[si]}').");

            // 9. Matrices.
            int nO = _plan.OutputSlots.Length;
            var A = new double[nS, nS]; var B = new double[nS, nI];
            foreach ((int si, double[] row, double _, string _) in _derivs)
            {
                for (int j = 0; j < nS; j++) A[si, j] = row[j];
                for (int j = 0; j < nI; j++) B[si, j] = row[nS + j];
            }
            var C = new double[nO, nS]; var D = new double[nO, nI];
            var yOp = new double[nO];
            for (int o = 0; o < nO; o++)
            {
                int slot = _plan.OutputSlots[o];
                double[] row = Form(lin, slot, w);
                for (int j = 0; j < nS; j++) C[o, j] = row[j];
                for (int j = 0; j < nI; j++) D[o, j] = row[nS + j];
                yOp[o] = _s[slot];
            }

            // 10. The model's own interpreter must hold the same outputs one step after this equilibrium.
            ModelInstance chk = _model.NewInstance();
            chk.Initialize(_init);
            chk.Step(u0, 1e-3);
            for (int o = 0; o < nO; o++)
                if (!(Math.Abs(chk.Output(o) - yOp[o]) <= 1e-9 * (1.0 + Math.Abs(yOp[o]))))
                    throw Refuse("not-at-equilibrium", "output " + _doc.Outputs[o].Name,
                        $"the interpreter holds {R(chk.Output(o))} at the equilibrium inputs but the operating output is {R(yOp[o])}.");

            return new LinearBlockModel
            {
                ModelId = Id, Role = _doc.Role, Hash = _doc.Sha256,
                StateNames = _stateNames.ToArray(), A = A, B = B, C = C, D = D,
                X0 = _x0.ToArray(), U0 = u0, Y0 = yOp, Notes = _notes.ToArray(),
            };
        }

        private readonly List<(int State, int AdvSlot, double Tw, string Id)> _pendingWashouts = new();

        private enum BlockKind { OutputState, Auxiliary, Frozen, Constant, PassThrough, Algebraic }

        private int AddState(string name, double x0)
        {
            _stateNames.Add(name);
            _x0.Add(x0);
            return _stateNames.Count - 1;
        }

        private static double[] Form(double[][] lin, int slot, int w)
        {
            if (slot < 0) return new double[w];
            return lin[slot] ?? new double[w];
        }

        private double ConstantOutput(OpSpec op, double[] pv)
            => op.Spec.Type switch
            {
                "hold" => pv[0],
                "lag" => Math.Clamp(pv[0], P(op, "min", double.NegativeInfinity), P(op, "max", double.PositiveInfinity)),
                "integrator" => Math.Clamp(pv[0], P(op, "min", double.NegativeInfinity), P(op, "max", double.PositiveInfinity)),
                _ => pv[0],
            };

        private double[] DefaultStepInputs(ModelInstance probe)
        {
            switch (_doc.Role)
            {
                case "exciter":
                    if (double.IsNaN(probe.EquilibriumError))
                        throw Refuse("no-equilibrium-error", "initialisation", "an exciter model must name its steady regulator error.");
                    return new[] { probe.EquilibriumError, 0.0 };
                case "governor": return new[] { 0.0 };
                case "stabiliser": return new[] { _init[0] };
                default:
                    throw Refuse("bad-inputs", "input ports", $"a '{_doc.Role}' model needs explicit operating input values.");
            }
        }

        // ---- combinational blocks: value and partial derivatives wrt each input -----------------------------------------------

        private double Comb(OpSpec op, double[] d)
        {
            if (d.Length < op.In.Length) Array.Resize(ref d, op.In.Length);
            double[] sIn = op.In.Select(i => _s[i]).ToArray();
            string id = op.Block.Id;
            switch (op.Spec.Type)
            {
                case "gain": { double k = P(op, "k"); d[0] = k; return k * sIn[0]; }
                case "div": { double dv = P(op, "d"); d[0] = 1.0 / dv; return sIn[0] / dv; }
                case "sum":
                {
                    string sg = op.Block.Signs!;
                    double acc = sg[0] == '+' ? sIn[0] : -sIn[0];
                    d[0] = sg[0] == '+' ? 1.0 : -1.0;
                    for (int i = 1; i < sIn.Length; i++)
                    {
                        acc = sg[i] == '+' ? acc + sIn[i] : acc - sIn[i];
                        d[i] = sg[i] == '+' ? 1.0 : -1.0;
                    }
                    return acc;
                }
                case "product":
                {
                    double acc = sIn[0];
                    for (int i = 1; i < sIn.Length; i++) acc *= sIn[i];
                    for (int i = 0; i < sIn.Length; i++)
                    {
                        double pr = 1.0;
                        for (int j = 0; j < sIn.Length; j++) if (j != i) pr *= sIn[j];
                        d[i] = pr;
                    }
                    return acc;
                }
                case "limit":
                {
                    double mn = P(op, "min"), mx = P(op, "max"), u = sIn[0];
                    if (u == mn || u == mx) _notes.Add($"block {id}: input {R(u)} is exactly at a limit; the limit is taken as active (gain 0).");
                    d[0] = u > mn && u < mx ? 1.0 : 0.0;
                    return Math.Clamp(u, mn, mx);
                }
                case "deadband":
                {
                    double lo = P(op, "lower"), hi = P(op, "upper"), u = sIn[0];
                    if (u == lo || u == hi) _notes.Add($"block {id}: input {R(u)} is exactly at a dead-band edge; the gain is taken as 0 (inside).");
                    d[0] = u > hi || u < lo ? 1.0 : 0.0;
                    return UserBlocks.DeadBand(u, lo, hi);
                }
                case "table1d":
                {
                    double[] x = op.Block.Table!.X.ToArray(), y = op.Block.Table.Y.ToArray();
                    double u = sIn[0];
                    int n = x.Length;
                    d[0] = 0.0;
                    if (u > x[0] && u < x[n - 1])
                    {
                        int k = 1;
                        while (k < n - 1 && !(u < x[k])) k++;
                        d[0] = (y[k] - y[k - 1]) / (x[k] - x[k - 1]);
                        if (u == x[k - 1]) _notes.Add($"block {id}: input {R(u)} is on a table breakpoint; the slope of the segment the interpreter uses is taken.");
                    }
                    return UserBlocks.Table(u, x, y);
                }
                case "expsat":
                {
                    double a = P(op, "a"), b = P(op, "b");
                    double v = UserBlocks.ExpSat(sIn[0], a, b);
                    d[0] = b * v;
                    return v;
                }
                case "min":
                case "max":
                {
                    bool isMin = op.Spec.Type == "min";
                    double acc = sIn[0]; int pick = 0;
                    for (int i = 1; i < sIn.Length; i++)
                    {
                        double nxt = isMin ? Math.Min(acc, sIn[i]) : Math.Max(acc, sIn[i]);
                        if (sIn[i] == acc) _notes.Add($"block {id}: inputs {pick} and {i} tie at {R(acc)}; the first is taken as active.");
                        else if (nxt != acc) pick = i;
                        acc = nxt;
                    }
                    for (int i = 0; i < sIn.Length; i++) d[i] = i == pick ? 1.0 : 0.0;
                    return acc;
                }
                case "switch":
                {
                    double thr = P(op, "threshold");
                    bool a = sIn[0] >= thr;
                    if (sIn[0] == thr) _notes.Add($"block {id}: the selector is exactly at its threshold; the 'a' branch is taken.");
                    d[0] = 0.0; d[1] = a ? 1.0 : 0.0; d[2] = a ? 0.0 : 1.0;
                    return a ? sIn[1] : sIn[2];
                }
                case "const":
                    return P(op, "value");
                default:
                    throw Refuse("unknown-block", "block " + id, $"'{op.Spec.Type}' is not a combinational block.");
            }
        }
    }
}
