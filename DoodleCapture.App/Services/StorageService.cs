namespace DoodleCapture.App.Services;

public sealed class StorageService
{
    public bool ValidateWriteAccess(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Storage root is required.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".doodlecapture_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
