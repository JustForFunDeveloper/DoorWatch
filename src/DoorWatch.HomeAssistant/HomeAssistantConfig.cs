namespace DoorWatch.HomeAssistant;

/// <summary>Connection settings for the Home Assistant instance.</summary>
public class HomeAssistantConfig
{
    /// <summary>Base URL of the Home Assistant instance, e.g. <c>http://homeassistant.local:8123</c>.</summary>
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    /// <summary>Long-lived access token used to authenticate API requests. Generate one under Profile → Security → Long-Lived Access Tokens.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Entity ID to control, e.g. <c>input_boolean.detected_door_state</c> or <c>light.eg_buero</c>.
    /// The domain prefix (before the first <c>.</c>) is parsed automatically to build the correct service URL.
    /// </summary>
    public string EntityId { get; set; } = "binary_sensor.sliding_door";
}
