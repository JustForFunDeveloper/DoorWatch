using DoorWatch.Camera;
using DoorWatch.Core;
using DoorWatch.HomeAssistant;

namespace DoorWatch.Worker;

/// <summary>
/// Main hosted service that runs the door-detection loop. On each tick it calls
/// <see cref="IDoorDetector.Detect"/>, logs both method scores, and — when the committed
/// state changes — notifies Home Assistant via <see cref="IHomeAssistantClient"/>.
/// </summary>
public sealed class DoorWatchWorker : BackgroundService
{
    private readonly IDoorDetector _detector;
    private readonly IHomeAssistantClient _haClient;
    private readonly ILogger<DoorWatchWorker> _logger;
    private readonly int _frameIntervalMs;
    private readonly CameraConfig _cameraConfig;
    private readonly DetectorConfig _detectorConfig;
    private readonly HomeAssistantConfig _haConfig;

    private DoorState _lastReportedState = DoorState.Unknown;

    public DoorWatchWorker(
        IDoorDetector detector,
        IHomeAssistantClient haClient,
        IConfiguration config,
        CameraConfig cameraConfig,
        DetectorConfig detectorConfig,
        HomeAssistantConfig haConfig,
        ILogger<DoorWatchWorker> logger)
    {
        _detector = detector;
        _haClient = haClient;
        _logger = logger;
        _cameraConfig = cameraConfig;
        _detectorConfig = detectorConfig;
        _haConfig = haConfig;
        _frameIntervalMs = config.GetValue("DoorWatch:FrameIntervalMs", 1000);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== DoorWatch configuration ===");
        _logger.LogInformation("  FrameIntervalMs          : {V}", _frameIntervalMs);
        _logger.LogInformation("  Camera.Source            : {V}", _cameraConfig.Source);
        _logger.LogInformation("  Camera.RtspUrl           : {V}", RedactUrl(_cameraConfig.RtspUrl));
        _logger.LogInformation("  Detector.Method          : {V}", _detectorConfig.Method);
        _logger.LogInformation("  Detector.Roi             : X={X} Y={Y} W={W} H={H}",
            _detectorConfig.Roi.X, _detectorConfig.Roi.Y,
            _detectorConfig.Roi.Width, _detectorConfig.Roi.Height);
        _logger.LogInformation("  Detector.PixelDiffThreshold  : open {Open}% / close {Close}%",
            _detectorConfig.ChangeThresholdPercent,
            _detectorConfig.ChangeCloseThresholdPercent ?? _detectorConfig.ChangeThresholdPercent);
        _logger.LogInformation("  Detector.EdgeDiffThreshold   : open {Open}% / close {Close}%",
            _detectorConfig.EdgeChangeThresholdPercent,
            _detectorConfig.EdgeCloseThresholdPercent ?? _detectorConfig.EdgeChangeThresholdPercent);
        if (_detectorConfig.NightEdgeChangeThresholdPercent is { } nightEdgeOpen)
            _logger.LogInformation("  Detector.NightEdgeDiffThreshold : open {Open}% / close {Close}%",
                nightEdgeOpen, _detectorConfig.NightEdgeCloseThresholdPercent ?? nightEdgeOpen);
        if (_detectorConfig.NightChangeThresholdPercent is { } nightPixelOpen)
            _logger.LogInformation("  Detector.NightPixelDiffThreshold : open {Open}% / close {Close}%",
                nightPixelOpen, _detectorConfig.NightChangeCloseThresholdPercent ?? nightPixelOpen);
        _logger.LogInformation("  Detector.NightSaturationThreshold : {V}", _detectorConfig.NightSaturationThreshold);
        _logger.LogInformation("  Detector.DebounceFrames  : {V}", _detectorConfig.DebounceFrames);
        _logger.LogInformation("  Detector.BaselineImagePath   : {V}", _detectorConfig.BaselineImagePath);
        _logger.LogInformation("  HomeAssistant.BaseUrl    : {V}", _haConfig.BaseUrl);
        _logger.LogInformation("  HomeAssistant.Token      : {V}", RedactToken(_haConfig.Token));
        _logger.LogInformation("  HomeAssistant.EntityId   : {V}", _haConfig.EntityId);
        _logger.LogInformation("=== Starting detection loop ===");

        _logger.LogInformation("DoorWatch started. Frame interval: {Interval}ms", _frameIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _detector.Detect();

                if (result.State != DoorState.Unknown)
                    _logger.LogDebug(
                        "State: {State} ({Lighting}) | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                        result.State, result.Lighting, result.PixelDiffPercent, result.EdgeChangedPercent);

                if (result.State != _lastReportedState && result.State != DoorState.Unknown)
                {
                    _logger.LogInformation(
                        "Door state changed: {Old} → {New} ({Lighting}) | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                        _lastReportedState, result.State, result.Lighting, result.PixelDiffPercent, result.EdgeChangedPercent);

                    await _haClient.UpdateDoorStateAsync(result.State, stoppingToken);
                    _lastReportedState = result.State;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during detection cycle.");
            }

            await Task.Delay(_frameIntervalMs, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _detector.Dispose();
        base.Dispose();
    }

    private static string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "(empty)";
        try
        {
            var uri = new Uri(url);
            if (uri.UserInfo.Length == 0) return url;
            var user = uri.UserInfo.Split(':')[0];
            return url.Replace(uri.UserInfo, $"{user}:***");
        }
        catch { return "(invalid)"; }
    }

    private static string RedactToken(string token) =>
        string.IsNullOrEmpty(token) ? "(empty)" : $"{token[..Math.Min(8, token.Length)]}***";
}
