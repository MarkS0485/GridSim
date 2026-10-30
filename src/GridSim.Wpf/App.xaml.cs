using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using GridSim.Core.Diagnostics;
using GridSim.Core.Licensing;

namespace GridSim.Wpf;

public partial class App : Application
{
    /// <summary>
    /// The active software licence, resolved once at start-up, fully offline (no network call). The WPF
    /// app has no DI container, so this shared static is the pragmatic single instance - mirroring how the
    /// window reads <c>AppSettings.Load()</c>. Precedence: <c>GRIDSIM_LICENCE</c> env var, then
    /// <c>%LOCALAPPDATA%\GridSim\licence.gridsim</c>, then the free Personal tier.
    /// </summary>
    public static LicenceGate Licence { get; private set; } = new(Entitlement.FreeDefault);

    /// <summary>
    /// The exit code used after a crash: the same code the .NET runtime itself uses on Windows for an unhandled managed
    /// exception (0xE0434352), so a crash of the desktop app looks the same to a script as a crash of the CLI.
    /// </summary>
    private const int CrashExitCode = unchecked((int)0xE0434352);

    /// <summary>Set once the first crash has been reported, so a cascade of follow-on errors shows one message.</summary>
    private static int _crashReported;

    protected override void OnStartup(StartupEventArgs e)
    {
        // F08: crash handling, registered before anything else can fail. Before this, an exception on the UI thread
        // closed the app with no message and no record. Now every unhandled exception writes a crash log
        // (GridSim.Core.Diagnostics.CrashLog: build, runtime, OS, full stack) and shows a plain message naming it,
        // then the app closes. A crash is never swallowed and the app never carries on in a broken state.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        PinInvariantCulture();

        Licence = LicenceGate.Resolve();
        base.OnStartup(e);
    }

    /// <summary>
    /// D-DOC-138: pin the invariant culture for every number the app formats or parses, as the CLI, the web host and the
    /// case editor already do. Before this the desktop app followed the machine's regional settings, so on a machine set
    /// to (for example) German every <c>ToString("F2")</c> in a view model printed a decimal comma ("49,98 Hz") and a
    /// parse of a dot-decimal value could read it differently. Two separate settings have to be pinned:
    /// <list type="bullet">
    /// <item><description>the .NET thread culture (<c>CultureInfo.CurrentCulture</c> and the default for every new
    /// thread, so the background solve and bridge threads agree with the UI thread): this drives every
    /// <c>ToString</c>, interpolated string and <c>Parse</c> in the C# code;</description></item>
    /// <item><description>the WPF element language (<c>FrameworkElement.Language</c>): XAML bindings with a
    /// <c>StringFormat</c> format with the element's language, NOT the thread culture, and it defaults to en-US. The
    /// invariant culture's language tag is the empty string, which WPF maps back to the invariant culture.</description></item>
    /// </list>
    /// Must run before any window is created: the language default is read when an element is first built.
    /// </summary>
    private static void PinInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.InvariantCulture.IetfLanguageTag)));
    }

    /// <summary>An exception on the UI thread: log it, tell the user, close with the crash exit code.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception, showMessage: true);
        // Handled so WPF does not raise its own second report; the process still ends straight away below.
        e.Handled = true;
        Environment.Exit(CrashExitCode);
    }

    /// <summary>
    /// An exception on any other thread. The runtime ends the process after this handler returns, so only log and tell
    /// the user (the message box is shown on this thread; it is modal and blocks until dismissed).
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => ReportCrash(e.ExceptionObject, showMessage: true);

    /// <summary>
    /// A background task failed and nobody awaited it. .NET does not end the process for this, and the result it would
    /// have produced is simply lost, so record it in a crash log (no message box: the app is still consistent).
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => CrashLog.Write("GridSim.Wpf-task", e.Exception, Environment.GetCommandLineArgs());

    /// <summary>Write the crash log and, the first time only, show the message. Never throws.</summary>
    private static void ReportCrash(object? error, bool showMessage)
    {
        string? path = CrashLog.Write("GridSim.Wpf", error, Environment.GetCommandLineArgs());
        if (!showMessage || Interlocked.Exchange(ref _crashReported, 1) != 0) return;
        string what = error is Exception ex ? $"{ex.GetType().Name}: {ex.Message}" : $"{error}";
        string where = path is null
            ? "The crash log could not be written (no writable folder)."
            : $"A crash log was written to:\n{path}\n\nPlease include it when reporting the problem.";
        try
        {
            MessageBox.Show(
                $"GridSim hit an internal error and has to close. This is a defect in GridSim, not in your data.\n\n{what}\n\n{where}\n\n{GridSim.Core.BuildInfo.DisplayLine}",
                "GridSim - internal error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
#pragma warning disable CA1031 // Showing the message is best effort; the crash log is already on disk.
        catch (Exception)
#pragma warning restore CA1031
        {
            // No UI available (for example during shutdown): the crash log is the record.
        }
    }
}
