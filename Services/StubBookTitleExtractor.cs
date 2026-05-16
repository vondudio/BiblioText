using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIDevGallery.Sample.Services;

/// <summary>
/// Placeholder <see cref="IBookTitleExtractor"/> used while the crop pre-processing
/// pipeline is being validated. Returns a deterministic-ish stub title after a small
/// fake delay so the UI can be wired up end-to-end without an Azure endpoint.
/// </summary>
/// <remarks>
/// Replace this with an Azure OpenAI client once the crop quality looks right. The
/// real implementation should:
/// <code>
/// // 1. Build the data URL Azure expects:
/// string dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(crop.Jpeg);
///
/// // 2. POST to your deployment's chat/completions endpoint with a multimodal
/// //    user message (image_url part + a "extract the book title" text part):
/// //    https://&lt;resource&gt;.openai.azure.com/openai/deployments/&lt;gpt-5.4&gt;/chat/completions
/// //    ?api-version=2024-10-21
/// //
/// //    Body shape (truncated):
/// //    { "messages": [{ "role":"user", "content":[
/// //        { "type":"text", "text":"Return ONLY the book title and author from this spine, comma-separated. If unreadable, reply 'unknown'." },
/// //        { "type":"image_url", "image_url":{ "url": dataUrl, "detail":"high" } }
/// //    ] }], "max_tokens": 64, "temperature": 0 }
/// //
/// // 3. Parse choices[0].message.content. Strip whitespace. Return.
/// </code>
/// </remarks>
internal sealed class StubBookTitleExtractor : IBookTitleExtractor
{
    private int _counter;

    public async Task<string> ExtractAsync(BookCrop crop, CancellationToken ct = default)
    {
        // Simulate the Azure roundtrip so the UI sees a realistic latency profile.
        await Task.Delay(150, ct);
        int n = Interlocked.Increment(ref _counter);
        return $"(stub #{n}) {crop.Label} {crop.PixelWidth}x{crop.PixelHeight} ({crop.Jpeg.Length / 1024} KB)";
    }
}
