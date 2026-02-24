using System.Globalization;
using System.Windows.Data;

namespace Revie.App.Infrastructure;

public sealed class WidthOffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || double.IsInfinity(width))
        {
            return 0d;
        }

        var offset = 0d;
        if (parameter is not null)
        {
            _ = double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out offset);
        }

        return Math.Max(0d, width - offset);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
