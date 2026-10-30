using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GridSim.Ui.Dashboard;

/// <summary>
/// The minimal change-notification base of the platform-neutral view-models, written by hand so GridSim.Ui keeps no
/// package reference (UiProjectNeutralityTests pins that). It behaves exactly as the WPF app's <c>Notify</c> base it
/// was copied from: a property raises <see cref="PropertyChanged"/> only when its value really changes (by
/// <see cref="EqualityComparer{T}.Default"/>), once, with its own name.
///
/// <para>One addition: every notification goes through <see cref="OnPropertyChanged"/>, which a front end may override
/// to raise its own adapter properties as well. The WPF dashboard uses it to say "StatusBrush changed" when the neutral
/// colour token "StatusColour" changes, so the existing XAML bindings keep working unchanged.</para>
/// </summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Store <paramref name="value"/> in <paramref name="field"/> and notify, unless it is equal to what is there.</summary>
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    /// <summary>Notify a derived or computed property alongside a <see cref="Set{T}"/>.</summary>
    protected void Raise(string name) => OnPropertyChanged(name);

    /// <summary>
    /// Raise <see cref="PropertyChanged"/> for <paramref name="name"/>. An override must call this base first, so the
    /// neutral property is always announced before any adapter property derived from it.
    /// </summary>
    protected virtual void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
