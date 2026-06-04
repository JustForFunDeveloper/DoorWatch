namespace DoorWatch.Core;

public interface IDoorDetector : IDisposable
{
    DetectionResult Detect();
}
