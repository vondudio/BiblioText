#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

/// <summary>
/// Tries a chain of metadata providers in order. Each provider's hits are returned, but the chain
/// stops early as soon as a provider supplies a usable description, which is what
/// <see cref="BookDescriptionService"/> actually needs for grounding.
/// </summary>
internal sealed class CompositeMetadataLookupService : IBookMetadataLookupService
{
    private readonly IReadOnlyList<IBookMetadataLookupService> _providers;

    public CompositeMetadataLookupService(params IBookMetadataLookupService[] providers)
        : this((IReadOnlyList<IBookMetadataLookupService>)providers)
    {
    }

    public CompositeMetadataLookupService(IReadOnlyList<IBookMetadataLookupService> providers)
    {
        _providers = providers;
    }

    public async Task<IReadOnlyList<BookMetadataSource>> LookupAsync(
        string title,
        string? author,
        CancellationToken ct = default)
    {
        var aggregated = new List<BookMetadataSource>();
        foreach (var provider in _providers)
        {
            IReadOnlyList<BookMetadataSource> hits;
            try
            {
                hits = await provider.LookupAsync(title, author, ct);
            }
            catch (HttpRequestException)
            {
                hits = [];
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                hits = [];
            }
            catch (JsonException)
            {
                hits = [];
            }

            if (hits.Count > 0)
            {
                aggregated.AddRange(hits);

                // If we have at least one hit with a real description, additional providers are
                // unlikely to improve grounding — but their structured metadata (covers, ratings)
                // can still be useful. Keep the rest for fallback enrichment up to a small cap.
                if (hits.Any(h => !string.IsNullOrWhiteSpace(h.Description)) && aggregated.Count >= 3)
                {
                    break;
                }
            }
        }

        aggregated.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
        return aggregated;
    }
}
