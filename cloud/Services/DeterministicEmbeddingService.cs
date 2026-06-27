using System.Security.Cryptography;
using System.Text;
using BiblioText.Cloud.Catalog.Entities;
using Pgvector;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Deterministic, dependency-free embedder used when Azure OpenAI isn't
/// configured (local dev / tests). It hashes token n-grams into a fixed-width
/// L2-normalized vector. NOT semantically meaningful — it just lets the publish
/// → store → search pipeline run end-to-end and return stable, repeatable
/// nearest-neighbour ordering without any external service.
/// </summary>
public sealed class DeterministicEmbeddingService : IEmbeddingService
{
    private const int Dimensions = Book.EmbeddingDimensions;

    public Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vector = new float[Dimensions];
        var tokens = Tokenize(text);

        foreach (var token in tokens)
        {
            // Hash each token to a bucket + sign so the same text always maps to
            // the same coordinates.
            var hash = StableHash(token);
            var index = (int)(hash % Dimensions);
            var sign = (hash & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        Normalize(vector);
        return Task.FromResult(new Vector(vector));
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static uint StableHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        foreach (var v in vector)
        {
            sumSquares += v * (double)v;
        }

        if (sumSquares <= 0)
        {
            return;
        }

        var inv = (float)(1.0 / Math.Sqrt(sumSquares));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inv;
        }
    }
}
