using BiblioText.Cloud.Api;
using BiblioText.Cloud.Auth;
using BiblioText.Cloud.Catalog;
using BiblioText.Cloud.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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

// Members-only auth: config allowlist + passwordless magic links + cookie session.
var authOptions = builder.Configuration
    .GetSection(AuthOptions.SectionName)
    .Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);
builder.Services.AddScoped<MagicLinkService>();

// Persist Data Protection keys so magic links + session cookies survive restarts.
// (Phase 5: move key ring to Blob/Key Vault for a multi-instance deploy.)
var keyRingPath = Path.Combine(builder.Environment.ContentRootPath, "dp-keys");
builder.Services.AddDataProtection()
    .SetApplicationName("BiblioText.Cloud")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = authOptions.SessionLifetime;
        options.SlidingExpiration = true;
        options.Cookie.Name = "BiblioText.Members";
    });
builder.Services.AddAuthorization();

// Catalog pages require a signed-in member; account + error pages stay anonymous.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Privacy");
});

var app = builder.Build();

// Apply EF migrations at startup so a fresh deploy self-provisions its schema
// (and the pgvector extension). Fail fast if the catalog DB is unreachable.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapPublishEndpoints();
app.MapSearchEndpoints();

app.Run();
