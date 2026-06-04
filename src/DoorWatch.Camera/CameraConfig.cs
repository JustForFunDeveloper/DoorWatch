namespace DoorWatch.Camera;

public class CameraConfig
{
    public CameraSourceType Source { get; set; } = CameraSourceType.Usb;

    /// <summary>USB camera device index (e.g. 0 for /dev/video0).</summary>
    public int DeviceIndex { get; set; } = 0;

    /// <summary>RTSP or HTTP stream URL when Source = Rtsp.</summary>
    public string RtspUrl { get; set; } = string.Empty;
}

public enum CameraSourceType
{
    Usb,
    Rtsp
}
