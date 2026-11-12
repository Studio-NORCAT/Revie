using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Revie.App.Infrastructure;

public sealed class SafeImagePathConverter : IValueConverter
{
    private static readonly ImageSource EmptyImageSource = CreateEmptyImageSource();
    private static readonly ConcurrentDictionary<string, ImageSource> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsUsableImage(ImageSource? source)
    {
        try
        {
            return source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;
        }
        catch
        {
            return false;
        }
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = (value as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return EmptyImageSource;
        }

        var decodeWidth = TryParseDecodeWidth(parameter);
        var lastWriteTicks = TryGetLastWriteTicks(path);
        var cacheKey = $"{path}|{decodeWidth}|{lastWriteTicks}";
        if (ImageCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = OpenReadStream(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0)
                {
                    bitmap.DecodePixelWidth = decodeWidth;
                }

                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                if (ImageCache.Count > 256)
                {
                    ImageCache.Clear();
                }

                ImageCache[cacheKey] = bitmap;
                return bitmap;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(45 * attempt);
            }
            catch
            {
                return EmptyImageSource;
            }
        }

        return EmptyImageSource;
    }

    private static int TryParseDecodeWidth(object? parameter)
    {
        return parameter switch
        {
            int width when width > 0 => width,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0 => width,
            _ => 0
        };
    }

    private static ImageSource CreateEmptyImageSource()
    {
        var image = new BitmapImage();
        image.Freeze();
        return image;
    }

    private static long TryGetLastWriteTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static FileStream OpenReadStream(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
