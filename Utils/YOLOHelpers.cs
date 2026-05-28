using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BiblioText.Utils;

internal static class YOLOHelpers
{
    /// <summary>
    /// YOLOv4-style 3-grid + 3-anchor decoder. Reads tensor data via Span&lt;float&gt; for
    /// speed and uses float math throughout (the previous version used integer division
    /// inputWidth/416 which silently rounded to 1 for the bundled 416x416 model and
    /// would have collapsed to 0 for any smaller input size).
    /// </summary>
    public static List<Prediction> ExtractPredictions(
        List<Tensor<float>> gridTensors,
        List<(float Width, float Height)> anchors,
        int inputWidth,
        int inputHeight,
        int originalWidth,
        int originalHeight,
        IReadOnlyList<string> labels,
        float confidenceThreshold = 0.5f)
    {
        var predictions = new List<Prediction>();
        int anchorCounter = 0;

        var letterbox = Letterbox.Compute(originalWidth, originalHeight, inputWidth, inputHeight);

        foreach (var tensor in gridTensors)
        {
            // tensor shape: [1, gridY, gridX, numAnchors, 5 + numClasses]
            int gridY = tensor.Dimensions[1];
            int gridX = tensor.Dimensions[2];
            int numAnchors = tensor.Dimensions[3];
            int inner = tensor.Dimensions[^1];
            int numClasses = inner - 5;

            ReadOnlySpan<float> data = ((DenseTensor<float>)tensor).Buffer.Span;

            float cellW = (float)inputWidth / gridX;
            float cellH = (float)inputHeight / gridY;
            // Anchors are expressed at the model's training resolution (416) - rescale to current input.
            float anchorScaleX = inputWidth / 416f;
            float anchorScaleY = inputHeight / 416f;

            for (int i = 0; i < gridY; i++)
            {
                for (int j = 0; j < gridX; j++)
                {
                    for (int anchor = 0; anchor < numAnchors; anchor++)
                    {
                        int baseIdx = ((i * gridX + j) * numAnchors + anchor) * inner;

                        float bx = Sigmoid(data[baseIdx + 0]);
                        float by = Sigmoid(data[baseIdx + 1]);
                        float bw = MathF.Exp(data[baseIdx + 2]) * anchors[anchorCounter + anchor].Width  * anchorScaleX;
                        float bh = MathF.Exp(data[baseIdx + 3]) * anchors[anchorCounter + anchor].Height * anchorScaleY;
                        float objectness = Sigmoid(data[baseIdx + 4]);

                        if (objectness < confidenceThreshold)
                        {
                            continue;
                        }

                        // Find best class without allocating a list.
                        int bestClass = -1;
                        float bestProb = 0f;
                        for (int k = 0; k < numClasses; k++)
                        {
                            float p = Sigmoid(data[baseIdx + 5 + k]);
                            if (p > bestProb)
                            {
                                bestProb = p;
                                bestClass = k;
                            }
                        }

                        float score = objectness * bestProb;
                        if (score < confidenceThreshold || bestClass < 0)
                        {
                            continue;
                        }

                        // Convert grid-relative offsets to absolute coords in the input image.
                        float cx = (bx + j) * cellW;
                        float cy = (by + i) * cellH;

                        Box box = letterbox.UndoOnBox(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2);

                        predictions.Add(new Prediction
                        {
                            Box = box,
                            Label = labels[bestClass],
                            Confidence = score
                        });
                    }
                }
            }

            anchorCounter += numAnchors;
        }

        return predictions;
    }

    /// <summary>
    /// Decodes the Ultralytics YOLO26 end-to-end (one-to-one) head output of shape [1, 300, 6]
    /// where each row is [x1, y1, x2, y2, score, classId] in input-image (letterboxed) pixel
    /// coordinates. No NMS required - the model already produced final predictions.
    /// </summary>
    public static List<Prediction> ExtractYolo26EndToEnd(
        Tensor<float> tensor,
        Letterbox letterbox,
        IReadOnlyList<string> labels,
        float confidenceThreshold = 0.25f)
    {
        var predictions = new List<Prediction>();

        // Expected dims: [1, N, 6]. Tolerate batch == 1 only.
        if (tensor.Dimensions.Length != 3 || tensor.Dimensions[0] != 1 || tensor.Dimensions[2] < 6)
        {
            throw new InvalidOperationException(
                $"Unexpected YOLO26 end-to-end output shape [{string.Join(',', tensor.Dimensions.ToArray())}]; expected [1,N,6+].");
        }

        int n = tensor.Dimensions[1];
        int stride = tensor.Dimensions[2];
        ReadOnlySpan<float> data = ((DenseTensor<float>)tensor).Buffer.Span;

        for (int i = 0; i < n; i++)
        {
            int o = i * stride;
            float score = data[o + 4];
            if (score < confidenceThreshold)
            {
                // E2E predictions come sorted by score descending; we can early-out.
                break;
            }

            int classId = (int)data[o + 5];
            if ((uint)classId >= (uint)labels.Count)
            {
                continue;
            }

            Box box = letterbox.UndoOnBox(data[o + 0], data[o + 1], data[o + 2], data[o + 3]);
            predictions.Add(new Prediction
            {
                Box = box,
                Label = labels[classId],
                Confidence = score
            });
        }

        return predictions;
    }

    /// <summary>
    /// Decodes the Ultralytics one-to-many head output of shape [1, 4 + nc, 8400]
    /// (channel-first: rows 0..3 are cx,cy,w,h; rows 4..(4+nc-1) are per-class sigmoid scores).
    /// Applies confidence threshold then class-wise NMS.
    /// </summary>
    public static List<Prediction> ExtractYolo26OneToMany(
        Tensor<float> tensor,
        Letterbox letterbox,
        IReadOnlyList<string> labels,
        float confidenceThreshold = 0.25f,
        float nmsIoUThreshold = 0.45f)
    {
        if (tensor.Dimensions.Length != 3 || tensor.Dimensions[0] != 1)
        {
            throw new InvalidOperationException(
                $"Unexpected YOLO26 one-to-many output shape [{string.Join(',', tensor.Dimensions.ToArray())}]; expected [1, 4+nc, N].");
        }

        int channels = tensor.Dimensions[1];
        int anchors = tensor.Dimensions[2];
        int numClasses = channels - 4;
        if (numClasses <= 0)
        {
            throw new InvalidOperationException($"YOLO26 one-to-many tensor channel count {channels} too small.");
        }

        ReadOnlySpan<float> data = ((DenseTensor<float>)tensor).Buffer.Span;
        var raw = new List<Prediction>(capacity: 256);

        for (int i = 0; i < anchors; i++)
        {
            // Channel-first stride: data[c * anchors + i]
            float cx = data[0 * anchors + i];
            float cy = data[1 * anchors + i];
            float w  = data[2 * anchors + i];
            float h  = data[3 * anchors + i];

            int bestClass = -1;
            float bestScore = confidenceThreshold;
            for (int c = 0; c < numClasses; c++)
            {
                float s = data[(4 + c) * anchors + i];
                if (s > bestScore)
                {
                    bestScore = s;
                    bestClass = c;
                }
            }

            if (bestClass < 0)
            {
                continue;
            }

            Box box = letterbox.UndoOnBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
            raw.Add(new Prediction
            {
                Box = box,
                Label = labels[bestClass],
                Confidence = bestScore
            });
        }

        return ApplyNms(raw, nmsIoUThreshold);
    }

    public static List<Prediction> ApplyNms(List<Prediction> predictions, float nmsThreshold)
    {
        var filteredPredictions = new List<Prediction>();
        var groupedPredictions = predictions.GroupBy(p => p.Label);

        foreach (var group in groupedPredictions)
        {
            var sortedGroup = group.OrderByDescending(p => p.Confidence).ToList();
            while (sortedGroup.Count > 0)
            {
                var bestPrediction = sortedGroup[0];
                filteredPredictions.Add(bestPrediction);
                sortedGroup.RemoveAt(0);
                sortedGroup = sortedGroup
                    .Where(p => IoU(bestPrediction.Box!, p.Box!) < nmsThreshold)
                    .ToList();
            }
        }

        return filteredPredictions;
    }

    /// <summary>
    /// Decodes the YOLO26 segmentation E2E head: per-detection [x1,y1,x2,y2,score,classId, c0..c31]
    /// + a shared prototype tensor [1, 32, 160, 160]. Each detection's per-pixel mask
    /// is computed as <c>sigmoid(coeffs &middot; prototypes)</c> at 160x160, then bilinear-sampled
    /// to the original-image bounding box. Mask values are quantized to 0-255 (>= 128 = inside).
    /// </summary>
    public static List<MaskedPrediction> ExtractYolo26Segmentation(
        Tensor<float> detections,
        Tensor<float> prototypes,
        Letterbox letterbox,
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight,
        IReadOnlyList<string> labels,
        float confidenceThreshold = 0.25f)
    {
        if (detections.Dimensions.Length != 3 || detections.Dimensions[0] != 1 || detections.Dimensions[2] < 6)
        {
            throw new InvalidOperationException(
                $"Unexpected YOLO26-seg detection shape [{string.Join(',', detections.Dimensions.ToArray())}]; expected [1,N,6+nm].");
        }
        if (prototypes.Dimensions.Length != 4 || prototypes.Dimensions[0] != 1)
        {
            throw new InvalidOperationException(
                $"Unexpected YOLO26-seg prototype shape [{string.Join(',', prototypes.Dimensions.ToArray())}]; expected [1,nm,H,W].");
        }

        int n = detections.Dimensions[1];
        int detStride = detections.Dimensions[2];
        int numCoeffs = detStride - 6;
        int protoCount = prototypes.Dimensions[1];
        int protoH = prototypes.Dimensions[2];
        int protoW = prototypes.Dimensions[3];

        if (numCoeffs != protoCount)
        {
            throw new InvalidOperationException(
                $"YOLO26-seg coefficient count {numCoeffs} != prototype channel count {protoCount}.");
        }

        ReadOnlySpan<float> detData = ((DenseTensor<float>)detections).Buffer.Span;
        ReadOnlySpan<float> protoData = ((DenseTensor<float>)prototypes).Buffer.Span;

        // Mapping from original-image pixel coords to mask-grid (160x160) coords.
        // original -> letterbox: lx = origX * scale + padX
        // letterbox -> mask:     mx = lx * protoW / inputWidth
        // composed:              mx = origX * (scale * protoW / inputWidth) + padX * protoW / inputWidth
        float sx = letterbox.Scale * protoW / inputWidth;
        float sy = letterbox.Scale * protoH / inputHeight;
        float ox0 = letterbox.PadX * (float)protoW / inputWidth;
        float oy0 = letterbox.PadY * (float)protoH / inputHeight;

        var results = new List<MaskedPrediction>();
        var mask160 = new float[protoH * protoW];

        for (int i = 0; i < n; i++)
        {
            int o = i * detStride;
            float score = detData[o + 4];
            if (score < confidenceThreshold)
            {
                // E2E head emits detections sorted by descending score; safe to early-out.
                break;
            }

            int classId = (int)detData[o + 5];
            if ((uint)classId >= (uint)labels.Count)
            {
                continue;
            }

            // Box in input (letterbox) space -> original-image coords.
            Box origBox = letterbox.UndoOnBox(detData[o + 0], detData[o + 1], detData[o + 2], detData[o + 3]);
            int boxX0 = Math.Clamp((int)MathF.Floor(origBox.Xmin), 0, originalWidth - 1);
            int boxY0 = Math.Clamp((int)MathF.Floor(origBox.Ymin), 0, originalHeight - 1);
            int boxX1 = Math.Clamp((int)MathF.Ceiling(origBox.Xmax), boxX0 + 1, originalWidth);
            int boxY1 = Math.Clamp((int)MathF.Ceiling(origBox.Ymax), boxY0 + 1, originalHeight);
            int bw = boxX1 - boxX0;
            int bh = boxY1 - boxY0;
            if (bw <= 0 || bh <= 0)
            {
                continue;
            }

            // Compute mask160[i] = sigmoid(sum_k coeffs[k] * proto[k, i]).
            ReadOnlySpan<float> coeffs = detData.Slice(o + 6, numCoeffs);
            int hw = protoH * protoW;
            Array.Clear(mask160, 0, hw);
            for (int k = 0; k < protoCount; k++)
            {
                float c = coeffs[k];
                if (c == 0f)
                {
                    continue;
                }
                int baseIdx = k * hw;
                for (int j = 0; j < hw; j++)
                {
                    mask160[j] += c * protoData[baseIdx + j];
                }
            }
            for (int j = 0; j < hw; j++)
            {
                mask160[j] = 1f / (1f + MathF.Exp(-mask160[j]));
            }

            // Bilinear-sample mask160 at every pixel inside the original-image box.
            byte[] outMask = new byte[bw * bh];
            for (int py = 0; py < bh; py++)
            {
                float my = (boxY0 + py) * sy + oy0;
                int my0 = (int)MathF.Floor(my);
                int my1 = my0 + 1;
                float fy = my - my0;
                if (my0 < 0) { my0 = 0; my1 = 0; fy = 0; }
                else if (my1 >= protoH) { my1 = protoH - 1; my0 = my1; fy = 0; }

                int row0 = my0 * protoW;
                int row1 = my1 * protoW;
                int outRow = py * bw;

                for (int px = 0; px < bw; px++)
                {
                    float mx = (boxX0 + px) * sx + ox0;
                    int mx0 = (int)MathF.Floor(mx);
                    int mx1 = mx0 + 1;
                    float fx = mx - mx0;
                    if (mx0 < 0) { mx0 = 0; mx1 = 0; fx = 0; }
                    else if (mx1 >= protoW) { mx1 = protoW - 1; mx0 = mx1; fx = 0; }

                    float v00 = mask160[row0 + mx0];
                    float v01 = mask160[row0 + mx1];
                    float v10 = mask160[row1 + mx0];
                    float v11 = mask160[row1 + mx1];
                    float v0 = v00 * (1 - fx) + v01 * fx;
                    float v1 = v10 * (1 - fx) + v11 * fx;
                    float v = v0 * (1 - fy) + v1 * fy;
                    outMask[outRow + px] = (byte)Math.Clamp(v * 255f, 0f, 255f);
                }
            }

            results.Add(new MaskedPrediction
            {
                Box = new Box(boxX0, boxY0, boxX1, boxY1),
                Label = labels[classId],
                Confidence = score,
                Mask = outMask,
                Width = bw,
                Height = bh,
            });
        }

        return results;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float IoU(Box a, Box b)
    {
        float x1 = MathF.Max(a.Xmin, b.Xmin);
        float y1 = MathF.Max(a.Ymin, b.Ymin);
        float x2 = MathF.Min(a.Xmax, b.Xmax);
        float y2 = MathF.Min(a.Ymax, b.Ymax);

        float intersection = MathF.Max(0, x2 - x1) * MathF.Max(0, y2 - y1);
        float union = (a.Xmax - a.Xmin) * (a.Ymax - a.Ymin) +
                      (b.Xmax - b.Xmin) * (b.Ymax - b.Ymin) -
                      intersection;

        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// Extracts predictions from RF-DETR ONNX output.
    /// Outputs: boxes [1,300,4], logits [1,300], classes [1,300].
    /// RF-DETR uses direct resize (no letterbox padding), so boxes map
    /// directly from input coordinates to original image coordinates.
    /// </summary>
    public static List<Prediction> ExtractRfDetr(
        IReadOnlyCollection<Microsoft.ML.OnnxRuntime.DisposableNamedOnnxValue> results,
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight,
        IReadOnlyList<string> labels,
        float confidenceThreshold)
    {
        // Find outputs by name
        Tensor<float>? boxesTensor = null;
        Tensor<float>? logitsTensor = null;
        Tensor<int>? classesTensor = null;

        foreach (var r in results)
        {
            switch (r.Name)
            {
                case "boxes":
                    boxesTensor = r.AsTensor<float>();
                    break;
                case "logits":
                    logitsTensor = r.AsTensor<float>();
                    break;
                case "classes":
                    classesTensor = r.AsTensor<int>();
                    break;
            }
        }

        if (boxesTensor == null || logitsTensor == null || classesTensor == null)
            return [];

        var predictions = new List<Prediction>();
        int numDetections = logitsTensor.Dimensions[1]; // 300

        // Scale factors: RF-DETR outputs are relative to the 560×560 input
        // which is a direct resize of the original image (no letterbox padding)
        float scaleX = (float)originalWidth / inputWidth;
        float scaleY = (float)originalHeight / inputHeight;

        for (int i = 0; i < numDetections; i++)
        {
            float score = logitsTensor[0, i];
            if (score < confidenceThreshold) continue;

            int classIdx = classesTensor[0, i];
            string label = classIdx >= 0 && classIdx < labels.Count ? labels[classIdx] : $"class_{classIdx}";

            float v0 = boxesTensor[0, i, 0];
            float v1 = boxesTensor[0, i, 1];
            float v2 = boxesTensor[0, i, 2];
            float v3 = boxesTensor[0, i, 3];

            float xmin, ymin, xmax, ymax;

            // Detect format: values in 0–1 range = normalized coords
            if (v0 <= 1.0f && v1 <= 1.0f && v2 <= 1.0f && v3 <= 1.0f)
            {
                // Normalized [x1, y1, x2, y2] → scale to original image directly
                xmin = v0 * originalWidth;
                ymin = v1 * originalHeight;
                xmax = v2 * originalWidth;
                ymax = v3 * originalHeight;
            }
            else
            {
                // Pixel coordinates in input space → scale to original
                xmin = v0 * scaleX;
                ymin = v1 * scaleY;
                xmax = v2 * scaleX;
                ymax = v3 * scaleY;
            }

            predictions.Add(new Prediction
            {
                Box = new Box(xmin, ymin, xmax, ymax),
                Label = label,
                Confidence = score,
            });
        }

        return predictions;
    }
}
