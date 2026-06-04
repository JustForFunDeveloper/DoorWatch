namespace DoorWatch.Core;

public record DetectionResult(
    DoorState State,
    double ChangedPercent,
    DateTimeOffset Timestamp);
