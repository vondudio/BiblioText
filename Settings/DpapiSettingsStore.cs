#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIDevGallery.Sample.Settings;

public sealed class DpapiSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public DpapiSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YOLO_Object_DetectionSample",
            "settings.dat"))
    {
    }

    public DpapiSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        byte[] protectedBytes = File.ReadAllBytes(_settingsPath);
        byte[] jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(jsonBytes, SerializerOptions);
        return settings ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        byte[] protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_settingsPath, protectedBytes);
    }
}
