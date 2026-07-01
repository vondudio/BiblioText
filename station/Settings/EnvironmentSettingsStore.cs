#nullable enable

using System;

namespace BiblioText.Settings;

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
            GoogleBooksApiKey = Environment.GetEnvironmentVariable("GOOGLE_BOOKS_API_KEY"),
            CloudEndpoint = Environment.GetEnvironmentVariable("BIBLIOTEXT_CLOUD_ENDPOINT"),
            CloudOperatorToken = Environment.GetEnvironmentVariable("BIBLIOTEXT_CLOUD_OPERATOR_TOKEN"),
            StationId = Environment.GetEnvironmentVariable("BIBLIOTEXT_STATION_ID"),
            OwnerHousehold = Environment.GetEnvironmentVariable("BIBLIOTEXT_OWNER_HOUSEHOLD"),
        };
    }

    public void Save(AppSettings settings)
    {
    }
}
