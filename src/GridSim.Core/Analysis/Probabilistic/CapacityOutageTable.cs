using GridSim.Core.Diagnostics;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>
/// The capacity outage probability table of a set of independent units, by exact convolution on the capacity lattice.
/// Each unit is a discrete law of its available capacity (two-state: 0 or rated; multi-state: derated steps). The table
/// holds <c>P(available capacity = k x step)</c>; with every capacity an integer multiple of the step it is the exact
/// distribution (no discretisation), accumulated unit by unit in the order given.
/// </summary>
public sealed class CapacityOutageTable
{
    private readonly LatticePmf _pmf;
    private readonly double[] _cumP;     // sum of P(avail = v_j), j <= k
    private readonly double[] _cumPV;    // sum of v_j P(avail = v_j), j <= k

    /// <summary>The capacity step (MW).</summary>
    public double StepMw { get; }
    /// <summary>The installed capacity (MW): every unit up.</summary>
    public double InstalledMw { get; }
    /// <summary>The available-capacity levels and their probabilities, ascending, zero-mass levels included.</summary>
    public IReadOnlyList<(double CapacityMw, double Probability)> Levels { get; }

    private CapacityOutageTable(LatticePmf pmf, double step, double installed)
    {
        _pmf = pmf; StepMw = step; InstalledMw = installed;
        var levels = new List<(double, double)>(pmf.Mass.Length);
        _cumP = new double[pmf.Mass.Length];
        _cumPV = new double[pmf.Mass.Length];
        double cp = 0.0, cv = 0.0;
        for (int i = 0; i < pmf.Mass.Length; i++)
        {
            double v = pmf.ValueAt(pmf.FirstIndex + i);
            levels.Add((v, pmf.Mass[i]));
            cp += pmf.Mass[i]; cv += pmf.Mass[i] * v;
            _cumP[i] = cp; _cumPV[i] = cv;
        }
        Levels = levels;
    }

    /// <summary>The largest step that makes every capacity of every unit an integer multiple, when all are whole numbers of
    /// MW; refuses (asks for an explicit step) otherwise.</summary>
    public static double AutoStep(IReadOnlyList<UncertainDistribution> units)
    {
        long g = 0;
        foreach (UncertainDistribution u in units)
            foreach ((double v, _) in u.Atoms)
            {
                if (Math.Abs(v - Math.Round(v)) > 1e-9)
                    throw DomainRefusal.Mark(new InvalidOperationException(
                        $"Capacity {v:R} MW is not a whole number of MW: give an explicit capacity step (stepMw) that divides every capacity."));
                g = Gcd(g, (long)Math.Abs(Math.Round(v)));
            }
        return g == 0 ? 1.0 : g;
    }

    private static long Gcd(long a, long b) { while (b != 0) { (a, b) = (b, a % b); } return a; }

    /// <summary>Build the table. Refuses a continuous unit law and a capacity that is not on the lattice.</summary>
    public static CapacityOutageTable Build(IReadOnlyList<UncertainDistribution> units, double? stepMw = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count == 0) throw DomainRefusal.Mark(new ArgumentException("At least one unit is needed."));
        foreach (UncertainDistribution u in units)
            if (!u.IsDiscrete)
                throw DomainRefusal.Mark(new InvalidOperationException("The capacity outage table needs discrete unit laws (two-state or multi-state)."));
        double step = stepMw ?? AutoStep(units);
        if (!(step > 0.0) || !double.IsFinite(step)) throw DomainRefusal.Mark(new ArgumentException("The capacity step must be finite and above 0."));
        LatticePmf acc = LatticePmf.PointMass(0.0, step);
        double installed = 0.0;
        foreach (UncertainDistribution u in units)
        {
            LatticePmf term = LatticePmf.OfTerm(u, 1.0, step);
            if (!term.AtomsExact)
                throw DomainRefusal.Mark(new InvalidOperationException(
                    $"A unit capacity is not an integer multiple of the {step:R} MW step: the convolution would be approximate, so it is out of scope. Choose a step that divides every capacity."));
            acc = acc.Convolve(term);
            installed += u.Atoms[^1].Value;
        }
        return new CapacityOutageTable(acc, step, installed);
    }

    /// <summary>P(available capacity &lt; load): the loss-of-load probability at this load (loss when capacity is short).</summary>
    public double LossOfLoadProbability(double loadMw)
    {
        int m = CountBelow(loadMw);
        return m == 0 ? 0.0 : _cumP[m - 1];
    }

    /// <summary>E[(load - available capacity)^+] in MW: the expected shortfall at this load.</summary>
    public double ExpectedShortfallMw(double loadMw)
    {
        int m = CountBelow(loadMw);
        return m == 0 ? 0.0 : loadMw * _cumP[m - 1] - _cumPV[m - 1];
    }

    // The number of lattice levels strictly below the load (tolerance one billionth of a step).
    private int CountBelow(double loadMw)
    {
        double limit = loadMw - 1e-9 * StepMw;
        int lo = 0, hi = _cumP.Length;   // first index whose value is >= limit
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_pmf.ValueAt(_pmf.FirstIndex + mid) < limit) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
