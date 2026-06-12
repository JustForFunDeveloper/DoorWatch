using DoorWatch.Core;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace DoorWatch.Camera;

/// <summary>
/// <see cref="IDoorDetector"/> implementation that uses OpenCvSharp to compare camera frames
/// against stored baseline images. Runs a dedicated background thread to continuously grab
/// frames so that <see cref="Detect"/> never blocks on network latency.
/// One baseline is kept per <see cref="LightingMode"/> (daylight colour vs. infrared night vision),
/// so a closed door scores near zero around the clock instead of drifting with the lighting.
/// </summary>
public sealed class PixelDifferenceDetector : IDoorDetector
{
    private readonly VideoCapture _capture;
    private readonly DetectorConfig _config;
    private readonly ILogger<PixelDifferenceDetector> _logger;

    private Mat? _baselineDay;
    private Mat? _baselineNight;
    private LightingMode? _lastLighting;
    private DoorState _committedState = DoorState.Unknown;
    private DoorState _candidateState = DoorState.Unknown;
    private int _candidateCount;

    private Mat? _latestFrame;
    private readonly object _frameLock = new();
    private readonly Thread _grabThread;
    private volatile bool _stopping;

    /// <summary>Initialises the detector, opens the camera source, loads any existing baselines, and starts the background grab thread.</summary>
    /// <param name="cameraConfig">Camera source settings (USB index or RTSP URL).</param>
    /// <param name="detectorConfig">Detection settings (ROI, method, thresholds, debounce, baseline path).</param>
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

        TryLoadBaselines();

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
            return new DetectionResult(DoorState.Unknown, 0, 0, _lastLighting ?? LightingMode.Day, DateTimeOffset.UtcNow);
        }

        using (frame)
        {
            LightingMode lighting = DetectLighting(frame);
            if (_lastLighting is { } previous && previous != lighting)
            {
                // The colour↔IR switch changes every pixel at once; resetting the debounce keeps
                // that single transition frame from counting towards a state change.
                _logger.LogInformation("Lighting mode changed: {Old} → {New} — resetting debounce.", previous, lighting);
                _candidateState = DoorState.Unknown;
                _candidateCount = 0;
            }
            _lastLighting = lighting;

            Mat? baseline = lighting == LightingMode.Night ? _baselineNight : _baselineDay;
            if (baseline is null)
            {
                _logger.LogInformation("No {Lighting} baseline found — capturing current frame as baseline.", lighting);
                SaveBaseline(frame, lighting);
                return new DetectionResult(DoorState.Unknown, 0, 0, lighting, DateTimeOffset.UtcNow);
            }

            var roi = new Rect(_config.Roi.X, _config.Roi.Y, _config.Roi.Width, _config.Roi.Height);

            double pixelDiffPercent   = ComputePixelDiffPercent(frame, baseline, roi);
            double edgeChangedPercent = ComputeEdgeChangedPercent(frame, baseline, roi);

            DoorState rawState = DecideRawState(pixelDiffPercent, edgeChangedPercent);
            _committedState = Debounce(rawState);

            _logger.LogDebug(
                "Frame analysed: {State} ({Lighting}) | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                rawState, lighting, pixelDiffPercent, edgeChangedPercent);

            return new DetectionResult(_committedState, pixelDiffPercent, edgeChangedPercent, lighting, DateTimeOffset.UtcNow);
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
            LightingMode lighting = DetectLighting(frame);
            SaveBaseline(frame, lighting);
            _logger.LogInformation("{Lighting} baseline captured and saved to {Path}.", lighting, BaselinePath(lighting));
        }
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

    /// <summary>
    /// Classifies a frame as daylight colour or infrared night vision. IR frames are pure greyscale,
    /// so their mean colour saturation collapses to ~0, while daylight frames score far higher —
    /// no brightness heuristics needed. Measured over the whole (downscaled) frame, not just the ROI,
    /// so colourful areas like hedges count even when the ROI itself is nearly monochrome.
    /// </summary>
    private LightingMode DetectLighting(Mat frame)
    {
        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(320, Math.Max(1, 320 * frame.Height / frame.Width)));
        using var hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        double meanSaturation = Cv2.Mean(hsv).Val1;
        return meanSaturation < _config.NightSaturationThreshold ? LightingMode.Night : LightingMode.Day;
    }

    /// <summary>
    /// Applies the active method's threshold with hysteresis: the door only counts as open at or above
    /// the open threshold, and an open door only counts as closed again at or below the (lower) close
    /// threshold. A score hovering around a single line therefore cannot flip the state back and forth.
    /// </summary>
    private DoorState DecideRawState(double pixelDiffPercent, double edgeChangedPercent)
    {
        double score, openThreshold, closeThreshold;
        if (_config.Method == DetectionMethod.EdgeBased)
        {
            score          = edgeChangedPercent;
            openThreshold  = _config.EdgeChangeThresholdPercent;
            closeThreshold = _config.EdgeCloseThresholdPercent ?? openThreshold;
        }
        else
        {
            score          = pixelDiffPercent;
            openThreshold  = _config.ChangeThresholdPercent;
            closeThreshold = _config.ChangeCloseThresholdPercent ?? openThreshold;
        }

        return _committedState == DoorState.Open
            ? score > closeThreshold ? DoorState.Open : DoorState.Closed
            : score >= openThreshold ? DoorState.Open : DoorState.Closed;
    }

    private double ComputePixelDiffPercent(Mat frame, Mat baseline, Rect roi)
    {
        using var roiFrame           = new Mat(frame,    roi);
        using var roiBaseline        = new Mat(baseline, roi);
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

    private double ComputeEdgeChangedPercent(Mat frame, Mat baseline, Rect roi)
    {
        using var roiFrame     = new Mat(frame,    roi);
        using var roiBaseline  = new Mat(baseline, roi);
        using var edgeFrame    = new Mat();
        using var edgeBaseline = new Mat();
        using var diff         = new Mat();

        DetectEdges(roiFrame,    edgeFrame);
        DetectEdges(roiBaseline, edgeBaseline);

        Cv2.Absdiff(edgeFrame, edgeBaseline, diff);

        int changedEdgePixels = Cv2.CountNonZero(diff);
        int totalPixels       = roi.Width * roi.Height;

        return 100.0 * changedEdgePixels / totalPixels;
    }

    /// <summary>
    /// Edge detection tuned to work across lighting conditions: Gaussian blur suppresses IR sensor
    /// grain, CLAHE lifts low-light contrast (same normalisation the PixelDiff path uses), and the
    /// Canny thresholds are derived from the image median so edge density stays comparable between
    /// bright daylight and dim IR frames — fixed thresholds find almost no edges in dark images.
    /// </summary>
    private static void DetectEdges(Mat roiBgr, Mat edges)
    {
        using var gray       = new Mat();
        using var blurred    = new Mat();
        using var normalized = new Mat();

        Cv2.CvtColor(roiBgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
        clahe.Apply(blurred, normalized);

        double median = Median(normalized);
        double lower  = Math.Max(10, 0.66 * median);
        double upper  = Math.Min(255, 1.33 * median);
        Cv2.Canny(normalized, edges, lower, upper);
    }

    private static double Median(Mat gray)
    {
        using var hist = new Mat();
        Cv2.CalcHist([gray], [0], null, hist, 1, [256], [new Rangef(0, 256)]);

        int half       = gray.Rows * gray.Cols / 2;
        int cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += (int)hist.Get<float>(i);
            if (cumulative >= half)
                return i;
        }

        return 255;
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

    private string BaselinePath(LightingMode lighting)
    {
        string dir    = Path.GetDirectoryName(_config.BaselineImagePath) ?? "";
        string name   = Path.GetFileNameWithoutExtension(_config.BaselineImagePath);
        string ext    = Path.GetExtension(_config.BaselineImagePath);
        string suffix = lighting == LightingMode.Night ? "-night" : "-day";
        return Path.Combine(dir, name + suffix + ext);
    }

    private void TryLoadBaselines()
    {
        _baselineDay   = TryLoadBaseline(LightingMode.Day);
        _baselineNight = TryLoadBaseline(LightingMode.Night);

        // Migrate a legacy single baseline (pre lighting-mode split) as the day baseline.
        if (_baselineDay is null)
        {
            string legacyPath = Path.GetFullPath(_config.BaselineImagePath);
            if (File.Exists(legacyPath))
            {
                _baselineDay = Cv2.ImRead(legacyPath);
                _logger.LogInformation("Loaded legacy baseline from {Path} as Day baseline.", legacyPath);
            }
        }
    }

    private Mat? TryLoadBaseline(LightingMode lighting)
    {
        string fullPath = Path.GetFullPath(BaselinePath(lighting));
        if (!File.Exists(fullPath))
        {
            _logger.LogInformation("No {Lighting} baseline at {Path} — will capture one when that lighting is first seen.", lighting, fullPath);
            return null;
        }

        _logger.LogInformation("Loaded {Lighting} baseline from {Path}.", lighting, fullPath);
        return Cv2.ImRead(fullPath);
    }

    private void SaveBaseline(Mat frame, LightingMode lighting)
    {
        var copy = frame.Clone();
        if (lighting == LightingMode.Night)
        {
            _baselineNight?.Dispose();
            _baselineNight = copy;
        }
        else
        {
            _baselineDay?.Dispose();
            _baselineDay = copy;
        }

        string path = BaselinePath(lighting);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        bool written = Cv2.ImWrite(path, copy);
        if (!written)
            _logger.LogError("Failed to write {Lighting} baseline image to {Path}.", lighting, path);
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
        _baselineDay?.Dispose();
        _baselineNight?.Dispose();
    }
}
