using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BiblioText.Settings;
using BiblioText.Services;
using Windows.Storage.Pickers;

namespace BiblioText.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
        VersionText.Text = $"Version {GetAppVersion()}";
        this.Loaded += SettingsPage_Loaded;
    }

    private static string GetAppVersion()
    {
        var asm = typeof(SettingsPage).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        var store = App.SettingsStore;
        if (store == null) return;

        var settings = store.Load();
        EndpointBox.Text = settings.AzureOpenAiEndpoint ?? "";
        ApiKeyBox.Password = settings.AzureOpenAiApiKey ?? "";
        DeploymentBox.Text = settings.AzureOpenAiDeployment ?? "";
        ApiVersionBox.Text = settings.ApiVersion ?? "2024-10-21";
        UseCameraToggle.IsOn = settings.UseCameraCapture;
        GoogleBooksKeyBox.Password = settings.GoogleBooksApiKey ?? "";

        SpinePromptBox.Text = settings.SpineExtractionPrompt ?? Services.DefaultPrompts.SpineExtraction;
        BookshelfSystemPromptBox.Text = settings.BookshelfAnalysisSystemPrompt ?? Services.DefaultPrompts.BookshelfAnalysisSystem;
        BookshelfUserPromptBox.Text = settings.BookshelfAnalysisUserPrompt ?? Services.DefaultPrompts.BookshelfAnalysisUser;
        DescriptionPromptBox.Text = settings.BookDescriptionPrompt ?? Services.DefaultPrompts.BookDescription;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var store = App.SettingsStore;
        if (store == null) return;

        var settings = new AppSettings
        {
            AzureOpenAiEndpoint = EndpointBox.Text.Trim(),
            AzureOpenAiApiKey = ApiKeyBox.Password,
            AzureOpenAiDeployment = DeploymentBox.Text.Trim(),
            ApiVersion = ApiVersionBox.Text.Trim(),
            UseCameraCapture = UseCameraToggle.IsOn,
            GoogleBooksApiKey = NullIfEmpty(GoogleBooksKeyBox.Password),
            SpineExtractionPrompt = NullIfEmpty(SpinePromptBox.Text),
            BookshelfAnalysisSystemPrompt = NullIfEmpty(BookshelfSystemPromptBox.Text),
            BookshelfAnalysisUserPrompt = NullIfEmpty(BookshelfUserPromptBox.Text),
            BookDescriptionPrompt = NullIfEmpty(DescriptionPromptBox.Text),
        };

        store.Save(settings);

        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = "Settings saved successfully.";
    }

    private void UseCameraToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var store = App.SettingsStore;
        if (store == null) return;

        var settings = store.Load();
        settings.UseCameraCapture = UseCameraToggle.IsOn;
        store.Save(settings);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            AzureOpenAiEndpoint = EndpointBox.Text.Trim(),
            AzureOpenAiApiKey = ApiKeyBox.Password,
            AzureOpenAiDeployment = DeploymentBox.Text.Trim(),
            ApiVersion = ApiVersionBox.Text.Trim()
        };

        if (!settings.IsConfigured)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "Please fill in all fields before testing.";
            return;
        }

        TestButton.IsEnabled = false;
        try
        {
            using var client = AzureOpenAiHttp.CreateClient();
            var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";
            var serializedBody = JsonSerializer.Serialize(new
            {
                messages = new object[]
                {
                    new { role = "user", content = "Say hello" }
                },
                max_completion_tokens = 5
            });

            var result = await AzureOpenAiHttp.SendAsync(
                client,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
                    request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
                    return request;
                },
                default);

            if (result.IsSuccess)
            {
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = "Connection successful! Azure OpenAI is reachable.";
            }
            else
            {
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Message = result.DiagnosticDetail == null
                    ? $"Connection failed: {result.ErrorMessage}"
                    : $"Connection failed: {result.ErrorMessage} {result.DiagnosticDetail}";
            }
        }
        catch (Exception ex)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ResetPromptsButton_Click(object sender, RoutedEventArgs e)
    {
        SpinePromptBox.Text = Services.DefaultPrompts.SpineExtraction;
        BookshelfSystemPromptBox.Text = Services.DefaultPrompts.BookshelfAnalysisSystem;
        BookshelfUserPromptBox.Text = Services.DefaultPrompts.BookshelfAnalysisUser;
        DescriptionPromptBox.Text = Services.DefaultPrompts.BookDescription;
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string SpinesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BiblioText",
        "spines");

    // ---- Library Data: Export to .zip -------------------------------------

    private async void ExportLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var repo = App.LibraryRepository;
        if (repo == null) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"BiblioText-backup-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("Zip archive", new List<string> { ".zip" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        ExportLibraryButton.IsEnabled = false;
        ResetLibraryButton.IsEnabled = false;
        try
        {
            var books = await repo.GetBooksAsync();
            var bookshelfPaths = books
                .Select(b => b.BookshelfImagePath)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var dbPath = repo.DatabasePath;
            var destPath = file.Path;
            int bookCount = books.Count;

            await Task.Run(() => BuildBackupZip(destPath, dbPath, SpinesFolder, bookshelfPaths, bookCount));

            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"Exported {bookCount} book(s) to {Path.GetFileName(destPath)}.";
        }
        catch (Exception ex)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Export failed: {ex.Message}";
        }
        finally
        {
            ExportLibraryButton.IsEnabled = true;
            ResetLibraryButton.IsEnabled = true;
        }
    }

    private static void BuildBackupZip(
        string destZipPath,
        string dbPath,
        string spinesFolder,
        IReadOnlyList<string> bookshelfPaths,
        int bookCount)
    {
        if (File.Exists(destZipPath))
        {
            File.Delete(destZipPath);
        }

        using var zipStream = new FileStream(destZipPath, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // Database (copied via a shared read so an open connection doesn't block it).
        if (File.Exists(dbPath))
        {
            AddFileShared(archive, dbPath, "library.db");
        }

        // Spine crops.
        if (Directory.Exists(spinesFolder))
        {
            foreach (var spine in Directory.EnumerateFiles(spinesFolder))
            {
                AddFileShared(archive, spine, $"spines/{Path.GetFileName(spine)}");
            }
        }

        // Original bookshelf photos (may live outside app storage). Dedupe entry
        // names so two imports with the same filename don't collide.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in bookshelfPaths)
        {
            var name = Path.GetFileName(photo);
            var entryName = $"bookshelf/{name}";
            int n = 1;
            while (!used.Add(entryName))
            {
                entryName = $"bookshelf/{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}";
            }
            AddFileShared(archive, photo, entryName);
        }

        var manifest = new StringBuilder();
        manifest.AppendLine("BiblioText library backup");
        manifest.AppendLine($"Created: {DateTime.Now:O}");
        manifest.AppendLine($"Books: {bookCount}");
        manifest.AppendLine();
        manifest.AppendLine("Contents:");
        manifest.AppendLine("  library.db   - SQLite catalog (books, scans, locations)");
        manifest.AppendLine("  spines/      - cropped book-spine images");
        manifest.AppendLine("  bookshelf/   - original bookshelf photos");
        manifest.AppendLine();
        manifest.AppendLine("To restore, copy library.db and the spines/ folder back into");
        manifest.AppendLine("%LOCALAPPDATA%\\BiblioText\\.");
        var entry = archive.CreateEntry("README.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(manifest.ToString());
    }

    private static void AddFileShared(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        source.CopyTo(entryStream);
    }

    // ---- Library Data: Reset ----------------------------------------------

    private async void ResetLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var repo = App.LibraryRepository;
        if (repo == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Reset library?",
            Content = "This permanently deletes every book, scan, location, spine image, "
                      + "and search index entry so you can start fresh. Your Azure / Google "
                      + "Books keys and prompt settings are kept.\n\nThis cannot be undone. "
                      + "Consider exporting a backup first.",
            PrimaryButtonText = "Reset library",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        ExportLibraryButton.IsEnabled = false;
        ResetLibraryButton.IsEnabled = false;
        try
        {
            // Capture ids before wiping so we can purge the OS semantic index.
            var books = await repo.GetBooksAsync();
            var ids = books.Select(b => b.Id).ToList();

            await repo.ResetLibraryAsync();

            // Delete spine crop files on disk.
            await Task.Run(() =>
            {
                if (Directory.Exists(SpinesFolder))
                {
                    foreach (var f in Directory.EnumerateFiles(SpinesFolder))
                    {
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
                }
            });

            // Purge the semantic search index.
            var search = App.SemanticSearchService;
            if (search != null)
            {
                await search.ClearAllAsync(ids);
            }

            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = "Library reset. Restart the app or revisit the Library to see it empty.";
        }
        catch (Exception ex)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Reset failed: {ex.Message}";
        }
        finally
        {
            ExportLibraryButton.IsEnabled = true;
            ResetLibraryButton.IsEnabled = true;
        }
    }
}
