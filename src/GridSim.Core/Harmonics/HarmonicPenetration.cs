using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>
/// The result of a harmonic penetration study: the complex bus voltage at every analysed order.
/// <see cref="Voltage"/> is indexed <c>[orderIndex][busIndex]</c>, aligned with <see cref="Orders"/> and
/// <see cref="BusIds"/>. Voltages are per-unit phasors at the harmonic frequency (a magnitude is a fraction
/// of the nominal fundamental voltage).
/// </summary>
public sealed record HarmonicPenetrationResult(
    IReadOnlyList<int> Orders,
    IReadOnlyList<int> BusIds,
    Complex[][] Voltage)
{
    /// <summary>
    /// N075 (October 2026): the source spectrum components this study did NOT analyse, because their order is not in
    /// <see cref="Orders"/> (exactly <see cref="HarmonicPenetration.ComponentsOutsideOrders"/> of the sources and options
    /// the study ran with: "bus B (type) order h at m %", source then spectrum order). <see cref="HarmonicPenetration.Solve"/>
    /// injects only at the analysed orders, so these components contribute NOTHING to <see cref="Voltage"/>; any THD
    /// computed from this result understates the distortion by their share. Empty when every non-zero component was
    /// analysed (the default library sources with the default or Study orders). Until 1.95.124 they were dropped without
    /// a word for every caller except the CLI, which has refused them since 1.95.107; a caller that wants the same
    /// refusal calls <see cref="HarmonicPenetration.RefuseComponentsOutsideOrders"/> before solving, and a caller that
    /// studies a subset of orders on purpose (a single-order test, a 5th-only check) reads this list instead.
    /// </summary>
    public IReadOnlyList<string> ComponentsOutsideOrders { get; init; } = Array.Empty<string>();

    /// <summary>The complex voltage at bus index <paramref name="busIndex"/> and the given order (0 if absent).</summary>
    public Complex VoltageAt(int order, int busIndex)
    {
        int oi = Orders is int[] arr ? Array.IndexOf(arr, order) : IndexOf(Orders, order);
        return oi < 0 ? Complex.Zero : Voltage[oi][busIndex];
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }
}

/// <summary>
/// Linear harmonic penetration: for each analysed order h, build the frequency-dependent admittance
/// <c>Y(h)</c>, assemble the nodal harmonic current injection <c>I(h)</c> from the sources, and solve
/// <c>Y(h) V(h) = I(h)</c> for the bus voltages via the complex-solver seam. This is the grounded core of the
/// harmonic engine - the physics (Y(h), the solve, KCL) never guesses; the only assumed input is the source
/// spectra, which arrive provenance-tagged. The default backend is the scalable real 2n-expansion solver, so
/// this runs at gb-full scale.
/// </summary>
public static class HarmonicPenetration
{
    /// <summary>Run the penetration study over the source set's orders.</summary>
    public static HarmonicPenetrationResult Solve(
        GridModel model,
        IReadOnlyList<HarmonicSource> sources,
        HarmonicModelOptions? options = null,
        IComplexLinearSolver? solver = null)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        bool ownSolver = solver is null; // only the default solver may be parallelised (per-worker instance)

        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        var busIds = new int[n];
        for (int i = 0; i < n; i++) { index[model.Buses[i].Id] = i; busIds[i] = model.Buses[i].Id; }

        IReadOnlyList<int> orders = opt.Orders;

        RefuseTriplensIfBalanced(sources, opt);
        RefuseUnknownSourceBuses(model, sources, "Harmonic penetration");

        // N348 / N149 (end pass, first run, 2026-10-11): refuse a non-physical active-front-end impedance HERE, once,
        // before any order is solved. FrequencyDependentYBus.Build checks it too, but inside the parallel per-order loop
        // below its ArgumentException reached the caller wrapped in an AggregateException (one per failing worker), so
        // the refusal's type depended on whether the orders ran in parallel. Checking up front gives the same plain
        // ArgumentException, naming the option, on every path. Validation only: no number changes.
        if (opt.ActiveFrontEndReactancePu is double xAfe)
            FrequencyDependentYBus.ValidateActiveFrontEndImpedance(opt.ActiveFrontEndResistancePu, xAfe);

        var voltage = new Complex[orders.Count][];
        var singular = new bool[orders.Count]; // per order: Y(h) could not be solved (written by index)

        // Each order's solve is independent and its result is written by index, so orders are bit-identical
        // regardless of execution order. When we own the solver, run them across cores with a per-worker
        // RealExpandedComplexSolver (its ordering cache is per-instance and NOT thread-safe). A
        // caller-supplied solver stays sequential (it may be stateful / non-thread-safe).
        void SolveOrder(int oi, IComplexLinearSolver s)
        {
            int h = orders[oi];
            var v = new Complex[n];
            if (h != 1) // the fundamental (h=1) carries no harmonic injection; report zero
            {
                // Injection first: an order with no current needs no Y(h) (its voltage is exactly zero). In
                // balanced mode that is every triplen order, whose Y(h) cannot be built (R029).
                var inj = new Complex[n];
                bool any = false;
                foreach (HarmonicSource src in sources)
                {
                    Complex ih = src.InjectionAt(h);
                    if (ih == Complex.Zero) continue;
                    if (index.TryGetValue(src.Bus, out int bi)) { inj[bi] += ih; any = true; }
                }
                if (any)
                {
                    ComplexCsr y = FrequencyDependentYBus.Build(model, h, opt);
                    // A null (singular) solve used to become a ZERO voltage vector, reported as 0% THD and
                    // "Compliant" (R001). It is now recorded and thrown below - never a result.
                    Complex[]? x = s.Solve(y, inj);
                    if (x is null) singular[oi] = true;
                    else v = x;
                }
            }
            voltage[oi] = v;
        }

        if (ownSolver && orders.Count > 1)
        {
            using var pool = new ThreadLocal<IComplexLinearSolver>(() => new RealExpandedComplexSolver());
            // Unwrapped and deterministic: a refused order surfaces as the serial loop would raise it (lowest order index).
            DeterministicParallel.For(orders.Count, oi => SolveOrder(oi, pool.Value!));
        }
        else
        {
            IComplexLinearSolver s = solver ?? new RealExpandedComplexSolver();
            for (int oi = 0; oi < orders.Count; oi++) SolveOrder(oi, s);
        }

        // Fail loud on the LOWEST singular harmonic order (deterministic whatever order the parallel workers ran
        // in, and whatever order the caller listed the orders in).
        int lowest = int.MaxValue;
        for (int oi = 0; oi < orders.Count; oi++)
            if (singular[oi] && orders[oi] < lowest) lowest = orders[oi];
        if (lowest != int.MaxValue) throw new SingularHarmonicNetworkException("Harmonic penetration", lowest);

        // N075: say what was left out (computed from the same sources and options; changes no voltage).
        return new HarmonicPenetrationResult(orders, busIds, voltage)
        {
            ComponentsOutsideOrders = ComponentsOutsideOrders(sources, opt),
        };
    }

    /// <summary>
    /// Refuse, naming every one, a harmonic source whose bus is not in the model. Such a source used to be dropped
    /// silently (its injection skipped at every order), so the study reported a network free of a source it was given.
    /// </summary>
    public static void RefuseUnknownSourceBuses(GridModel model, IReadOnlyList<HarmonicSource> sources, string what)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sources);
        var buses = new HashSet<int>(model.Buses.Select(b => b.Id));
        var unknown = sources.Where(s => !buses.Contains(s.Bus)).Select(s => s.Bus).Distinct().OrderBy(b => b).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"{what}: harmonic source(s) on bus(es) {string.Join(", ", unknown)}, which are not in the model.", nameof(sources));
    }

    /// <summary>
    /// N075 (October 2026): the fail-loud form of <see cref="ComponentsOutsideOrders"/> for code that embeds the engine.
    /// Throws <see cref="ArgumentException"/> naming every non-zero source component whose order the study would not
    /// analyse (the same rule, and in substance the same words, as the CLI <c>harmonics</c> verb's refusal since
    /// 1.95.107); does nothing when there is none. Call it before <see cref="Solve"/> when the spectra come from a user or
    /// a file, so a spectrum outside the analysed set can never be reported as a low THD.
    /// </summary>
    public static void RefuseComponentsOutsideOrders(IReadOnlyList<HarmonicSource> sources, HarmonicModelOptions opt)
    {
        IReadOnlyList<string> outside = ComponentsOutsideOrders(sources, opt);
        if (outside.Count == 0) return;
        throw new ArgumentException(
            $"{outside.Count} harmonic source component(s) are at orders this study does not analyse (it analyses orders " +
            $"{string.Join(",", opt.Orders)}): {string.Join("; ", outside)}. They would be ignored and the THD understated; " +
            "remove them from the spectra or add their orders to the study.", nameof(sources));
    }

    /// <summary>
    /// The source spectrum components that the study would NOT analyse: a non-zero component at an order that is not in
    /// <see cref="HarmonicModelOptions.Orders"/> (order 1, the fundamental reference of the percentages, is not a
    /// harmonic and is never listed). <see cref="Solve"/> injects only at the analysed orders, so such a component is
    /// dropped without a word - a file holding only such orders used to report 0.00 % THD and "compliant"
    /// (docs-programme finding). Callers that take spectra from a user refuse a non-empty answer; each entry names the
    /// source (bus and type) and the order, in source then spectrum order.
    /// </summary>
    public static IReadOnlyList<string> ComponentsOutsideOrders(IReadOnlyList<HarmonicSource> sources, HarmonicModelOptions opt)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(opt);
        var analysed = new HashSet<int>(opt.Orders);
        var outside = new List<string>();
        foreach (HarmonicSource src in sources)
            foreach (HarmonicComponent c in src.Spectrum)
                if (c.Order != 1 && c.MagnitudePercent != 0.0 && !analysed.Contains(c.Order))
                    outside.Add($"bus {src.Bus} ({src.SourceType}) order {c.Order} at {c.MagnitudePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)} %");
        return outside;
    }

    /// <summary>
    /// Balanced-harmonic mode (R029): a zero-sequence (triplen) injection cannot be represented by the
    /// positive-sequence network, and leaving it out would silently understate the distortion. Refuse it,
    /// naming the source, whatever orders were requested. No-op outside balanced mode.
    /// </summary>
    internal static void RefuseTriplensIfBalanced(IReadOnlyList<HarmonicSource> sources, HarmonicModelOptions opt)
    {
        if (!opt.BalancedHarmonicSequences) return;
        foreach (HarmonicSource src in sources)
            foreach (HarmonicComponent c in src.Spectrum)
                if (HarmonicSequence.IsTriplen(c.Order) && c.MagnitudePercent != 0.0)
                    throw new ArgumentException(
                        $"Harmonic source at bus {src.Bus} ({src.SourceType}) injects order {c.Order}, a triplen " +
                        "(zero sequence). The balanced positive-sequence study cannot model it (that needs the " +
                        "zero-sequence network); use the three-phase harmonic study.", nameof(sources));
    }
}
