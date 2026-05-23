# BiblioText - Copilot Instructions

## Build & Run

- **Build**: `dotnet build BiblioText.csproj -c Debug -p:Platform=ARM64`
- **Platform**: This is an ARM64 WinUI 3 / .NET 9 desktop app. Always use `-p:Platform=ARM64`.
- **Run**: Launch from Visual Studio or `dotnet run` (requires ARM64 platform).
- The project file is `BiblioText.csproj` (renamed from the original sample).

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
ConfidenceSlider.Value = initial.DefaultConfidence;  // in page load
ConfidenceSlider.Value = model.DefaultConfidence;    // on model change
```
To change the default, modify the code-behind assignment, not just the XAML attribute.

### AI title extraction response format

The title extraction API returns results as "Title, Author" (comma-separated). The parser in `ExtractButton_Click` must handle all separators:
- Comma: `"A Treasury of Kahlil Gibran, Kahlil Gibran"`
- Dash: `"Book Title - Author Name"`
- "by": `"Book Title by Author Name"`

### Azure OpenAI API specifics

- Model: GPT-5.4 (vision capable)
- Use `max_completion_tokens` not `max_tokens` (deprecated parameter)
- JSON responses use snake_case — C# properties need `[JsonPropertyName("snake_case")]` attributes; `PropertyNameCaseInsensitive` does NOT handle underscores

## Namespace & Paths

- Namespace: `BiblioText` (renamed from `AIDevGallery.Sample`)
- Git repo: `vondudio/BiblioText`
- Local disk path still uses `YOLO_Object_DetectionSample` directory name
- SQLite DB: `%LOCALAPPDATA%\YOLO_Object_DetectionSample\library.db`
