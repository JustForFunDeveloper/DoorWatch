namespace DoorWatch.Core;

/// <summary>Selects which computer-vision algorithm drives the door-state decision.</summary>
public enum DetectionMethod
{
    /// <summary>CLAHE-normalised pixel difference against the baseline. Robust to gradual lighting drift.</summary>
    PixelDiff,

    /// <summary>Canny edge-map difference against the baseline. Insensitive to uniform lighting changes.</summary>
    EdgeBased,
}
