using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Events;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T4 (fail-loud gate) - the distributed model must still pass the recorded-event credibility gate at the
/// aggregate. Because the COI-projected distributed response equals the lumped model (T1), it reproduces
/// the same event within the existing <see cref="ReplayValidator"/> tolerances (RoCoF +/-15%, nadir +/-0.15 Hz).
/// </summary>
public class DistributedReplayTests
{
    private static GridEvent SelfConsistent(double inertiaMwS, double demandMw, double lossMw)
    {
        var events = new[] { new FrequencyEvent(1.0, -lossMw) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, events);
        return new GridEvent(
            "synthetic", "self-consistent synthetic event", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    // A 2-domain model whose total inertia (sum H*MBase) matches the event's pre-event inertia.
    private static GridModel TwoDomainModel(double totalInertiaMwS)
    {
        double e1 = totalInertiaMwS * 0.7, e2 = totalInertiaMwS - e1;
        return new GridModel
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: e1 / 5.0, MBase: e1 / 5.0, H: 5.0, Type: "ccgt"),   // E = H*MBase = e1
                new(Bus: 2, Pg: e2 / 3.0, MBase: e2 / 3.0, H: 3.0, Type: "hydro"),  // E = e2
            },
            Branches = Array.Empty<Branch>(),
        };
    }

    [Fact]
    public void Distributed_coi_reproduces_a_recorded_event_within_replay_tolerance()
    {
        const double inertia = 200_000, demand = 35_000, loss = 1320;
        GridEvent ev = SelfConsistent(inertia, demand, loss);

        // Baseline: the lumped credibility gate reproduces it.
        ReplayResult lumped = ReplayValidator.Validate(ev);
        Assert.Equal(ReplayVerdict.Reproduced, lumped.Verdict);

        // Distributed COI on a 2-domain model with matching total inertia, device-free.
        var model = TwoDomainModel(inertia);
        var cfg = new DynamicsConfig
        {
            InertiaModel = InertiaModel.Distributed,
            DeviceRotationalInertiaMwS = 0.0,
            FastResponseMw = 0.0,
        };
        FrequencyResponse coi = new DistributedInertiaModel().Simulate(model, demand, ev.ToFrequencyEvents(), cfg);

        // COI equals the lumped simulated response (T1), so it reproduces the event too.
        Assert.Equal(lumped.Simulated.InitialRocofHzPerS, coi.InitialRocofHzPerS, 6);
        Assert.Equal(lumped.Simulated.NadirHz, coi.NadirHz, 6);

        // Explicit tolerance check of the distributed aggregate against the recorded outcome.
        ReplayTolerance tol = ReplayTolerance.Default;
        double recRocof = ev.Outcome.InitialRocofHzPerS;
        double rocofRelErr = Math.Abs(Math.Abs(coi.InitialRocofHzPerS) - Math.Abs(recRocof)) / Math.Abs(recRocof);
        double nadirErr = Math.Abs(coi.NadirHz - ev.Outcome.NadirHz);
        Assert.True(rocofRelErr <= tol.RocofRelTol, $"RoCoF rel err {rocofRelErr:P1}");
        Assert.True(nadirErr <= tol.NadirAbsToleranceHz, $"nadir err {nadirErr:F3} Hz");
    }
}
