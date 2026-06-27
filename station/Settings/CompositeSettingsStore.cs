#nullable enable

using System;

namespace BiblioText.Settings;

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
        AppSettings effective = settings.IsConfigured ? settings : _fallbackStore.Load();
        MigratePrompts(effective);
        return effective;
    }

    public void Save(AppSettings settings)
    {
        settings.PromptsVersion = Services.DefaultPrompts.CurrentVersion;
        _primaryStore.Save(settings);
    }

    private static void MigratePrompts(AppSettings settings)
    {
        if (settings.PromptsVersion >= Services.DefaultPrompts.CurrentVersion)
        {
            return;
        }

        // Stale defaults from older builds: discard saved overrides so the
        // current DefaultPrompts.* values take effect in both the Settings
        // editor and at runtime. User keeps a Reset button if they want to
        // re-apply.
        settings.SpineExtractionPrompt = null;
        settings.BookshelfAnalysisSystemPrompt = null;
        settings.BookshelfAnalysisUserPrompt = null;
        settings.BookDescriptionPrompt = null;
        settings.PromptsVersion = Services.DefaultPrompts.CurrentVersion;
    }
}
