using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(GridSim.App.Tests.TestAppBuilder))]

namespace GridSim.App.Tests;

/// <summary>
/// Starts the real <see cref="App"/> on Avalonia's headless platform for every [AvaloniaFact] in this assembly: the
/// same App class, theme and AXAML the desktop head starts, without a display.
/// <para>
/// Phase 1.5 (the map): the headless platform renders with Skia (UseHeadlessDrawing off), the same renderer the
/// desktop head uses, so a test can capture a rendered frame and read the map's pixels back. The bundled Inter font is
/// the default, as in the desktop head (WithInterFont), so text lays out the same on every OS.
/// </para>
/// </summary>
/// <remarks>
/// UI programme Phase 1.3: the headless platform draws with the real Skia renderer (UseSkia, UseHeadlessDrawing off)
/// instead of its no-op drawing stub, so a test can capture a rendered frame and check pixels of the custom-drawn
/// instruments (InstrumentControlTests). The bundled Inter font is registered as the desktop head does (WithInterFont),
/// so text renders the same on a machine with no system fonts (a bare CI image).
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
