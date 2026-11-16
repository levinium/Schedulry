using Avalonia;
using Avalonia.Controls;
using Schedulry.ViewModels;

namespace Schedulry.App.Controls;

/// <summary>
/// A small rounded label with a coloured dot: a status, a result. Its colours come from the
/// Badge{Kind} tokens through styles keyed on <see cref="Kind"/>; Info (running) pulses its dot.
/// </summary>
public class Chip : ContentControl
{
    public static readonly StyledProperty<ChipKind> KindProperty =
        AvaloniaProperty.Register<Chip, ChipKind>(nameof(Kind));

    public ChipKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
