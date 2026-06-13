#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

/// <summary>
/// Queries every configured provider in parallel and aggregates the results so the AI can
/// synthesize a single description from the union of evidence. Each provider's failure is
/// isolated — one rate limit or network error never blocks the others. The Library badges
/// downstream show every provider that actually contributed a hit.
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
        var lookupTasks = _providers
            .Select(provider => SafeLookupAsync(provider, title, author, ct))
            .ToArray();

        var results = await Task.WhenAll(lookupTasks);

        // Flatten, then sort highest match score first so the AI prompt leads with the strongest
        // candidate. Each provider's hits already arrive in its own ranked order.
        return results
            .SelectMany(r => r)
            .OrderByDescending(s => s.MatchScore)
            .ToList();
    }

    private static async Task<IReadOnlyList<BookMetadataSource>> SafeLookupAsync(
        IBookMetadataLookupService provider,
        string title,
        string? author,
        CancellationToken ct)
    {
        try
        {
            return await provider.LookupAsync(title, author, ct);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
