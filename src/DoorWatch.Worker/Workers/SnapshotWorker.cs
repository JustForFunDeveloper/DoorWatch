using DoorWatch.Camera;
using DoorWatch.Core;
using OpenCvSharp;

namespace DoorWatch.Worker;

/// <summary>
/// One-shot hosted service activated by the <c>--snapshot</c> command-line flag. Connects to the
/// configured camera, grabs a single frame, draws the current ROI as a red rectangle, saves the
/// result as <c>snapshot.png</c>, logs the full frame resolution, and then shuts the application down.
/// Use this to determine or verify the ROI coordinates before starting the main detection loop.
/// </summary>
public sealed class SnapshotWorker : BackgroundService
{
    private readonly CameraConfig _cameraConfig;
    private readonly DetectorConfig _detectorConfig;
    private readonly ILogger<SnapshotWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public SnapshotWorker(
        CameraConfig cameraConfig,
        DetectorConfig detectorConfig,
        ILogger<SnapshotWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _cameraConfig = cameraConfig;
        _detectorConfig = detectorConfig;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string outputPath = Path.GetFullPath("snapshot.png");

        using var capture = _cameraConfig.Source == CameraSourceType.Rtsp
            ? new VideoCapture(_cameraConfig.RtspUrl)
            : new VideoCapture(_cameraConfig.DeviceIndex);

        if (!capture.IsOpened())
        {
            _logger.LogError("Could not open camera source.");
            _lifetime.StopApplication();
            return Task.CompletedTask;
        }

        using var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
        {
            _logger.LogError("Could not read frame from camera.");
            _lifetime.StopApplication();
            return Task.CompletedTask;
        }

        _logger.LogInformation("Full frame size: {W}x{H} px — use these as the upper bounds for your ROI",
            frame.Width, frame.Height);

        var roi = new Rect(
            _detectorConfig.Roi.X,
            _detectorConfig.Roi.Y,
            _detectorConfig.Roi.Width,
            _detectorConfig.Roi.Height);

        Cv2.Rectangle(frame, roi, Scalar.Red, 2);
        Cv2.PutText(frame,
            $"ROI ({roi.X},{roi.Y}) {roi.Width}x{roi.Height}",
            new Point(roi.X, Math.Max(roi.Y - 8, 12)),
            HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 2);

        Cv2.ImWrite(outputPath, frame);
        _logger.LogInformation("Snapshot with ROI overlay saved to {Path}", outputPath);
        _logger.LogInformation("Open snapshot.png in any image editor — hover over the door gap to read the pixel coordinates, then update Roi in appsettings.json");

        _lifetime.StopApplication();
        return Task.CompletedTask;
    }
}
