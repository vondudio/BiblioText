# BiblioText Cloud

The cloud half of BiblioText: a single ASP.NET Core app that hosts both the
**publish API** (the station pushes reviewed books here) and the **members-only
website** (semantic search + "who owns it / where it lives"). Backed by
Postgres + [pgvector](https://github.com/pgvector/pgvector) for embeddings and
optional Azure Blob storage for images.

> Status: **Phase 5 — deployed.** The publish API, catalog store, embedding and
> image services, search API (Phase 3), the member website + magic-link auth
> (Phase 4), and a live Azure deployment with real Foundry embeddings (Phase 5)
> are all in place.

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
| `Auth:DevMode` | Show the magic link on-screen instead of emailing it | `false` (email required) |
| `Auth:AllowedEmails` | Allowlist of member emails permitted to sign in | empty (nobody can sign in) |
| `Auth:MagicLinkLifetimeMinutes` / `SessionLifetimeDays` | Token + cookie lifetimes | `15` / `30` |

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

## Deploy to Azure

The app is a framework-dependent ASP.NET Core deploy to a single Linux App
Service. On startup it runs `Database.Migrate()`, so a fresh database
self-provisions its schema (and the pgvector extension) on first boot — no
manual migration step against the cloud DB.

Provisioned resources (one resource group):

- **Azure Database for PostgreSQL Flexible Server** (public access + firewall
  rules for your IP and *Allow Azure services*), `azure.extensions=vector`
  enabled, a database created for the app.
- **Azure AI Foundry / OpenAI** with a `text-embedding-3-small` deployment.
- **Azure Storage** account with a public-read `book-images` blob container.
- **App Service** (Linux, `DOTNETCORE:10.0`) with all `Catalog:*`, `Auth:*`,
  and `ConnectionStrings:Catalog` values set in configuration.

```powershell
dotnet publish cloud\BiblioText.Cloud.csproj -c Release -o publish
Compress-Archive -Path publish\* -DestinationPath app.zip -Force
az webapp deploy --resource-group <rg> --name <app> --type zip --src-path app.zip
```

> **App settings gotcha:** connection strings contain `;`, which breaks inline
> `az ... --settings key=val`. Pass a JSON array via `--settings "@file.json"`.

> **Publish batch size:** very large `POST /api/publish` bodies (multi-MB, e.g.
> dozens of books with inline spine images) can be rejected with a `400`. Send
> books in smaller chunks — upserts are idempotent by `stationId+stationBookId`,
> so chunking (or retrying) is safe.



- `POST /api/publish` — body is a `BiblioText.Contracts.PublishBatch`; requires
  `X-Operator-Token` when one is configured. Returns a `PublishResult`.
- `GET /api/search?q={query}&limit={n}` — semantic search (empty `q` = recency
  browse). Returns books with their copies (owner + location).
