namespace DoorWatch.Core;

/// <summary>The outcome of a single detection cycle, carrying both method scores and the committed door state.</summary>
/// <param name="State">Committed door state after debouncing.</param>
/// <param name="PixelDiffPercent">Percentage of ROI pixels that differ from the baseline (PixelDiff score).</param>
/// <param name="EdgeChangedPercent">Percentage of ROI edge pixels that differ from the baseline (EdgeBased score).</param>
/// <param name="Lighting">Lighting mode the frame was classified as; determines which baseline was compared against.</param>
/// <param name="Timestamp">UTC timestamp of when the frame was analysed.</param>
public record DetectionResult(
    DoorState State,
    double PixelDiffPercent,
    double EdgeChangedPercent,
    LightingMode Lighting,
    DateTimeOffset Timestamp);
