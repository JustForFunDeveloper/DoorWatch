namespace DoorWatch.Core;

public class DetectorConfig
{
    public RoiConfig Roi { get; set; } = new();

    public DetectionMethod Method { get; set; } = DetectionMethod.PixelDiff;

    /// <summary>Minimum % of ROI pixels that must differ (PixelDiff method).</summary>
    public double ChangeThresholdPercent { get; set; } = 10.0;

    /// <summary>Minimum % of ROI pixels with edge changes to consider the door open (EdgeBased method).</summary>
    public double EdgeChangeThresholdPercent { get; set; } = 5.0;

    /// <summary>How many consecutive frames must agree before state is committed.</summary>
    public int DebounceFrames { get; set; } = 3;

    /// <summary>Path to save/load the baseline (closed-door) image.</summary>
    public string BaselineImagePath { get; set; } = "baseline.png";
}

public class RoiConfig
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 200;
}
