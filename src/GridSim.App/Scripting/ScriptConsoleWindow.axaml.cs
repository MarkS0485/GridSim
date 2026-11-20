using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GridSim.Ui.Models;

namespace GridSim.App.Scripting;

/// <summary>
/// The desktop Script Console window (RC4): an input line with history, the output pane, a script file to run or check, and the
/// open case on request. Everything it shows comes from <see cref="ScriptConsoleViewModel"/>, which hosts GridSim.Scripting.
/// Open it with <see cref="Open"/>.
/// </summary>
public sealed partial class ScriptConsoleWindow : Window
{
    public ScriptConsoleViewModel ViewModel { get; }

    /// <summary>For the AXAML loader and the designer only.</summary>
    public ScriptConsoleWindow() : this(new ScriptConsoleViewModel()) { }

    public ScriptConsoleWindow(ScriptConsoleViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        InputBox.AddHandler(KeyDownEvent, InputKeyDown, RoutingStrategies.Tunnel);
        ViewModel.Refreshed += (_, _) => ShowState();
        Closing += (_, _) => ViewModel.Close();
        Closed += (_, _) => ViewModel.Dispose();
        ShowState();
    }

    /// <summary>The command id of File / Script console... .</summary>
    public const string CommandId = "file.scriptConsole";

    /// <summary>File / Script console...: opens the console on the case the main window has open.</summary>
    public static GridSim.Ui.Commands.AppCommand Command(Func<MainWindow> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new GridSim.Ui.Commands.AppCommand
        {
            Id = CommandId,
            Title = "Script console...",
            MenuPath = "File/Script console...",
            Modes = GridSim.Ui.Commands.CommandModes.Both,
            Execute = () => { owner().OpenScriptConsole(); return Task.CompletedTask; },
        };
    }

    /// <summary>The folder sessions are recorded to by default: <c>GridSim/sessions</c> under the user's documents.</summary>
    public static string DefaultRecordDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GridSim", "sessions");

    /// <summary>Open a console window; <paramref name="openCase"/> is the case the main window has loaded (offered by Load open case).</summary>
    public static ScriptConsoleWindow Open(Window? owner, ModelSource? openCase, string? baseDirectory = null, string? recordDirectory = null)
    {
        var vm = new ScriptConsoleViewModel(baseDirectory ?? Environment.CurrentDirectory, recordDirectory ?? DefaultRecordDirectory()) { OpenCase = openCase };
        var w = new ScriptConsoleWindow(vm);
        if (owner is not null) w.Show(owner); else w.Show();
        return w;
    }

    private void ShowState()
    {
        ScriptConsoleViewModel vm = ViewModel;
        OutputBox.Text = vm.OutputText;
        OutputBox.CaretIndex = OutputBox.Text.Length;
        StatusLine.Text = vm.StatusText;
        PromptText.Text = vm.Prompt;
        bool idle = !vm.IsBusy;
        RunButton.IsEnabled = idle;
        CheckButton.IsEnabled = idle;
        SendButton.IsEnabled = idle;
        LoadCaseButton.IsEnabled = idle && vm.OpenCasePath() is not null;
        EndButton.IsEnabled = idle && vm.SessionGssPath is not null;
    }

    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); }
        else if (e.Key == Key.Up) { e.Handled = true; InputBox.Text = ViewModel.HistoryPrevious(InputBox.Text ?? ""); InputBox.CaretIndex = InputBox.Text?.Length ?? 0; }
        else if (e.Key == Key.Down) { e.Handled = true; InputBox.Text = ViewModel.HistoryNext(); }
    }

    /// <summary>Send the input line to the console and clear it (what Enter does).</summary>
    public async Task SendAsync()
    {
        string line = InputBox.Text ?? "";
        InputBox.Text = "";
        await ViewModel.SubmitAsync(line).ConfigureAwait(true);
        InputBox.Focus();
    }

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();
    private async void Run_Click(object? sender, RoutedEventArgs e) => await ViewModel.RunFileAsync(PathBox.Text ?? "");
    private async void Check_Click(object? sender, RoutedEventArgs e) => await ViewModel.CheckFileAsync(PathBox.Text ?? "");
    private async void LoadCase_Click(object? sender, RoutedEventArgs e) => await ViewModel.LoadOpenCaseAsync();
    private void End_Click(object? sender, RoutedEventArgs e) => ViewModel.EndSession();
    private void Clear_Click(object? sender, RoutedEventArgs e) => ViewModel.ClearOutput();
}
