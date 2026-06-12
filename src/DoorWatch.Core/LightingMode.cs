namespace DoorWatch.Core;

/// <summary>Lighting mode of a camera frame, used to select the matching baseline image.</summary>
public enum LightingMode
{
    /// <summary>Daylight colour image.</summary>
    Day,

    /// <summary>Infrared night-vision image (greyscale).</summary>
    Night
}
