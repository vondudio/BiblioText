using BiblioText.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BiblioText.Models;

internal enum ModelHead
{
    Yolov4Anchor,
    Yolo26EndToEnd,
    Yolo26OneToMany,
    Yolo26Segmentation,
}

internal enum TensorLayout
{
    Nhwc,
    Nchw,
}

internal sealed record ModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    ModelHead Head,
    int InputWidth,
    int InputHeight,
    TensorLayout Layout,
    IReadOnlyList<string> Labels,
    float DefaultConfidence)
{
    public string GetFullPath(string modelsDirectory) =>
        Path.Combine(modelsDirectory, FileName);

    public bool ExistsIn(string modelsDirectory) =>
        File.Exists(GetFullPath(modelsDirectory));
}

internal static class ModelRegistry
{
    private static readonly IReadOnlyList<string> CocoMinusBackground =
        RCNNLabelMap.Labels.Skip(1).ToArray();

    public static IReadOnlyList<ModelInfo> All { get; } =
    [
        new ModelInfo(
            Id: "yolov4",
            DisplayName: "YOLOv4 (legacy, 416)",
            FileName: "yolov4.onnx",
            Head: ModelHead.Yolov4Anchor,
            InputWidth: 416,
            InputHeight: 416,
            Layout: TensorLayout.Nhwc,
            Labels: CocoMinusBackground,
            DefaultConfidence: 0.5f),

        new ModelInfo(
            Id: "yolo26n",
            DisplayName: "YOLO26 nano (E2E, 640)",
            FileName: "yolo26n.onnx",
            Head: ModelHead.Yolo26EndToEnd,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        new ModelInfo(
            Id: "yolo26s",
            DisplayName: "YOLO26 small (E2E, 640)",
            FileName: "yolo26s.onnx",
            Head: ModelHead.Yolo26EndToEnd,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        new ModelInfo(
            Id: "yolo26m",
            DisplayName: "YOLO26 medium (E2E, 640)",
            FileName: "yolo26m.onnx",
            Head: ModelHead.Yolo26EndToEnd,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.45f),

        new ModelInfo(
            Id: "yolo26l",
            DisplayName: "YOLO26 large (E2E, 640)",
            FileName: "yolo26l.onnx",
            Head: ModelHead.Yolo26EndToEnd,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        new ModelInfo(
            Id: "yolo26n-o2m",
            DisplayName: "YOLO26 nano (one-to-many + NMS, 640)",
            FileName: "yolo26n-o2m.onnx",
            Head: ModelHead.Yolo26OneToMany,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        new ModelInfo(
            Id: "yolo26s-o2m",
            DisplayName: "YOLO26 small (one-to-many + NMS, 640)",
            FileName: "yolo26s-o2m.onnx",
            Head: ModelHead.Yolo26OneToMany,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        new ModelInfo(
            Id: "yolo26m-o2m",
            DisplayName: "YOLO26 medium (one-to-many + NMS, 640)",
            FileName: "yolo26m-o2m.onnx",
            Head: ModelHead.Yolo26OneToMany,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.25f),

        // -------- Segmentation (E2E head + 32 prototype masks) --------
        new ModelInfo(
            Id: "yolo26n-seg",
            DisplayName: "YOLO26 nano - SEG (E2E, 640)",
            FileName: "yolo26n-seg.onnx",
            Head: ModelHead.Yolo26Segmentation,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.35f),

        new ModelInfo(
            Id: "yolo26s-seg",
            DisplayName: "YOLO26 small - SEG (E2E, 640)",
            FileName: "yolo26s-seg.onnx",
            Head: ModelHead.Yolo26Segmentation,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.35f),

        new ModelInfo(
            Id: "yolo26m-seg",
            DisplayName: "YOLO26 medium - SEG (E2E, 640)",
            FileName: "yolo26m-seg.onnx",
            Head: ModelHead.Yolo26Segmentation,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.20f),

        new ModelInfo(
            Id: "yolo26l-seg",
            DisplayName: "YOLO26 large - SEG (E2E, 640)",
            FileName: "yolo26l-seg.onnx",
            Head: ModelHead.Yolo26Segmentation,
            InputWidth: 640,
            InputHeight: 640,
            Layout: TensorLayout.Nchw,
            Labels: CocoLabels.Labels,
            DefaultConfidence: 0.45f),
    ];

    public static IReadOnlyList<ModelInfo> Available(string modelsDirectory) =>
        All.Where(m => m.ExistsIn(modelsDirectory)).ToArray();

    /// <summary>Default model for viewing/display — prefers segmentation for visual overlay.</summary>
    public static ModelInfo? DefaultForViewing(string modelsDirectory)
    {
        var available = Available(modelsDirectory);
        if (available.Count == 0) return null;

        // Prefer YOLO26 medium SEG for visual display
        return available.FirstOrDefault(m => m.Id == "yolo26m-seg")
            ?? available.FirstOrDefault(m => m.Head == ModelHead.Yolo26Segmentation)
            ?? available.FirstOrDefault(m => m.Id == "yolo26m")
            ?? available.FirstOrDefault(m => m.Head == ModelHead.Yolo26EndToEnd)
            ?? available[0];
    }

    /// <summary>Default model for clipping/AI extraction — prefers bounding box model for clean crops.</summary>
    public static ModelInfo? DefaultForClipping(string modelsDirectory)
    {
        var available = Available(modelsDirectory);
        if (available.Count == 0) return null;

        // Prefer YOLO26 medium E2E (bounding boxes) for clipping
        return available.FirstOrDefault(m => m.Id == "yolo26m")
            ?? available.FirstOrDefault(m => m.Head == ModelHead.Yolo26EndToEnd)
            ?? available.FirstOrDefault(m => m.Id == "yolo26m-seg")
            ?? available[0];
    }

    [Obsolete("Use DefaultForViewing or DefaultForClipping")]
    public static ModelInfo? Default(string modelsDirectory) => DefaultForViewing(modelsDirectory);
}
