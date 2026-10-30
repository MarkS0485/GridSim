namespace GridSim.Core.Fleet;

/// <summary>
/// One device in a fleet: its real (1/2)J*omega^2 rotational inertia, its fast-response cap, and - crucially - its own
/// deadband, lag and <see cref="StaggerSeconds"/> onset delay. Many such units seeing the same system frequency
/// is what makes fleet FFR <b>non-additive</b>: they don't all cross their deadbands and saturate in lockstep.
/// </summary>
public sealed record FleetUnit(
    double RotationalInertiaMwS,
    double FastResponseMw,
    double FastResponseTimeConstantS = 0.5,
    double FastResponseDeadbandHz = 0.015,
    double StaggerSeconds = 0.0);

/// <summary>A fleet of devices. Intrinsic inertia is N-additive (energy adds); fast response is not.</summary>
public sealed record FleetSpec(IReadOnlyList<FleetUnit> Units)
{
    public int Count => Units.Count;
    public double TotalInertiaMwS => Units.Sum(u => u.RotationalInertiaMwS);
    public double TotalFfrCapMw => Units.Sum(u => u.FastResponseMw);

    /// <summary>
    /// <paramref name="n"/> identical units splitting a total inertia/FFR budget, onset-staggered evenly across
    /// <c>[0, maxStaggerS]</c>. With <c>n=1</c> and <c>maxStaggerS=0</c> this is a single aggregate device.
    /// </summary>
    public static FleetSpec Uniform(
        int n, double totalInertiaMwS, double totalFfrMw,
        double ffrTauS = 0.5, double deadbandHz = 0.015, double maxStaggerS = 0.0)
    {
        // GR-844: the fleet inputs must be physical. n = 0 stays the single aggregate device it always was; a negative
        // count, a non-finite or negative budget, time constant, deadband or stagger is refused rather than run.
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "A fleet has 0 (one aggregate device) or more units.");
        static void NonNegative(double v, string name)
        {
            if (!(v >= 0) || double.IsInfinity(v))
                throw new ArgumentOutOfRangeException(name, v, $"{name} must be a finite number, 0 or above.");
        }
        NonNegative(totalInertiaMwS, nameof(totalInertiaMwS));
        NonNegative(totalFfrMw, nameof(totalFfrMw));
        NonNegative(ffrTauS, nameof(ffrTauS));
        NonNegative(deadbandHz, nameof(deadbandHz));
        NonNegative(maxStaggerS, nameof(maxStaggerS));
        n = Math.Max(1, n);
        var units = new List<FleetUnit>(n);
        for (int i = 0; i < n; i++)
        {
            double stagger = n <= 1 ? 0.0 : maxStaggerS * i / (n - 1);
            units.Add(new FleetUnit(totalInertiaMwS / n, totalFfrMw / n, ffrTauS, deadbandHz, stagger));
        }
        return new FleetSpec(units);
    }
}
