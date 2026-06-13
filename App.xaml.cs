using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using BiblioText.Settings;
using BiblioText.Persistence;
using BiblioText.Services;

namespace BiblioText;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(_ => new CompositeSettingsStore());
        services.AddSingleton<SqliteLibraryRepository>();
        services.AddSingleton<ILibraryRepository>(sp => sp.GetRequiredService<SqliteLibraryRepository>());
        services.AddSingleton<IBookTitleExtractor, AzureOpenAiTitleExtractor>();
        services.AddSingleton<AzureOpenAiAnalysisClient>();
        services.AddSingleton<ScanWorkflowService>();
        services.AddSingleton<IReviewApplicationService, ReviewApplicationService>();
        services.AddSingleton<OpenLibraryBookMetadataLookupService>();
        services.AddSingleton<GoogleBooksMetadataLookupService>();
        services.AddSingleton<WikipediaMetadataLookupService>();
        services.AddSingleton<IBookMetadataLookupService>(sp => new CompositeMetadataLookupService(
            sp.GetRequiredService<GoogleBooksMetadataLookupService>(),
            sp.GetRequiredService<WikipediaMetadataLookupService>(),
            sp.GetRequiredService<OpenLibraryBookMetadataLookupService>()));
        services.AddSingleton<BookDescriptionService>();
        services.AddSingleton<SemanticSearchService>();
        Services = services.BuildServiceProvider();

        SettingsStore = Services.GetRequiredService<ISettingsStore>();

        var repo = Services.GetRequiredService<SqliteLibraryRepository>();
        await repo.InitializeAsync();
        LibraryRepository = repo;

        TitleExtractor = Services.GetRequiredService<IBookTitleExtractor>();
        AnalysisClient = Services.GetRequiredService<AzureOpenAiAnalysisClient>();
        WorkflowService = Services.GetRequiredService<ScanWorkflowService>();

        var searchService = Services.GetRequiredService<SemanticSearchService>();
        await searchService.InitializeAsync();
        SemanticSearchService = searchService;

        // Background backfill: re-add every existing book to the semantic
        // index. Catches books saved before the indexer was wired up (or
        // before the user upgraded to a Windows build that supports
        // AppContentIndex). AddOrUpdate is idempotent, so this is a no-op
        // on already-indexed libraries.
        _ = Task.Run(async () =>
        {
            try
            {
                var all = await repo.GetBooksAsync();
                await searchService.ReindexAllAsync(
                    all.Select(b => (b.Id, b.Title, b.Author, b.LongDescription)));
            }
            catch
            {
                // Non-critical: search degrades gracefully if this fails.
            }
        });

        Window = new MainWindow();
        Window.Activate();
    }

    internal static MainWindow? Window { get; private set; }
    internal static IServiceProvider? Services { get; private set; }
    internal static ISettingsStore? SettingsStore { get; private set; }
    internal static ILibraryRepository? LibraryRepository { get; private set; }
    internal static IBookTitleExtractor? TitleExtractor { get; private set; }
    internal static AzureOpenAiAnalysisClient? AnalysisClient { get; private set; }
    internal static ScanWorkflowService? WorkflowService { get; private set; }
    internal static SemanticSearchService? SemanticSearchService { get; private set; }
}
