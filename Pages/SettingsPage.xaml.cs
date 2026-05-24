using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Net.Http;
using BiblioText.Settings;

namespace BiblioText.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += SettingsPage_Loaded;
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

        SpinePromptBox.Text = settings.SpineExtractionPrompt ?? "";
        BookshelfSystemPromptBox.Text = settings.BookshelfAnalysisSystemPrompt ?? "";
        BookshelfUserPromptBox.Text = settings.BookshelfAnalysisUserPrompt ?? "";
        DescriptionPromptBox.Text = settings.BookDescriptionPrompt ?? "";
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
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("api-key", settings.AzureOpenAiApiKey);
            var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";

            var body = new StringContent(
                """{"messages":[{"role":"user","content":"Say hello"}],"max_completion_tokens":5}""",
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, body);

            if (response.IsSuccessStatusCode)
            {
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Message = "Connection successful! Azure OpenAI is reachable.";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Message = $"Connection failed: {response.StatusCode} — {content[..Math.Min(content.Length, 200)]}";
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
}
