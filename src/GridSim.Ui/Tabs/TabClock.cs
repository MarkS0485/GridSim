using GridSim.Core.Simulation;

namespace GridSim.Ui.Tabs;

/// <summary>
/// The frame clock a tab is handed with each frame (UI programme Phase 1.6). The WPF dashboard passes some tabs its own
/// <c>Clock</c> string (for example MECHANICAL's <c>Update(f, Clock)</c>), which <c>DashboardModel.Update</c> has just
/// set from the frame's hour. A front end that feeds a tab straight from its run pipeline, without going through the
/// dashboard, needs exactly the same string, so the formula is written here once, character for character the
/// dashboard's: the whole hour and the whole minute of <see cref="SimFrame.Hour"/>, each two digits.
/// <para>UiTabsBatch2ParityTests pins this against <c>DashboardModel.Clock</c> after an <c>Update</c> for every minute
/// of a day and for awkward hours, so the two can never drift apart.</para>
/// </summary>
public static class TabClock
{
    /// <summary>The "HH:MM" clock of <paramref name="f"/>, as <c>DashboardModel.Update</c> writes it.</summary>
    public static string Of(SimFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return Of(f.Hour);
    }

    /// <summary>The "HH:MM" clock of a frame hour, as <c>DashboardModel.Update</c> writes it (the same casts, the same
    /// format, the current culture as the dashboard uses).</summary>
    public static string Of(double hour)
    {
        int h = (int)hour, m = (int)((hour % 1) * 60);
        return $"{h:00}:{m:00}";
    }
}
