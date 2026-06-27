using BiblioText.Cloud.Api;
using BiblioText.Cloud.Catalog;
using BiblioText.Cloud.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Catalog configuration (Azure OpenAI / Blob / operator token). Optional bits
// fall back to local-dev implementations when absent.
var catalogOptions = builder.Configuration
    .GetSection(CatalogOptions.SectionName)
    .Get<CatalogOptions>() ?? new CatalogOptions();
builder.Services.AddSingleton(catalogOptions);
builder.Services.AddSingleton(catalogOptions.AzureOpenAI);
builder.Services.AddSingleton(catalogOptions.Blob);

// Postgres + pgvector catalog store.
var connectionString = builder.Configuration.GetConnectionString("Catalog")
    ?? "Host=localhost;Port=5432;Database=bibliotext;Username=postgres;Password=postgres";
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));

// Embedding: real Azure OpenAI when configured, deterministic dev fallback otherwise.
if (catalogOptions.AzureOpenAI.IsConfigured)
{
    builder.Services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
}
else
{
    builder.Services.AddSingleton<IEmbeddingService, DeterministicEmbeddingService>();
}

// Image storage: Azure Blob when configured, local wwwroot/uploads otherwise.
if (catalogOptions.Blob.IsConfigured)
{
    builder.Services.AddSingleton<IImageStorageService, BlobImageStorageService>();
}
else
{
    builder.Services.AddSingleton<IImageStorageService, LocalImageStorageService>();
}

builder.Services.AddScoped<PublishService>();
builder.Services.AddScoped<SearchService>();

builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapPublishEndpoints();
app.MapSearchEndpoints();

app.Run();
