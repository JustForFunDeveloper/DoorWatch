namespace DoorWatch.Camera;

/// <summary>Connection settings for the camera source.</summary>
public class CameraConfig
{
    /// <summary>Selects whether to use a local USB device or an RTSP network stream.</summary>
    public CameraSourceType Source { get; set; } = CameraSourceType.Usb;

    /// <summary>USB camera device index (e.g. <c>0</c> for <c>/dev/video0</c>). Only used when <see cref="Source"/> is <see cref="CameraSourceType.Usb"/>.</summary>
    public int DeviceIndex { get; set; } = 0;

    /// <summary>Full RTSP URL including credentials (e.g. <c>rtsp://user:pass@192.168.1.x/stream</c>). Only used when <see cref="Source"/> is <see cref="CameraSourceType.Rtsp"/>.</summary>
    public string RtspUrl { get; set; } = string.Empty;
}

/// <summary>Specifies the type of camera source.</summary>
public enum CameraSourceType
{
    /// <summary>Local USB or V4L2 camera device.</summary>
    Usb,

    /// <summary>Network camera accessed via an RTSP stream.</summary>
    Rtsp
}
