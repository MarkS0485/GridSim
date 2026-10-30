using System.Collections.ObjectModel;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Domains;

/// <summary>
/// One speed domain shown on the SPEED DOMAINS tab.
/// <para>UI programme Phase 1.6: the WPF <c>SpeedDomainRow</c>, with <c>Accent</c> now the colour token
/// <see cref="AccentColour"/>. Every value is fixed when the row is made (the tab rebuilds its rows on each refresh), so
/// a front end's own row type copies them through <see cref="SpeedDomainRowModel(SpeedDomainRowModel)"/>.</para>
/// </summary>
public class SpeedDomainRowModel : ObservableModel
{
    /// <summary>An empty row, for an object initialiser.</summary>
    public SpeedDomainRowModel() { }

    /// <summary>A row with every value of <paramref name="source"/> (a front end's own row type is made this way).</summary>
    protected SpeedDomainRowModel(SpeedDomainRowModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Name = source.Name;
        Speed = source.Speed;
        Kind = source.Kind;
        Machines = source.Machines;
        InertiaGvaS = source.InertiaGvaS;
        GvadGva = source.GvadGva;
        InertiaLabel = source.InertiaLabel;
        BarWidth = source.BarWidth;
        InertiaBarWidth = source.InertiaBarWidth;
        AccentColour = source.AccentColour;
        DominantTag = source.DominantTag;
    }

    public string Name { get; init; } = "";
    public string Speed { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Machines { get; init; } = "";
    public string InertiaGvaS { get; init; } = "";
    public string GvadGva { get; init; } = "";
    public string InertiaLabel { get; init; } = "";
    public double BarWidth { get; init; }           // px, ~ GVad - drives the dominance bar chart
    public double InertiaBarWidth { get; init; }    // px, ~ spinning inertia - the second bar chart
    public UiColour AccentColour { get; init; } = DashboardColours.Gray;
    public string DominantTag { get; init; } = "";
}

/// <summary>
/// SPEED DOMAINS tab - the experimental distributed-inertia view. Decomposes the current network into
/// speed domains (machines sharing a mechanical speed / pole ratio, LOS sec. 2.7/sec. 5.5), shows the GVad
/// dominance bar chart + metric (deltaGVa >= 10% => a dominant domain can anchor recovery). Domain structure
/// and GVad are grounded in the model; per-bus spatial spread and propagation are unvalidated at scale.
/// Degrades to a single-domain (lumped-equivalent) summary. Reads any solved <see cref="SimFrame"/>.
///
/// <para>UI programme Phase 1.6: this is the logic of the WPF <c>Views/DomainsVm.cs</c>, moved here line for line so
/// the Avalonia app shows the same tab from the same code. Every displayed string and number is produced by the same
/// formatting call, in the same culture, in the same order. The only change is at the framework boundary: each WPF
/// <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes; <c>DominantBrush</c> is now
/// <see cref="DominantColour"/>). The WPF <c>DomainsVm</c> derives from this class and maps them back under the old
/// names, so its XAML is unchanged.</para>
/// </summary>
public class DomainsModel : ObservableModel
{
    private const double MaxBarPx = 620.0;

    // The tab's fixed colours: the bytes of the WPF Color.FromRgb(...) brushes it used (opaque).
    private static readonly UiColour Electric = new(0xFF, 0x3F, 0xA9, 0xF5);
    private static readonly UiColour Secure = new(0xFF, 0x2F, 0xD2, 0x7F);
    private static readonly UiColour Inverter = new(0xFF, 0x6A, 0x5A, 0x8C);
    private static readonly UiColour Muted = new(0xFF, 0x8A, 0x97, 0xA6);

    private SimFrame? _pending;
    private bool _active;

    /// <summary>
    /// Set by the shell while the SPEED DOMAINS tab is showing, as the ASSETS and EXPERIMENTAL tabs are gated. The
    /// domain assignment and the row rebuild used to run on the UI thread for every frame whatever tab was shown; while
    /// false, <see cref="Update"/> only remembers the newest frame, and showing the tab catches up on it at once.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (_active && _pending is not null) Refresh(_pending);
        }
    }

    public ObservableCollection<SpeedDomainRowModel> Domains { get; } = new();

    private string _status = "Speed-domain decomposition of the current network.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _domainCount = "0"; public string DomainCount { get => _domainCount; set => Set(ref _domainCount, value); }
    private string _deltaGva = " - "; public string DeltaGva { get => _deltaGva; set => Set(ref _deltaGva, value); }
    private string _dominant = " - "; public string Dominant { get => _dominant; set => Set(ref _dominant, value); }
    /// <summary>The dominance read-out's colour (was the WPF <c>DominantBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _dominantColour = DashboardColours.Gray; public UiColour DominantColour { get => _dominantColour; set => Set(ref _dominantColour, value); }

    private string _gap = ""; public string Gap { get => _gap; set => Set(ref _gap, value); }

    private string _provenance =
        "Domain structure & GVad are grounded in the model; per-bus spatial spread and propagation delay are UNVALIDATED AT SCALE.";
    public string Provenance { get => _provenance; set => Set(ref _provenance, value); }

    /// <summary>Accept a new frame: computed now when the tab is showing, otherwise kept for when it is.</summary>
    public void Update(SimFrame f)
    {
        if (f?.Model is null) return;
        _pending = f;
        if (_active) Refresh(f);
    }

    private void Refresh(SimFrame f)
    {
        (IReadOnlyList<SpeedDomain> domains, DominanceReport dom) = DomainAssignment.Assign(f.Model);

        double maxGvad = domains.Count > 0 ? Math.Max(1.0, domains.Max(x => x.Gvad)) : 1.0;
        double maxInertia = domains.Count > 0 ? Math.Max(1.0, domains.Max(x => x.AggregateInertiaMwS)) : 1.0;

        Domains.Clear();
        foreach (SpeedDomain d in domains.OrderByDescending(x => x.Gvad))
        {
            bool isDom = dom.DominantDomainId == d.Id;
            bool synchronous = d.Kind == DomainKind.Synchronous;
            Domains.Add(CreateRow(new SpeedDomainRowModel
            {
                Name = $"Domain {d.Id}",
                Speed = d.Poles > 0 ? $"{d.Poles}-pole * {d.Rpm:F0} rpm" : "inverter (virtual)",
                Kind = d.Kind.ToString(),
                Machines = $"{d.MemberGenIndices.Count} machine(s)",
                InertiaGvaS = $"{d.AggregateInertiaGvaS:F2} GVA.s spinning",
                GvadGva = $"{d.Gvad / 1000.0:F2} GVA",
                InertiaLabel = $"{d.AggregateInertiaGvaS:F2} GVA.s",
                BarWidth = Math.Max(3.0, MaxBarPx * d.Gvad / maxGvad),
                InertiaBarWidth = Math.Max(0.0, MaxBarPx * d.AggregateInertiaMwS / maxInertia),
                AccentColour = isDom ? Secure : synchronous ? Electric : Inverter,
                DominantTag = isDom ? "* DOMINANT" : "",
            }));
        }

        DomainCount = domains.Count.ToString();
        DeltaGva = double.IsInfinity(dom.DeltaGVa) ? "sole domain" : $"{dom.DeltaGVa * 100.0:F0}%";
        DominantColour = dom.HasDominant ? Secure : Muted;
        Dominant = dom.HasDominant
            ? $"Domain {dom.DominantDomainId} carries waveform authority"
            : "none - inertia too evenly spread";
        Status = domains.Count > 1
            ? $"{domains.Count} speed domains, ranked by apparent-power stiffness GVad = sum(S*C)."
            : "Single speed domain - equivalent to the lumped model.";

        // The dominance gap narrative (LOS sec. 5.5).
        if (double.IsInfinity(dom.DeltaGVa))
            Gap = "A single dominant domain trivially anchors recovery.";
        else if (dom.DomainsByGvadDesc.Count >= 2)
        {
            string verdict = dom.HasDominant
                ? ">= 10% -> a dominant domain can carry the waveform through recovery."
                : "< 10% -> inertia is too evenly spread; no domain can anchor recovery (the LOS fragmentation mode).";
            Gap = $"Top two domains differ by deltaGVa = {dom.DeltaGVa * 100.0:F0}%  *  {verdict}";
        }
        else Gap = "";
    }

    /// <summary>The row to show for <paramref name="row"/> (the WPF tab makes its <c>SpeedDomainRow</c> from it).</summary>
    protected virtual SpeedDomainRowModel CreateRow(SpeedDomainRowModel row) => row;
}
