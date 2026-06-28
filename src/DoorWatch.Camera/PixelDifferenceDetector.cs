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
    private VideoCapture _capture;
    private readonly CameraConfig _cameraConfig;
    private readonly DetectorConfig _config;
    private readonly DetectionStatus _status;
    private readonly ILogger<PixelDifferenceDetector> _logger;

    private Mat? _baselineDay;
    private Mat? _baselineNight;
    private LightingMode? _lastLighting;
    private DoorState _committedState = DoorState.Unknown;
    private DoorState _candidateState = DoorState.Unknown;
    private int _candidateCount;

    private Mat? _latestFrame;
    private DateTimeOffset _latestFrameUtc;
    private readonly object _frameLock = new();
    private readonly Thread _grabThread;
    private volatile bool _stopping;

    // ~2 s of failed grabs at the 100 ms retry cadence before we tear down and reopen the source.
    private const int FailuresBeforeReconnect = 20;
    private static readonly int[] ReconnectBackoffSeconds = [1, 2, 5, 10];

    /// <summary>Initialises the detector, opens the camera source, loads any existing baselines, and starts the background grab thread.</summary>
    /// <param name="cameraConfig">Camera source settings (USB index or RTSP URL).</param>
    /// <param name="detectorConfig">Detection settings (ROI, method, thresholds, debounce, baseline path).</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="InvalidOperationException">Thrown when the camera source cannot be opened.</exception>
    public PixelDifferenceDetector(
        CameraConfig cameraConfig,
        DetectorConfig detectorConfig,
        DetectionStatus status,
        ILogger<PixelDifferenceDetector> logger)
    {
        _cameraConfig = cameraConfig;
        _config = detectorConfig;
        _status = status;
        _logger = logger;

        // Force RTSP over TCP to avoid H.264 decode errors caused by UDP packet loss
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");

        _capture = OpenCapture();

        if (!_capture.IsOpened())
            throw new InvalidOperationException("Failed to open camera source.");

        TryLoadBaselines();

        _grabThread = new Thread(GrabLoop) { IsBackground = true, Name = "CameraGrab" };
        _grabThread.Start();
    }

    private VideoCapture OpenCapture() =>
        _cameraConfig.Source == CameraSourceType.Rtsp
            ? new VideoCapture(_cameraConfig.RtspUrl, VideoCaptureAPIs.FFMPEG)
            : new VideoCapture(_cameraConfig.DeviceIndex);

    public DetectionResult Detect()
    {
        Mat? frame;
        DateTimeOffset frameUtc;
        lock (_frameLock)
        {
            frame = _latestFrame?.Clone();
            frameUtc = _latestFrameUtc;
        }

        if (frame is null)
        {
            _logger.LogWarning("No frame available yet from camera.");
            return new DetectionResult(DoorState.Unknown, 0, 0, _lastLighting ?? LightingMode.Day, DateTimeOffset.UtcNow);
        }

        var frameAge = DateTimeOffset.UtcNow - frameUtc;
        if (frameAge > TimeSpan.FromSeconds(_config.StaleFrameSeconds))
        {
            frame.Dispose();
            _logger.LogWarning(
                "Latest frame is {Age:F0}s old (> {Limit}s) — camera feed appears frozen; reporting Unknown.",
                frameAge.TotalSeconds, _config.StaleFrameSeconds);
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

            DoorState rawState = DecideRawState(pixelDiffPercent, edgeChangedPercent, lighting, out double openThreshold, out double closeThreshold);
            _committedState = Debounce(rawState);

            _logger.LogDebug(
                "Frame analysed: {State} ({Lighting}) | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                rawState, lighting, pixelDiffPercent, edgeChangedPercent);

            var result = new DetectionResult(_committedState, pixelDiffPercent, edgeChangedPercent, lighting, DateTimeOffset.UtcNow);
            _status.RecordDetection(result, rawState, _config.Method, openThreshold, closeThreshold);
            return result;
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
        int consecutiveFailures = 0;
        int backoffIndex = 0;

        while (!_stopping)
        {
            var frame = new Mat();
            if (_capture.Read(frame) && !frame.Empty())
            {
                var now = DateTimeOffset.UtcNow;
                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = frame;
                    _latestFrameUtc = now;
                }

                if (consecutiveFailures > 0)
                    _logger.LogInformation("Camera stream recovered after {Count} failed grab(s).", consecutiveFailures);

                consecutiveFailures = 0;
                backoffIndex = 0;
                _status.RecordGrabSuccess(now);
            }
            else
            {
                frame.Dispose();
                consecutiveFailures++;
                _status.RecordGrabFailure();

                // Log once when the stream first goes bad; the periodic reconnect logs cover the rest,
                // so we don't flood the log 10×/second the way the old loop did.
                if (consecutiveFailures == 1)
                    _logger.LogWarning("Camera grab failed — retrying.");

                if (consecutiveFailures >= FailuresBeforeReconnect)
                {
                    int waitSeconds = ReconnectBackoffSeconds[Math.Min(backoffIndex, ReconnectBackoffSeconds.Length - 1)];
                    _status.RecordReconnecting();
                    _logger.LogWarning(
                        "Camera stream appears dead after {Count} failed grabs — reopening source, next retry in {Wait}s.",
                        consecutiveFailures, waitSeconds);

                    Reconnect();
                    backoffIndex++;
                    consecutiveFailures = 0;

                    if (Wait(TimeSpan.FromSeconds(waitSeconds)))
                        break;
                }
                else
                {
                    if (Wait(TimeSpan.FromMilliseconds(100)))
                        break;
                }
            }
        }
    }

    /// <summary>Disposes the current capture and opens a fresh one. A dead RTSP/FFMPEG handle never
    /// recovers on its own, so reconnecting is the only way to resume grabbing without a restart.</summary>
    private void Reconnect()
    {
        try { _capture.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing camera capture during reconnect."); }

        try
        {
            _capture = OpenCapture();
            if (_capture.IsOpened())
                _logger.LogInformation("Camera source reopened.");
            else
                _logger.LogWarning("Reconnect attempt could not open the camera source — will retry.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reopening camera source during reconnect.");
        }
    }

    /// <summary>Sleeps in short slices so a stop request is honoured promptly. Returns true if stopping.</summary>
    private bool Wait(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (!_stopping && DateTime.UtcNow < deadline)
            Thread.Sleep(Math.Min(100, (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds)));
        return _stopping;
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
    /// At night the optional night thresholds take over, because dim IR frames produce a compressed
    /// score range that day thresholds may never reach.
    /// </summary>
    private DoorState DecideRawState(double pixelDiffPercent, double edgeChangedPercent, LightingMode lighting, out double openThreshold, out double closeThreshold)
    {
        bool edgeBased = _config.Method == DetectionMethod.EdgeBased;
        double score = edgeBased ? edgeChangedPercent : pixelDiffPercent;

        double dayOpen   = edgeBased ? _config.EdgeChangeThresholdPercent : _config.ChangeThresholdPercent;
        double dayClose  = (edgeBased ? _config.EdgeCloseThresholdPercent : _config.ChangeCloseThresholdPercent) ?? dayOpen;
        double? nightOpen  = edgeBased ? _config.NightEdgeChangeThresholdPercent : _config.NightChangeThresholdPercent;
        double? nightClose = edgeBased ? _config.NightEdgeCloseThresholdPercent : _config.NightChangeCloseThresholdPercent;

        if (lighting == LightingMode.Night && nightOpen is { } nightOpenValue)
        {
            openThreshold  = nightOpenValue;
            // Deliberately not falling back to the day close threshold: it can sit above the
            // night open threshold, which would lock the state permanently open.
            closeThreshold = nightClose ?? nightOpenValue;
        }
        else
        {
            openThreshold  = dayOpen;
            closeThreshold = dayClose;
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
