using BiblioText.Cloud.Services;

namespace BiblioText.Cloud.Api;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (
                string? q,
                int? limit,
                SearchService searchService,
                CancellationToken cancellationToken) =>
            {
                var results = await searchService.SearchAsync(q, limit ?? 50, cancellationToken);
                return Results.Ok(results);
            })
            .WithName("Search");

        return app;
    }
}
