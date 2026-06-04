using DoorWatch.Core;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace DoorWatch.Camera;

public sealed class PixelDifferenceDetector : IDoorDetector
{
    private readonly VideoCapture _capture;
    private readonly DetectorConfig _config;
    private readonly ILogger<PixelDifferenceDetector> _logger;

    private Mat? _baseline;
    private DoorState _committedState = DoorState.Unknown;
    private DoorState _candidateState = DoorState.Unknown;
    private int _candidateCount;

    public PixelDifferenceDetector(
        CameraConfig cameraConfig,
        DetectorConfig detectorConfig,
        ILogger<PixelDifferenceDetector> logger)
    {
        _config = detectorConfig;
        _logger = logger;

        _capture = cameraConfig.Source == CameraSourceType.Rtsp
            ? new VideoCapture(cameraConfig.RtspUrl)
            : new VideoCapture(cameraConfig.DeviceIndex);

        if (!_capture.IsOpened())
            throw new InvalidOperationException("Failed to open camera source.");

        TryLoadBaseline();
    }

    public DetectionResult Detect()
    {
        using var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
        {
            _logger.LogWarning("Could not read frame from camera.");
            return new DetectionResult(DoorState.Unknown, 0, DateTimeOffset.UtcNow);
        }

        if (_baseline is null)
        {
            _logger.LogInformation("No baseline found — capturing current frame as baseline.");
            SaveBaseline(frame);
            return new DetectionResult(DoorState.Unknown, 0, DateTimeOffset.UtcNow);
        }

        double changedPercent = ComputeChangedPercent(frame);
        DoorState rawState = changedPercent >= _config.ChangeThresholdPercent
            ? DoorState.Open
            : DoorState.Closed;

        _committedState = Debounce(rawState);

        return new DetectionResult(_committedState, changedPercent, DateTimeOffset.UtcNow);
    }

    public void CaptureBaseline()
    {
        using var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
            throw new InvalidOperationException("Could not read frame for baseline.");

        SaveBaseline(frame);
        _logger.LogInformation("Baseline captured and saved to {Path}.", _config.BaselineImagePath);
    }

    private double ComputeChangedPercent(Mat frame)
    {
        var roi = new Rect(_config.Roi.X, _config.Roi.Y, _config.Roi.Width, _config.Roi.Height);

        using var roiFrame = new Mat(frame, roi);
        using var roiBaseline = new Mat(_baseline!, roi);
        using var grayFrame = new Mat();
        using var grayBaseline = new Mat();
        using var diff = new Mat();
        using var thresholded = new Mat();

        Cv2.CvtColor(roiFrame, grayFrame, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(roiBaseline, grayBaseline, ColorConversionCodes.BGR2GRAY);
        Cv2.Absdiff(grayFrame, grayBaseline, diff);
        Cv2.Threshold(diff, thresholded, 25, 255, ThresholdTypes.Binary);

        int changedPixels = Cv2.CountNonZero(thresholded);
        int totalPixels = _config.Roi.Width * _config.Roi.Height;

        return 100.0 * changedPixels / totalPixels;
    }

    private DoorState Debounce(DoorState raw)
    {
        if (raw == _candidateState)
        {
            _candidateCount++;
        }
        else
        {
            _candidateState = raw;
            _candidateCount = 1;
        }

        return _candidateCount >= _config.DebounceFrames ? _candidateState : _committedState;
    }

    private void TryLoadBaseline()
    {
        string fullPath = Path.GetFullPath(_config.BaselineImagePath);
        _logger.LogInformation("Looking for baseline at {Path}.", fullPath);
        if (!File.Exists(fullPath))
            return;
        
        _baseline = Cv2.ImRead(fullPath);
        _logger.LogInformation("Loaded baseline from {Path}.", fullPath);
    }

    private void SaveBaseline(Mat frame)
    {
        _baseline?.Dispose();
        _baseline = frame.Clone();

        var dir = Path.GetDirectoryName(_config.BaselineImagePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        
        bool written = Cv2.ImWrite(_config.BaselineImagePath, _baseline);
        if (!written)
            _logger.LogError("Failed to write baseline image to {Path}.", _config.BaselineImagePath);
    }

    public void Dispose()
    {
        _capture.Dispose();
        _baseline?.Dispose();
    }
}
