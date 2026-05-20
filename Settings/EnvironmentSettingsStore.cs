#nullable enable

using System;

namespace AIDevGallery.Sample.Settings;

public sealed class EnvironmentSettingsStore : ISettingsStore
{
    public AppSettings Load()
    {
        return new AppSettings
        {
            AzureOpenAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            AzureOpenAiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            AzureOpenAiDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            ApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-10-21",
        };
    }

    public void Save(AppSettings settings)
    {
    }
}
