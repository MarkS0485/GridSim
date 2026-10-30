using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation;

/// <summary>Knobs for <see cref="SyntheticMeasurements.Sample"/>. Sigmas are per measurement family, and the
/// MW figure is used UNCHANGED for the matching MVAr kind: <see cref="InjSigmaMw"/> is the sigma (and noise
/// standard deviation) of both PInjMw and QInjMvar, and <see cref="FlowSigmaMw"/> of both PFlowMw and QFlowMvar,
/// numerically the same value in MVAr as in MW (it is not doubled or otherwise scaled). Coverages are the fraction
/// of candidate locations that get a meter, selected by a deterministic stride over model order.</summary>
public sealed record SyntheticMeasurementOptions
{
    /// <summary>RNG seed. The default follows the repo's date-literal convention for
    /// deterministic fixtures.</summary>
    public int Seed { get; init; } = 20260713;

    public double VmSigmaPu { get; init; } = 0.004;
    public double InjSigmaMw { get; init; } = 1.0;
    public double FlowSigmaMw { get; init; } = 1.6;

    /// <summary>Scales the injected noise: 1 = nominal sigmas, 0 = exact values (the
    /// machine-precision recovery fixture). Declared sigmas are unaffected.</summary>
    public double NoiseScale { get; init; } = 1.0;

    /// <summary>Fraction of buses carrying a voltage meter.</summary>
    public double VmCoverage { get; init; } = 1.0;
    /// <summary>Fraction of buses carrying P/Q injection telemetry.</summary>
    public double InjCoverage { get; init; } = 1.0;
    /// <summary>Fraction of branches carrying P/Q flow telemetry (both ends when selected).</summary>
    public double FlowCoverage { get; init; } = 1.0;

    /// <summary>Stamp written to the set - a fixed, safely-historical instant by default (the
    /// sampler must stay deterministic, so it never reads the clock).</summary>
    public DateTime TakenUtc { get; init; } = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
}

/// <summary>
/// Samples a synthetic measurement set z = h(x_true) + N(0, sigma)*NoiseScale from a solved
/// power-flow truth - the estimator's test fixture and demo feed. Deterministic per options:
/// one seeded RNG, buses and branches walked strictly in model order, Box-Muller Gaussians.
/// Values are read straight off <see cref="PowerFlowResult"/> (same units and sign conventions),
/// so estimate-vs-truth comparisons are closed under one set of conventions.
/// </summary>
public static class SyntheticMeasurements
{
    public static MeasurementSet Sample(GridModel model, PowerFlowResult truth, SyntheticMeasurementOptions? options = null)
    {
        var opt = options ?? new SyntheticMeasurementOptions();
        var rng = new Random(opt.Seed);
        var ms = new List<Measurement>();

        for (int i = 0; i < model.BusCount; i++)
        {
            int id = truth.BusIds[i];
            if (Covered(i, opt.VmCoverage))
                ms.Add(new Measurement(MeasurementKind.VmPu,
                    truth.Vm[i] + Noise(rng, opt.VmSigmaPu, opt.NoiseScale), opt.VmSigmaPu,
                    Bus: id, Source: "synthetic"));
            if (Covered(i, opt.InjCoverage))
            {
                ms.Add(new Measurement(MeasurementKind.PInjMw,
                    truth.PInjMw[i] + Noise(rng, opt.InjSigmaMw, opt.NoiseScale), opt.InjSigmaMw,
                    Bus: id, Source: "synthetic"));
                ms.Add(new Measurement(MeasurementKind.QInjMvar,
                    truth.QInjMvar[i] + Noise(rng, opt.InjSigmaMw, opt.NoiseScale), opt.InjSigmaMw,
                    Bus: id, Source: "synthetic"));
            }
        }

        // Parallel circuits are disambiguated by the ordinal circuit form "#k" that
        // MeasurementSet.TryResolveBranch understands - used for EVERY parallel pair, because
        // real cases (gb-full SGT banks) carry duplicate NAMES on parallel branches, so a name
        // is only a safe key when the pair is unique.
        var pairCount = new Dictionary<(int From, int To), int>();
        foreach (Branch br in model.Branches)
            pairCount[(br.From, br.To)] = pairCount.GetValueOrDefault((br.From, br.To)) + 1;

        for (int k = 0; k < truth.LineFlows.Count; k++)
        {
            if (!Covered(k, opt.FlowCoverage)) continue;
            LineFlow lf = truth.LineFlows[k];
            string? circuit = pairCount[(lf.From, lf.To)] > 1 ? $"#{k}"
                : !string.IsNullOrEmpty(lf.Name) ? lf.Name
                : null;
            ms.Add(new Measurement(MeasurementKind.PFlowMw,
                lf.PFromMw + Noise(rng, opt.FlowSigmaMw, opt.NoiseScale), opt.FlowSigmaMw,
                From: lf.From, To: lf.To, Circuit: circuit, Source: "synthetic"));
            ms.Add(new Measurement(MeasurementKind.QFlowMvar,
                lf.QFromMvar + Noise(rng, opt.FlowSigmaMw, opt.NoiseScale), opt.FlowSigmaMw,
                From: lf.From, To: lf.To, Circuit: circuit, Source: "synthetic"));
            ms.Add(new Measurement(MeasurementKind.PFlowMw,
                lf.PToMw + Noise(rng, opt.FlowSigmaMw, opt.NoiseScale), opt.FlowSigmaMw,
                From: lf.From, To: lf.To, End: BranchEnd.To, Circuit: circuit, Source: "synthetic"));
            ms.Add(new Measurement(MeasurementKind.QFlowMvar,
                lf.QToMvar + Noise(rng, opt.FlowSigmaMw, opt.NoiseScale), opt.FlowSigmaMw,
                From: lf.From, To: lf.To, End: BranchEnd.To, Circuit: circuit, Source: "synthetic"));
        }

        return new MeasurementSet
        {
            Case = model.Name,
            BaseMva = model.BaseMva,
            TakenUtc = opt.TakenUtc,
            Note = $"synthetic seed={opt.Seed} noise={opt.NoiseScale} " +
                   $"coverage=vm:{opt.VmCoverage}/inj:{opt.InjCoverage}/flow:{opt.FlowCoverage}",
            Measurements = ms,
        };
    }

    /// <summary>Deterministic stride selection: item <paramref name="index"/> is covered when the
    /// running quota floor((index+1)*coverage) advances - spreads meters evenly over model order.</summary>
    private static bool Covered(int index, double coverage) =>
        (int)((index + 1) * coverage) > (int)(index * coverage);

    /// <summary>One Gaussian draw (Box-Muller) scaled to sigma*noiseScale.</summary>
    private static double Noise(Random rng, double sigma, double noiseScale)
    {
        if (noiseScale == 0) return 0; // exact mode draws nothing from the RNG
        double u1 = 1.0 - rng.NextDouble(); // (0, 1]: log never sees zero
        double u2 = rng.NextDouble();
        // D1 (October 2026, N175): log and cos are GridSim's deterministic fdlibm routines. With System.Math they came
        // from the operating system's libm, so the same seed drew last-bit-different noise on Linux and Windows (the
        // case57 noisy-J difference, 407.14427112449795 vs 407.14427112449448). The seeded System.Random is managed
        // .NET code and already the same everywhere.
        double z = Math.Sqrt(-2.0 * DetMath.Log(u1)) * DetMath.Cos(2.0 * Math.PI * u2);
        return sigma * noiseScale * z;
    }
}
