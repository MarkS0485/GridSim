using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Generation;

/// <summary>
/// The GENERATION tab's swing study for a map trip (UI programme Phase 1.6), callable from any front end.
///
/// <para>When the WPF map trips a unit (right-click TRIP at a bus, or the T key), the window does two things: it
/// injects the unit's output into the live frequency integrator (the run pipeline's job: <c>SimHost.TripInfeed</c>),
/// and it runs the GENERATION tab's analytical swing study on the dashboard: the unit's own rotating mass is taken out
/// of the system's, the loss is played against the remaining inertia and the case's demand with and without the TSGB
/// device (<see cref="DashboardModel.AnalyseTrip"/>, which also scores it against the limits, sizes the device and
/// refreshes the GENERATION tab's trace), the event-log line carries the baseline RoCoF and nadir, and a TRIP alarm is
/// raised. This class is that second half, moved here line for line from the WPF <c>MainWindow.TripAtBus</c> so the
/// Avalonia trip runs exactly the same study with exactly the same numbers. The WPF window now calls it too.</para>
///
/// <para>How the Avalonia trip uses it: after <c>RunViewModel.TripAtBus</c> has picked the unit and called
/// <c>Host.TripInfeed(gen.Pg)</c>, call <c>TripSwingStudy.Apply(dashboardModel, lastFrame.Model, gen, busId)</c> on the
/// UI thread, in place of logging the short line; it returns the baseline response (null only if the dashboard has
/// none) and has already written the log line and the alarm.</para>
///
/// <para>No numerics are new here: the study is <see cref="DashboardModel.AnalyseTrip"/> (Core
/// <c>FrequencyDynamics</c>), fed the same three numbers the WPF window computed. UiTripSwingStudyParityTests compares
/// every input, the log line, the alarm and the whole dashboard state against a verbatim copy of the old WPF code.</para>
/// </summary>
public static class TripSwingStudy
{
    /// <summary>
    /// The study's inputs for tripping <paramref name="gen"/> (picked at bus <paramref name="busId"/>) out of
    /// <paramref name="model"/>: the rotating inertia left once the unit stops spinning, the case's demand, the MW lost
    /// and the label the study is reported under.
    /// </summary>
    public readonly record struct Inputs(double RemainingInertiaMwS, double DemandMw, double LostMw, string Label);

    /// <summary>The colour of the TRIP alarm (the WPF window's <c>Palette.Loading(1.1)</c>).</summary>
    public static UiColour AlarmColour => ColourRamps.Loading(1.1);

    /// <summary>
    /// The WPF window's study inputs, statement for statement. N325 (DOC-17): the N289 rotating-mass total and the
    /// tripped unit's own share of it, as gridsim trip uses since N314 (this used to sum H x MBase over EVERY unit);
    /// unchanged on every bundled case. The label is the unit's name, else its bus's name, else "unit @ bus N".
    /// </summary>
    public static Inputs InputsFor(GridModel model, Generator gen, int busId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(gen);
        Bus? bus = model.Buses.FirstOrDefault(b => b.Id == busId);
        double totalInertia = model.SystemInertiaMwS;                                           // MWs
        double remaining = Math.Max(1.0, totalInertia - GridModel.RotatingInertiaMwS(gen));     // tripped unit stops spinning
        double demand = model.Buses.Sum(b => b.Pd);
        return new Inputs(remaining, demand, gen.Pg, gen.Name ?? bus?.Name ?? $"unit @ bus {busId}");
    }

    /// <summary>The event-log line of a trip: with the study's baseline RoCoF and nadir when there is a response (the
    /// WPF text, character for character).</summary>
    public static string LogLine(Generator gen, FrequencyResponse? baseline)
    {
        ArgumentNullException.ThrowIfNull(gen);
        return baseline is null
            ? $" TRIP {gen.Name} ({gen.Pg:F0} MW)"
            : $" TRIP {gen.Name} ({gen.Pg:F0} MW)  RoCoF {baseline.InitialRocofHzPerS:F2} Hz/s  nadir {baseline.NadirHz:F2} Hz";
    }

    /// <summary>The TRIP alarm's text (the WPF text).</summary>
    public static string AlarmText(Generator gen)
    {
        ArgumentNullException.ThrowIfNull(gen);
        return $"TRIP {gen.Name} -{gen.Pg:F0} MW";
    }

    /// <summary>
    /// Run the swing study for a map trip on <paramref name="dashboard"/>, in the WPF window's order: the study (which
    /// refreshes the GENERATION tab's read-outs and trace and logs the TSGB compare line), then the trip's own log line
    /// with the baseline RoCoF and nadir, then the TRIP alarm. The header lamp is NOT forced here: the next frame's lamp
    /// rule decides, so a small trip that stays in band does not light it (as WPF). Returns the baseline response.
    /// </summary>
    public static FrequencyResponse? Apply(DashboardModel dashboard, GridModel model, Generator gen, int busId)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        Inputs i = InputsFor(model, gen, busId);
        // The swing study runs ONCE, inside AnalyseTrip (which also runs the TSGB variant and the sizer); the log line
        // reads its baseline response.
        dashboard.AnalyseTrip(i.RemainingInertiaMwS, i.DemandMw, i.LostMw, i.Label);
        FrequencyResponse? resp = dashboard.BaselineResponse;
        dashboard.Log(dashboard.Clock, LogLine(gen, resp));
        dashboard.AddAlarm(AlarmText(gen), AlarmColour);
        return resp;
    }
}
