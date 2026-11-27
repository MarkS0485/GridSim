using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP16 (RC5): the DC network of an MTDC1 grid in per unit (system MVA base, the grid's DC voltage base). The state of
/// each DC bus is W = Vdc^2. A bus with stored energy (H above 0) follows
/// <code>
///   H_i dW_i/dt = P_i - P_dc,i(V),   P_dc,i(V) = V_i (sum_j y_ij (V_i - V_j) + g_i V_i)
/// </code>
/// P_dc,i is the power bus i sends into its lines and to ground, with H_i = C_i Vbase^2 / (2 Sbase) (s), y_ij the
/// conductance of the lines between buses i and j and g_i the bus's ground conductance (pu), and P_i the power the
/// converters on the bus send into it. The line flows are formed from the voltage differences, not as sums of
/// conductance-times-voltage terms: on a low-resistance line those terms are large and cancel, and their rounding would
/// swamp the flows. A bus held by a DC voltage converter keeps its voltage (an ideal controller).
/// Each step is backward Euler, solved by Newton's method on W of the free buses:
/// <code>
///   r_i(W) = dt (P_i - P_dc,i(V)) - H_i (W_i - W_i^n) = 0,   V = sqrt(W)
/// </code>
/// Backward Euler is used, not forward Euler, because a resistive DC network is stiff: a bus's time constant is about
/// 2 H R, 2e-4 s for H = 0.01 s and R = 0.01 pu, below any RMS step, and forward Euler is unstable for dt above twice it.
/// Nor trapezoidal: it is A-stable but not L-stable, so a mode far faster than the step rings instead of decaying (its
/// factor (1 - x/2)/(1 + x/2) at x = dt/tau = 25 is -0.85, backward Euler's 1/(1 + x) is 0.038). Backward Euler is first
/// order, which matches the held-power (zero-order hold) coupling every RMS device uses, so a second-order scheme would
/// not raise the order of the whole. With P constant and no lines the step is exact (W rises by dt P / H, the linear ramp
/// of a constant power), and every scheme has the same steady state P_i = P_dc,i.
/// The converter powers are held over the step, as every RMS device's are. Fixed order everywhere: no unordered
/// iteration.
/// </summary>
internal sealed class MtdcDcNetwork
{
    private readonly int _n;
    private readonly double[] _h;
    private readonly double[,] _y;      // line conductances between buses (off the diagonal; the diagonal is not read)
    private readonly double[] _gnd;
    private readonly bool[] _held;
    private readonly int[] _free;
    private readonly double[] _w;
    private readonly string _who;

    /// <summary>Most Newton iterations per step before the step is refused.</summary>
    internal const int MaxIterations = 50;

    /// <param name="h">H of each bus (s); a bus that is not held needs H above 0.</param>
    /// <param name="g">The conductance matrix of the lines (pu, symmetric); its off-diagonal entries are read, as -y_ij.</param>
    /// <param name="held">True for a bus a DC voltage converter holds.</param>
    /// <param name="v0">The initial voltages (pu).</param>
    /// <param name="who">The name for messages.</param>
    /// <param name="ground">Each bus's ground conductance (pu); null for none.</param>
    internal MtdcDcNetwork(double[] h, double[,] g, bool[] held, double[] v0, string who, double[]? ground = null)
    {
        _n = h.Length;
        _h = (double[])h.Clone();
        _y = new double[_n, _n];
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
                if (i != j) _y[i, j] = -g[i, j];
        _gnd = ground is null ? new double[_n] : (double[])ground.Clone();
        _held = (bool[])held.Clone();
        _who = who;
        _w = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            if (!(v0[i] > 0) || !double.IsFinite(v0[i])) throw new ArgumentException($"{who}: DC bus {i + 1} starts at {ControlBlocks.Num(v0[i])} pu.");
            if (!held[i] && !(h[i] > 0)) throw new ArgumentException($"{who}: DC bus {i + 1} has no capacitance and no converter holding its voltage, so its voltage is not defined; give it a capacitance.");
            _w[i] = v0[i] * v0[i];
        }
        _free = Enumerable.Range(0, _n).Where(i => !held[i]).ToArray();
        V0 = (double[])v0.Clone();
        // A held bus keeps its initial voltage exactly (V0, not sqrt(V0^2)).
        _v = (double[])v0.Clone();
    }

    private readonly double[] _v;

    internal int Count => _n;

    /// <summary>The initial voltages (pu).</summary>
    internal double[] V0 { get; }

    /// <summary>The voltage of bus <paramref name="i"/> (pu).</summary>
    internal double V(int i) => _v[i];

    /// <summary>The state W of bus <paramref name="i"/> (pu squared).</summary>
    internal double W(int i) => _held[i] ? _v[i] * _v[i] : _w[i];

    /// <summary>P_dc,i at the present voltages: the power bus <paramref name="i"/> sends into the lines and to ground (pu).</summary>
    internal double PowerOut(int i) => PowerOut(i, _v);

    private double PowerOut(int i, double[] v) => v[i] * Current(i, v);

    /// <summary>The current bus <paramref name="i"/> sends into its lines and to ground (pu), from the voltage differences.</summary>
    private double Current(int i, double[] v)
    {
        double s = 0;
        for (int j = 0; j < _n; j++) if (j != i && _y[i, j] != 0) s += _y[i, j] * (v[i] - v[j]);
        return _gnd[i] == 0 ? s : s + _gnd[i] * v[i];
    }

    /// <summary>Advance one step of <paramref name="dt"/> with the converter powers <paramref name="p"/> (pu, into each bus)
    /// held; the held buses' entries are not read.</summary>
    internal void Step(double[] p, double dt)
    {
        int m = _free.Length;
        if (m == 0) return;
        var wn = new double[m];
        for (int r = 0; r < m; r++) wn[r] = _w[_free[r]];
        var v = (double[])_v.Clone();
        var a = new double[m, m + 1];
        double last = 0;
        for (int it = 0; it < MaxIterations; it++)
        {
            for (int r = 0; r < m; r++)
            {
                int i = _free[r];
                double gv = Current(i, v), ySum = _gnd[i];
                for (int j = 0; j < _n; j++) if (j != i) ySum += _y[i, j];
                // Residual and the Jacobian row d r_i / d W_k = -H_i delta_ik - dt dP_dc,i/dV_k / (2 V_k).
                a[r, m] = -(dt * (p[i] - v[i] * gv) - _h[i] * (_w[i] - wn[r]));
                for (int c = 0; c < m; c++)
                {
                    int k = _free[c];
                    double dPdV = k == i ? gv + v[i] * ySum : -v[i] * _y[i, k];
                    a[r, c] = (k == i ? -_h[i] : 0.0) - (dPdV == 0 ? 0.0 : dt * dPdV / (2 * v[k]));
                }
            }
            double[] dw = Solve(a, m);
            bool done = true;
            last = 0;
            for (int r = 0; r < m; r++)
            {
                int i = _free[r];
                double wNew = _w[i] + dw[r];
                if (!(wNew > 0) || !double.IsFinite(wNew))
                    throw new InvalidOperationException($"{_who}: the DC voltage at DC bus {i + 1} collapses (the DC grid cannot carry the converters' powers); MTDC1 does not model DC faults or a collapsed DC grid.");
                if (!(Math.Abs(dw[r]) <= 1e-14 * (1 + Math.Abs(wNew)))) done = false;
                last = Math.Max(last, Math.Abs(dw[r]));
                _w[i] = wNew;
                v[i] = Math.Sqrt(wNew);
            }
            if (done)
            {
                Array.Copy(v, _v, _n);
                return;
            }
        }
        throw new InvalidOperationException($"{_who}: the DC network step does not converge in {MaxIterations} Newton iterations (last correction {ControlBlocks.Num(last)} in W).");
    }

    /// <summary>Gaussian elimination with partial pivoting (ties to the lower row), fixed order.</summary>
    private double[] Solve(double[,] a, int m)
    {
        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (!(Math.Abs(a[piv, col]) > 0)) throw new InvalidOperationException($"{_who}: the DC network step is singular.");
            if (piv != col) for (int c = col; c <= m; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
            for (int r = col + 1; r < m; r++)
            {
                double f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (int c = col; c <= m; c++) a[r, c] -= f * a[col, c];
            }
        }
        var x = new double[m];
        for (int r = m - 1; r >= 0; r--)
        {
            double s = a[r, m];
            for (int c = r + 1; c < m; c++) s -= a[r, c] * x[c];
            x[r] = s / a[r, r];
        }
        return x;
    }
}

/// <summary>
/// WP16 (RC5): the initialised MTDC1 (<see cref="MtdcGridDynamicsData"/>) of one multi-terminal DC link on its power-flow
/// state. Every converter's terminal unit shares it; each run's grid state is made once and found by every terminal
/// through the run's converter-node list.
/// </summary>
/// <remarks>
/// The power flow (<see cref="MtdcLinks"/>) solves the link as PSS/E does, with line-commutated converters; the RMS model
/// takes its DC voltages and converter powers and treats every converter as a voltage-source converter (VSC1's AC side),
/// lossless as the power flow's converters are, with its reactive power held at the power-flow value or under AC voltage
/// control. The line-commutated angle and tap equations are not part of the RMS model.
/// </remarks>
internal sealed class MtdcGridPlan
{
    private readonly ConditionalWeakTable<IReadOnlyList<int>, MtdcGridState> _runs = new();

    /// <summary>The bound on each DC bus's balance at the start, relative to the sum of the magnitudes of the powers it
    /// balances. The power flow stops when its DC voltages move by at most 1e-12 (1 + V) kV in a pass; a power order is
    /// turned into a current with the voltage of the pass before, so a bus's balance can be off by |P| times that relative
    /// change, about 1e-12 |P| for a few-hundred-kV grid, plus a few roundings of each term; 1e-9 leaves a factor of
    /// several hundred.</summary>
    internal const double StartBalanceTolerance = 1e-9;

    /// <param name="link">The link.</param>
    /// <param name="pf">Its power-flow solution.</param>
    /// <param name="data">The MTDC1 data.</param>
    /// <param name="baseMva">The system base (MVA).</param>
    /// <param name="v0">The power-flow AC voltage of each converter, in converter order.</param>
    /// <param name="frequencyHz">The AC frequency, for an MMC's arm reactance.</param>
    /// <param name="acBaseKv">The base voltage of each converter's AC bus (kV), for an MMC's arm reactance; null takes
    /// each converter's transformer base.</param>
    internal MtdcGridPlan(MtdcLink link, MtdcLinkResult pf, MtdcGridDynamicsData data, double baseMva, IReadOnlyList<Complex> v0,
        double frequencyHz = 50, IReadOnlyList<double>? acBaseKv = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(pf);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Controls);
        ArgumentNullException.ThrowIfNull(data.BusCapacitances);
        string who = $"MTDC1 '{link.Name}'";
        Who = who;
        Link = link; BaseMva = baseMva;
        if (link.Mode == LccMode.Blocked) throw new ArgumentException($"{who}: the link is blocked in the power flow.");
        int nc = link.Converters.Count, nb = link.DcBuses.Count;
        if (pf.Converters.Count != nc || pf.DcBusKv.Count != nb) throw new ArgumentException($"{who}: the power-flow result does not match the link.");
        if (v0.Count != nc) throw new ArgumentException($"{who}: {v0.Count} AC voltages for {nc} converters.");

        // Controls: exactly one per converter, by AC bus.
        Controls = new MtdcTerminalControl[nc];
        for (int c = 0; c < data.Controls.Count; c++)
        {
            MtdcTerminalControl tc = data.Controls[c] ?? throw new ArgumentException($"{who}: control {c} is null.");
            int k = -1;
            for (int j = 0; j < nc; j++) if (link.Converters[j].Bus == tc.ConverterBus) { if (k >= 0) throw new ArgumentException($"{who}: two converters are on AC bus {tc.ConverterBus}, so a control cannot name one by its bus."); k = j; }
            if (k < 0) throw new ArgumentException($"{who}: the link has no converter on AC bus {tc.ConverterBus}.");
            if (Controls[k] is not null) throw new ArgumentException($"{who}: the converter on AC bus {tc.ConverterBus} is given two controls.");
            tc.Validate($"{who} converter {tc.ConverterBus}");
            Controls[k] = tc;
        }
        for (int k = 0; k < nc; k++)
            if (Controls[k] is null) throw new ArgumentException($"{who}: the converter on AC bus {link.Converters[k].Bus} has no control; give every converter one.");

        // Bases and the DC bus of each converter.
        var busIx = new Dictionary<int, int>(nb);
        for (int b = 0; b < nb; b++) busIx[link.DcBuses[b].Number] = b;
        DcBusOf = new int[nc];
        for (int k = 0; k < nc; k++) DcBusOf[k] = busIx[link.Converters[k].DcBus];
        int vc = -1;
        for (int k = 0; k < nc; k++) if (link.Converters[k].Bus == link.VoltageConverterBus) vc = k;
        if (vc < 0) throw new ArgumentException($"{who}: the voltage converter bus {link.VoltageConverterBus} is not one of its converters.");
        DcBaseKv = data.DcBaseKv > 0 ? data.DcBaseKv : pf.DcBusKv[DcBusOf[vc]];
        if (!(DcBaseKv > 0) || !double.IsFinite(DcBaseKv)) throw new ArgumentException($"{who}: the DC voltage base must be above 0 kV.");
        double zBase = DcBaseKv * DcBaseKv / baseMva;   // ohms
        double hPerFarad = 0.5 * (DcBaseKv * 1e3) * (DcBaseKv * 1e3) / (baseMva * 1e6);   // H (s) per farad

        // DC network.
        var g = new double[nb, nb];
        foreach (MtdcDcLine l in link.Lines)
        {
            if (!(l.ResistanceOhms > 0)) throw new ArgumentException($"{who}: the DC line {l.FromDcBus}-{l.ToDcBus} has a resistance of {ControlBlocks.Num(l.ResistanceOhms)} ohm; MTDC1 needs it above 0.");
            int i = busIx[l.FromDcBus], j = busIx[l.ToDcBus];
            double y = zBase / l.ResistanceOhms;
            g[i, i] += y; g[j, j] += y; g[i, j] -= y; g[j, i] -= y;
        }
        var gnd = new double[nb];
        for (int b = 0; b < nb; b++) if (link.DcBuses[b].GroundOhms > 0) gnd[b] = zBase / link.DcBuses[b].GroundOhms;
        G = g; Ground = gnd;

        var h = new double[nb];
        var held = new bool[nb];
        MmcEnergy = new bool[nc];
        for (int k = 0; k < nc; k++)
        {
            MtdcTerminalControl tc = Controls[k];
            int b = DcBusOf[k];
            if (tc.Mode == MtdcControlMode.DcVoltage)
            {
                if (held[b]) throw new ArgumentException($"{who}: two converters hold the voltage of DC bus {link.DcBuses[b].Number}.");
                held[b] = true;
            }
            h[b] += tc.CapacitanceMicroFarads * 1e-6 * hPerFarad;
            if (tc.Mmc is { } mmc)
            {
                if (mmc.EnergyControl) MmcEnergy[k] = true;
                else h[b] += mmc.EquivalentCapacitanceFarads * hPerFarad;
            }
        }
        foreach (MtdcDcBusCapacitance bc in data.BusCapacitances)
        {
            if (bc is null || !busIx.TryGetValue(bc.DcBus, out int b)) throw new ArgumentException($"{who}: a bus capacitance names DC bus {bc?.DcBus}, which the link does not have.");
            if (!(bc.MicroFarads >= 0) || !double.IsFinite(bc.MicroFarads)) throw new ArgumentException($"{who}: the capacitance of DC bus {bc.DcBus} must be 0 or above.");
            h[b] += bc.MicroFarads * 1e-6 * hPerFarad;
        }
        for (int b = 0; b < nb; b++)
            if (!held[b] && !(h[b] > 0))
                throw new ArgumentException($"{who}: DC bus {link.DcBuses[b].Number} has no capacitance and no converter holding its voltage, so its voltage is not defined; give it a capacitance.");
        H = h; Held = held;

        // The power-flow state in per unit.
        Vdc0 = pf.DcBusKv.Select(x => x / DcBaseKv).ToArray();
        P0 = new double[nc];   // into the DC grid
        Q0 = new double[nc];   // injected into the AC bus
        V0 = v0.ToArray();
        ImaxSys = new double[nc];
        for (int k = 0; k < nc; k++)
        {
            MtdcConverterResult r = pf.Converters[k];
            P0[k] = r.DrawnMw / baseMva;
            Q0[k] = -r.AbsorbedMvar / baseMva;
            ImaxSys[k] = Controls[k].ImaxPu * Controls[k].MbaseMva / baseMva;
            double vm = V0[k].Magnitude;
            if (!(vm > DeviceCurrents.MinVoltagePu)) throw new ArgumentException($"{who}: the converter on AC bus {link.Converters[k].Bus} has no voltage in the power flow.");
            double i = Math.Sqrt(P0[k] * P0[k] + Q0[k] * Q0[k]) / vm;
            if (!(i <= ImaxSys[k] * (1 + 1e-12)))
                throw new ArgumentException($"{who}: the converter on AC bus {link.Converters[k].Bus} carries {ControlBlocks.Num(i / (Controls[k].MbaseMva / baseMva))} pu of its MBASE in the power flow, above Imax = {ControlBlocks.Num(Controls[k].ImaxPu)}.");
        }

        // The DC balance at the start: each bus's converters send what its lines and ground take.
        for (int b = 0; b < nb; b++)
        {
            double pin = 0, scale = 0, gv = gnd[b] * Vdc0[b];
            scale += Math.Abs(gv * Vdc0[b]);
            for (int k = 0; k < nc; k++) if (DcBusOf[k] == b) { pin += P0[k]; scale += Math.Abs(P0[k]); }
            for (int j = 0; j < nb; j++)
                if (j != b && g[b, j] != 0) { double f = -g[b, j] * (Vdc0[b] - Vdc0[j]); gv += f; scale += Math.Abs(f * Vdc0[b]); }
            double residual = pin - Vdc0[b] * gv;
            if (!(Math.Abs(residual) <= StartBalanceTolerance * (1 + scale)))
                throw new ArgumentException($"{who}: the power flow's DC bus {link.DcBuses[b].Number} is out of balance by {ControlBlocks.Num(residual * baseMva)} MW, above {ControlBlocks.Num(StartBalanceTolerance)} of the powers it balances; the start would not be an equilibrium.");
        }

        // MMC: the arm reactance (system pu) and the energy controller's stored energy at the start (pu s).
        Xarm = new double[nc];
        E0 = new double[nc];
        for (int k = 0; k < nc; k++)
        {
            if (Controls[k].Mmc is not { } mmc) continue;
            double kv = acBaseKv is not null ? acBaseKv[k] : link.Converters[k].BaseKv;
            if (mmc.ArmInductanceHenries > 0 && !(kv > 0))
                throw new ArgumentException($"{who}: the converter on AC bus {link.Converters[k].Bus} has no AC base voltage, so its arm reactance cannot be put in per unit.");
            // X = w L_arm / 2 (ohm) on the AC base impedance kV^2 / Sbase.
            Xarm[k] = mmc.ArmInductanceHenries > 0 ? 2 * Math.PI * frequencyHz * mmc.ArmInductanceHenries / 2 / (kv * kv / baseMva) : 0.0;
            E0[k] = mmc.EquivalentCapacitanceFarads * hPerFarad * Vdc0[DcBusOf[k]] * Vdc0[DcBusOf[k]];
        }

        // The droop loop is explicit: the order uses the start-of-step DC voltage and the converter's DC power follows one
        // step later (the held-power coupling of every RMS device). On a DC bus on its own, with dV = dt P / (2 H V0) per
        // step and the order -Kd dV through the exact zero-order hold a = exp(-dt / Tp), the step map of (dV, P) has the
        // characteristic polynomial z^2 - (1 + a) z + a + k (1 - a) with k = dt Kd / (2 H V0); by the Jury test it is
        // stable exactly when k < 1, whatever Tp (a = 0 when Tp = 0). Lines to other buses only widen that range (their
        // flows are implicit). So a step dt >= 2 H V0 / sum(Kd) at any free DC bus is refused when the run starts.
        DroopStepLimitSeconds = double.PositiveInfinity;
        for (int b = 0; b < nb; b++)
        {
            if (held[b]) continue;
            double kd = 0;
            for (int k = 0; k < nc; k++) if (DcBusOf[k] == b && Controls[k].Mode == MtdcControlMode.Droop) kd += Controls[k].DroopGain;
            if (!(kd > 0)) continue;
            double limit = 2 * h[b] * Vdc0[b] / kd;
            if (limit < DroopStepLimitSeconds) { DroopStepLimitSeconds = limit; DroopLimitDcBus = link.DcBuses[b].Number; }
        }
    }

    internal string Who { get; }
    internal MtdcLink Link { get; }
    internal double BaseMva { get; }
    internal double DcBaseKv { get; }
    internal MtdcTerminalControl[] Controls { get; }
    internal int[] DcBusOf { get; }
    internal double[,] G { get; }
    internal double[] Ground { get; }
    internal double[] H { get; }
    internal bool[] Held { get; }
    internal double[] Vdc0 { get; }
    internal double[] P0 { get; }
    internal double[] Q0 { get; }
    internal Complex[] V0 { get; }
    internal double[] ImaxSys { get; }
    internal bool[] MmcEnergy { get; }
    internal double[] Xarm { get; }
    internal double[] E0 { get; }

    /// <summary>The largest step the explicit droop loop allows, 2 H V0 / sum(Kd) at the tightest free DC bus (s), or
    /// infinity with no droop converter.</summary>
    internal double DroopStepLimitSeconds { get; }

    /// <summary>The DC bus that sets <see cref="DroopStepLimitSeconds"/> (0 with no droop converter).</summary>
    internal int DroopLimitDcBus { get; }

    internal MtdcGridState StateFor(IReadOnlyList<int> nodeBuses) => _runs.GetValue(nodeBuses, nb => new MtdcGridState(this, nb));
}

/// <summary>
/// WP16 (RC5): the run state of an MTDC1 grid. Per converter: the power order lag (Power and Droop), the reactive state
/// (as VSC1), the power it sends into the DC grid, and for an MMC with the energy controller its stored energy. Per DC
/// bus: the voltage (<see cref="MtdcDcNetwork"/>). Each step, from the start-of-step AC voltages:
/// <list type="number">
/// <item>each Power or Droop converter sends into the DC grid the power it actually exchanges at its AC bus (after its
/// current limit), or with the MMC energy controller the controller's DC power;</item>
/// <item>the DC network steps (backward Euler) with those powers held;</item>
/// <item>each DC voltage converter takes what balances its bus at the new voltages, which is its AC power for the next
/// step (its own current limit is not fed back to the DC side);</item>
/// <item>the lags advance by the exact zero-order hold: P_ord = P0 - Kd (Vdc - Vdc0) for droop at the start-of-step DC
/// voltage. That loop is explicit, so a step of 2 H V0 / Kd or more at a droop bus is refused
/// (<see cref="MtdcGridPlan.DroopStepLimitSeconds"/>).</item>
/// </list>
/// Within a step each converter is a phase-locked current source <c>(Id - j Iq) V / |V|</c>, as VSC1.
/// </summary>
internal sealed class MtdcGridState
{
    private readonly MtdcGridPlan _p;
    private readonly int[] _node;
    private readonly MtdcDcNetwork _dc;
    private readonly FirstOrderLag[] _pf;    // the DC power order (into the grid), Power and Droop
    private readonly FirstOrderLag[] _q;
    private readonly double[] _pDc;          // the power each converter sends into the DC grid this step
    private readonly double[] _pAc;          // the AC power each converter injects this step (before its current limit)
    private readonly double[] _e, _eInt;     // MMC energy controller: stored energy (pu s) and its integral
    private readonly FirstOrderLag?[] _eOut;
    private readonly double[] _eConv;        // MMC: the converter's internal voltage magnitude (pu), for the trace
    private readonly string[] _stateNames;
    private readonly List<double[]> _samples = new();

    internal MtdcGridState(MtdcGridPlan plan, IReadOnlyList<int>? nodeBuses)
    {
        _p = plan;
        int nc = plan.Controls.Length, nb = plan.Vdc0.Length;
        _node = Enumerable.Repeat(-1, nc).ToArray();
        if (nodeBuses is not null)
            for (int k = 0; k < nc; k++)
            {
                int bus = plan.Link.Converters[k].Bus;
                for (int i = 0; i < nodeBuses.Count; i++) if (nodeBuses[i] == bus) { _node[k] = i; break; }
            }
        _dc = new MtdcDcNetwork(plan.H, plan.G, plan.Held, plan.Vdc0, plan.Who, plan.Ground);
        _pf = new FirstOrderLag[nc];
        _q = new FirstOrderLag[nc];
        _pDc = (double[])plan.P0.Clone();
        _pAc = plan.P0.Select(x => -x).ToArray();
        _e = (double[])plan.E0.Clone();
        _eInt = new double[nc];
        _eOut = new FirstOrderLag?[nc];
        _eConv = new double[nc];
        var names = new List<string>();
        for (int b = 0; b < nb; b++) names.Add("vdc_pu_" + plan.Link.DcBuses[b].Number.ToString(CultureInfo.InvariantCulture));
        for (int k = 0; k < nc; k++)
        {
            MtdcTerminalControl c = plan.Controls[k];
            string bus = plan.Link.Converters[k].Bus.ToString(CultureInfo.InvariantCulture);
            _pf[k] = new FirstOrderLag(1.0, c.Tp);
            _pf[k].Initialize(plan.P0[k]);
            _q[k] = new FirstOrderLag(1.0, c.Tq);
            _q[k].Initialize(c.Kv > 0 ? plan.Q0[k] / plan.V0[k].Magnitude : plan.Q0[k]);
            names.Add("p_dc_pu_" + bus);
            names.Add("q_state_" + bus);
            if (c.Mmc is { } mmc)
            {
                names.Add("e_conv_pu_" + bus);
                _eConv[k] = InternalVoltage(k, plan.V0[k]);
                if (plan.MmcEnergy[k])
                {
                    _eOut[k] = new FirstOrderLag(1.0, mmc.Tw);
                    _eOut[k]!.Initialize(plan.P0[k]);
                    names.Add("e_arm_pus_" + bus);
                }
            }
        }
        _stateNames = names.ToArray();
    }

    internal MtdcGridPlan Plan => _p;

    internal MtdcDcNetwork Dc => _dc;

    /// <summary>The power converter <paramref name="k"/> sends into the DC grid this step (pu).</summary>
    internal double DcPower(int k) => _pDc[k];

    /// <summary>The MMC energy controller's stored energy of converter <paramref name="k"/> (pu s).</summary>
    internal double StoredEnergy(int k) => _e[k];

    /// <summary>The (Id, Iq) currents (system pu, Iq exporting positive) of converter <paramref name="k"/> at voltage
    /// magnitude <paramref name="vm"/>, the states held.</summary>
    internal (double Id, double Iq) Currents(int k, double vm)
    {
        double v = Math.Max(vm, DeviceCurrents.MinVoltagePu);
        double iq = _p.Controls[k].Kv > 0 ? _q[k].Output : _q[k].Output / v;
        return VscQuasiSteady.Limit(_pAc[k] / v, iq, _p.ImaxSys[k], _p.Controls[k].Priority);
    }

    internal Complex TrialCurrent(int k, Complex v)
    {
        double vm = v.Magnitude;
        if (!(vm > DeviceCurrents.MinVoltagePu)) return Complex.Zero;
        (double id, double iq) = Currents(k, vm);
        return new Complex(id, -iq) * (v / vm);
    }

    private double InternalVoltage(int k, Complex v) =>
        _p.Xarm[k] == 0 ? v.Magnitude : (v + new Complex(0, _p.Xarm[k]) * TrialCurrent(k, v)).Magnitude;

    internal void Record()
    {
        var s = new double[_stateNames.Length];
        int n = 0;
        for (int b = 0; b < _dc.Count; b++) s[n++] = _dc.V(b);
        for (int k = 0; k < _p.Controls.Length; k++)
        {
            s[n++] = _pDc[k];
            s[n++] = _q[k].Output;
            if (_p.Controls[k].Mmc is not null)
            {
                s[n++] = _eConv[k];
                if (_p.MmcEnergy[k]) s[n++] = _e[k];
            }
        }
        _samples.Add(s);
    }

    internal RmsDeviceTrace Trace => new(_p.Link.Name, "MTDC1", _stateNames, _samples);

    /// <summary>Advance the grid one step from the start-of-step AC voltages <paramref name="v"/> (one per converter, in the
    /// link's converter order).</summary>
    internal void Advance(double t, Complex[] v, double dt)
    {
        if (!(dt < _p.DroopStepLimitSeconds))
            throw new InvalidOperationException($"{_p.Who}: the step {ControlBlocks.Num(dt)} s is too long for the droop at DC bus "
                + $"{_p.DroopLimitDcBus.ToString(CultureInfo.InvariantCulture)}: the droop loop is explicit and is stable only for a step below "
                + $"2 H V0 / Kd = {ControlBlocks.Num(_p.DroopStepLimitSeconds)} s there. Use a shorter step, a smaller droop gain or more DC capacitance.");
        int nc = _p.Controls.Length, nb = _dc.Count;
        // 1. The power each Power or Droop converter sends into the DC grid: what it exchanges at its AC bus after the
        // current limit (its AC power itself while Id is not clipped, kept exact), or the MMC energy controller's DC power.
        var pAcActual = new double[nc];
        for (int k = 0; k < nc; k++)
        {
            double vm = v[k].Magnitude;
            if (!(vm > DeviceCurrents.MinVoltagePu)) { pAcActual[k] = 0.0; continue; }
            (double id, _) = Currents(k, vm);
            pAcActual[k] = id == _pAc[k] / Math.Max(vm, DeviceCurrents.MinVoltagePu) ? _pAc[k] : vm * id;
            if (_p.Controls[k].Mmc is not null) _eConv[k] = InternalVoltage(k, v[k]);
        }
        var pBus = new double[nb];
        for (int k = 0; k < nc; k++)
        {
            if (_p.Controls[k].Mode == MtdcControlMode.DcVoltage) continue;
            _pDc[k] = _p.MmcEnergy[k] ? _eOut[k]!.Output : -pAcActual[k];
            pBus[_p.DcBusOf[k]] += _pDc[k];
        }
        var vdcStart = new double[nb];
        for (int b = 0; b < nb; b++) vdcStart[b] = _dc.V(b);

        // 2. The DC network.
        _dc.Step(pBus, dt);

        // 3. Each DC voltage converter balances its bus at the new voltages.
        for (int k = 0; k < nc; k++)
        {
            if (_p.Controls[k].Mode != MtdcControlMode.DcVoltage) continue;
            int b = _p.DcBusOf[k];
            _pDc[k] = _dc.PowerOut(b) - pBus[b];
            _pAc[k] = -_pDc[k];
        }

        // 4. The MMC energy controllers (forward Euler on E and its integral, the output lag by the zero-order hold).
        for (int k = 0; k < nc; k++)
        {
            if (!_p.MmcEnergy[k]) continue;
            Mmc1Data mmc = _p.Controls[k].Mmc!;
            double pIn = -pAcActual[k];
            double err = _e[k] - _p.E0[k];
            double target = pIn + mmc.KpW * err + mmc.KiW * _eInt[k];
            _e[k] += dt * (pIn - _eOut[k]!.Output);
            _eInt[k] += dt * err;
            _eOut[k]!.Step(target, dt);
        }

        // 5. The power order and reactive lags.
        for (int k = 0; k < nc; k++)
        {
            MtdcTerminalControl c = _p.Controls[k];
            double vm = v[k].Magnitude;
            if (c.Kv > 0) _q[k].Step(_p.Q0[k] / _p.V0[k].Magnitude + c.Kv * (_p.V0[k].Magnitude - vm), dt);
            else _q[k].Step(_p.Q0[k], dt);
            if (c.Mode == MtdcControlMode.DcVoltage) continue;
            double p0 = OrderStepChecks.At(c.OrderSteps, t, dt, _p.P0[k] * _p.BaseMva) / _p.BaseMva;
            double order = c.Mode == MtdcControlMode.Droop ? p0 - c.DroopGain * (vdcStart[_p.DcBusOf[k]] - _p.Vdc0[_p.DcBusOf[k]]) : p0;
            _pAc[k] = -_pf[k].Step(order, dt);
        }
    }

    internal void AdvanceFromNodes(double t, Complex vFirst, Complex[] nodeVoltages, double dt)
    {
        int nc = _p.Controls.Length;
        var v = new Complex[nc];
        for (int k = 0; k < nc; k++)
            v[k] = k == 0 ? vFirst : _node[k] >= 0 && _node[k] < nodeVoltages.Length ? nodeVoltages[_node[k]] : _p.V0[k];
        Advance(t, v, dt);
    }
}

/// <summary>WP16: one converter of an MTDC1 grid as a kernel converter model. The first converter advances and records
/// the shared grid state.</summary>
internal sealed class MtdcTerminalModel : IRmsConverterModel, IRmsDeviceStateRecorder
{
    private readonly MtdcGridState _s;
    private readonly int _k;
    private double _ip, _iq;

    internal MtdcTerminalModel(MtdcGridState state, int k) { _s = state; _k = k; }

    public Complex TrialCurrent(Complex v) => _s.TrialCurrent(_k, v);

    public Complex Commit(Complex v)
    {
        Complex i = TrialCurrent(v);
        (_ip, _iq) = DeviceCurrents.Split(i, v);
        if (_k == 0) _s.Record();
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        if (_k == 0) _s.AdvanceFromNodes(t, v, nodeVoltages, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;

    public RmsDeviceTrace? Trace => _k == 0 ? _s.Trace : null;
}

/// <summary>WP16: the plan of one MTDC1 converter (the kernel unit's <see cref="RmsConverterUnit.Device"/>).</summary>
internal sealed class MtdcTerminalPlan(MtdcGridPlan grid, int k) : RmsDevicePlan
{
    internal override IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => new MtdcTerminalModel(grid.StateFor(nodeBuses), k);
}
