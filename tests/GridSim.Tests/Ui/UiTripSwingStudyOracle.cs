using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Tests.Ui.OldDashboard;

// Test oracle for UiTripSwingStudyParityTests (UI programme Phase 1.6). NOT production code.
//
// Below, between the markers, is the swing-study half of the WPF window's TripAtBus exactly as it was before it moved
// to src/GridSim.Ui/Tabs/Generation/TripSwingStudy.cs (src/GridSim.Wpf/MainWindow.xaml.cs at 2.0.25: from the comment
// "Also drive the Generation tab's analytical trace" to the TRIP alarm). The text was copied mechanically from git by a
// script; it runs against the old dashboard view-model (verbatim in UiDashboardOracle.cs) and its Palette shim. The
// only lines added are the method around it and the bus lookup TripAtBus made earlier in the same method, marked
// "ORACLE:".

namespace GridSim.Tests.Ui.OldTrip;

public static class OldTripAtBus
{
    /// <summary>The old window's swing-study half for <paramref name="gen"/> picked at <paramref name="busId"/> of
    /// <paramref name="m"/>, run on the old dashboard <paramref name="_vm"/>.</summary>
#pragma warning disable IDE1006 // ORACLE: the parameter keeps the window's field name so the copied lines compile unchanged
    public static void StudyHalf(DashboardViewModel _vm, GridModel m, Generator gen, int busId)
#pragma warning restore IDE1006
    {
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == busId);   // ORACLE: TripAtBus looked the bus up earlier
        // ---- copied from git, unchanged ----
        // Also drive the Generation tab's analytical trace + a headline RoCoF/nadir for the log.
        // N325 (DOC-17): the N289 rotating-mass total and the tripped unit's own share of it, as gridsim trip uses since
        // N314 (this used to sum H x MBase over EVERY unit); unchanged on every bundled case.
        double totalInertia = m.SystemInertiaMwS;                                        // MWs
        double remaining = Math.Max(1.0, totalInertia - GridModel.RotatingInertiaMwS(gen));   // tripped unit stops spinning
        double demand = m.Buses.Sum(b => b.Pd);
        // The swing study runs ONCE, inside AnalyseTrip (which also runs the TSGB variant and the sizer); the log line
        // reads its baseline response. A second, identical baseline simulation used to run here first.
        _vm.AnalyseTrip(remaining, demand, gen.Pg, gen.Name ?? bus?.Name ?? $"unit @ bus {busId}");
        FrequencyResponse? resp = _vm.BaselineResponse;

        // The header lamp is NOT forced to FREQUENCY EVENT here: the next frame's lamp rule (the shared deviation and
        // RoCoF tests) decides, so a 5 MW trip that stays in band does not light the lamp.
        _vm.Log(_vm.Clock, resp is null
            ? $" TRIP {gen.Name} ({gen.Pg:F0} MW)"
            : $" TRIP {gen.Name} ({gen.Pg:F0} MW)  RoCoF {resp.InitialRocofHzPerS:F2} Hz/s  nadir {resp.NadirHz:F2} Hz");
        _vm.AddAlarm($"TRIP {gen.Name} -{gen.Pg:F0} MW", Palette.Frozen(Palette.Loading(1.1)));
        // ---- end of the copy ----
    }
}
