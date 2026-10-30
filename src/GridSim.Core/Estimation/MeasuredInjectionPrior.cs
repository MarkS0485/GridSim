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
/// (duplicates summed; unmeasured buses default to zero injection). Buses injecting more than
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
        var pMeas = new Dictionary<int, double>();
        var qMeas = new Dictionary<int, double>();
        foreach (Measurement m in set.Measurements)
        {
            if (m.Kind == MeasurementKind.PInjMw)
                pMeas[m.Bus] = pMeas.GetValueOrDefault(m.Bus) + m.Value;
            else if (m.Kind == MeasurementKind.QInjMvar)
                qMeas[m.Bus] = qMeas.GetValueOrDefault(m.Bus) + m.Value;
        }

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
