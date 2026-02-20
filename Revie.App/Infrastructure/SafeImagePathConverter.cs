using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Revie.App.Infrastructure;

public sealed class SafeImagePathConverter : IValueConverter
{
    private static readonly ImageSource EmptyImageSource = new BitmapImage();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = (value as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return EmptyImageSource;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return EmptyImageSource;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
