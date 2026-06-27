using BiblioText.Cloud.Catalog;

namespace BiblioText.Cloud.Api;

/// <summary>
/// Endpoint filter guarding publish: requires the station's <c>X-Operator-Token</c>
/// header to match <see cref="CatalogOptions.OperatorToken"/>. When no token is
/// configured the filter allows the call through (local dev only).
/// </summary>
public sealed class OperatorTokenFilter(CatalogOptions options) : IEndpointFilter
{
    public const string HeaderName = "X-Operator-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.OperatorToken;
        if (!string.IsNullOrWhiteSpace(expected))
        {
            var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
            if (!string.Equals(provided, expected, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
