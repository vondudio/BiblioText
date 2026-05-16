# YOLO Object Detection Sample

A WinUI 3 / .NET 9 desktop sample (`AIDevGallery.Sample`) that runs YOLO object detectors on the local machine via [ONNX Runtime](https://onnxruntime.ai/) + the Windows AI `ExecutionProviderCatalog`. Pick an image with the upload button, choose a model from the picker, and the boxes are drawn over the source bitmap.

The sample now ships with two model families:

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

---

## Build & run

### From Visual Studio
1. Open `YOLO_Object_DetectionSample.sln`.
2. Solution Platform → **x64** (or **ARM64** on Snapdragon).
3. Right-click the project → **Set as Startup Project** → **F5** (deploys as a packaged WinUI app).
4. Default image (`Assets\team.jpg`) is detected on launch. Use the upload button for any `.png`, `.jpg`, `.jpeg`, or `.bmp`.

### From the command line
```pwsh
dotnet restore .\YOLO_Object_DetectionSample.csproj
dotnet build   .\YOLO_Object_DetectionSample.csproj -c Debug   -p:Platform=x64
dotnet build   .\YOLO_Object_DetectionSample.csproj -c Release -p:Platform=x64
```
Use `-p:Platform=ARM64` on Snapdragon devices.

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

- **Model picker** (top right) — lists every model whose `.onnx` is present in `Models\`. Switching models disposes the current `InferenceSession`, reloads, and re-activates the current image (cache hit on the new `(modelId, confidence)` key is instant).
- **Confidence slider + spin entry** (top right) — slider has a TextBox + RepeatButton spinner pair coupled to it.
  - Inference only re-runs **on slider release** (pointer up / capture lost) — dragging never spams the model.
  - Keyboard nudges and spin-button clicks fall through a 350 ms `DispatcherTimer` debounce.
  - Defaults: 0.5 for yolov4, 0.45 for YOLO26 medium (the launch default), 0.25 for the other YOLO26 sizes.
- **Status bar** (bottom left) — detection count and stage timings: `pre`, `infer`, `post`, `total` ms. Shows `(cached)` when a previously-computed result is restored from the per-image cache.

### Bottom queue strip
- **`QUEUE / PENDING`** panel docked below the main viewer, with a live "N ITEMS" counter on the right.
- **120 × 120 thumbnail tiles** showing the filename underneath. The active tile gets an accent-color border. Click a tile to make it active.
- **Import New Scan tile** (last item in the strip) — dashed-border tile with an up-arrow icon. Click to open the multi-file picker; the first selected file becomes active, the rest are appended.
- **Drag-and-drop** — drop one or more `.png` / `.jpg` / `.jpeg` / `.bmp` files anywhere on the main viewer or the strip to add them. Other extensions are silently skipped.
- **REMOVE button** at the bottom-left of the panel deletes the active image, disposes its bitmaps + cache, and activates the neighboring tile.
- **EXTRACT TITLES button** next to REMOVE. Crops every detection on the active image, JPEG-encodes each crop (default ≤1024 px long edge, quality 85), saves them to `%TEMP%\YOLO_Crops\<image-stem>_<HHmmss>\` for human inspection, and pipes each through `IBookTitleExtractor.ExtractAsync(...)`. Result dialog shows per-crop dimensions + bytes + the extracted title with a button to open the temp folder. Uses `StubBookTitleExtractor` today; swap to a real Azure OpenAI GPT-5.4 client at `Services\StubBookTitleExtractor.cs` (the file's XML doc has the exact data-URL + endpoint contract).
- **Per-image cache.** Each `ImageItem` keeps a `Dictionary<(modelId|conf2dp), CachedOutput>` of rendered outputs + the underlying predictions (and per-pixel masks for `-seg` models). Switching back to an image you've already processed at the current model + confidence shows the result instantly with `(cached)` in the status bar — no re-inference. EXTRACT TITLES uses the cached predictions directly so it never re-runs the model.

### Segmentation models (`-seg`)
Selecting any `yolo26{n,s,m,l}-seg` entry in the model picker switches the inference path to use the prototype-mask output. The viewer overlays a translucent class-colored mask under the bounding box. EXTRACT TITLES then uses those per-pixel masks to crop with the background filled white (cleaner OCR / vision-model input than a rectangular crop). Run `.\scripts\download-models.ps1 -Tasks seg` to fetch the seg files; missing files are silently filtered out of the picker.

### Image viewer
- The image fills the available window space at 1× while preserving aspect ratio (`Viewbox` + `Stretch="Uniform"` inside a `ScrollViewer` whose viewport drives the Viewbox dimensions).
- **Mouse wheel** zooms in/out — plain scroll, no Ctrl required. ~10% per notch, capped at **3×**, anchored on the cursor so the pixel under the pointer stays put.
- **Left-click + drag** pans the image when zoomed in (`ZoomFactor > 1`); the gesture captures the pointer and drives `ScrollViewer.ChangeView` from the pointer delta.
- **Double-click** resets to 1× and scrolls back to (0, 0).
- **EXIF orientation** is normalized at load time (`BitmapFunctions.NormalizeOrientation`) so phone-camera portraits with `Orientation=6` are inferenced and rendered upright — boxes line up with the displayed `BitmapImage`, which honors EXIF natively.

---

## Manual test matrix

There are no automated tests in this repo. Smoke-test with the matrix below after any change to the inference pipeline:

| Model | Expected output on `Assets\team.jpg` |
|---|---|
| `yolov4` | Several `person` boxes (legacy decoder) |
| `yolo26n` (E2E) | Same `person` set, lower latency, no NMS step in the status bar |
| `yolo26s` (E2E) | Same set with higher confidence scores |
| `yolo26m` (E2E) | Same set + occasional secondary-object boxes (`tie`, `cell phone`) |
| `yolo26{n,s,m}-o2m` | Same boxes as the E2E counterparts; postprocessing `post` time is non-zero (NMS) |

Also smoke-test the **viewer**: scroll-zoom up to 3× anchored on the cursor, left-drag to pan, double-click to reset, and drag the confidence slider end-to-end — the model should re-run only once when you let go. Drop an upright phone-camera JPEG (EXIF Orientation 6) on the upload picker and confirm both the image and its boxes display upright, not sideways.

Smoke-test the **queue strip**: import 2-3 photos via the Import New Scan tile, click between tiles (active one gets the accent border, image swaps with no stale boxes), drag-and-drop another file onto the viewer (it appends), change models — every tile keeps its own cache so switching back is instant with `(cached)` in the status bar. Hit REMOVE on the active tile; its neighbor activates and the count decrements.

Run on each EP available on your machine (CPU + DirectML on x64; CPU + QNN on Snapdragon X). The current UI uses the CPU EP; non-CPU EPs are wired up in `Utils\WinMLHelpers.cs` for callers that want to add an EP picker.

---

## Project layout

```
Models\
  ModelInfo.cs       # ModelInfo record + ModelRegistry (file-presence filtered; yolo26m is the launch default)
  ImageItem.cs       # One loaded image: pristine bitmap + pre-decoded BitmapImage + thumbnail + per-(model,conf) cache
  CocoLabels.cs      # 80 canonical COCO classes (YOLO26 returns 0..79)
  yolov4.onnx        # bundled legacy model
  yolo26*.onnx       # downloaded by scripts\download-models.ps1
Utils\
  BitmapFunctions.cs # Resize, letterbox, NCHW & NHWC preprocess, render boxes, EXIF orientation normalization
  Letterbox.cs       # Scale/pad math + UndoOnBox helper for postprocess
  YOLOHelpers.cs     # ExtractPredictions (v4), ExtractYolo26EndToEnd, ExtractYolo26OneToMany, ApplyNms
  WinMLHelpers.cs    # ExecutionProvider plumbing (DML/QNN/OpenVINO/EPContext)
  DeviceUtils.cs     # DXGI adapter & EP enumeration
scripts\
  download-models.ps1
Sample.xaml          # Main viewer (row 0) + bottom QUEUE/PENDING thumbnail strip (row 1)
Sample.xaml.cs       # ObservableCollection<ImageItem> _images, ActivateImageAsync, DetectObjects, wheel-zoom + pan + slider debounce
SelectedBorderThicknessConverter.cs  # Toggles tile accent ring from ImageItem.IsSelected
MainWindow.xaml.cs   # Mica window host, ShowException, ModelLoaded
App.xaml             # Registers SelectedBorderThicknessConverter as a global resource
App.xaml.cs          # Application entry
```

---

## License notes

- The original sample code in this repo is provided under the AI Dev Gallery license.
- **Ultralytics YOLO26** weights and any models you derive from them are **AGPL-3.0**. An [enterprise license](https://www.ultralytics.com/license) is required for closed-source commercial distribution.
- COCO labels come from the COCO 2017 dataset (Common Objects in Context).
