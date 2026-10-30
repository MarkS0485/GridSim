using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;

namespace GridSim.Core.Assets;

/// <summary>One swept control parameter and its plausible low/high literature range (both [VERIFY]).</summary>
public sealed record AssetSweepParam(string ClassName, string ParamName, double Low, double High);

/// <summary>The swing outcome of one sweep sample (a specific corner of the parameter space).
/// <c>InitialRocofHzPerS</c> is the sample's PEAK RoCoF (Hz/s, sign kept), copied from
/// <see cref="Dynamics.FrequencyResponse.InitialRocofHzPerS"/> (N141: historical name).</summary>
public sealed record AssetSweepSample(
    IReadOnlyDictionary<string, double> Params, double NadirHz, double InitialRocofHzPerS, int TrippedCount);

/// <summary>One parameter's leverage: how far the nadir moves as it goes low→high (others at baseline).</summary>
public sealed record TornadoEntry(string ClassName, string ParamName, double NadirAtLow, double NadirAtHigh)
{
    public double NadirSwingHz => Math.Abs(NadirAtHigh - NadirAtLow);
}

/// <summary>
/// A parameter-sweep ENVELOPE, not a point prediction - the honest deliverable for any real-fleet claim.
/// The nadir/RoCoF <b>band</b> spans the plausible ranges of the unmeasurable [VERIFY] control parameters;
/// <see cref="Tornado"/> attributes which knob moved the answer; <see cref="CascadeIsPossible"/> flags when
/// the band brackets both "embedded generation trips" and "it doesn't" - in which case that IS the finding.
/// Tagged <see cref="ProvenanceTag.UnvalidatedAtScale"/>: self-consistent, passes the reduction gate, but
/// its absolute value at scale is not validated (awaits the 4-node Gridradar PMU back-test).
/// </summary>
public sealed record AssetEnvelopeResult(
    double NadirMinHz, double NadirMaxHz, double NadirMedianHz,
    double RocofMinHzPerS, double RocofMaxHzPerS,
    bool CascadeIsPossible,
    IReadOnlyList<AssetSweepSample> Samples,
    IReadOnlyList<TornadoEntry> Tornado,
    ProvenanceTag Provenance = ProvenanceTag.UnvalidatedAtScale);

/// <summary>
/// Runs a corner sweep over the unmeasurable inverter control parameters and reports the nadir/RoCoF
/// envelope + a tornado sensitivity attribution - so a reviewer sees the result is parameter-driven and
/// where the leverage sits, rather than a single authoritative-looking curve.
/// </summary>
public static class AssetEnvelope
{
    /// <summary>
    /// Sweep the 2^k corners of <paramref name="sweep"/> (k parameters each at their low/high extreme; k=0
    /// yields the single un-overridden case), and compute a one-at-a-time tornado (each param low vs high,
    /// others at their midpoint). k is capped at 12 (4096 corners) to stay tractable.
    /// </summary>
    public static AssetEnvelopeResult Sweep(
        GridModel model, double baseDemandMw, IReadOnlyList<FrequencyEvent> events,
        DynamicsConfig cfg, IReadOnlyList<AssetSweepParam> sweep)
    {
        if (model.AssetDynamics is null)
            throw new InvalidOperationException("Sweep needs a model carrying an authored asset-dynamics bundle.");
        int k = sweep.Count;
        if (k > 12) throw new ArgumentException($"At most 12 swept parameters ({1 << 12} corners); got {k}.", nameof(sweep));

        var samples = new List<AssetSweepSample>(1 << k);
        int corners = k == 0 ? 1 : 1 << k;
        for (int mask = 0; mask < corners; mask++)
        {
            var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < k; i++)
            {
                bool high = (mask & (1 << i)) != 0;
                overrides[Key(sweep[i])] = high ? sweep[i].High : sweep[i].Low;
            }
            samples.Add(RunSample(model, baseDemandMw, events, cfg, sweep, overrides));
        }

        // Tornado: each parameter low vs high, all others at their midpoint.
        // GR-605 (October 2026): a parameter with an infinite end (an element switched off by an infinite threshold) has
        // no finite midpoint; 0.5 (Low + High) gave infinity (or NaN for -inf..+inf) and ran every other parameter's
        // tornado with that value. Such a parameter is now left at the class's own authored value while the others
        // swing. A NaN end is refused: it is no value at all.
        var mid = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (AssetSweepParam p in sweep)
        {
            if (double.IsNaN(p.Low) || double.IsNaN(p.High))
                throw new ArgumentException($"Swept parameter {Key(p)} has a NaN end ({p.Low} .. {p.High}); give both ends as numbers.", nameof(sweep));
            double m = 0.5 * (p.Low + p.High);
            if (double.IsFinite(m)) mid[Key(p)] = m;
        }
        var tornado = new List<TornadoEntry>(k);
        foreach (AssetSweepParam p in sweep)
        {
            var lo = new Dictionary<string, double>(mid, StringComparer.Ordinal) { [Key(p)] = p.Low };
            var hi = new Dictionary<string, double>(mid, StringComparer.Ordinal) { [Key(p)] = p.High };
            double nLo = RunSample(model, baseDemandMw, events, cfg, sweep, lo).NadirHz;
            double nHi = RunSample(model, baseDemandMw, events, cfg, sweep, hi).NadirHz;
            tornado.Add(new TornadoEntry(p.ClassName, p.ParamName, nLo, nHi));
        }
        GridSim.Core.Numerics.FrozenIntroSort.Sort(tornado, (a, b) => b.NadirSwingHz.CompareTo(a.NadirSwingHz));   // most leverage first; GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)

        var nadirs = samples.Select(s => s.NadirHz).OrderBy(x => x).ToList();
        // GR-605: the median of an even count (every sweep with k >= 1: 2^k corners) is the mean of the two middle
        // values; it used to be the upper of the two.
        int half = nadirs.Count / 2;
        double median = nadirs.Count % 2 == 1 ? nadirs[half] : 0.5 * (nadirs[half - 1] + nadirs[half]);
        double nMin = nadirs[0], nMax = nadirs[^1];
        double rMin = samples.Min(s => s.InitialRocofHzPerS);
        double rMax = samples.Max(s => s.InitialRocofHzPerS);
        bool cascadePossible = samples.Any(s => s.TrippedCount > 0) && samples.Any(s => s.TrippedCount == 0);

        return new AssetEnvelopeResult(nMin, nMax, median, rMin, rMax, cascadePossible, samples, tornado);
    }

    private static AssetSweepSample RunSample(
        GridModel model, double baseDemandMw, IReadOnlyList<FrequencyEvent> events,
        DynamicsConfig cfg, IReadOnlyList<AssetSweepParam> sweep, IReadOnlyDictionary<string, double> overrides)
    {
        GridModel m = WithOverrides(model, sweep, overrides);
        DynamicsConfig runCfg = cfg with { EnableAssetDynamics = true };
        AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(m, baseDemandMw, events, runCfg);
        return new AssetSweepSample(new Dictionary<string, double>(overrides), r.Response.NadirHz, r.Response.InitialRocofHzPerS, r.TrippedCount);
    }

    private static string Key(AssetSweepParam p) => p.ClassName + "::" + p.ParamName;

    /// <summary>Clone the model's asset library with the swept parameters overridden per class.</summary>
    private static GridModel WithOverrides(
        GridModel model, IReadOnlyList<AssetSweepParam> sweep, IReadOnlyDictionary<string, double> overrides)
    {
        AssetDynamicsModel spec = model.AssetDynamics!;
        var newClasses = new List<AssetClass>();
        foreach (AssetClass c in spec.Library.Classes)
        {
            Dictionary<string, double>? changed = null;
            foreach (AssetSweepParam p in sweep)
            {
                if (!string.Equals(p.ClassName, c.Name, StringComparison.Ordinal)) continue;
                if (!overrides.TryGetValue(Key(p), out double v)) continue;
                changed ??= new Dictionary<string, double>(c.Params, StringComparer.OrdinalIgnoreCase);
                changed[p.ParamName] = v;
            }
            newClasses.Add(changed is null ? c : c with { Params = changed });
        }
        var library = new AssetClassLibrary(newClasses);
        // N314 (LF5, October 2026): copied through the shared GridModel.CopyWith (R270), which carries EVERY property, with
        // only the asset-dynamics bundle replaced. This copy used to be hand-built (CopyWith could not replace
        // AssetDynamics), so a GridModel property added later would have been dropped here silently. Same result as the
        // hand-built copy, part for part: CopyWith with no generators given carries the whole fleet (out-of-service units
        // included, N094) in the same order, the same in-service list, the same buses, branches, OLTCs, boundaries, nominal
        // frequency, name and protections (the placed devices must stay: omitting them once ran every sample with all DG
        // LoM blocks, LFDD stages and reclosers removed).
        return model.CopyWith(assetDynamics: new AssetDynamicsModel(library, spec.Bindings));
    }
}
