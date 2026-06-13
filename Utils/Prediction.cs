namespace BiblioText.Utils;

internal class Prediction
{
    public Box? Box { get; set; }
    public required string Label { get; set; }
    public required float Confidence { get; set; }

    /// <summary>
    /// True when the user has deselected this box in the overlay; cropping and
    /// AI submission skip excluded predictions. Resets when detection re-runs
    /// (a fresh CachedOutput replaces the cached list).
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// True for boxes the user drew by hand via the Draw Box toggle (not from
    /// the YOLO model). Cleared when detection re-runs.
    /// </summary>
    public bool IsManual { get; set; }
}

internal class Box
{
    public float Xmin { get; set; }
    public float Ymin { get; set; }
    public float Xmax { get; set; }
    public float Ymax { get; set; }

    public Box(float xmin, float ymin, float xmax, float ymax)
    {
        Xmin = xmin;
        Ymin = ymin;
        Xmax = xmax;
        Ymax = ymax;
    }
}