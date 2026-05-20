#nullable enable

using System;

namespace AIDevGallery.Sample.Settings;

public sealed class CompositeSettingsStore : ISettingsStore
{
    private readonly ISettingsStore _primaryStore;
    private readonly ISettingsStore _fallbackStore;

    public CompositeSettingsStore()
        : this(new DpapiSettingsStore(), new EnvironmentSettingsStore())
    {
    }

    public CompositeSettingsStore(ISettingsStore primaryStore, ISettingsStore fallbackStore)
    {
        _primaryStore = primaryStore ?? throw new ArgumentNullException(nameof(primaryStore));
        _fallbackStore = fallbackStore ?? throw new ArgumentNullException(nameof(fallbackStore));
    }

    public AppSettings Load()
    {
        AppSettings settings = _primaryStore.Load();
        return settings.IsConfigured ? settings : _fallbackStore.Load();
    }

    public void Save(AppSettings settings)
    {
        _primaryStore.Save(settings);
    }
}
