using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation;

/// <summary>
/// The triad's solve stage: turn a measurement set into a MEASURED-INJECTION POWER FLOW and
/// solve it with the validated Newton engine. GDA provides the warm-start data (the measured
/// per-bus injections inside the set), GridSim solves it into a physically exact operating
/// point, and the estimator then fuses that solved prior with the same telemetry - the prior
/// replaces the flat voltage-schedule guess with solved physics, and the solution doubles as
/// the Gauss-Newton warm start.
///
/// Model construction: every bus's net injection is read off the set's PInjMw/QInjMvar rows
/// (several rows at one bus are redundant meters of the same injection and are combined by their inverse-variance
/// weighted mean, bug hunt 2026-11-01 #183; unmeasured buses default to zero injection). Buses injecting more than
/// <see cref="PvThresholdMw"/> become PV at <see cref="PvSetpointPu"/> (a generating site holds
/// its voltage - without var support a pure-PQ conversion sags unrealistically); everything
/// else is PQ with the measured injection as (negative) load. The slack keeps its reference
/// role and absorbs the residual imbalance, exactly as it does in the estimator.
/// </summary>
public static class MeasuredInjectionPrior
{
    /// <summary>Buses injecting at least this much measured MW hold voltage as PV sites.</summary>
    public const double PvThresholdMw = 50.0;

    /// <summary>Voltage setpoint for measured generating sites (grid-code operating band).</summary>
    public const double PvSetpointPu = 1.01;

    // ---- Named constants (October 2026 review, WS13 R174). Values unchanged.

    /// <summary>Smallest reactive-capability base (MVA) given to a measured PV site: the site's Q range is
    /// +/-<see cref="QCapabilityRatio"/> x max(|P|, this), so a small site still has room to hold voltage.</summary>
    public const double MinQCapabilityBaseMva = 100.0;

    /// <summary>A measured PV site's reactive range as a fraction of its capability base (0.6, roughly a 0.85
    /// power-factor capability at full output).</summary>
    public const double QCapabilityRatio = 0.6;

    /// <summary>Newton iteration cap of the measured-injection solve (generous: the conditioned case can start
    /// far from its solution).</summary>
    public const int PriorSolveMaxIterations = 100;

    /// <summary>Default sigma (pu) of a voltage-schedule prior row once it carries the SOLVED voltage.</summary>
    public const double DefaultVoltagePriorSigmaPu = 0.02;

    /// <summary>Source tag of a flat voltage-schedule pseudo-measurement (written by the measurement-layer
    /// producer) that <see cref="ApplyVoltagePrior"/> replaces with the solved profile.</summary>
    public const string VoltageScheduleSource = "pseudo-voltage-schedule";

    /// <summary>Source tag given to a voltage-schedule row once it carries the solved prior voltage.</summary>
    public const string SolvedPriorSource = "prior-solved";

    /// <summary>Solve the measured-injection operating point. Returns the solved state
    /// (check <see cref="PowerFlowResult.Converged"/>) - the caller uses it as the estimator's
    /// warm start and as the value source for voltage-schedule prior measurements.</summary>
    public static PowerFlowResult Solve(GridModel model, MeasurementSet set, IPowerFlowSolver solver)
    {
        // Bug hunt 2026-11-01 (#183): the WLS treats EVERY PInjMw / QInjMvar row as an observation of the WHOLE bus
        // injection (MeasurementModel: a PInjMw row's h(x) is the bus's net injection), so two rows at one bus are two
        // redundant meters of one quantity, not two parts of it. The prior used to SUM them: two redundant 300 MW meters
        // built a 600 MW bus, so the prior-solved VmPu rows (sigma 0.02, excluded from chi-square per N078) and the warm
        // start came from a wrong operating point. The rows are now combined as the estimator would weigh them, by the
        // inverse-variance weighted mean sum(z / sigma^2) / sum(1 / sigma^2) (the WLS estimate of one quantity from
        // several meters of it). A bus with a single row keeps that row's value exactly, so every set without duplicate
        // injection rows solves the same conditioned case, bit for bit, as before.
        Dictionary<int, double> pMeas = CombineRedundantRows(set, MeasurementKind.PInjMw);
        Dictionary<int, double> qMeas = CombineRedundantRows(set, MeasurementKind.QInjMvar);

        int slackId = model.Buses.First(b => b.Type == BusType.Slack).Id;
        var buses = new List<Bus>(model.BusCount);
        var gens = new List<Generator>();
        foreach (Bus b in model.Buses)
        {
            double p = pMeas.GetValueOrDefault(b.Id);
            double q = qMeas.GetValueOrDefault(b.Id);
            if (b.Id == slackId)
            {
                buses.Add(b with { Pd = 0, Qd = 0 });
                // Keep the original slack source (voltage setpoint + generous var range). GR-527: EVERY unit at the
                // slack bus, as the model has them, so the set point follows the engines' shared rule (the last unit
                // at the bus) and the bus keeps its summed reactive range. Only the first unit used to be kept, so a
                // slack with two units took the first unit's Vg and its Q range alone.
                foreach (Generator g in model.Generators)
                    if (g.Bus == slackId) gens.Add(g);
                continue;
            }

            if (p >= PvThresholdMw)
            {
                // A measured generating site: PV holding the operating band, injecting P.
                buses.Add(b with { Type = BusType.Pv, Pd = 0, Qd = -q });
                double cap = Math.Max(Math.Abs(p), MinQCapabilityBaseMva);
                gens.Add(new Generator(b.Id, p, Vg: PvSetpointPu,
                    Qmin: -QCapabilityRatio * cap, Qmax: QCapabilityRatio * cap,
                    Name: $"measured injection @ {b.Id}"));
            }
            else
            {
                // Everything else: PQ with the measured net injection as (negative) load.
                buses.Add(b with { Type = BusType.Pq, Pd = -p, Qd = -q });
            }
        }

        // CopyWith keeps asset dynamics and protections too (R270).
        GridModel conditioned = model.CopyWith(
            buses: buses, generators: gens, name: $"{model.Name}-measured-injection");
        return solver.Solve(conditioned, new PowerFlowOptions { MaxIterations = PriorSolveMaxIterations, EnforceQLimits = true });
    }

    /// <summary>#183: each bus's value of the <paramref name="kind"/> rows: the row's own value when there is one, the
    /// inverse-variance weighted mean when several meter the same injection. Fails loud on a duplicate row whose sigma
    /// is not a finite number above 0 (it cannot be weighed).</summary>
    private static Dictionary<int, double> CombineRedundantRows(MeasurementSet set, MeasurementKind kind)
    {
        // Per bus, in set order: the first value, the row count, and the weight sums.
        var acc = new Dictionary<int, (double First, int Count, double SumW, double SumWz)>();
        foreach (Measurement m in set.Measurements)
        {
            if (m.Kind != kind) continue;
            acc.TryGetValue(m.Bus, out var a);
            double w = m.Sigma > 0.0 && double.IsFinite(m.Sigma) ? 1.0 / (m.Sigma * m.Sigma) : double.NaN;
            acc[m.Bus] = a.Count == 0 ? (m.Value, 1, w, w * m.Value) : (a.First, a.Count + 1, a.SumW + w, a.SumWz + w * m.Value);
        }
        var combined = new Dictionary<int, double>(acc.Count);
        foreach ((int bus, var a) in acc)
        {
            if (a.Count == 1) { combined[bus] = a.First; continue; }   // bit for bit the single meter
            if (!(a.SumW > 0.0) || !double.IsFinite(a.SumW) || !double.IsFinite(a.SumWz))
                throw new ArgumentException(
                    $"Measured-injection prior: bus {bus} has {a.Count} {kind} rows and at least one has a sigma that is not a "
                    + "finite number above 0, so the redundant meters cannot be weighed.", nameof(set));
            combined[bus] = a.SumWz / a.SumW;
        }
        return combined;
    }

    /// <summary>Replace the values of voltage-schedule prior rows (source
    /// <c>"pseudo-voltage-schedule"</c> / <c>"reference"</c>-adjacent flat guesses) with the
    /// SOLVED voltage profile, tightening sigma - the prior is now physics, not a flat number.
    /// Rows for buses the solve does not cover are left untouched.</summary>
    public static MeasurementSet ApplyVoltagePrior(MeasurementSet set, PowerFlowResult solved, double sigmaPu = DefaultVoltagePriorSigmaPu)
    {
        var vmByBus = new Dictionary<int, double>(solved.BusIds.Count);
        for (int i = 0; i < solved.BusIds.Count; i++) vmByBus[solved.BusIds[i]] = solved.Vm[i];

        var ms = set.Measurements.Select(m =>
            m.Kind == MeasurementKind.VmPu
            && string.Equals(m.Source, VoltageScheduleSource, StringComparison.Ordinal)
            && vmByBus.TryGetValue(m.Bus, out double vm)
                ? m with { Value = vm, Sigma = sigmaPu, Source = SolvedPriorSource }
                : m).ToArray();
        return set with { Measurements = ms };
    }
}
