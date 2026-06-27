using BiblioText.Cloud.Services;
using BiblioText.Contracts;

namespace BiblioText.Cloud.Api;

public static class PublishEndpoints
{
    public static IEndpointRouteBuilder MapPublishEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(PublishContract.PublishRoute, async (
                PublishBatch batch,
                PublishService publishService,
                CancellationToken cancellationToken) =>
            {
                var result = await publishService.ApplyAsync(batch, cancellationToken);
                return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
            })
            .AddEndpointFilter<OperatorTokenFilter>()
            .WithName("Publish");

        return app;
    }
}
