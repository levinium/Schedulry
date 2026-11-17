using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Schedulry.App.Controls;

/// <summary>
/// An error message that can be selected, and copied whole with its Copy button, so it can be
/// pasted into a search, a ticket or a message.
/// </summary>
public sealed class ErrorText : Grid
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ErrorText, string?>(nameof(Text));

    readonly SelectableTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _copy = new() { Content = "Copy", VerticalAlignment = VerticalAlignment.Top };
    IDisposable? _reset;

    public ErrorText()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 10;
        _copy.Classes.Add("neutral");
        _copy.Classes.Add("small");
        SetColumn(_copy, 1);
        Children.Add(_text);
        Children.Add(_copy);
        _copy.Click += async (_, _) =>
        {
            if (Text is not { Length: > 0 } text || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                return;
            await clipboard.SetTextAsync(text);
            _copy.Content = "Copied";
            _reset?.Dispose();
            _reset = DispatcherTimer.RunOnce(() => _copy.Content = "Copy", TimeSpan.FromSeconds(2));
        };
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _text.Text = Text;
    }
}
