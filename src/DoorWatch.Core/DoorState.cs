namespace DoorWatch.Core;

/// <summary>Represents the observed state of the monitored door.</summary>
public enum DoorState
{
    /// <summary>State has not yet been determined — startup or no baseline image available.</summary>
    Unknown,

    /// <summary>The door is closed: no significant change detected in the ROI compared to the baseline.</summary>
    Closed,

    /// <summary>The door is open: a significant change was detected in the ROI compared to the baseline.</summary>
    Open
}
