using System;
using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Storage carried through a time series of AC power flows (<see cref="StorageTimeSeries"/>) and scheduled by
/// rule or linear programme (<see cref="StorageLpScheduler"/>): SoC persists between steps inside its window, the
/// network sees what the chemistry delivered (the slack is relieved by it), a linear-programme arbitrage plan
/// survives execution through the chemistry, and the run's energy books close with a plausible round trip.
/// </summary>
public class StorageTimeSeriesTests
{
    private static readonly GridModel Case = IeeeCases.Case118();
    private const int LoadBus = 59;        // a heavy load bus on IEEE 118

    private static StorageSite Site(ChemistryParameters chem, double mw, double mwh, double soc, IStorageDispatchPolicy policy) =>
        new("BESS", LoadBus, mw, PackModel.Sized(chem, mwh * 1e6, 1000, soc, ratedPowerW: mw * 1e6), policy);

    private static StorageTimeSeriesSpec Day(int steps, Func<int, double>? price = null) =>
        new(steps, 24.0 / steps, s => LoadProfile.DailyFactor(24.0 * s / steps), price);

    private static double DemandAt(double factor) => Case.Buses.Sum(b => b.Pd) * factor;

    [Fact]
    public void Peak_shaving_day_relieves_the_slack_and_keeps_soc_in_its_window()
    {
        double high = DemandAt(0.90), low = DemandAt(0.70);
        StorageSite bess = Site(BatteryChemistryLibrary.Lfp, 100, 200, 0.5, new PeakShavePolicy(high, low));
        StorageSite idle = Site(BatteryChemistryLibrary.Lfp, 100, 200, 0.5, new IdlePolicy());
        var solver = new NewtonRaphsonPowerFlow();
        StorageTimeSeriesResult run = StorageTimeSeries.Run(Case, new[] { bess }, Day(48), solver);
        StorageTimeSeriesResult baseline = StorageTimeSeries.Run(Case, new[] { idle }, Day(48), solver);

        Assert.All(run.Steps, s => Assert.True(s.Converged, $"step {s.Step} did not converge"));
        ChemistryParameters c = BatteryChemistryLibrary.Lfp;
        // Dispatch never crosses the BMS window; below the floor only self-discharge acts (a day's worth).
        Assert.All(run.Steps, s => Assert.InRange(s.Sites[0].Soc, c.SocMin - c.SelfDischargePerMonth / 30.0, c.SocMax + 1e-9));

        // It discharges only at the peak and charges only in the trough.
        Assert.All(run.Steps, s =>
        {
            double d = s.Sites[0].DeliveredMw;
            if (d > 1) Assert.True(s.DemandMw > high);
            if (d < -1) Assert.True(s.DemandMw < low);
        });
        Assert.Contains(run.Steps, s => s.Sites[0].DeliveredMw > 50);
        Assert.Contains(run.Steps, s => s.Sites[0].DeliveredMw < -50);

        // The slack is relieved by what the battery delivered (to within the change in losses).
        StorageStepRecord peak = run.Steps.OrderByDescending(s => s.Sites[0].DeliveredMw).First();
        StorageStepRecord same = baseline.Steps[peak.Step];
        double relief = same.SlackMw - peak.SlackMw;
        double delivered = peak.Sites[0].DeliveredMw - peak.Sites[0].AuxiliaryMw;
        Assert.True(Math.Abs(relief - delivered) < 0.05 * delivered, $"slack relief {relief:F1} MW vs delivered {delivered:F1} MW");
    }

    [Fact]
    public void Lp_arbitrage_plan_buys_low_sells_high_and_survives_execution_through_the_chemistry()
    {
        double[] prices = Enumerable.Range(0, 24).Select(h => h < 12 ? 20.0 : 100.0).ToArray();
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 100e6, 1000, 0.5, ratedPowerW: 50e6);
        StorageEnvelope env = StorageEnvelope.FromPack(pack, 50, 0.98, 1.0);
        StorageLpScheduler.Plan plan = StorageLpScheduler.Solve(env, prices, 1.0);
        Assert.Equal(LpStatus.Optimal, plan.Status);
        Assert.True(plan.NetMw.Take(12).All(p => p <= 1e-6), "no discharge while cheap");
        Assert.True(plan.NetMw.Skip(12).All(p => p >= -1e-6), "no charge while dear");
        Assert.True(plan.ObjectiveGbp > 0);

        var site = new StorageSite("BESS", LoadBus, 50, pack, new SchedulePolicy(plan.NetMw, "lp"));
        StorageTimeSeriesResult run = StorageTimeSeries.Run(Case, new[] { site },
            new StorageTimeSeriesSpec(24, 1.0, _ => 1.0, s => prices[s]), new NewtonRaphsonPowerFlow());
        SiteSummary sum = run.Sites[0];
        Assert.True(sum.RevenueGbp > 0);
        // Executed within 10 % of plan: the linear envelope is a fair description of the chemistry here.
        Assert.True(Math.Abs(sum.RevenueGbp - plan.ObjectiveGbp) < 0.10 * plan.ObjectiveGbp,
            $"executed £{sum.RevenueGbp:F0} vs planned £{plan.ObjectiveGbp:F0}");
        double tracking = run.Steps.Max(s => Math.Abs(s.Sites[0].DeliveredMw - plan.NetMw[s.Step]));
        Assert.True(tracking < 5.0, $"worst tracking error {tracking:F2} MW");
    }

    [Fact]
    public void Energy_books_close_with_a_plausible_round_trip_and_rainflow_counts_the_daily_cycle()
    {
        double high = DemandAt(0.90), low = DemandAt(0.70);
        StorageSite bess = Site(BatteryChemistryLibrary.Lfp, 100, 200, 0.5, new PeakShavePolicy(high, low));
        StorageTimeSeriesResult run = StorageTimeSeries.Run(Case, new[] { bess }, Day(48), new NewtonRaphsonPowerFlow());
        SiteSummary s = run.Sites[0];
        Assert.InRange(s.RoundTripEfficiency, 0.85, 0.97);          // converter both ways + LFP resistive loss
        Assert.True(s.EnergyOutMwh < s.EnergyInMwh + Math.Max(0, -s.StoredChangeMwh) + 1e-6, "no energy from nowhere");
        (double depth, _) = s.Cycles.OrderByDescending(c => c.Depth).First();
        Assert.InRange(depth, 0.2, 0.95);                            // one deep daily swing
        Assert.True(s.RainflowCycleFade > 0 && s.CalendarFade > 0);
    }
}
