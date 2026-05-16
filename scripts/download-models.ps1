<#
.SYNOPSIS
    Downloads pre-exported Ultralytics YOLO26 ONNX models into the Models\ folder.

.DESCRIPTION
    Pulls ready-to-use *.onnx files from the community Hugging Face mirror
    https://huggingface.co/zwh20081/yolo26-onnx (no Python or PyTorch needed).

    Each file is the Ultralytics one-to-one (end-to-end / NMS-free) export with
    input [1, 3, 640, 640] and output [1, 300, 6] - exactly what
    YOLOHelpers.ExtractYolo26EndToEnd expects.

    Files already present in Models\ are skipped. Hash and size are not verified
    against an upstream manifest because the mirror does not publish one - if
    integrity matters, re-export from your own ultralytics install (see
    -UseUltralytics) or pin to a specific revision via -Ref.

    NOTE: YOLO26 weights are AGPL-3.0 (Ultralytics). Review
    https://www.ultralytics.com/license before redistributing.

.PARAMETER Sizes
    Which model sizes to fetch. Defaults to n, s, m. Supports n, s, m, l, x.

.PARAMETER Tasks
    Which model variants to fetch. Defaults to detect. Supports detect, seg.
    'detect' -> yolo26{size}.onnx (object detection, output [1,300,6])
    'seg'    -> yolo26{size}-seg.onnx (instance segmentation, output [1,300,38] + prototype tensor [1,32,160,160])

.PARAMETER ModelsDir
    Output directory. Defaults to <repo-root>\Models.

.PARAMETER Ref
    Hugging Face revision (branch / tag / commit) to pin against. Defaults to 'main'.

.PARAMETER UseUltralytics
    Skip the HF download and instead invoke the Ultralytics Python exporter to
    produce ONNX from the .pt checkpoints (requires Python 3.11+ and PyTorch).
    Use this if you need the one-to-many head, a custom opset, or you don't want
    to trust the community mirror.

.EXAMPLE
    .\scripts\download-models.ps1                               # n + s + m detect
    .\scripts\download-models.ps1 -Sizes n,m
    .\scripts\download-models.ps1 -Tasks seg -Sizes n,m         # n + m segmentation
    .\scripts\download-models.ps1 -Tasks detect,seg             # both for n + s + m
    .\scripts\download-models.ps1 -UseUltralytics -Sizes n,m
#>
[CmdletBinding()]
param(
    [string[]] $Sizes = @('n', 's', 'm'),
    [ValidateSet('detect', 'seg')]
    [string[]] $Tasks = @('detect'),
    [string]   $ModelsDir,
    [string]   $Ref = 'main',
    [switch]   $UseUltralytics
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ModelsDir) {
    $ModelsDir = Join-Path $repoRoot 'Models'
}
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

if (-not $UseUltralytics) {
    # ----------------------------------------------------------------------
    # Path A: direct HF download (default, works on Snapdragon / arm64).
    # ----------------------------------------------------------------------
    $base = "https://huggingface.co/zwh20081/yolo26-onnx/resolve/$Ref"
    Write-Host "Downloading YOLO26 ONNX from $base"
    Write-Host "Output directory: $ModelsDir"
    Write-Host "Tasks: $($Tasks -join ', ')"

    foreach ($task in $Tasks) {
        $suffix = if ($task -eq 'seg') { '-seg' } else { '' }
        foreach ($size in $Sizes) {
            $name = "yolo26${size}${suffix}.onnx"
            $out  = Join-Path $ModelsDir $name
            if (Test-Path $out) {
                Write-Host "  skip $name (exists)"
                continue
            }
            $url = "$base/$name"
            Write-Host "  downloading $name ..."
            & curl.exe -L --fail --progress-bar -o $out $url
            if ($LASTEXITCODE -ne 0) {
                if (Test-Path $out) { Remove-Item $out -Force }
                throw "Failed to download $url (curl exit $LASTEXITCODE). Re-run with -UseUltralytics if the mirror is unavailable."
            }
        }
    }
}
else {
    # ----------------------------------------------------------------------
    # Path B: export from .pt via the Ultralytics Python package.
    # Use this for the one-to-many head or to avoid third-party mirrors.
    #
    # Known issue: on Windows arm64, PyTorch wheels are only available for
    # torch >= 2.7 (Python 3.11/3.12/3.13) and pip can be slow to resolve.
    # If pip's resolver complains, try:
    #     pip install torch==2.7.0 --extra-index-url https://download.pytorch.org/whl/cpu
    #     pip install ultralytics --no-deps
    # ----------------------------------------------------------------------
    $python = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
    if (-not $python) {
        throw "Python 3.11+ required on PATH for -UseUltralytics. Install via 'winget install Python.Python.3.12'."
    }

    $venvPath = Join-Path $env:LOCALAPPDATA 'YOLO_Object_DetectionSample\venv'
    if (-not (Test-Path (Join-Path $venvPath 'Scripts\python.exe'))) {
        Write-Host "Creating virtualenv at $venvPath ..."
        & $python.Source -m venv $venvPath
    }
    $venvPython = Join-Path $venvPath 'Scripts\python.exe'

    Write-Host 'Ensuring ultralytics is installed ...'
    & $venvPython -m pip install --upgrade pip | Out-Null
    & $venvPython -m pip install --upgrade ultralytics onnx onnxslim
    if ($LASTEXITCODE -ne 0) {
        throw "pip failed to install ultralytics. On arm64 try: $venvPython -m pip install torch --extra-index-url https://download.pytorch.org/whl/cpu, then re-run."
    }

    $exportScript = @'
import shutil, sys
from pathlib import Path
from ultralytics import YOLO

dest = Path(sys.argv[1])
sizes = sys.argv[2].split(',')
for size in sizes:
    pt = f'yolo26{size}.pt'
    print(f'\n>>> {pt}')
    e2e_out = dest / f'yolo26{size}.onnx'
    if not e2e_out.exists():
        path = YOLO(pt).export(format='onnx', opset=17, simplify=True, dynamic=False, imgsz=640)
        shutil.move(path, e2e_out)
        print(f'    wrote {e2e_out.name}')
    o2m_out = dest / f'yolo26{size}-o2m.onnx'
    if not o2m_out.exists():
        path = YOLO(pt).export(format='onnx', opset=17, simplify=True, dynamic=False, imgsz=640, end2end=False)
        shutil.move(path, o2m_out)
        print(f'    wrote {o2m_out.name}')
print('\nDone.')
'@
    $exportPath = Join-Path $env:TEMP 'yolo26_export.py'
    Set-Content -Path $exportPath -Value $exportScript -Encoding UTF8
    & $venvPython $exportPath $ModelsDir ($Sizes -join ',')
    if ($LASTEXITCODE -ne 0) {
        throw "Ultralytics export script failed."
    }
}

Write-Host "`nFiles in $ModelsDir :"
Get-ChildItem -Path $ModelsDir -Filter 'yolo26*.onnx' | Format-Table Name, @{Name='Size(MB)';Expression={[math]::Round($_.Length/1MB,1)}} -AutoSize
