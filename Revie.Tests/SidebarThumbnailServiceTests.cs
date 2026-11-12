using System.Drawing;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SidebarThumbnailServiceTests
{
    [Fact]
    public void EnsureThumbnail_CreatesSmallPersistentPreview()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RevieTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "capture.png");
            using (var bitmap = new Bitmap(1920, 1080)) bitmap.Save(source);

            var preview = SidebarThumbnailService.EnsureThumbnail(source, directory);
            Assert.Equal(Path.Combine(directory, SidebarThumbnailService.FileName), preview);
            using (var image = Image.FromFile(preview))
            {
                Assert.True(image.Width <= 320);
                Assert.True(image.Height <= 200);
            }

            var firstWrite = File.GetLastWriteTimeUtc(preview);
            Assert.Equal(preview, SidebarThumbnailService.EnsureThumbnail(source, directory));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(preview));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
