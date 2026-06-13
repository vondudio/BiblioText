using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BiblioText.Settings;
using BiblioText.Services;

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
}
