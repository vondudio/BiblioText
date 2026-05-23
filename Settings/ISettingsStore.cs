#nullable enable

namespace BiblioText.Settings;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
