using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R303: trip controls were offered on replay and bridge sessions, where SimSession.Trip silently does nothing.
/// <see cref="SimSession.CanTrip"/> now gates every trip affordance (the transport button and the map hint are
/// hidden, a map trip and a /ws/sim trip say it is not available).
/// </summary>
public sealed class TripAvailabilityTests : IDisposable
{
    private readonly string _dir;

    public TripAvailabilityTests()
    {
        // A two-frame case9 replay directory.
        _dir = Path.Combine(Path.GetTempPath(), $"gridsim-tripavail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int tick in new[] { 1, 2 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(_dir, "exports", $"slice-{tick:D9}.json"), model, r, trace, meta);
        }
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public async Task A_replay_session_cannot_be_tripped_and_says_so()
    {
        SimSession s = SimSession.CreateReplay("t", "run", "Replay run", PresolvedReplay.Load(_dir));
        try
        {
            Assert.False(s.CanTrip);
            SimSession.TripOutcome outcome = s.TripLargestInfeed(null);
            Assert.False(outcome.Tripped);
            Assert.Equal(0.0, outcome.LostMw);
            Assert.Contains("not available on this feed", outcome.Message);
        }
        finally
        {
            await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_live_session_can_be_tripped()
    {
        SimSession s = SimSession.CreateLive("t", "case9", "IEEE 9", IeeeCases.Case9(), holdSteady: true);
        try
        {
            Assert.True(s.CanTrip);
        }
        finally
        {
            await s.DisposeAsync();
        }
    }
}
