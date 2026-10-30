using Avalonia;
using Avalonia.Headless;

namespace GridSim.UiSmoke;

// UiSmoke: the cross-platform successor of tools/AssetSmoke (UI programme Phase 1 item 7; it replaces AssetSmoke in
// Phase 2, when WPF is retired, and until then both are kept). It renders every page of the Avalonia window to PNG with
// the real Skia renderer on Avalonia's headless platform: no display, no GPU, no window grabbing, no synthesised input,
// the same on Windows, Linux and macOS.
//
//   dotnet run --project tools/UiSmoke -c Release -- [--case <name or path>]... [--out <folder>] [--ticks N]
//                                                    [--size WxH] [--pages a,b,...]
//
// The pictures go in <out>/<case>/<page>.png, with <out>/index.html (a contact sheet) and <out>/manifest.json (page,
// file, size, SHA-256). The page names are AssetSmoke's where the WPF app has the same page, so the two sets can be laid
// side by side (tools/UiSmoke/PARITY-CHECKLIST.md). Exit code 0 when every page rendered, 1 when one failed, 2 for a
// refused command line.
internal static class Program
{
    private static int Main(string[] args)
    {
        UiSmokeOptions options;
        try
        {
            options = UiSmokeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("UiSmoke: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.Write(UiSmokeOptions.Usage);
            return 2;
        }
        if (options.ShowHelp)
        {
            Console.Write(UiSmokeOptions.Usage);
            return 0;
        }

        // The headless platform on this thread, set up exactly as the app's tests set it up (TestAppBuilder): the real
        // App class and theme, the Skia renderer (UseHeadlessDrawing off, so pages really draw), and the bundled Inter
        // font as the default, as the desktop head has it, so text renders the same on every OS. No lifetime is started:
        // UiSmoke opens and closes its windows itself, and this thread is the UI thread.
        BuildAvaloniaApp().SetupWithoutStarting();
        UiSmokeReport report = UiSmokeRunner.Run(options, Console.Out);
        return report.Ok ? 0 : 1;
    }

    /// <summary>The headless app builder (the same configuration as tests/GridSim.App.Tests' TestAppBuilder).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GridSim.App.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
