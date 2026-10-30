using System.Globalization;
using GridSim.Core.Simulation;

namespace GridSim.App.Run;

/// <summary>
/// The text of one frame as the run pipeline records it (UI programme Phase 1.2: it was the text of the temporary live
/// panel, which the integration of 2026-10-20 removed; the dashboard tiles now show these values on screen, and the run
/// pipeline keeps this record for its status, its error area and the tests). Every value is formatted exactly as the
/// WPF window formats the same quantity, with the same format
/// string and the invariant culture (the WPF app pins the invariant culture at start-up, D-DOC-138, and so does this
/// app), so the two show the same characters for the same frame. No number is computed here: each one is read
/// straight off the <see cref="SimFrame"/> the host produced.
/// <para>
/// Where each format comes from (all WPF unless said otherwise):
/// <list type="bullet">
/// <item><description>Tick, SimTime, Frequency, FrequencyDeviation, Demand, Generation, Losses:
/// <c>DashboardViewModel.Update</c> and <c>SetFrequency</c> (the header tiles).</description></item>
/// <item><description>Rocof: the OPERATOR tab (<c>OperatorVm</c>), the one WPF view that prints the frame's
/// RoCoF.</description></item>
/// <item><description>Convergence: "NOT CONVERGED" is the WPF status lamp's text for a frame that did not converge; a
/// converged frame shows Core's own <c>PowerFlowResult.ConvergedLabel</c>, which names the test that decided it. The
/// rest of the lamp's verdicts (frequency event, voltage alert, corridor alert, system normal) belong to the status-lamp
/// presenter, which is being moved into GridSim.Ui separately; they arrive with the tiles (Phase 1.4).</description></item>
/// <item><description>Iterations and Mismatch: WPF does not show these anywhere, so the formats are the command-line
/// tool's (<c>gridsim</c> prints the mismatch with "E2"); there is nothing in WPF to differ from.</description></item>
/// </list>
/// </para>
/// </summary>
/// <param name="Tick">The frame's tick number.</param>
/// <param name="SimTime">The simulated clock, HH:MM.</param>
/// <param name="Frequency">System frequency in Hz, two decimals.</param>
/// <param name="FrequencyDeviation">Frequency minus the case's nominal, signed, two decimals.</param>
/// <param name="Rocof">Rate of change of frequency, with its unit (Hz/s), signed, three decimals.</param>
/// <param name="Convergence">The convergence verdict (see above).</param>
/// <param name="Iterations">Solver iterations of the frame's power flow.</param>
/// <param name="Mismatch">The largest power mismatch left (pu), in E2 notation.</param>
/// <param name="Generation">Total generation in GW, two decimals.</param>
/// <param name="Demand">Total demand in GW, two decimals.</param>
/// <param name="Losses">Total real-power losses in MW, no decimals.</param>
/// <param name="Converged">True when the frame's power flow converged (a frame that did not is shown as an error).</param>
public sealed record LivePanelText(
    string Tick, string SimTime, string Frequency, string FrequencyDeviation, string Rocof, string Convergence,
    string Iterations, string Mismatch, string Generation, string Demand, string Losses, bool Converged)
{
    /// <summary>The WPF status lamp's text for a frame whose power flow did not converge.</summary>
    public const string NotConvergedText = "NOT CONVERGED";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The panel text for <paramref name="f"/>, every value formatted as WPF formats it.</summary>
    public static LivePanelText From(SimFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);

        // DashboardViewModel.Update: the clock is the frame's hour split into whole hours and minutes.
        int h = (int)f.Hour, m = (int)((f.Hour % 1) * 60);
        string clock = $"{h.ToString("00", Inv)}:{m.ToString("00", Inv)}";

        // DashboardViewModel.SetFrequency: the deviation is taken from the case's own nominal (the model's frequency),
        // with an explicit "+" for zero and above.
        double dev = f.FrequencyHz - f.Model.FrequencyHz;
        string devText = (dev >= 0 ? "+" : "") + dev.ToString("F2", Inv);

        bool converged = f.Result.Converged;
        return new LivePanelText(
            Tick: f.Tick.ToString(Inv),
            SimTime: clock,
            Frequency: f.FrequencyHz.ToString("F2", Inv),
            FrequencyDeviation: devText,
            // OperatorVm: $"{f.RocofHzPerS:+0.000;-0.000;0.000} Hz/s".
            Rocof: f.RocofHzPerS.ToString("+0.000;-0.000;0.000", Inv) + " Hz/s",
            Convergence: converged ? f.Result.ConvergedLabel() : NotConvergedText,
            Iterations: f.Result.Iterations.ToString(Inv),
            Mismatch: f.Result.MaxMismatch.ToString("E2", Inv),
            // DashboardViewModel.Update: generation and demand in GW ("F2"), losses in MW ("F0").
            Generation: (f.GenerationMw / 1000.0).ToString("F2", Inv),
            Demand: (f.DemandMw / 1000.0).ToString("F2", Inv),
            Losses: f.Result.TotalLossMw.ToString("F0", Inv),
            Converged: converged);
    }
}
