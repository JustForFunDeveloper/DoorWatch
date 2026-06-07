using DoorWatch.Core;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace DoorWatch.Camera;

/// <summary>
/// <see cref="IDoorDetector"/> implementation that uses OpenCvSharp to compare camera frames
/// against a stored baseline image. Runs a dedicated background thread to continuously grab
/// frames so that <see cref="Detect"/> never blocks on network latency.
/// </summary>
public sealed class PixelDifferenceDetector : IDoorDetector
{
    private readonly VideoCapture _capture;
    private readonly DetectorConfig _config;
    private readonly ILogger<PixelDifferenceDetector> _logger;

    private Mat? _baseline;
    private DoorState _committedState = DoorState.Unknown;
    private DoorState _candidateState = DoorState.Unknown;
    private int _candidateCount;

    private Mat? _latestFrame;
    private readonly object _frameLock = new();
    private readonly Thread _grabThread;
    private volatile bool _stopping;

    /// <summary>Initialises the detector, opens the camera source, loads any existing baseline, and starts the background grab thread.</summary>
    /// <param name="cameraConfig">Camera source settings (USB index or RTSP URL).</param>
    /// <param name="detectorConfig">Detection settings (ROI, method, thresholds, baseline path).</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="InvalidOperationException">Thrown when the camera source cannot be opened.</exception>
    public PixelDifferenceDetector(
        CameraConfig cameraConfig,
        DetectorConfig detectorConfig,
        ILogger<PixelDifferenceDetector> logger)
    {
        _config = detectorConfig;
        _logger = logger;

        // Force RTSP over TCP to avoid H.264 decode errors caused by UDP packet loss
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");

        _capture = cameraConfig.Source == CameraSourceType.Rtsp
            ? new VideoCapture(cameraConfig.RtspUrl, VideoCaptureAPIs.FFMPEG)
            : new VideoCapture(cameraConfig.DeviceIndex);

        if (!_capture.IsOpened())
            throw new InvalidOperationException("Failed to open camera source.");

        TryLoadBaseline();

        _grabThread = new Thread(GrabLoop) { IsBackground = true, Name = "CameraGrab" };
        _grabThread.Start();
    }

    public DetectionResult Detect()
    {
        Mat? frame;
        lock (_frameLock)
        {
            frame = _latestFrame?.Clone();
        }

        if (frame is null)
        {
            _logger.LogWarning("No frame available yet from camera.");
            return new DetectionResult(DoorState.Unknown, 0, 0, DateTimeOffset.UtcNow);
        }

        using (frame)
        {
            if (_baseline is null)
            {
                _logger.LogInformation("No baseline found — capturing current frame as baseline.");
                SaveBaseline(frame);
                return new DetectionResult(DoorState.Unknown, 0, 0, DateTimeOffset.UtcNow);
            }

            var roi = new Rect(_config.Roi.X, _config.Roi.Y, _config.Roi.Width, _config.Roi.Height);

            double pixelDiffPercent    = ComputePixelDiffPercent(frame, roi);
            double edgeChangedPercent  = ComputeEdgeChangedPercent(frame, roi);

            DoorState rawState = _config.Method == DetectionMethod.EdgeBased
                ? edgeChangedPercent >= _config.EdgeChangeThresholdPercent ? DoorState.Open : DoorState.Closed
                : pixelDiffPercent   >= _config.ChangeThresholdPercent     ? DoorState.Open : DoorState.Closed;

            _committedState = Debounce(rawState);
            
            _logger.LogDebug(
                "Door state changed: {State} | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                rawState, pixelDiffPercent, edgeChangedPercent);

            return new DetectionResult(_committedState, pixelDiffPercent, edgeChangedPercent, DateTimeOffset.UtcNow);
        }
    }

    public void CaptureBaseline()
    {
        Mat? frame;
        lock (_frameLock)
        {
            frame = _latestFrame?.Clone();
        }

        if (frame is null)
            throw new InvalidOperationException("No frame available yet for baseline capture.");

        using (frame)
        {
            SaveBaseline(frame);
        }

        _logger.LogInformation("Baseline captured and saved to {Path}.", _config.BaselineImagePath);
    }

    private void GrabLoop()
    {
        while (!_stopping)
        {
            var frame = new Mat();
            if (_capture.Read(frame) && !frame.Empty())
            {
                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = frame;
                }
            }
            else
            {
                frame.Dispose();
                _logger.LogWarning("Camera grab failed — retrying.");
                Thread.Sleep(100);
            }
        }
    }

    private double ComputePixelDiffPercent(Mat frame, Rect roi)
    {
        using var roiFrame           = new Mat(frame,     roi);
        using var roiBaseline        = new Mat(_baseline!, roi);
        using var grayFrame          = new Mat();
        using var grayBaseline       = new Mat();
        using var normalizedFrame    = new Mat();
        using var normalizedBaseline = new Mat();
        using var diff               = new Mat();
        using var thresholded        = new Mat();

        Cv2.CvtColor(roiFrame,    grayFrame,    ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(roiBaseline, grayBaseline, ColorConversionCodes.BGR2GRAY);

        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
        clahe.Apply(grayFrame,    normalizedFrame);
        clahe.Apply(grayBaseline, normalizedBaseline);

        Cv2.Absdiff(normalizedFrame, normalizedBaseline, diff);
        Cv2.Threshold(diff, thresholded, 25, 255, ThresholdTypes.Binary);

        int changedPixels = Cv2.CountNonZero(thresholded);
        int totalPixels   = roi.Width * roi.Height;

        return 100.0 * changedPixels / totalPixels;
    }

    private double ComputeEdgeChangedPercent(Mat frame, Rect roi)
    {
        using var roiFrame    = new Mat(frame,     roi);
        using var roiBaseline = new Mat(_baseline!, roi);
        using var grayFrame    = new Mat();
        using var grayBaseline = new Mat();
        using var edgeFrame    = new Mat();
        using var edgeBaseline = new Mat();
        using var diff         = new Mat();

        Cv2.CvtColor(roiFrame,    grayFrame,    ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(roiBaseline, grayBaseline, ColorConversionCodes.BGR2GRAY);

        Cv2.Canny(grayFrame,    edgeFrame,    50, 150);
        Cv2.Canny(grayBaseline, edgeBaseline, 50, 150);

        Cv2.Absdiff(edgeFrame, edgeBaseline, diff);

        int changedEdgePixels = Cv2.CountNonZero(diff);
        int totalPixels       = roi.Width * roi.Height;

        return 100.0 * changedEdgePixels / totalPixels;
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
        _stopping = true;
        _grabThread.Join(TimeSpan.FromSeconds(2));

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
        }

        _capture.Dispose();
        _baseline?.Dispose();
    }
}
