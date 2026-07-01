using System.Text.Json;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Parses the station's <c>description_sources_json</c> passthrough into short
/// provenance badges (G / W / OL / AI). Defensive: unknown shapes yield no badges.
/// </summary>
public static class SourceBadges
{
    public readonly record struct Badge(string Label, string Title);

    public static IReadOnlyList<Badge> Parse(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(sourcesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var badges = new List<Badge>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var provider = TryGet(item, "Provider") ?? TryGet(item, "provider");
                if (string.IsNullOrWhiteSpace(provider) || !seen.Add(provider))
                {
                    continue;
                }

                badges.Add(ToBadge(provider));
            }

            return badges;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryGet(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static Badge ToBadge(string provider)
    {
        var p = provider.Replace(" ", "", StringComparison.Ordinal);
        return p switch
        {
            _ when p.Contains("GoogleBooks", StringComparison.OrdinalIgnoreCase) => new("G", "Google Books"),
            _ when p.Contains("Google", StringComparison.OrdinalIgnoreCase) => new("G", "Google Books"),
            _ when p.Contains("Wikipedia", StringComparison.OrdinalIgnoreCase) => new("W", "Wikipedia"),
            _ when p.Contains("OpenLibrary", StringComparison.OrdinalIgnoreCase) => new("OL", "Open Library"),
            _ when p.Contains("AI", StringComparison.OrdinalIgnoreCase) => new("AI", "AI-synthesized"),
            _ => new(provider.Length <= 3 ? provider : provider[..2], provider),
        };
    }
}
