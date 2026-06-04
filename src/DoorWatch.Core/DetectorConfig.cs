namespace DoorWatch.Core;

public class DetectorConfig
{
    public RoiConfig Roi { get; set; } = new();

    /// <summary>Minimum percentage of changed pixels in the ROI to consider the door open.</summary>
    public double ChangeThresholdPercent { get; set; } = 10.0;

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
