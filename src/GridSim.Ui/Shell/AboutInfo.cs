using System.Runtime.InteropServices;
using GridSim.Core;

namespace GridSim.Ui.Shell;

/// <summary>
/// The About box text (UI programme Phase 1.1), the same four lines the WPF About tab shows, built in one place so
/// every front end names the release the same way: the build line (<see cref="BuildInfo.DisplayLine"/>, also shown by
/// the CLI version flag and the web footer), the full informational version, the runtime and the licence in use.
/// </summary>
public static class AboutInfo
{
    /// <summary>The heading above the lines.</summary>
    public const string Heading = "About GridSim";

    /// <summary>The note under the lines, as in WPF.</summary>
    public const string ReportNote =
        "Report a problem with the version line above. A crash writes a crash log; its path is shown in the crash message.";

    /// <summary>
    /// The lines, in the WPF order. <paramref name="licenceDescription"/> is the licence gate's own description (the
    /// caller resolves the licence; this class reads no file and no environment variable).
    /// </summary>
    public static IReadOnlyList<string> Lines(string licenceDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenceDescription);
        return new[]
        {
            BuildInfo.DisplayLine,
            $"Build:   {BuildInfo.InformationalVersion}",
            $"Runtime: {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}",
            $"Licence: {licenceDescription}",
        };
    }
}
