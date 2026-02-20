namespace DoodleCapture.App.Models;

public static class ReviewState
{
    public const string None = "";
    public const string InReview = "InReview";
    public const string Completed = "Completed";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return None;
        }

        if (string.Equals(normalized, InReview, StringComparison.OrdinalIgnoreCase))
        {
            return InReview;
        }

        if (string.Equals(normalized, Completed, StringComparison.OrdinalIgnoreCase))
        {
            return Completed;
        }

        return None;
    }

    public static string ToDisplay(string? value)
    {
        var normalized = Normalize(value);
        if (string.Equals(normalized, InReview, StringComparison.Ordinal))
        {
            return "In Review";
        }

        if (string.Equals(normalized, Completed, StringComparison.Ordinal))
        {
            return "Completed";
        }

        return "No State";
    }
}
