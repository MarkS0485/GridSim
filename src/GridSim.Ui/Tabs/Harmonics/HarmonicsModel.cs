namespace GridSim.Ui.Tabs.Harmonics;

/// <summary>
/// The HARMONICS tab (UI programme Phase 1.6, slice 1.7): what the tab says, for every front end.
/// <para>
/// The WPF tab (src/GridSim.Wpf/MainWindow.xaml, the TabItem with the header "HARMONICS") holds no logic and no data:
/// it is four fixed paragraphs written straight into the XAML, a heading, a summary of what the command-line study does
/// and does not do, the command's usage line, and a note on the spectra schemas. Nothing in the WPF code-behind or in the
/// WPF dashboard feeds it, so it shows the same words on every frame of every case. There is therefore nothing to move
/// out of WPF (the WPF XAML stays as it is, and stays the reference): this class only holds the same four texts once, so
/// the Avalonia view binds them instead of repeating them, and a parity test
/// (tests/GridSim.Tests/Ui/UiHarmonicsParityTests.cs) reads the WPF XAML and checks every text here is exactly the text
/// WPF shows, in the same order.
/// </para>
/// <para>
/// No numbers are shown or computed here. The study itself runs only from the command line
/// (<c>gridsim harmonics</c>); the tab says so, as WPF does.
/// </para>
/// </summary>
public sealed class HarmonicsModel
{
    /// <summary>The tab's header on the tab strip, as the WPF TabItem spells it.</summary>
    public const string Header = "HARMONICS";

    /// <summary>The heading (WPF: FontSize 18, bold).</summary>
    public const string TitleText = "Harmonic analysis";

    /// <summary>The summary paragraph: what the command-line study covers, and which engines cannot be run yet.</summary>
    public const string SummaryText =
        "Frequency-domain harmonics from the command line (gridsim harmonics): harmonic penetration, voltage THD, IEEE 519 " +
        "compliance, the G5/5 headline THD planning levels (the per-order G5/5 tables are not encoded), and a resonance / " +
        "impedance scan with a fine frequency sweep. Three-phase harmonics, coupled harmonic power flow and harmonic state " +
        "estimation exist in GridSim.Core as experimental engines but cannot be run from this app or the CLI yet. The only " +
        "assumed input is the harmonic source spectra, which are provenance-tagged and upgrade in place when measured data " +
        "exists.";

    /// <summary>The usage line of the command (WPF: in the Consolas font). The XAML writes the angle brackets as
    /// &amp;lt; and &amp;gt;; the text shown has the brackets themselves.</summary>
    public const string CommandText =
        "gridsim harmonics <case> [--spectra <gridsim-harmonics/1.json> | --synth-sources] [--enableExport]";

    /// <summary>The note on the spectra schemas (WPF: in grey).</summary>
    public const string NoteText =
        "Source spectra follow the gridsim-harmonics/1 schema (authoritative contract in GDA: " +
        "NetworkModel/HarmonicDataSchema.json); the study emits gridsim-harmonic-export/1. As synchronous machines retire, " +
        "resonances sharpen - the under-told half of the low-inertia story.";

    /// <summary>Every text the tab shows, top to bottom, in the WPF order (heading, summary, usage line, note).</summary>
    public static IReadOnlyList<string> Texts { get; } = new[] { TitleText, SummaryText, CommandText, NoteText };

    /// <summary>The heading, for binding (always <see cref="TitleText"/>).</summary>
    public string Title { get; } = TitleText;

    /// <summary>The summary paragraph, for binding (always <see cref="SummaryText"/>).</summary>
    public string Summary { get; } = SummaryText;

    /// <summary>The command's usage line, for binding (always <see cref="CommandText"/>).</summary>
    public string Command { get; } = CommandText;

    /// <summary>The note on the spectra schemas, for binding (always <see cref="NoteText"/>).</summary>
    public string Note { get; } = NoteText;
}
