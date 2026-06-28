namespace DoorWatch.Core;

/// <summary>Connection state of the camera grab loop, surfaced for diagnostics.</summary>
public enum ConnectionState
{
    /// <summary>The grab loop has not yet produced a frame since startup.</summary>
    Connecting,

    /// <summary>Frames are being grabbed successfully.</summary>
    Connected,

    /// <summary>Grabs are failing and the loop is tearing down and reopening the camera source.</summary>
    Reconnecting
}

/// <summary>
/// Thread-safe holder for the most recent detection state, written by the detector and the worker
/// and read on demand by the diagnostics HTTP endpoint. Lets you inspect what the running container
/// currently "sees" — both scores, the thresholds in effect, frame freshness, and connection health —
/// without enabling debug logging or restarting the process.
/// </summary>
public sealed class DetectionStatus
{
    private readonly object _lock = new();

    private DoorState _state = DoorState.Unknown;
    private DoorState _rawState = DoorState.Unknown;
    private double _pixelDiffPercent;
    private double _edgeChangedPercent;
    private LightingMode _lighting = LightingMode.Day;
    private DetectionMethod _method;
    private double _openThreshold;
    private double _closeThreshold;
    private ConnectionState _connection = ConnectionState.Connecting;
    private int _consecutiveGrabFailures;
    private DateTimeOffset? _lastGrabUtc;
    private DateTimeOffset? _lastFrameAnalyzedUtc;
    private DoorState _lastReportedState = DoorState.Unknown;
    private DateTimeOffset? _lastStateChangeUtc;
    private DateTimeOffset? _lastHomeAssistantPushUtc;

    /// <summary>Records a successful frame grab. Clears the failure streak and marks the source connected.</summary>
    public void RecordGrabSuccess(DateTimeOffset utc)
    {
        lock (_lock)
        {
            _lastGrabUtc = utc;
            _consecutiveGrabFailures = 0;
            _connection = ConnectionState.Connected;
        }
    }

    /// <summary>Records a failed frame grab, incrementing the consecutive-failure counter.</summary>
    public void RecordGrabFailure()
    {
        lock (_lock) { _consecutiveGrabFailures++; }
    }

    /// <summary>Marks that the grab loop is actively reconnecting to the camera source.</summary>
    public void RecordReconnecting()
    {
        lock (_lock) { _connection = ConnectionState.Reconnecting; }
    }

    /// <summary>Records the outcome of a detection cycle, including the thresholds that drove the decision.</summary>
    public void RecordDetection(
        DetectionResult result,
        DoorState rawState,
        DetectionMethod method,
        double openThreshold,
        double closeThreshold)
    {
        lock (_lock)
        {
            _state = result.State;
            _rawState = rawState;
            _pixelDiffPercent = result.PixelDiffPercent;
            _edgeChangedPercent = result.EdgeChangedPercent;
            _lighting = result.Lighting;
            _method = method;
            _openThreshold = openThreshold;
            _closeThreshold = closeThreshold;
            _lastFrameAnalyzedUtc = result.Timestamp;
        }
    }

    /// <summary>Records that a state change was pushed to Home Assistant.</summary>
    public void RecordStateReported(DoorState state, DateTimeOffset utc)
    {
        lock (_lock)
        {
            _lastReportedState = state;
            _lastStateChangeUtc = utc;
            _lastHomeAssistantPushUtc = utc;
        }
    }

    /// <summary>Takes an immutable, point-in-time snapshot of the current status for serialization.</summary>
    public DetectionStatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            return new DetectionStatusSnapshot(
                State: _state,
                RawState: _rawState,
                PixelDiffPercent: _pixelDiffPercent,
                EdgeChangedPercent: _edgeChangedPercent,
                Lighting: _lighting,
                Method: _method,
                OpenThreshold: _openThreshold,
                CloseThreshold: _closeThreshold,
                Connection: _connection,
                ConsecutiveGrabFailures: _consecutiveGrabFailures,
                LastGrabUtc: _lastGrabUtc,
                FrameAgeMs: _lastGrabUtc is { } g ? (long)(now - g).TotalMilliseconds : null,
                LastFrameAnalyzedUtc: _lastFrameAnalyzedUtc,
                LastReportedState: _lastReportedState,
                LastStateChangeUtc: _lastStateChangeUtc,
                LastHomeAssistantPushUtc: _lastHomeAssistantPushUtc);
        }
    }
}

/// <summary>Immutable snapshot of <see cref="DetectionStatus"/>, shaped for JSON serialization.</summary>
public record DetectionStatusSnapshot(
    DoorState State,
    DoorState RawState,
    double PixelDiffPercent,
    double EdgeChangedPercent,
    LightingMode Lighting,
    DetectionMethod Method,
    double OpenThreshold,
    double CloseThreshold,
    ConnectionState Connection,
    int ConsecutiveGrabFailures,
    DateTimeOffset? LastGrabUtc,
    long? FrameAgeMs,
    DateTimeOffset? LastFrameAnalyzedUtc,
    DoorState LastReportedState,
    DateTimeOffset? LastStateChangeUtc,
    DateTimeOffset? LastHomeAssistantPushUtc);
