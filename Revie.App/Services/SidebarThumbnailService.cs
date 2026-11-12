using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Revie.App.Services;

public static class SidebarThumbnailService
{
    public const string FileName = "sidebar-preview.jpg";
    private static readonly SemaphoreSlim GenerationSlots = new(2, 2);

    public static string EnsureThumbnail(string sourcePath, string captureDirectory)
    {
        if (string.IsNullOrWhiteSpace(captureDirectory)) return sourcePath;
        return EnsureThumbnailAtPath(sourcePath, Path.Combine(captureDirectory, FileName));
    }

    public static string EnsureThumbnailAtPath(string sourcePath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) ||
            string.IsNullOrWhiteSpace(targetPath))
            return sourcePath;

        try
        {
            if (File.Exists(targetPath) && File.GetLastWriteTimeUtc(targetPath) >= File.GetLastWriteTimeUtc(sourcePath))
                return targetPath;

            GenerationSlots.Wait();
            try
            {
                if (File.Exists(targetPath) && File.GetLastWriteTimeUtc(targetPath) >= File.GetLastWriteTimeUtc(sourcePath))
                    return targetPath;

                using var image = Image.FromFile(sourcePath);
                var scale = Math.Min(1.0, Math.Min(320.0 / image.Width, 200.0 / image.Height));
                var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.Clear(Color.Black);
                    graphics.DrawImage(image, 0, 0, width, height);
                }

                var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    bitmap.Save(temporaryPath, encoder, parameters);
                    File.Move(temporaryPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                return targetPath;
            }
            finally
            {
                GenerationSlots.Release();
            }
        }
        catch
        {
            return File.Exists(targetPath) ? targetPath : sourcePath;
        }
    }
}
