using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GridSim.App.Map;
using GridSim.Ui.Presentation;

namespace GridSim.App.Tabs;

/// <summary>
/// The binding converter from a neutral colour token (<see cref="UiColour"/>) to an Avalonia brush (UI programme Phase
/// 1.6): the cached immutable solid brush of exactly those four bytes (<see cref="MapPaint.Frozen(UiColour)"/>, the same
/// cache the map draws with), as the WPF tabs turn each token into <c>Palette.Frozen(token)</c>. So a tab's text or chip
/// shows the colour WPF shows, byte for byte. A value that is not a token is a wiring mistake in the AXAML and throws.
/// One way only: a brush is never turned back into a token.
/// </summary>
public sealed class TokenBrush : IValueConverter
{
    /// <summary>The one instance the views use (<c>{x:Static tabs:TokenBrush.Instance}</c>).</summary>
    public static TokenBrush Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        UiColour c => MapPaint.Frozen(c),
        _ => throw new ArgumentException($"TokenBrush converts a UiColour colour token; got {value?.GetType().Name ?? "null"}.", nameof(value)),
    };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A tab colour is shown, never edited: there is no brush-to-token conversion.");

    /// <summary>The brush a view shows for <paramref name="c"/> (for tests and code-behind).</summary>
    public static IBrush For(UiColour c) => MapPaint.Frozen(c);
}
