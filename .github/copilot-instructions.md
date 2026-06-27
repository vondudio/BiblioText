# BiblioText - Copilot Instructions

## Workflow (single-user dev, no team)

This is a one-developer project. The user runs the app from **Visual Studio**, with the
solution open at the primary checkout (referred to below as the "main checkout").
Sessions usually live in a different worktree (`copilot-worktrees\...`). Two rules
follow from that:

- **Don't propose worktree branching strategies, stacked PRs, or "let's keep this
  isolated."** Single user, single branch flow. Commit on whatever branch the
  session is on and push.
- **Always sync the main checkout after pushing.** When you commit & push from the worktree,
  immediately run in the main checkout (no need to ask first):
  ```powershell
  Push-Location <path-to-main-checkout>
  git fetch origin
  git reset --hard origin/<current-branch>
  Pop-Location
  ```
  Otherwise the VS instance the user is staring at keeps showing stale code and
  they have to ask "where is this, not running from VS."

## Build & Run

- **Build**: `dotnet build station\BiblioText.csproj -c Debug -p:Platform=ARM64 --nologo -v:m`
  (or build the whole solution: `dotnet build BiblioText.sln -c Debug -p:Platform=ARM64`).
- **Platform**: ARM64 WinUI 3 / .NET 9 desktop app. Always use `-p:Platform=ARM64` —
  the project will not load on x64/AnyCPU.
- **Run**: User launches from Visual Studio (F5). `dotnet run` works but is rarely
  the right answer — the user wants to debug in VS.
- **Monorepo layout**: the desktop capture station lives under `station/`
  (`station\BiblioText.csproj`); the shared station→cloud publish DTOs live in
  `contracts\BiblioText.Contracts.csproj`. `BiblioText.sln` at the repo root
  references both. The csproj was renamed from the original sample.


## Architecture

- **WinUI 3 / WinAppSDK** desktop app with NavigationView (Scan → Review → Library → Settings)
- **Scan pane** (`Sample.xaml/.cs`): YOLO object detection on bookshelf images, bounding boxes, crop extraction, AI title extraction
- **Review pane** (`Pages/ReviewPage.xaml/.cs`): Multi-scan queue, editable title/author list, save to library with AI descriptions
- **Library pane** (`Pages/LibraryPage.xaml/.cs`): SQLite-backed book catalog with search, sort, overlay views
- **Services/**: `BookDescriptionService`, `SemanticSearchService`, `ScanWorkflowService`, `TitleExtractorService`
- **Persistence/**: `SqliteLibraryRepository` — all DB access
- **Static singletons** in `App.xaml.cs`: SettingsStore, LibraryRepository, TitleExtractor, AnalysisClient, WorkflowService, SemanticSearchService

## Critical Patterns

### Object lifetime when clearing the scan pane

When deferring image processing (extraction/AI analysis) after removing an image from the UI:
- Remove the `ImageItem` from `_images` collection (clears UI)
- Do **NOT** call `item.Dispose()` until processing completes — `SourceBitmap` is still needed by `CropExtractor`
- Dispose in a `finally` block after all bitmap operations are done
- `RemoveActiveImageAsync()` calls Dispose — never use it before background processing

### XAML DataTemplate bindings

Do not wrap bound controls (TextBox, etc.) in additional Grid/StackPanel containers inside a `DataTemplate` unless you verify `{Binding ..., Mode=TwoWay}` still resolves. WinUI data context inheritance can break with restructured visual trees in templates.

### Confidence slider default

The XAML `Value="0.20"` on `ConfidenceSlider` is **overridden at startup** by:
```csharp
ConfidenceSlider.Value = initial.DefaultConfidence;  // in OnNavigatedTo
ConfidenceSlider.Value = model.DefaultConfidence;    // on model change
```
To change the default, edit the model's `DefaultConfidence` in `ModelRegistry.All`
(in `Models/ModelInfo.cs`) — **not** the XAML attribute and **not** the
assignment in code-behind. The currently-preferred model is whatever
`ModelRegistry.DefaultForViewing` returns (today: `yolo26l`).

### BitmapImage.PixelWidth is async — don't size overlays from it

`BitmapFunctions.EncodeBitmapToBitmapImage` calls `BitmapImage.SetSource`, which
decodes the PNG **asynchronously**. `PixelWidth` / `PixelHeight` stay at 0 until
the decode completes, so anything that runs synchronously right after (e.g.
sizing the scan pane's `BoxOverlay` Canvas, clamping draw-mode pointer coords,
laying out children at pixel offsets) will see zeros and silently lay things out
in a 0×0 coordinate space.

The reliable size source for the loaded image is `ImageItem.SourceBitmap.Width`
and `SourceBitmap.Height` (a synchronous System.Drawing.Bitmap). The scan
pane's `RefreshBoxOverlay` reads from there and only falls back to
`BitmapImage.PixelWidth/Height` when no SourceBitmap is available.

### AI title extraction response format

The title extraction API returns results as "Title, Author" (comma-separated). The parser in `ExtractButton_Click` must handle all separators:
- Comma: `"A Treasury of Kahlil Gibran, Kahlil Gibran"`
- Dash: `"Book Title - Author Name"`
- "by": `"Book Title by Author Name"`

### Azure OpenAI API specifics

- Model: GPT-5.4 (vision capable)
- Use `max_completion_tokens` not `max_tokens` (deprecated parameter)
- JSON responses use snake_case — C# properties need `[JsonPropertyName("snake_case")]` attributes; `PropertyNameCaseInsensitive` does NOT handle underscores

### Default prompts are versioned — bump `DefaultPrompts.CurrentVersion` on every change

`Services/BookshelfAnalysisPrompt.cs` defines `DefaultPrompts.SpineExtraction`,
`BookshelfAnalysisSystem`, `BookshelfAnalysisUser`, `BookDescription`. The
Settings UI shows `settings.<Foo>Prompt ?? DefaultPrompts.<Foo>` — so once a user
has saved any value (or persisted one from an older build), the in-source default
is masked forever.

`CompositeSettingsStore.Load` auto-migrates: if `AppSettings.PromptsVersion <
DefaultPrompts.CurrentVersion`, it nulls all four prompt overrides so the new
defaults take effect, then `Save` stamps the new version.

**Whenever you change a default prompt, bump `DefaultPrompts.CurrentVersion`.**
Otherwise the user's old saved prompt is what runs at runtime AND what the
Settings editor displays — the new default is invisible.

### Metadata enrichment pipeline (multi-provider → AI synthesis)

The description generation flow is fanned out across three providers in parallel,
not a fallback chain:

1. `CompositeMetadataLookupService` runs Google Books, Wikipedia, and Open
   Library concurrently via `Task.WhenAll`. Per-provider failure is isolated
   (`SafeLookupAsync` catches HttpRequestException / JsonException /
   TaskCanceledException). Union is aggregated and sorted by `MatchScore`.
2. `BookDescriptionService` passes those snippets as `sources` to Azure OpenAI,
   which synthesizes the final short + long descriptions in a single call. When
   all providers return empty, a synthetic `BookMetadataSource { Provider="AI" }`
   is injected so the badge system renders an "AI" chip.
3. Sources are serialized to `books.description_sources_json`. The Library page
   renders one badge per provider (`G`/`W`/`OL`/`AI`).

Provider-specific gotchas:

- **Google Books**: API key is **required** (anonymous quota = 0). Set via
  `Settings → Book Metadata Providers` (stored DPAPI-encrypted in
  `AppSettings.GoogleBooksApiKey`) or `GOOGLE_BOOKS_API_KEY` env var. Service
  returns `[]` silently if no key.
- **Wikipedia**: Free, no key. Must use `action=query&list=search` (relevance
  ranker), **not** `action=opensearch` (prefix matcher — concatenating
  "Title Author" returns garbage like "Don Herbert" for "Dune Herbert").
  Prefer page titles suffixed `(novel)/(book)/(memoir)/(play)/(poem)` when
  present.
- **Open Library**: Metadata-only — do not re-add the `/works/{key}.json`
  description hop, it produces noisy markdown blobs. Use it for covers,
  ratings, ISBN, subjects, first sentence.

### Layered Images in a Grid cell need explicit Visibility on every layer

When stacking `<Image>` elements in the same Grid cell (e.g. cover + spine
fallback in the Library / Review thumbnails), **every layer must have a
`Visibility` binding**. A null `Source` doesn't keep WinUI from painting the
empty image — the topmost declared Image will hide everything underneath it.
The library thumbnail bug went unnoticed because the spine `<Image>` was
declared after the cover with no Visibility binding, so it always painted over
a perfectly good Google Books / OL cover URL. Pattern to follow:

```xml
<Image Source="{Binding FallbackImage}" Visibility="{Binding FallbackVisibility}" />
<Image Source="{Binding PrimaryImage}"  Visibility="{Binding PrimaryVisibility}" />
```
with `PrimaryVisibility` Visible iff `PrimaryImage != null`, and `FallbackVisibility`
the inverse.

### DI registration of ISettingsStore

Register the composite store as a **concrete instance**, not a factory that
re-resolves the same interface:

```csharp
services.AddSingleton<ISettingsStore>(_ => new CompositeSettingsStore());
```

`CompositeSettingsStore`'s parameterless ctor wires up `DpapiSettingsStore` →
`EnvironmentSettingsStore` internally. Asking DI to inject `ISettingsStore` into
its own ctor causes `InvalidOperationException: A circular dependency was
detected for the service of type 'BiblioText.Settings.ISettingsStore'` at app
startup.

## Namespace & Paths

- Namespace: `BiblioText` (renamed from `AIDevGallery.Sample`)
- Git repo: `vondudio/BiblioText`
- Local disk path still uses `YOLO_Object_DetectionSample` directory name
- SQLite DB: `%LOCALAPPDATA%\YOLO_Object_DetectionSample\library.db`
