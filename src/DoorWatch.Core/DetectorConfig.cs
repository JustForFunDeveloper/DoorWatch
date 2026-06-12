namespace DoorWatch.Core;

/// <summary>Configuration for the door detector — ROI, detection method, thresholds, and debounce behaviour.</summary>
public class DetectorConfig
{
    /// <summary>The region of interest within the camera frame that is analysed. Pixels outside this rectangle are ignored.</summary>
    public RoiConfig Roi { get; set; } = new();

    /// <summary>Which detection algorithm drives the door-state decision. Both scores are always computed and logged regardless of this setting.</summary>
    public DetectionMethod Method { get; set; } = DetectionMethod.PixelDiff;

    /// <summary>Minimum percentage of ROI pixels that must differ from the baseline to consider the door open (PixelDiff method).</summary>
    public double ChangeThresholdPercent { get; set; } = 10.0;

    /// <summary>
    /// Score at or below which an open door is considered closed again (PixelDiff method).
    /// Set this lower than <see cref="ChangeThresholdPercent"/> to add hysteresis so a score hovering
    /// around a single threshold cannot flip the state back and forth. Defaults to the open threshold when unset.
    /// </summary>
    public double? ChangeCloseThresholdPercent { get; set; }

    /// <summary>Minimum percentage of ROI edge pixels that must differ from the baseline to consider the door open (EdgeBased method).</summary>
    public double EdgeChangeThresholdPercent { get; set; } = 5.0;

    /// <summary>
    /// Score at or below which an open door is considered closed again (EdgeBased method).
    /// Set this lower than <see cref="EdgeChangeThresholdPercent"/> to add hysteresis so a score hovering
    /// around a single threshold cannot flip the state back and forth. Defaults to the open threshold when unset.
    /// </summary>
    public double? EdgeCloseThresholdPercent { get; set; }

    /// <summary>
    /// Mean colour saturation (0–255, measured over the whole frame) below which a frame is classified as
    /// infrared night-vision. IR frames are pure greyscale so their saturation is near zero, while daylight
    /// colour frames score far higher.
    /// </summary>
    public double NightSaturationThreshold { get; set; } = 10.0;

    /// <summary>Number of consecutive frames that must agree on the same state before it is committed. Prevents single-frame flicker from triggering Home Assistant.</summary>
    public int DebounceFrames { get; set; } = 3;

    /// <summary>
    /// Base file path for the baseline (closed-door reference) images. One baseline is kept per lighting mode
    /// by inserting a suffix before the extension (e.g. <c>baseline.png</c> → <c>baseline-day.png</c> and
    /// <c>baseline-night.png</c>). Delete a file to force a re-capture the next time that lighting mode is seen.
    /// </summary>
    public string BaselineImagePath { get; set; } = "baseline.png";
}

/// <summary>Defines a rectangular region of interest within the camera frame.</summary>
public class RoiConfig
{
    /// <summary>X coordinate of the top-left corner in pixels.</summary>
    public int X { get; set; }

    /// <summary>Y coordinate of the top-left corner in pixels.</summary>
    public int Y { get; set; }

    /// <summary>Width of the region in pixels.</summary>
    public int Width { get; set; } = 200;

    /// <summary>Height of the region in pixels.</summary>
    public int Height { get; set; } = 200;
}
