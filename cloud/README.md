# BiblioText Cloud

The cloud half of BiblioText: a single ASP.NET Core app that hosts both the
**publish API** (the station pushes reviewed books here) and the **members-only
website** (semantic search + "who owns it / where it lives"). Backed by
Postgres + [pgvector](https://github.com/pgvector/pgvector) for embeddings and
optional Azure Blob storage for images.

> Status: **Phase 3 — backend.** The publish API, catalog store, embedding and
> image services, and search API are in place. The member website pages
> (browse/search UI) and magic-link auth land in Phase 4.

## Architecture

```
Station ──PublishBatch (JSON)──► POST /api/publish ──► PublishService
                                                          │  upsert Book (title+author)
                                                          │  upsert Copy (stationId+stationBookId)
                                                          │  embed text, store images
                                                          ▼
                                              Postgres + pgvector   Blob/local images
                                                          ▲
Member ──"sci-fi about time"──► GET /api/search?q= ──► SearchService (embed query → cosine KNN)
```

- **Catalog model** — canonical `Book` (deduped by title+author via
  `CatalogKey.Normalize`, mirroring the station's `BookKey`) with one or more
  physical `Copy` rows. Embedding + cover live on the Book; owner/location +
  spine image live on the Copy.
- **Embeddings are cloud-owned.** The same `IEmbeddingService` embeds stored
  book text (at publish) and search queries (at request time) so vectors are
  always comparable. Real implementation = Azure OpenAI
  `text-embedding-3-small` (1536 dims); a deterministic hash-based fallback runs
  when Azure isn't configured so the pipeline works locally.
- **Images** — inline bytes go to Azure Blob in production, or `wwwroot/uploads`
  locally; remote provider cover URLs pass through unchanged.

## Configuration

All under the `Catalog` section + a `Catalog` connection string. Secrets belong
in App Service config / Key Vault / user-secrets — never in source.

| Setting | Purpose | If empty |
|---|---|---|
| `ConnectionStrings:Catalog` | Postgres connection | defaults to local docker-compose db |
| `Catalog:OperatorToken` | Shared secret the station sends as `X-Operator-Token` | publish is open (dev only) |
| `Catalog:AzureOpenAI:Endpoint` / `ApiKey` | Azure OpenAI embeddings | deterministic dev embedder |
| `Catalog:AzureOpenAI:EmbeddingDeployment` | Deployment name | `text-embedding-3-small` |
| `Catalog:Blob:ConnectionString` | Azure Blob storage | local `wwwroot/uploads` |
| `Catalog:Blob:Container` / `PublicBaseUrl` | Blob container / CDN prefix | `book-images` / blob URL |

## Run locally

1. Start Postgres + pgvector:
   ```powershell
   docker compose -f cloud\docker-compose.yml up -d
   ```
2. Apply migrations:
   ```powershell
   dotnet-ef database update --project cloud\BiblioText.Cloud.csproj
   ```
3. Run the app:
   ```powershell
   dotnet run --project cloud\BiblioText.Cloud.csproj
   ```

With no Azure settings it uses the deterministic embedder and local image
storage, so `POST /api/publish` and `GET /api/search?q=...` work end-to-end
without any cloud account.

## API

- `POST /api/publish` — body is a `BiblioText.Contracts.PublishBatch`; requires
  `X-Operator-Token` when one is configured. Returns a `PublishResult`.
- `GET /api/search?q={query}&limit={n}` — semantic search (empty `q` = recency
  browse). Returns books with their copies (owner + location).
