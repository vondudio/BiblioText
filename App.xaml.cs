using System;
using Microsoft.UI.Xaml;
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
        // Initialize settings
        SettingsStore = new CompositeSettingsStore();

        // Initialize SQLite repository
        var repo = new SqliteLibraryRepository();
        await repo.InitializeAsync();
        LibraryRepository = repo;

        // Initialize AI services
        TitleExtractor = new AzureOpenAiTitleExtractor(SettingsStore);
        AnalysisClient = new AzureOpenAiAnalysisClient(SettingsStore);
        WorkflowService = new ScanWorkflowService(TitleExtractor, AnalysisClient, LibraryRepository);

        // Initialize semantic search
        var searchService = new SemanticSearchService();
        await searchService.InitializeAsync();
        SemanticSearchService = searchService;

        Window = new MainWindow();
        Window.Activate();
    }

    internal static MainWindow? Window { get; private set; }
    internal static ISettingsStore? SettingsStore { get; private set; }
    internal static ILibraryRepository? LibraryRepository { get; private set; }
    internal static IBookTitleExtractor? TitleExtractor { get; private set; }
    internal static AzureOpenAiAnalysisClient? AnalysisClient { get; private set; }
    internal static ScanWorkflowService? WorkflowService { get; private set; }
    internal static SemanticSearchService? SemanticSearchService { get; private set; }
}