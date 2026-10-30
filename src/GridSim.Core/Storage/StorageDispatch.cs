using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Numerics;

namespace GridSim.Core.Storage;

/// <summary>What a dispatch policy sees at one time-series step.</summary>
/// <param name="Step">Step index.</param>
/// <param name="Hour">Hour of day at the start of the step.</param>
/// <param name="StepHours">Step length (h).</param>
/// <param name="SystemDemandMw">Total system demand this step (MW).</param>
/// <param name="PriceGbpPerMwh">Energy price this step (£/MWh), NaN when no price series is given.</param>
/// <param name="Soc">The unit's state of charge now.</param>
/// <param name="RatedMw">The unit's rated AC power (MW).</param>
public readonly record struct DispatchContext(
    int Step, double Hour, double StepHours, double SystemDemandMw, double PriceGbpPerMwh, double Soc, double RatedMw);

/// <summary>A storage dispatch rule: the AC power to request this step (MW, discharge positive). The chemistry
/// model then decides what is actually delivered.</summary>
public interface IStorageDispatchPolicy
{
    string Name { get; }
    double Request(in DispatchContext ctx);
}

/// <summary>Idle: the unit holds its state (it still self-discharges and draws standby power).</summary>
public sealed class IdlePolicy : IStorageDispatchPolicy
{
    public string Name => "idle";
    public double Request(in DispatchContext ctx) => 0.0;
}

/// <summary>Peak shaving: discharge the demand above <see cref="HighMw"/>, recharge into the headroom below
/// <see cref="LowMw"/>, each capped at the unit's rating.</summary>
public sealed record PeakShavePolicy(double HighMw, double LowMw) : IStorageDispatchPolicy
{
    public string Name => "peak-shave";
    public double Request(in DispatchContext ctx)
    {
        if (ctx.SystemDemandMw > HighMw) return Math.Min(ctx.RatedMw, ctx.SystemDemandMw - HighMw);
        if (ctx.SystemDemandMw < LowMw) return -Math.Min(ctx.RatedMw, LowMw - ctx.SystemDemandMw);
        return 0.0;
    }
}

/// <summary>Price thresholds: charge at full rating below <see cref="BuyBelow"/>, discharge above
/// <see cref="SellAbove"/> (£/MWh).</summary>
public sealed record PriceThresholdPolicy(double BuyBelow, double SellAbove) : IStorageDispatchPolicy
{
    public string Name => "price-threshold";
    public double Request(in DispatchContext ctx)
    {
        if (double.IsNaN(ctx.PriceGbpPerMwh)) return 0.0;
        if (ctx.PriceGbpPerMwh >= SellAbove) return ctx.RatedMw;
        if (ctx.PriceGbpPerMwh <= BuyBelow) return -ctx.RatedMw;
        return 0.0;
    }
}

/// <summary>Response holding: keep the state of charge near <see cref="TargetSoc"/> so the unit stays ready to
/// deliver a frequency-response service both ways; outside the dead band it trickles back at
/// <see cref="RestoreFraction"/> of its rating.</summary>
public sealed record ResponseHoldingPolicy(double TargetSoc = 0.5, double Band = 0.05, double RestoreFraction = 0.2)
    : IStorageDispatchPolicy
{
    public string Name => "response-holding";
    public double Request(in DispatchContext ctx)
    {
        if (ctx.Soc > TargetSoc + Band) return RestoreFraction * ctx.RatedMw;
        if (ctx.Soc < TargetSoc - Band) return -RestoreFraction * ctx.RatedMw;
        return 0.0;
    }
}

/// <summary>A fixed schedule (MW per step) - e.g. the output of <see cref="StorageLpScheduler"/>.</summary>
public sealed record SchedulePolicy(IReadOnlyList<double> Mw, string Label = "schedule") : IStorageDispatchPolicy
{
    public string Name => Label;
    public double Request(in DispatchContext ctx) => ctx.Step < Mw.Count ? Mw[ctx.Step] : 0.0;
}

/// <summary>The linearised envelope a scheduler plans against: power limits, the usable energy window and the
/// one-way efficiencies (AC to stored energy and back), derived from a pack by <see cref="FromPack"/>.</summary>
public sealed record StorageEnvelope(
    double MaxDischargeMw, double MaxChargeMw, double EnergyMinMwh, double EnergyMaxMwh, double EnergyNowMwh,
    double DischargeEfficiency, double ChargeEfficiency)
{
    // ---- Named constants (October 2026 review, WS13 R198). Values unchanged.

    /// <summary>The pack's power capability is planned at this state of charge (mid-window), not the start SoC.</summary>
    public const double PlanningSoc = 0.5;

    /// <summary>Cap (s) on the state-of-power horizon used for one planning step (30 minutes).</summary>
    public const double MaxSopHorizonSeconds = 1800.0;

    /// <summary>Substeps of the state-of-power simulation behind an envelope.</summary>
    public const int SopSubsteps = 20;

    /// <summary>Floor (A) on the rated per-cell current used for the cell efficiency estimate.</summary>
    public const double MinRatedCellCurrentA = 1e-9;

    /// <summary>Floor (V) on the cell EMF where it is used as a divisor.</summary>
    public const double MinCellEmfV = 1e-6;

    /// <summary>Lower clamp on the cell's terminal/OCV efficiency at rated current.</summary>
    public const double MinCellEfficiency = 0.5;

    /// <summary>
    /// The envelope of <paramref name="pack"/> behind a converter of efficiency <paramref name="converterEfficiency"/>
    /// and AC rating <paramref name="ratedMw"/>. Power limits: the rating, capped by the pack's sustained state of
    /// power over one step. Energy window: the SoC window at nominal voltage. Efficiencies: converter times the
    /// cell's terminal/OCV ratio at rated current (plus coulombic efficiency on charge) - the chemistry's own
    /// resistive loss at the planned rate.
    /// </summary>
    public static StorageEnvelope FromPack(PackModel pack, double ratedMw, double converterEfficiency, double stepHours)
    {
        ChemistryParameters c = pack.Chemistry;
        // Power capability is planned at mid state of charge (a representative operating point), not at the
        // start state - an empty or full start would otherwise shrink the whole plan's power limits.
        var mid = new PackModel(c, pack.SeriesCells, pack.ParallelStrings, PlanningSoc, pack.AmbientC, pack.RatedPowerW, trackAgeing: false);
        (double sopDis, double sopChg) = mid.StateOfPower(Math.Min(stepHours * 3600.0, MaxSopHorizonSeconds), substeps: SopSubsteps);
        double eta = converterEfficiency;
        double maxDis = Math.Min(ratedMw, sopDis * eta / 1e6);
        double maxChg = Math.Min(ratedMw, sopChg / eta / 1e6);

        double wh = pack.Cell.CapacityAh * pack.ParallelStrings * pack.SeriesCells * c.NominalVoltage;   // Wh per unit SoC
        double e = mid.Cell.InternalEmf, r = mid.Cell.R0;
        double iRated = Math.Max(MinRatedCellCurrentA, maxDis * 1e6 / eta / (pack.SeriesCells * pack.ParallelStrings) / Math.Max(e, MinCellEmfV));
        double cellEta = Math.Clamp(1.0 - iRated * r / e, MinCellEfficiency, 1.0);
        return new StorageEnvelope(
            maxDis, maxChg, c.SocMin * wh / 1e6, c.SocMax * wh / 1e6, pack.Soc * wh / 1e6,
            eta * cellEta, eta * cellEta * c.CoulombicEfficiency);
    }
}

/// <summary>
/// Inter-temporal storage scheduling as a linear programme over a price series: choose discharge
/// <c>d_t</c> and charge <c>c_t</c> (AC MW) and stored energy <c>e_t</c> (MWh) to
/// <code>
///   minimise   sum_t [ -price_t (d_t - c_t) + w (d_t + c_t) ] dt
///   subject to e_t = e_{t-1} - d_t dt / eta_d + c_t dt eta_c
///              0 &lt;= d_t &lt;= Dmax,  0 &lt;= c_t &lt;= Cmax,  Emin &lt;= e_t &lt;= Emax,  e_T &gt;= e_end
/// </code>
/// where <c>w</c> is a degradation cost per MWh of throughput. Solved by <see cref="BoundedSimplex"/>. The
/// plan is linear; executing it through the chemistry pack shows where reality differs.
/// </summary>
public static class StorageLpScheduler
{
    public sealed record Plan(double[] NetMw, double[] EnergyMwh, double ObjectiveGbp, LpStatus Status);

    public static Plan Solve(StorageEnvelope env, IReadOnlyList<double> priceGbpPerMwh, double stepHours,
        double degradationGbpPerMwh = 0.0, double? endEnergyMwh = null)
        => Solve(env, priceGbpPerMwh, stepHours, degradationGbpPerMwh, endEnergyMwh, BoundedSimplex.NearTiePricingMargin);

    /// <summary>
    /// N262 (October 2026, ruled: the N240 near-tie rule for every BoundedSimplex user): the schedule LP solved at an
    /// explicit pricing tie margin. Production always passes <see cref="BoundedSimplex.NearTiePricingMargin"/> (relative
    /// 1e-10): with a flat or repeating price many schedules are optimal, and the strict rule let the last bit of a cost
    /// coefficient choose between periods that are tied in exact arithmetic; with the margin the lowest-index tied column
    /// enters. Tests pass 0 (the strict rule every schedule used before N262) to prove that a schedule whose pivot path
    /// meets no near-tie is bit-identical, and to measure what moves where one does.
    /// </summary>
    internal static Plan Solve(StorageEnvelope env, IReadOnlyList<double> priceGbpPerMwh, double stepHours,
        double degradationGbpPerMwh, double? endEnergyMwh, double pricingTieMargin)
    {
        int T = priceGbpPerMwh.Count;
        if (T == 0) return new Plan(Array.Empty<double>(), Array.Empty<double>(), 0, LpStatus.Optimal);
        int n = 3 * T;                      // [d_0..d_T-1, c_0..c_T-1, e_0..e_T-1]
        int D(int t) => t;
        int C(int t) => T + t;
        int E(int t) => 2 * T + t;
        var a = new double[T, n];
        var b = new double[T];
        var cost = new double[n];
        var lo = new double[n];
        var hi = new double[n];
        double dt = stepHours;
        for (int t = 0; t < T; t++)
        {
            // e_t - e_{t-1} + d_t dt/eta_d - c_t dt eta_c = 0   (e_{-1} = energy now, moved to the rhs)
            a[t, E(t)] = 1.0;
            if (t > 0) a[t, E(t - 1)] = -1.0; else b[t] = env.EnergyNowMwh;
            a[t, D(t)] = dt / env.DischargeEfficiency;
            a[t, C(t)] = -dt * env.ChargeEfficiency;

            cost[D(t)] = (-priceGbpPerMwh[t] + degradationGbpPerMwh) * dt;
            cost[C(t)] = (priceGbpPerMwh[t] + degradationGbpPerMwh) * dt;
            hi[D(t)] = env.MaxDischargeMw;
            hi[C(t)] = env.MaxChargeMw;
            lo[E(t)] = env.EnergyMinMwh;
            hi[E(t)] = env.EnergyMaxMwh;
        }
        // GR-626 (October 2026): an explicit end-energy request outside the store's energy window (or not a number) is
        // refused; it used to be clamped into the window without a word, so a plan asked to end at 120 MWh of a 100 MWh
        // store ended at 100 and read as meeting the request. The default (end where the store is now) is still clamped,
        // because a store that starts outside its window cannot be asked to end there.
        if (endEnergyMwh is double asked && !(asked >= env.EnergyMinMwh && asked <= env.EnergyMaxMwh))
            throw new ArgumentOutOfRangeException(nameof(endEnergyMwh), asked, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The end-energy request {asked} MWh is outside the store's energy window [{env.EnergyMinMwh}, {env.EnergyMaxMwh}] MWh."));
        lo[E(T - 1)] = Math.Clamp(endEnergyMwh ?? env.EnergyNowMwh, env.EnergyMinMwh, env.EnergyMaxMwh);

        (LpStatus status, double[] x, double obj) = BoundedSimplex.Solve(a, b, cost, lo, hi, pricingTieMargin: pricingTieMargin);
        var net = new double[T];
        var energy = new double[T];
        for (int t = 0; t < T; t++) { net[t] = x[D(t)] - x[C(t)]; energy[t] = x[E(t)]; }
        return new Plan(net, energy, -obj, status);
    }
}
