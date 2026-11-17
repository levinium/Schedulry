using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Schedulry.App.Views;

public partial class MainView : UserControl
{
    /// <summary>Where the table's column widths and order are kept; null (screenshots, tests) keeps nothing.</summary>
    public static string? ColumnLayoutFile { get; set; }

    const string PreviewKey = "preview";
    const int PreviewColumn = 4;

    sealed record ColumnState(double Width, DataGridLengthUnitType Unit, int Index);

    Window? _window;

    public MainView()
    {
        InitializeComponent();
        WireTaskSelection();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RestoreColumns();
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.Closing += OnWindowClosing;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.Closing -= OnWindowClosing;
        base.OnDetachedFromVisualTree(e);
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e) => SaveColumns();

    void RestoreColumns()
    {
        if (ColumnLayoutFile is not { } file || !File.Exists(file))
            return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, ColumnState>>(File.ReadAllText(file));
            if (saved is null)
                return;
            if (saved.TryGetValue(PreviewKey, out var preview) && preview.Width >= 300)
                BodyGrid.ColumnDefinitions[PreviewColumn].Width = new GridLength(preview.Width);
            foreach (var column in TaskGrid.Columns)
            {
                if (column.Tag is string key && saved.TryGetValue(key, out var state) && state.Width > 0)
                    column.Width = new DataGridLength(state.Width, state.Unit);
            }
            // Order second, lowest index first, so each move lands where it was.
            foreach (var (column, state) in TaskGrid.Columns
                         .Select(c => (Column: c, State: c.Tag is string k && saved.TryGetValue(k, out var s) ? s : null))
                         .Where(p => p.State is not null)
                         .OrderBy(p => p.State!.Index)
                         .ToList())
            {
                if (state!.Index >= 0 && state.Index < TaskGrid.Columns.Count)
                    column.DisplayIndex = state.Index;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException)
        {
            // A damaged layout file just means the default widths.
        }
    }

    void SaveColumns()
    {
        if (ColumnLayoutFile is not { } file)
            return;
        try
        {
            var state = TaskGrid.Columns.Where(c => c.Tag is string).ToDictionary(
                c => (string)c.Tag!,
                c => new ColumnState(c.Width.IsAuto ? c.ActualWidth : c.Width.Value, c.Width.IsAuto ? DataGridLengthUnitType.Pixel : c.Width.UnitType, c.DisplayIndex));
            state[PreviewKey] = new ColumnState(BodyGrid.ColumnDefinitions[PreviewColumn].ActualWidth, DataGridLengthUnitType.Pixel, -1);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not worth bothering anyone about at closing time.
        }
    }
}
