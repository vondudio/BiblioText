#nullable enable

using System.Linq;

namespace BiblioText.Models;

/// <summary>
/// Canonical key used to decide whether two books are "the same" title for
/// duplicate detection. Lower-cased, stripped to letters/digits, so casing,
/// punctuation, and spacing differences collapse to one key.
/// </summary>
public static class BookKey
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
