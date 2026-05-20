#nullable enable

namespace AIDevGallery.Sample.Settings;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
