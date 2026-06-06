using DoorWatch.Core;
using DoorWatch.HomeAssistant;

namespace DoorWatch.Worker;

public sealed class DoorWatchWorker : BackgroundService
{
    private readonly IDoorDetector _detector;
    private readonly IHomeAssistantClient _haClient;
    private readonly ILogger<DoorWatchWorker> _logger;
    private readonly int _frameIntervalMs;

    private DoorState _lastReportedState = DoorState.Unknown;

    public DoorWatchWorker(
        IDoorDetector detector,
        IHomeAssistantClient haClient,
        IConfiguration config,
        ILogger<DoorWatchWorker> logger)
    {
        _detector = detector;
        _haClient = haClient;
        _logger = logger;
        _frameIntervalMs = config.GetValue("DoorWatch:FrameIntervalMs", 1000);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DoorWatch started. Frame interval: {Interval}ms", _frameIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _detector.Detect();

                if (result.State != DoorState.Unknown)
                    _logger.LogDebug(
                        "State: {State} | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                        result.State, result.PixelDiffPercent, result.EdgeChangedPercent);

                if (result.State != _lastReportedState && result.State != DoorState.Unknown)
                {
                    _logger.LogInformation(
                        "Door state changed: {Old} → {New} | PixelDiff: {PixelPct:F1}% | EdgeDiff: {EdgePct:F1}%",
                        _lastReportedState, result.State, result.PixelDiffPercent, result.EdgeChangedPercent);

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
}
