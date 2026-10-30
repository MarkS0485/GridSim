using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Generation;
using Xunit;
using OldDash = GridSim.Tests.Ui.OldDashboard;
using OldTripStudy = GridSim.Tests.Ui.OldTrip.OldTripAtBus;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.6: the GENERATION tab's swing study for a map trip moved from the WPF window's TripAtBus into
/// <see cref="TripSwingStudy"/>, so the Avalonia trip can run it. The study's numbers must not change: for every unit a
/// map trip can pick in case9 and the GB reduced case (the shared <see cref="InfeedSelection.LargestOnline"/> rule, bus by
/// bus), and for the label fallbacks (a unit with no name, at a bus that is not in the model), the new code run on the
/// neutral dashboard and the old window code (verbatim in <c>UiTripSwingStudyOracle.cs</c>) run on the old dashboard
/// view-model (verbatim in <c>UiDashboardOracle.cs</c>) must leave the two dashboards in exactly the same state: every
/// GENERATION read-out, both traces point for point by their bits, the bands, the log lines (the TSGB compare and the
/// trip line with its RoCoF and nadir) and the TRIP alarm and its colour.
/// </summary>
public sealed class UiTripSwingStudyParityTests
{
    public static TheoryData<string> Cases => new() { "case9", "uk" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_map_trip_runs_the_old_swing_study_with_the_same_numbers_texts_and_alarm(string name)
    {
        SimHost host = UiTabsBatch2ParityTests.Host(name);
        var neu = new DashboardModel();
        var old = new OldDash.DashboardViewModel();
        SimFrame f = null!;
        for (int tick = 1; tick <= 4; tick++)
        {
            HostFrame hf = UiTabsBatch2ParityTests.Next(host);
            f = hf.Frame!;
            neu.Update(f, hf.Boundaries, hf.SolveMs);
            old.Update(f, hf.Boundaries, hf.SolveMs);
        }
        UiTabsBatch2ParityTests.AssertSameDashboard(old, neu, $"{name} before any trip");

        GridModel m = f.Model;
        int trips = 0;
        foreach (Bus bus in m.Buses)
        {
            (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(m, bus.Id);
            if (pick is null) continue;
            Generator gen = pick.Value.Unit;
            FrequencyResponse? resp = TripSwingStudy.Apply(neu, m, gen, bus.Id);
            OldTripStudy.StudyHalf(old, m, gen, bus.Id);
            trips++;
            Assert.Same(neu.BaselineResponse, resp);
            UiTabsBatch2ParityTests.AssertSameDashboard(old, neu, $"{name} trip at bus {bus.Id}");
            // The trip's own lines, as the old window wrote them: the log line second-newest is the TSGB compare, the
            // newest the trip with its RoCoF and nadir; the newest alarm the TRIP.
            Assert.Equal($"{neu.Clock}  {TripSwingStudy.LogLine(gen, resp)}", neu.Events[0]);
            Assert.Equal(TripSwingStudy.AlarmText(gen), neu.Alarms[0].Text);
            Assert.Equal(ColourRamps.Loading(1.1), neu.Alarms[0].Colour);
            if (trips == 3) break;
        }
        Assert.True(trips >= 1, $"{name}: no unit a map trip could pick");

        // The label fallbacks: no unit name, so the bus name; no unit name and no such bus, so "unit @ bus N".
        Generator largest = InfeedSelection.LargestOnline(m)!.Value.Unit;
        Generator nameless = largest with { Name = null };
        foreach (int busId in new[] { nameless.Bus, -12345 })
        {
            TripSwingStudy.Apply(neu, m, nameless, busId);
            OldTripStudy.StudyHalf(old, m, nameless, busId);
            UiTabsBatch2ParityTests.AssertSameDashboard(old, neu, $"{name} nameless unit at bus {busId}");
        }
        Assert.Equal($"unit @ bus -12345 -{Math.Abs(nameless.Pg):F0} MW", neu.TripBaseline);
    }

    [Fact]
    public void The_study_inputs_are_the_old_window_arithmetic()
    {
        // The three numbers the study is fed, against the old window's statements written out here (the oracle runs
        // them in place): the rotating-mass total less the unit's own share (floored at 1 MWs), the summed bus demand.
        foreach (string name in new[] { "case9", "uk" })
        {
            GridModel m = UiTabsBatch2ParityTests.Next(UiTabsBatch2ParityTests.Host(name)).Frame!.Model;
            foreach (Generator gen in m.Generators)
            {
                TripSwingStudy.Inputs i = TripSwingStudy.InputsFor(m, gen, gen.Bus);
                double remaining = Math.Max(1.0, m.SystemInertiaMwS - GridModel.RotatingInertiaMwS(gen));
                double demand = m.Buses.Sum(b => b.Pd);
                Assert.Equal(BitConverter.DoubleToInt64Bits(remaining), BitConverter.DoubleToInt64Bits(i.RemainingInertiaMwS));
                Assert.Equal(BitConverter.DoubleToInt64Bits(demand), BitConverter.DoubleToInt64Bits(i.DemandMw));
                Assert.Equal(BitConverter.DoubleToInt64Bits(gen.Pg), BitConverter.DoubleToInt64Bits(i.LostMw));
                Assert.Equal(gen.Name ?? m.Buses.First(b => b.Id == gen.Bus).Name ?? $"unit @ bus {gen.Bus}", i.Label);
            }
        }
    }

    [Fact]
    public void A_study_with_no_response_logs_the_short_line()
    {
        var gen = new Generator(Bus: 3, Pg: 123.4, Name: "Unit X");
        Assert.Equal(" TRIP Unit X (123 MW)", TripSwingStudy.LogLine(gen, null));
        Assert.Equal("TRIP Unit X -123 MW", TripSwingStudy.AlarmText(gen));
        GridModel m = UiTabsBatch2ParityTests.Next(UiTabsBatch2ParityTests.Host("case9")).Frame!.Model;
        Assert.Throws<ArgumentNullException>(() => TripSwingStudy.Apply(null!, m, gen, 3));
        Assert.Throws<ArgumentNullException>(() => TripSwingStudy.InputsFor(null!, gen, 3));
        Assert.Throws<ArgumentNullException>(() => TripSwingStudy.InputsFor(m, null!, 3));
    }
}
