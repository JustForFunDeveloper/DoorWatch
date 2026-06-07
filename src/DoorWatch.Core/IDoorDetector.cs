namespace DoorWatch.Core;

/// <summary>
/// Captures frames from a camera source and compares them against a stored baseline image
/// to detect whether a door is open or closed.
/// </summary>
public interface IDoorDetector : IDisposable
{
    /// <summary>
    /// Analyses the latest available camera frame and returns the current detection result.
    /// Both PixelDiff and EdgeBased scores are always computed; which one drives the
    /// <see cref="DetectionResult.State"/> depends on the configured <see cref="DetectionMethod"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="DetectionResult"/> with both method scores and the debounced door state.
    /// Returns <see cref="DoorState.Unknown"/> until the first frame and baseline are available.
    /// </returns>
    DetectionResult Detect();
}
