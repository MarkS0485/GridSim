namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>A load point with its bus resolved against the case.</summary>
internal sealed record ResolvedLoadPoint(string Id, int BusId, int BusPos, int Customers, double AverageLoadMw);

/// <summary>Everything the enumeration and the sampler share: the failable components in model order, the event plan of
/// each, and the resolved load points.</summary>
internal sealed record PreparedNetworkStudy(IReadOnlyList<FailureComponent> Components, IReadOnlyList<EventPlan> Plans,
    IReadOnlyList<ResolvedLoadPoint> LoadPoints, double? DefaultSwitchingHours, double HoursPerYear);

/// <summary>
/// RC4 R4-13 deterministic first-order outage enumeration: each component, in model order, fails alone (independent
/// failures, overlapping outages neglected: the first-order approximation of the RC3 study); the event plan says which
/// load points it interrupts and for how long each waits; the load-point figures accumulate in component order, which
/// fixes the floating-point order, and CustomerIndices turns them into system indices. Second-order (overlapping)
/// enumeration is deferred.
/// </summary>
internal static class OutageEnumeration
{
    public static (IReadOnlyList<LoadPointReliability> Points, IReadOnlyList<MissingRateData> Missing, IReadOnlyList<NetworkOutageEvent> Events)
        Run(PreparedNetworkStudy p)
    {
        var missing = new List<MissingRateData>();
        var missingIndex = new Dictionary<(string, string), List<string>>();
        void Missing(string component, string field, string loadPoint)
        {
            if (!missingIndex.TryGetValue((component, field), out var list))
            {
                list = [];
                missingIndex[(component, field)] = list;
                missing.Add(new MissingRateData(component, field, list));
            }
            if (!list.Contains(loadPoint)) list.Add(loadPoint);
        }

        var points = new List<LoadPointReliability>(p.LoadPoints.Count);
        for (int j = 0; j < p.LoadPoints.Count; j++)
        {
            ResolvedLoadPoint lp = p.LoadPoints[j];
            var exposures = new List<ReliabilityExposure>();
            double lambda = 0, unavailability = 0;
            bool lambdaKnown = true, uKnown = true;
            for (int ci = 0; ci < p.Components.Count; ci++)
            {
                LoadRestoration? lr = p.Plans[ci].PerLoad[j];
                if (lr is null) continue;
                FailureComponent comp = p.Components[ci];
                string restoration;
                double? duration;
                if (lr.Class == RestorationClass.Repair)
                {
                    restoration = "Repair";
                    duration = comp.RepairHours;
                    if (duration is null) Missing(comp.Id, "repairHours", lp.Id);
                }
                else
                {
                    restoration = lr.Class == RestorationClass.Switching ? "Switching" : "BackFeed";
                    duration = comp.SwitchingHours ?? p.DefaultSwitchingHours;
                    if (duration is null) Missing("(study)", "switchingHours", lp.Id);
                }
                double? rate = comp.FailuresPerYear;
                if (rate is null) Missing(comp.Id, comp.RateIssue ?? "failuresPerYear", lp.Id);
                double? u = rate is double lr1 && duration is double d ? lr1 * d : null;
                exposures.Add(new ReliabilityExposure(comp.Id, p.Plans[ci].ClearedBy, restoration, rate, duration, u));
                if (rate is double rr) lambda += rr; else lambdaKnown = false;
                if (u is double uu) unavailability += uu; else uKnown = false;
            }
            double? lam = lambdaKnown ? lambda : null;
            double? un = uKnown && lambdaKnown ? unavailability : null;
            double? perInterruption = lam is double l2 && un is double u2 && l2 > 0 ? u2 / l2 : null;
            double? ens = un is double u3 ? lp.AverageLoadMw * u3 : null;
            points.Add(new LoadPointReliability(lp.Id, lp.Customers, lp.AverageLoadMw, lam, un, perInterruption, ens, exposures));
        }

        var events = new List<NetworkOutageEvent>(p.Components.Count);
        for (int ci = 0; ci < p.Components.Count; ci++)
        {
            FailureComponent comp = p.Components[ci];
            EventPlan plan = p.Plans[ci];
            double? s = comp.SwitchingHours ?? p.DefaultSwitchingHours;
            var loads = new List<NetworkLoadOutcome>();
            for (int j = 0; j < p.LoadPoints.Count; j++)
            {
                LoadRestoration? lr = plan.PerLoad[j];
                if (lr is null) continue;
                loads.Add(new NetworkLoadOutcome(p.LoadPoints[j].Id,
                    lr.Class switch { RestorationClass.Repair => "Repair", RestorationClass.Switching => "Switching", _ => "BackFeed" },
                    lr.Class == RestorationClass.Repair ? comp.RepairHours : s, lr.Tie, lr.Reason));
            }
            events.Add(new NetworkOutageEvent(comp.Id, comp.Branch, comp.Name, comp.Group, comp.FailuresPerYear, comp.RepairHours, s,
                plan.ClearedBy, plan.ClearingBranch, plan.IsolatingSwitch, plan.Opened, plan.InterruptedBuses, loads, plan.Path, plan.Ac, plan.AcNote));
        }
        return (points, missing, events);
    }
}
