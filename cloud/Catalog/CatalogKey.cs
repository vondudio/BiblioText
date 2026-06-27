namespace BiblioText.Cloud.Catalog;

/// <summary>
/// Canonical title+author key, kept byte-for-byte identical to the station's
/// <c>BiblioText.Models.BookKey.Normalize</c> so a book published from the
/// station collapses onto the same canonical catalog row regardless of which
/// owner's shelf it came from. Lower-cased, stripped to letters/digits.
/// </summary>
public static class CatalogKey
{
    public static string Normalize(string? title, string? author)
    {
        var normalizedTitle = NormalizePart(title);
        if (normalizedTitle.Length == 0)
        {
            return string.Empty;
        }

        return $"{normalizedTitle}|{NormalizePart(author)}";
    }

    private static string NormalizePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }
}
