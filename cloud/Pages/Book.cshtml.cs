using BiblioText.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiblioText.Cloud.Pages;

public class BookModel(SearchService search) : PageModel
{
    public BookResult? Book { get; private set; }
    public IReadOnlyList<SourceBadges.Badge> Badges { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        Book = await search.GetBookAsync(id, cancellationToken);
        if (Book is null)
        {
            return NotFound();
        }

        Badges = SourceBadges.Parse(Book.DescriptionSourcesJson);
        return Page();
    }
}
