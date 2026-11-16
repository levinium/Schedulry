using Avalonia;
using Avalonia.Data.Converters;

namespace Schedulry.App.Controls;

public static class Converters
{
    /// <summary>A number of pixels as a left margin (folder indents).</summary>
    public static readonly IValueConverter LeftMargin =
        new FuncValueConverter<double, Thickness>(left => new Thickness(left, 0, 0, 0));
}
