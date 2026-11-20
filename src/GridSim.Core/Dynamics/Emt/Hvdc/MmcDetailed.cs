namespace GridSim.Core.Dynamics.Emt.Hvdc;

/// <summary>
/// The sub-module-level arm stack: <c>N</c> half-bridge sub-modules, each a capacitor <c>C_sm</c> that is either inserted (its
/// voltage is in the arm voltage and the arm current flows through it) or bypassed. Per step the number to insert is
/// <c>k = round(n N)</c> (nearest-level modulation, midpoints away from zero), clamped to 0..N. Capacitor voltage balancing picks WHICH
/// sub-modules: for an arm current that is not negative (the capacitors charge) the <c>k</c> sub-modules with the LOWEST voltage are
/// inserted, for a negative current the <c>k</c> HIGHEST. The ranking is a total order on (voltage, index) with the sub-module index
/// as the tie-break, so the choice is the same on every machine and in every run order (a plain sort of equal voltages would not
/// be). The stack voltage is the sum of the inserted capacitor voltages at the start of the step, in rank order; each inserted
/// capacitor then integrates the step's arm current with backward Euler (<c>C dv = i dt</c>).
/// </summary>
public sealed class DetailedArmBank : IMmcSubmoduleBank
{
    private readonly int _n;
    private readonly double _c, _v0;
    private readonly double[] _vc;
    private readonly int[] _order;
    private int _inserted;

    /// <summary>Create the stack.</summary>
    public DetailedArmBank(int submodules, double submoduleCapF, double initialSubmoduleVolts)
    {
        _n = submodules; _c = submoduleCapF; _v0 = initialSubmoduleVolts;
        _vc = new double[submodules]; _order = new int[submodules];
        Reset();
    }

    /// <inheritdoc />
    public void Reset()
    {
        Array.Fill(_vc, _v0);
        for (int k = 0; k < _n; k++) _order[k] = k;
        _inserted = 0;
    }

    /// <summary>The sub-module voltages, by index.</summary>
    public IReadOnlyList<double> SubmoduleVoltages => _vc;

    /// <summary>How many sub-modules were inserted at the last step.</summary>
    public int InsertedCount => _inserted;

    /// <inheritdoc />
    public double Emf(double n, double iLast, double dt)
    {
        int k = (int)Math.Clamp(Math.Round(n * _n, MidpointRounding.AwayFromZero), 0.0, _n);
        _inserted = k;
        for (int j = 0; j < _n; j++) _order[j] = j;
        double[] vc = _vc;
        if (iLast >= 0.0) Array.Sort(_order, (x, y) => { int c = vc[x].CompareTo(vc[y]); return c != 0 ? c : x.CompareTo(y); });
        else Array.Sort(_order, (x, y) => { int c = vc[y].CompareTo(vc[x]); return c != 0 ? c : x.CompareTo(y); });
        double sum = 0.0;
        for (int j = 0; j < k; j++) sum += vc[_order[j]];
        return sum;
    }

    /// <inheritdoc />
    public void Accept(double n, double i, double dt)
    {
        double dv = dt * i / _c;
        for (int j = 0; j < _inserted; j++) _vc[_order[j]] += dv;
    }

    /// <inheritdoc />
    public double SumCapacitorVoltage
    {
        get { double s = 0.0; for (int j = 0; j < _n; j++) s += _vc[j]; return s; }
    }

    /// <inheritdoc />
    public double Energy
    {
        get { double s = 0.0; for (int j = 0; j < _n; j++) s += 0.5 * _c * _vc[j] * _vc[j]; return s; }
    }
}
