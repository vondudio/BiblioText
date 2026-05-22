# BiblioText

A WinUI 3 / .NET 9 desktop application for scanning bookshelves, detecting individual book spines with YOLO object detection, and extracting titles and authors via Azure OpenAI vision. Detected books are reviewed, edited, and saved to a local SQLite library with bookshelf location tagging.

## Features

- **Scan** — Load bookshelf photos (file picker, drag-and-drop, or live camera capture) and run YOLO detection to identify individual book spines with numbered bounding boxes.
- **AI Analysis** — Send the annotated bookshelf image to Azure OpenAI GPT-5.4 vision to extract title and author for each numbered detection.
- **Review** — Review detected books with editable title/author fields, cropped spine thumbnails, accept/reject per item, and bookshelf location tagging before committing to the library.
- **Library** — Browse all saved books with spine images, detection index badges, and inline editing of title/author via ellipsis menu.
- **Camera Capture** — On-device camera (front/rear selectable) with live preview dialog and still photo capture for processing.
- **Settings** — Configure Azure OpenAI endpoint, API key, deployment name, and camera preferences.

## Detection Models

| Family | Files | Head | Output | Postprocess |
|---|---|---|---|---|
| YOLOv4 (legacy) | `yolov4.onnx` (~257 MB, included) | anchor-based, 3-grid | `[1, gY, gX, 3, 5+nc]` × 3 | sigmoid + exp anchor decode + class-grouped NMS |
| Ultralytics YOLO26 | `yolo26{n,s,m,l}.onnx` | end-to-end (one-to-one) NMS-free | `[1, 300, 6]` | confidence filter + letterbox-undo |
| Ultralytics YOLO26 | `yolo26{n,s,m,l}-o2m.onnx` | one-to-many | `[1, 4+nc, 8400]` | argmax + class-grouped NMS |

The `Models\ModelInfo.cs` registry only lists models whose `.onnx` is actually present on disk, so missing files are silently hidden from the picker.

---

## Prerequisites

- Windows 11 (x64 or ARM64).
- Visual Studio 2022 17.12+ with the **Universal Windows Platform development** workload (the `.vsconfig` in the repo pins this) and the **.NET Desktop / WinUI** components, or:
- The **.NET 10 SDK** + **Windows 11 SDK 26100** + **Windows App SDK 2.0 experimental** runtime.
- (Optional, for YOLO26 weights) **Python 3.8+** on `PATH`.
- (Optional, for AI analysis) **Azure OpenAI** resource with a GPT-5.4 vision deployment.

---

## Build & run

### From Visual Studio
1. Open `YOLO_Object_DetectionSample.sln`.
2. Solution Platform → **x64** (or **ARM64** on Snapdragon).
3. Right-click the project → **Set as Startup Project** → **F5** (deploys as a packaged WinUI app).

### From the command line
```pwsh
dotnet restore .\YOLO_Object_DetectionSample.csproj
dotnet build   .\YOLO_Object_DetectionSample.csproj -c Debug   -p:Platform=ARM64
dotnet build   .\YOLO_Object_DetectionSample.csproj -c Release -p:Platform=ARM64
```
Use `-p:Platform=x64` on Intel/AMD devices.

---

## Get the YOLO26 ONNX weights

The YOLO26 weights are licensed **AGPL-3.0** by Ultralytics and are not redistributed in this repo. The included script has two paths; the default needs no Python and works fine on Snapdragon / arm64.

### Default — direct download (recommended)
```pwsh
.\scripts\download-models.ps1                       # n + s + m detection
.\scripts\download-models.ps1 -Sizes n,m            # just two sizes
.\scripts\download-models.ps1 -Sizes n,s,m,l        # also the large detection model
.\scripts\download-models.ps1 -Tasks seg            # segmentation variants (n + s + m)
.\scripts\download-models.ps1 -Tasks detect,seg     # everything
```
Pulls pre-exported ONNX from the community Hugging Face mirror at
`https://huggingface.co/zwh20081/yolo26-onnx`. Detection files are the Ultralytics
one-to-one (end-to-end, NMS-free) export with input `[1, 3, 640, 640]` and output
`[1, 300, 6]`. Segmentation files have the same input plus an extra prototype
tensor (`output1: [1, 32, 160, 160]`) and the per-detection vector grows to
`[1, 300, 38]` (4 box coords + score + classId + 32 mask coefficients). Existing
files in `Models\` are skipped. Pin a revision with `-Ref <commit-sha>` for
byte-for-byte reproducibility.

### Fallback — local export from Ultralytics (`.pt` → `.onnx`)
If you need the one-to-many head (`*-o2m.onnx`), a custom `opset`, or you don't want
to trust the community mirror:
```pwsh
.\scripts\download-models.ps1 -UseUltralytics                 # n + s + m, both heads
.\scripts\download-models.ps1 -UseUltralytics -Sizes n,m
```
Requires **Python 3.11+** on `PATH`. Creates an isolated venv at
`%LOCALAPPDATA%\YOLO_Object_DetectionSample\venv`, installs `ultralytics`, then runs
`YOLO("yolo26{size}.pt").export(format="onnx", opset=17, simplify=True, end2end=...)`.
On Windows arm64, PyTorch wheels are only available for `torch >= 2.7` on Python
3.11/3.12/3.13. If pip's resolver complains, install torch from the CPU index first:
```pwsh
& "$env:LOCALAPPDATA\YOLO_Object_DetectionSample\venv\Scripts\python.exe" -m pip `
    install torch --extra-index-url https://download.pytorch.org/whl/cpu
```
then re-run the script.

After either path, just rebuild / F5 — the project's `<Content Include="Models\*.onnx" />`
glob picks up the new files automatically.

> Approximate ONNX file sizes: `n` ~10 MB, `s` ~37 MB, `m` ~80 MB, `l` ~98 MB.

---

## UI

The app uses a **NavigationView** shell with four pages:

### Scan Page
- **Model picker** (top right) — lists every model whose `.onnx` is present in `Models\`. Switching models re-runs detection.
- **Confidence slider** — inference re-runs only on slider release (no spamming on drag).
- **Upload / Drag-and-drop** — import `.png`, `.jpg`, `.jpeg`, `.bmp` files.
- **Camera Capture** — opens a dialog with live camera preview (front/rear selectable) and captures a still photo at the device's native resolution.
- **Queue strip** (bottom) — thumbnail tiles of loaded images with an import tile and remove button.
- **EXTRACT TITLES** — crops each detection and sends to AI for title/author extraction.
- **AI ANALYZE** — sends the full annotated image (with numbered bounding boxes) to Azure OpenAI GPT-5.4 vision. The AI references detections by their numbered labels.
- **Numbered bounding boxes** — each detection gets a 1-based numerical ID badge drawn on the image.

### Review Page
- Lists detected books with editable title and author fields.
- Cropped spine thumbnail to the left of each item — click to view an enlarged zoomable overlay.
- Accept/reject toggle per book.
- **Location dropdown** — assign a bookshelf location before saving.
- **Save to Library** — commits accepted books to the SQLite database.

### Library Page
- Browse all saved books with spine image thumbnails and detection index badges.
- Ellipsis menu per book for inline editing of title or author.
- Books display their bookshelf location.

### Settings Page
- Azure OpenAI endpoint, API key, and deployment name configuration.
- Camera toggle (enable/disable camera capture feature).

### Image Viewer (Scan Page)
- Mouse wheel zooms (no Ctrl needed), capped at 3×, anchored on cursor position.
- Left-click + drag pans when zoomed in.
- Double-click resets to 1×.
- EXIF orientation is normalized at load time.

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  MainWindow.xaml  (NavigationView shell)            │
│  ├── Sample.xaml         (Scan page)                │
│  ├── Pages/ReviewPage    (Review detected books)    │
│  ├── Pages/LibraryPage   (Saved book library)       │
│  └── Pages/SettingsPage  (Configuration)            │
└─────────────────────────────────────────────────────┘

Models/           Domain models (Book, ReviewCandidate, ImageItem, Scan, Location)
Services/         AI integration (AzureOpenAiAnalysisClient, CropExtractor, ScanWorkflowService)
Persistence/      SQLite repository (ILibraryRepository, SqliteLibraryRepository)
Settings/         Encrypted settings storage (DPAPI + environment variable fallback)
Utils/            Image processing (BitmapFunctions, YOLOHelpers, Letterbox)
ViewModels/       MVVM view models (CommunityToolkit.Mvvm)
scripts/          Model download automation
```

### Key Design Decisions
- **NavigationCacheMode.Required** on all pages — pane state is preserved when navigating between tabs.
- **Per-image inference cache** — switching back to a previously-processed image is instant.
- **Camera via MediaCapture API** — uses `CapturePhotoToStreamAsync` for still photos (not frame grab). On ARM64, discovers `MediaFrameSourceGroup` matching the device for reliable preview.
- **Single ContentDialog rule** — WinUI 3 only allows one ContentDialog open at a time; camera dialog uses `Opened` event for deferred init.
- **Azure OpenAI GPT-5.4** — uses `max_completion_tokens` (not `max_tokens`), `api-key` header, API version `2024-10-21`.
- **SQLite persistence** — auto-migrates schema with `ALTER TABLE ADD COLUMN` for new fields.

---

## Configuration

On first launch, go to **Settings** and configure:

| Setting | Description |
|---|---|
| Azure OpenAI Endpoint | e.g. `https://your-resource.openai.azure.com/` |
| API Key | Your Azure OpenAI resource key |
| Deployment Name | The GPT-5.4 (or compatible vision model) deployment name |

Settings are encrypted at rest using DPAPI and stored in the app's local data folder.

---

## Manual Test Matrix

| Area | Test |
|---|---|
| **Scan** | Import image → run detection → numbered boxes appear |
| **Camera** | Click Capture → preview shows → take photo → image loads for detection |
| **AI Analyze** | Run detection → click AI Analyze → titles/authors populate → navigate to Review |
| **Extract Titles** | Run detection → Extract Titles → crops saved to temp → per-crop results shown |
| **Review** | Accept/reject books, edit title/author, set location, click crop thumbnail → overlay |
| **Review overlay** | Tap overlay → closes; zoom in overlay → pinch/scroll works |
| **Library** | Save from Review → books appear with spine images and detection badges |
| **Library edit** | Ellipsis → edit title/author → changes persist |
| **Settings** | Set Azure endpoint + key + deployment → values persist across restart |
| **Navigation** | Switch between pages → state preserved (NavigationCacheMode) |
| **Models** | Switch model in picker → re-detects with new model |
| **Confidence** | Drag slider → re-detects only on release |
| **Zoom** | Scroll-zoom to 3×, drag pan, double-click reset |

Run on each EP available on your machine (CPU + DirectML on x64; CPU + QNN on Snapdragon X).

---

## Project Layout

```
Models/
  ModelInfo.cs              # ModelInfo record + ModelRegistry (file-presence filtered)
  ImageItem.cs             # Loaded image: bitmap + thumbnail + per-(model,conf) cache
  Book.cs                  # Book entity (title, author, location, spine image, detection index)
  ReviewCandidate.cs       # Detection candidate for review (crop, confidence, index)
  Location.cs              # Bookshelf location model
  Scan.cs                  # Scan session model
  CocoLabels.cs            # 80 COCO classes
  yolov4.onnx              # Bundled legacy model
  yolo26*.onnx             # Downloaded by scripts\download-models.ps1

Pages/
  ReviewPage.xaml(.cs)     # Review detected books, crop overlay, location tagging
  LibraryPage.xaml(.cs)    # Browse saved library with spine images
  SettingsPage.xaml(.cs)   # Azure OpenAI + camera configuration

Services/
  AzureOpenAiAnalysisClient.cs   # GPT-5.4 vision API for full-image analysis
  AzureOpenAiTitleExtractor.cs   # Per-crop title extraction
  CropExtractor.cs               # Crop bounding boxes from detection results
  ScanWorkflowService.cs         # Orchestrates crop → AI → ReviewCandidate pipeline
  BookshelfAnalysisPrompt.cs     # System prompt for bookshelf AI analysis

Persistence/
  ILibraryRepository.cs          # Repository interface
  SqliteLibraryRepository.cs     # SQLite CRUD with auto-migration

Settings/
  AppSettings.cs                 # App configuration model
  DpapiSettingsStore.cs          # DPAPI-encrypted settings storage
  CompositeSettingsStore.cs      # Cascading settings (DPAPI → environment)

Utils/
  BitmapFunctions.cs       # Resize, letterbox, NCHW/NHWC, render boxes with ID badges
  Letterbox.cs             # Scale/pad math
  YOLOHelpers.cs           # Prediction extraction (v4, YOLO26 E2E, one-to-many, NMS)
  WinMLHelpers.cs          # ExecutionProvider plumbing

ViewModels/                # MVVM view models (CommunityToolkit.Mvvm)

Sample.xaml(.cs)           # Main Scan page: detection, camera, AI analyze, extract
MainWindow.xaml(.cs)       # NavigationView shell
scripts/
  download-models.ps1      # YOLO26 weight download automation
```

---

## License notes

- The original sample code in this repo is provided under the AI Dev Gallery license.
- **Ultralytics YOLO26** weights and any models you derive from them are **AGPL-3.0**. An [enterprise license](https://www.ultralytics.com/license) is required for closed-source commercial distribution.
- COCO labels come from the COCO 2017 dataset (Common Objects in Context).
