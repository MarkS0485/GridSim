using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Sensitivities;

/// <summary>
/// The linearised AC power-flow equations at a SOLVED operating point (R4-03): the polar Newton Jacobian J of the
/// mismatch F = S_calc(V, theta) + S_load(V) - S_spec, restricted to the unknowns the solve ended with (theta at every
/// non-slack bus, |V| at every PQ bus), factored once, and used for exact first-order sensitivities:
/// <c>J dx = dS_spec</c> for a change of specified injection, <c>dx = -J^-1 dF/da</c> for a parameter a (a tap ratio, a
/// voltage set point). One adjoint solve (<c>J^T y = e</c>) gives the effect of EVERY candidate control on one target
/// voltage.
///
/// <para>Method. The derivatives are the analytic ones of the complex power S_i = V_i conj(sum_j Y_ij V_j):
/// <c>dS/dtheta_j = j V_i conj(delta_ij I_i - Y_ij V_j)</c> and
/// <c>dS/d|V|_j = V_i conj(Y_ij V_j/|V_j|) + delta_ij conj(I_i) V_i/|V_i|</c>, the same pi-model admittance cells the solver
/// builds (<see cref="YBus.BuildSparse"/>), plus the ZIP-load voltage slope the solver adds. The bus types are the ones the
/// solve ENDED with (<see cref="PowerFlowResult.SolvedBusTypes"/>), so a unit pinned at a reactive limit is a PQ bus and
/// its bus voltage responds; a unit still regulating holds its voltage and cannot respond.</para>
///
/// <para>Scope (refused, never approximated): a model with on-load tap changers, DC links, FACTS devices, SVCs, induction
/// machines or transformer flow controls. Their outer loops move controls the Jacobian here does not contain, so the
/// sensitivities would not be those of the solved state. Units: angles in radians, voltages in pu, power in MW/MVAr
/// (the system base is divided out).</para>
/// </summary>
public sealed class AcSensitivityEngine
{
    private readonly GridModel _model;
    private readonly int _n;
    private readonly double _base;
    private readonly int[] _theta;    // bus -> row/col of its angle unknown, or -1 (slack)
    private readonly int[] _volt;     // bus -> row/col of its |V| unknown, or -1 (slack, PV)
    private readonly int _nu;         // number of unknowns
    private readonly Complex[] _v;
    private readonly double[] _vm;
    private readonly Dictionary<int, int> _index;
    private readonly SparseLuFactorization _lu;
    private readonly SparseLuFactorization _luT;
    private readonly IReadOnlyDictionary<(int Row, int Col), Complex> _y;
    private readonly Complex[] _i;
    private readonly Dictionary<int, List<(int Row, Complex Y)>> _byCol = new();

    /// <summary>The bus type each bus ended the solve with (model order).</summary>
    public IReadOnlyList<BusType> Types { get; }

    /// <summary>Bus ids in model order (the order of every returned array).</summary>
    public IReadOnlyList<int> BusIds { get; }

    private AcSensitivityEngine(GridModel model, PowerFlowResult solved)
    {
        _model = model;
        _n = model.BusCount;
        _base = model.BaseMva;
        BusIds = model.Buses.Select(b => b.Id).ToArray();
        _index = new Dictionary<int, int>(_n);
        for (int i = 0; i < _n; i++) _index[model.Buses[i].Id] = i;

        var types = new BusType[_n];
        for (int i = 0; i < _n; i++)
            types[i] = solved.SolvedBusTypes is { } st ? st[i] : (model.Buses[i].Type == BusType.Pv && !model.Generators.Any(g => g.Bus == model.Buses[i].Id) ? BusType.Pq : model.Buses[i].Type);
        Types = types;

        _theta = new int[_n];
        _volt = new int[_n];
        int u = 0;
        for (int i = 0; i < _n; i++) _theta[i] = types[i] == BusType.Slack ? -1 : u++;
        int nT = u;
        for (int i = 0; i < _n; i++) _volt[i] = types[i] == BusType.Pq ? u++ : -1;
        _nu = u;
        if (nT == _n) throw new InvalidOperationException("AC sensitivities need a slack bus: none ended the solve as the slack.");

        _vm = new double[_n];
        _v = new Complex[_n];
        for (int i = 0; i < _n; i++)
        {
            _vm[i] = solved.Vm[i];
            _v[i] = DetMath.FromPolar(solved.Vm[i], solved.VaDeg[i] * Math.PI / 180.0);
        }

        SparseYBus yb = YBus.BuildSparse(model);
        _y = yb.Cells;
        _i = new Complex[_n];
        foreach (((int r, int c), Complex yv) in _y)
        {
            _i[r] += yv * _v[c];
            if (!_byCol.TryGetValue(c, out List<(int, Complex)>? list)) _byCol[c] = list = new List<(int, Complex)>();
            list.Add((r, yv));
        }

        // The Jacobian, entry by entry (sparse), and its transpose for the adjoint solves.
        var entries = new List<(int R, int C, double V)>();
        void Add(int i, int j, Complex dSdTheta, Complex dSdV)
        {
            int rp = _theta[i], rq = _volt[i];
            if (_theta[j] >= 0)
            {
                if (rp >= 0) entries.Add((rp, _theta[j], dSdTheta.Real));
                if (rq >= 0) entries.Add((rq, _theta[j], dSdTheta.Imaginary));
            }
            if (_volt[j] >= 0)
            {
                if (rp >= 0) entries.Add((rp, _volt[j], dSdV.Real));
                if (rq >= 0) entries.Add((rq, _volt[j], dSdV.Imaginary));
            }
        }
        foreach (((int i, int j), Complex yv) in _y)
        {
            if (i == j) continue;
            Add(i, j, Complex.ImaginaryOne * _v[i] * Complex.Conjugate(-yv * _v[j]),
                _v[i] * Complex.Conjugate(yv * (_v[j] / _vm[j])));
        }
        for (int i = 0; i < _n; i++)
        {
            _y.TryGetValue((i, i), out Complex yii);
            Add(i, i, Complex.ImaginaryOne * _v[i] * Complex.Conjugate(_i[i] - yii * _v[i]),
                _v[i] * Complex.Conjugate(yii * (_v[i] / _vm[i])) + Complex.Conjugate(_i[i]) * (_v[i] / _vm[i]));
            // The ZIP load slope, as the solver adds it (diagonal, constant-power buses add nothing).
            Bus b = model.Buses[i];
            if (b.Zip is { VariesWithVoltage: true } zip)
            {
                if (_theta[i] >= 0 && _volt[i] >= 0) entries.Add((_theta[i], _volt[i], b.Pd * zip.DPMultiplier(_vm[i]) / _base));
                if (_volt[i] >= 0) entries.Add((_volt[i], _volt[i], b.Qd * zip.DQMultiplier(_vm[i]) / _base));
            }
        }
        var bf = new CsrMatrix.Builder(_nu);
        var bt = new CsrMatrix.Builder(_nu);
        foreach ((int r, int c, double val) in entries) { bf.Add(r, c, val); bt.Add(c, r, val); }
        _lu = SparseLu.Factor(bf.Build())
            ?? throw new InvalidOperationException("The AC Jacobian at the solved state is singular (an island without a slack, or the nose of the PV curve).");
        _luT = SparseLu.Factor(bt.Build()) ?? throw new InvalidOperationException("The transposed AC Jacobian is singular.");
    }

    /// <summary>Build the engine for <paramref name="model"/> at the converged state <paramref name="solved"/>. Throws
    /// <see cref="InvalidOperationException"/> for a non-converged state, a mismatching state, an out-of-scope model or a
    /// singular Jacobian.</summary>
    public static AcSensitivityEngine Build(GridModel model, PowerFlowResult solved)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(solved);
        if (!solved.Converged) throw new InvalidOperationException("AC sensitivities need a converged power flow; the base case did not converge.");
        if (solved.Vm.Length != model.BusCount || solved.VaDeg.Length != model.BusCount)
            throw new ArgumentException("the solved state does not belong to this model (bus count differs).", nameof(solved));
        for (int i = 0; i < model.BusCount; i++)
            if (solved.BusIds[i] != model.Buses[i].Id)
                throw new ArgumentException("the solved state's bus order differs from the model's.", nameof(solved));
        string? refusal = model.Oltcs.Count > 0 ? "on-load tap changers"
            : model.LccLinks.Count + model.VscLinks.Count + model.MtdcLinks.Count > 0 ? "DC links"
            : model.FactsDevices.Count > 0 ? "FACTS devices"
            : model.StaticVarCompensators.Count > 0 ? "static var compensators"
            : model.InductionMachines.Count > 0 ? "induction machines"
            : model.TransformerFlowControls.Count > 0 ? "transformer flow controls"
            : null;
        if (refusal is not null)
            throw new InvalidOperationException($"OUT OF SCOPE: the case has {refusal}; their outer control loops are not in the Jacobian, so the sensitivities would not be those of the solved state.");
        return new AcSensitivityEngine(model, solved);
    }

    private int IndexOf(int busId) => _index.TryGetValue(busId, out int i) ? i : throw new ArgumentException($"bus {busId} is not in the model.", nameof(busId));

    private (double[] DTheta, double[] DVm) Expand(double[] x)
    {
        var dth = new double[_n];
        var dv = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            if (_theta[i] >= 0) dth[i] = x[_theta[i]];
            if (_volt[i] >= 0) dv[i] = x[_volt[i]];
        }
        return (dth, dv);
    }

    /// <summary>The response of every bus angle (rad) and voltage magnitude (pu) to a change of specified injection at
    /// <paramref name="busId"/> of <paramref name="dPMw"/> and <paramref name="dQMvar"/> (generation positive). A slack bus
    /// absorbs it; a regulating (PV) bus holds its voltage, so its reactive change has no row and moves nothing.</summary>
    public (double[] DTheta, double[] DVm) InjectionResponse(int busId, double dPMw, double dQMvar)
    {
        int b = IndexOf(busId);
        var rhs = new double[_nu];
        if (_theta[b] >= 0) rhs[_theta[b]] = dPMw / _base;
        if (_volt[b] >= 0) rhs[_volt[b]] = dQMvar / _base;
        return Expand(_lu.Solve(rhs));
    }

    // dS_calc/da rows for the tap ratio of branch k (only the two end rows change).
    private double[] TapRhs(int branch)
    {
        Branch br = _model.Branches[branch];
        if (!br.InService) throw new ArgumentException($"branch {branch} is out of service.", nameof(branch));
        int f = IndexOf(br.From), t = IndexOf(br.To);
        double a = br.TapRatio == 0 ? 1.0 : br.TapRatio;
        BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
        Complex dyff = -2.0 * s.Yff / a, dyft = -s.Yft / a, dytf = -s.Ytf / a;
        var rhs = new double[_nu];
        void Put(int bus, Complex dS)
        {
            if (_theta[bus] >= 0) rhs[_theta[bus]] += dS.Real;
            if (_volt[bus] >= 0) rhs[_volt[bus]] += dS.Imaginary;
        }
        Put(f, _v[f] * Complex.Conjugate(dyff * _v[f] + dyft * _v[t]));
        Put(t, _v[t] * Complex.Conjugate(dytf * _v[f]));
        return rhs;
    }

    /// <summary>The response of every bus angle (rad) and voltage (pu) to a unit increase (1.0 = 100 per cent) of the tap
    /// ratio of in-service branch <paramref name="branch"/> (the off-nominal ratio on the From side, the solver's convention).</summary>
    public (double[] DTheta, double[] DVm) TapResponse(int branch)
    {
        double[] rhs = TapRhs(branch);
        for (int k = 0; k < rhs.Length; k++) rhs[k] = -rhs[k];
        return Expand(_lu.Solve(rhs));
    }

    // dS_calc/dV_j rows for the voltage set point at a regulated bus j.
    private double[] SetpointRhs(int j)
    {
        var rhs = new double[_nu];
        void Put(int bus, Complex dS)
        {
            if (_theta[bus] >= 0) rhs[_theta[bus]] += dS.Real;
            if (_volt[bus] >= 0) rhs[_volt[bus]] += dS.Imaginary;
        }
        Complex vn = _v[j] / _vm[j];
        if (_byCol.TryGetValue(j, out List<(int Row, Complex Y)>? col))
            foreach ((int r, Complex yv) in col)
                if (r != j) Put(r, _v[r] * Complex.Conjugate(yv * vn));
        _y.TryGetValue((j, j), out Complex yjj);
        Put(j, _v[j] * Complex.Conjugate(yjj * vn) + Complex.Conjugate(_i[j]) * vn);
        Bus b = _model.Buses[j];
        if (b.Zip is { VariesWithVoltage: true } zip && _theta[j] >= 0) rhs[_theta[j]] += b.Pd * zip.DPMultiplier(_vm[j]) / _base;
        return rhs;
    }

    /// <summary>The response to a unit change of the voltage set point of the regulated (PV) bus <paramref name="busId"/>.</summary>
    public (double[] DTheta, double[] DVm) SetpointResponse(int busId)
    {
        int j = IndexOf(busId);
        if (Types[j] != BusType.Pv) throw new ArgumentException($"bus {busId} is not a regulating (PV) bus at the solved state.", nameof(busId));
        double[] rhs = SetpointRhs(j);
        for (int k = 0; k < rhs.Length; k++) rhs[k] = -rhs[k];
        return Expand(_lu.Solve(rhs));
    }

    /// <summary>The adjoint vector y of the voltage magnitude of <paramref name="targetBusId"/> (<c>J^T y = e</c>): the
    /// effect on that voltage of any control is then a dot product with the control's right-hand side.</summary>
    public double[] Adjoint(int targetBusId)
    {
        int j = IndexOf(targetBusId);
        if (_volt[j] < 0) throw new ArgumentException($"bus {targetBusId} holds its voltage at the solved state (slack or regulating PV bus); it has no voltage sensitivity.", nameof(targetBusId));
        var e = new double[_nu];
        e[_volt[j]] = 1.0;
        return _luT.Solve(e);
    }

    /// <summary>dV_target/dQ_bus (pu per MVAr) from an <see cref="Adjoint"/> vector; 0 where the bus has no reactive row.</summary>
    public double EffectOfReactiveInjection(double[] adjoint, int busId)
    {
        int b = IndexOf(busId);
        return _volt[b] >= 0 ? adjoint[_volt[b]] / _base : 0.0;
    }

    /// <summary>dV_target/dtap (pu per unit ratio) of branch <paramref name="branch"/> from an <see cref="Adjoint"/> vector.</summary>
    public double EffectOfTap(double[] adjoint, int branch)
    {
        double[] rhs = TapRhs(branch);
        double s = 0.0;
        for (int k = 0; k < rhs.Length; k++) s -= adjoint[k] * rhs[k];
        return s;
    }

    /// <summary>dV_target/dV_set (pu per pu) of the regulated bus <paramref name="busId"/> from an <see cref="Adjoint"/> vector.</summary>
    public double EffectOfSetpoint(double[] adjoint, int busId)
    {
        int j = IndexOf(busId);
        double[] rhs = SetpointRhs(j);
        double s = 0.0;
        for (int k = 0; k < rhs.Length; k++) s -= adjoint[k] * rhs[k];
        return s;
    }
}
