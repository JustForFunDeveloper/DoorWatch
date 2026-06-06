namespace DoorWatch.Core;

public record DetectionResult(
    DoorState State,
    double PixelDiffPercent,
    double EdgeChangedPercent,
    DateTimeOffset Timestamp);
