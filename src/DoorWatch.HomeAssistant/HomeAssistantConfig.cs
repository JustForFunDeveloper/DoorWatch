namespace DoorWatch.HomeAssistant;

public class HomeAssistantConfig
{
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";
    public string Token { get; set; } = string.Empty;

    /// <summary>Entity ID to update, e.g. "binary_sensor.sliding_door".</summary>
    public string EntityId { get; set; } = "binary_sensor.sliding_door";
}
