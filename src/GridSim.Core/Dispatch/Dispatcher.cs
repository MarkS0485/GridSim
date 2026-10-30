using GridSim.Core.Model;

namespace GridSim.Core.Dispatch;

/// <summary>
/// Turns capacities into a balanced dispatch. Non-slack generators run at their
/// chosen output (from <c>OutputMw</c>, or capacity x capacity-factor); the slack
/// generator  -  the marginal plant, gas in GB  -  is set to make total generation
/// meet demand plus an allowance for losses. The power flow then balances it
/// exactly (the slack bus absorbs the true losses), but starting balanced gives a
/// clean, fast, "natural" steady state and lets us sanity-check the slack stays
/// within its capacity: <see cref="BalanceDetailed"/> reports a marginal target outside the
/// marginal plant's [Pmin, Pmax] (R076; the check was promised here but never made).
/// Multi-slack models: only the FIRST slack bus (in bus order) carries the marginal plant; any other
/// slack bus keeps its generators at their chosen outputs, and the result says how many slack buses
/// there were.
/// </summary>
public static class Dispatcher
{
    // N345 (CF3, October 2026): the arms of DefaultCapacityFactor below, named so the v2 kind specs cite each
    // value by symbol instead of quoting the source line. Each constant is the literal it replaces, so no number moves.
    internal const double DefaultCapacityFactorNuclear = 0.90;   // "nuclear"
    internal const double DefaultCapacityFactorBiomass = 0.70;   // "biomass"
    internal const double DefaultCapacityFactorHydro = 0.40;   // "hydro"
    internal const double DefaultCapacityFactorPumped = 0.20;   // "pumped" or "pumpedstorage" or "storage" or "battery"
    internal const double DefaultCapacityFactorWindOffshore = 0.45;   // "wind_offshore" or "offshore"
    internal const double DefaultCapacityFactorWindOnshore = 0.30;   // "wind_onshore" or "onshore" or "wind"
    internal const double DefaultCapacityFactorSolar = 0.15;   // "solar" or "pv"
    internal const double DefaultCapacityFactorInterconnector = 0.60;   // "interconnector" or "hvdc"
    internal const double DefaultCapacityFactorCcgt = 0.50;   // "ccgt" or "gas"
    internal const double DefaultCapacityFactorOcgt = 0.05;   // "ocgt" or "peaker"
    internal const double DefaultCapacityFactorCoal = 0.0;   // "coal"
    internal const double DefaultCapacityFactorOther = 0.40;   // _

    /// <summary>Representative GB capacity factors for a typical operating snapshot.</summary>
    /// <remarks>Both spellings of the compound technology names are matched: the underscore form (older callers) and
    /// the hyphen form, which is the ONLY form the case schemas allow (their type pattern admits no underscore) and the
    /// one the JSON loader itself recommends. Before, a hyphenated <c>wind-onshore</c> / <c>wind-offshore</c> unit with
    /// no output of its own fell through to the 0.40 "other" factor instead of 0.30 / 0.45.</remarks>
    public static double DefaultCapacityFactor(string type) => type.ToLowerInvariant() switch
    {
        "nuclear" => DefaultCapacityFactorNuclear,
        "biomass" => DefaultCapacityFactorBiomass,
        "hydro" => DefaultCapacityFactorHydro,
        "pumped" or "pumpedstorage" or "pumped-storage" or "pumped_storage" or "storage" or "battery" => DefaultCapacityFactorPumped,
        "wind_offshore" or "wind-offshore" or "offshore" => DefaultCapacityFactorWindOffshore,
        "wind_onshore" or "wind-onshore" or "onshore" or "wind" => DefaultCapacityFactorWindOnshore,
        "solar" or "pv" => DefaultCapacityFactorSolar,
        "interconnector" or "hvdc" => DefaultCapacityFactorInterconnector,
        "ccgt" or "gas" => DefaultCapacityFactorCcgt,
        "ocgt" or "peaker" => DefaultCapacityFactorOcgt,
        "coal" => DefaultCapacityFactorCoal,
        _ => DefaultCapacityFactorOther,
    };

    /// <summary>The balanced model plus what the balance had to assume (R076).</summary>
    /// <param name="Model">The model with the marginal plant set to the slack target.</param>
    /// <param name="SlackTargetMw">The marginal plant's target output (MW), or NaN when the slack bus has no generator.</param>
    /// <param name="SlackCapacityNote">Null when the target is inside the marginal plant's [Pmin, Pmax]; otherwise a
    /// plain-English note of the shortfall or excess. Never silently clamped: the power flow decides what is feasible.</param>
    /// <param name="SlackBusCount">How many slack buses the model has (only the first carries the marginal plant).</param>
    public sealed record BalanceResult(GridModel Model, double SlackTargetMw, string? SlackCapacityNote, int SlackBusCount);

    /// <summary>Balance and return the model only (unchanged behaviour). See <see cref="BalanceDetailed"/>.</summary>
    public static GridModel Balance(GridModel model, double lossAllowance = 0.02) => BalanceDetailed(model, lossAllowance).Model;

    /// <summary>
    /// Balance as <see cref="Balance"/> does (bit-identical model) and also report whether the marginal target is
    /// within the marginal plant's capacity, and how many slack buses there are.
    /// </summary>
    public static BalanceResult BalanceDetailed(GridModel model, double lossAllowance = 0.02)
    {
        int slackBusId = model.Buses.First(b => b.Type == BusType.Slack).Id;
        double totalDemand = model.Buses.Sum(b => b.Pd);

        // Exactly one generator on the slack bus is the marginal plant that absorbs the
        // balance; every other generator  -  including any *additional* generators that
        // happen to sit on the slack bus (real GDA/synthetic buses can carry several)  -
        // runs at its chosen output and is counted once. Picking the first slack-bus
        // generator keeps the single-slack-generator case bit-for-bit unchanged.
        int marginalIdx = -1;
        for (int i = 0; i < model.Generators.Count; i++)
        {
            if (model.Generators[i].Bus == slackBusId) { marginalIdx = i; break; }
        }

        double fixedGen = 0;
        for (int i = 0; i < model.Generators.Count; i++)
        {
            if (i != marginalIdx) fixedGen += model.Generators[i].Pg;
        }

        double rawTarget = totalDemand * (1 + lossAllowance) - fixedGen;
        double slackTarget = Math.Max(0, rawTarget);

        var gens = model.Generators.ToList();
        if (marginalIdx >= 0)
            gens[marginalIdx] = gens[marginalIdx] with { Pg = slackTarget };

        string? note = null;
        if (marginalIdx >= 0)
        {
            Generator g = model.Generators[marginalIdx];
            if (slackTarget > g.Pmax)
                note = FormattableString.Invariant($"slack target {slackTarget:F1} MW exceeds the marginal plant's Pmax {g.Pmax:F1} MW at bus {slackBusId} ") +
                       FormattableString.Invariant($"(short by {slackTarget - g.Pmax:F1} MW).");
            else if (slackTarget < g.Pmin)
                note = FormattableString.Invariant($"slack target {slackTarget:F1} MW is below the marginal plant's Pmin {g.Pmin:F1} MW at bus {slackBusId} ") +
                       FormattableString.Invariant($"(excess generation {g.Pmin - slackTarget:F1} MW).");
            // A generation surplus is clamped to a zero target; with Pmin 0 (the common case) that used to say nothing,
            // and the power flow then drove the slack strongly negative with no warning.
            else if (rawTarget < 0)
                note = FormattableString.Invariant($"fixed generation exceeds demand plus losses by {-rawTarget:F1} MW; the slack target at bus {slackBusId} ") +
                       "is held at 0 MW and the slack will absorb the surplus.";
        }
        else note = $"slack bus {slackBusId} has no generator: nothing absorbs the balance.";
        int slackBuses = model.Buses.Count(b => b.Type == BusType.Slack);

        return new BalanceResult(Clone(model, generators: gens), marginalIdx >= 0 ? slackTarget : double.NaN, note, slackBuses);
    }

    // A copy with the in-service generators and/or branches replaced. It goes through GridModel.CopyWith (F16) so the
    // model's OUT-OF-SERVICE generators are carried across: the balance above works on the in-service fleet only
    // (model.Generators), and rebuilding the model by hand from that list used to be where a JSON case's
    // "inService": false units would have been lost. Every property is the same one the hand-built copy set.
    internal static GridModel Clone(
        GridModel m,
        IReadOnlyList<Generator>? generators = null,
        IReadOnlyList<Branch>? branches = null) => m.CopyWith(generators: generators, branches: branches);
}
