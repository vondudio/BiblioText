using BiblioText.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiblioText.Cloud.Pages;

public class IndexModel(SearchService search) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "owner")]
    public string? Owner { get; set; }

    public IReadOnlyList<BookResult> Results { get; private set; } = [];
    public IReadOnlyList<string> Owners { get; private set; } = [];
    public bool IsSemantic => !string.IsNullOrWhiteSpace(Query);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Owners = await search.GetOwnersAsync(cancellationToken);

        var results = await search.SearchAsync(Query, 60, cancellationToken);

        if (!string.IsNullOrWhiteSpace(Owner))
        {
            results = results
                .Where(r => r.Copies.Any(c =>
                    string.Equals(c.OwnerHousehold, Owner, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        Results = results;
    }

    /// <summary>Cosine distance → rough match percentage for display.</summary>
    public static int MatchPercent(double? distance) =>
        distance is null ? 0 : (int)Math.Clamp((1 - distance.Value) * 100, 0, 100);
}
