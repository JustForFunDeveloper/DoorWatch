using System.Net.Http.Json;
using DoorWatch.Core;
using Microsoft.Extensions.Logging;

namespace DoorWatch.HomeAssistant;

/// <summary>
/// <see cref="IHomeAssistantClient"/> implementation that calls the Home Assistant REST Services API
/// to turn an entity on or off based on the detected door state.
/// </summary>
public sealed class HomeAssistantClient : IHomeAssistantClient
{
    private readonly HttpClient _http;
    private readonly HomeAssistantConfig _config;
    private readonly ILogger<HomeAssistantClient> _logger;

    public HomeAssistantClient(
        HttpClient http,
        HomeAssistantConfig config,
        ILogger<HomeAssistantClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task UpdateDoorStateAsync(DoorState state, CancellationToken ct = default)
    {
        if (state == DoorState.Unknown)
            return;

        // entity_id format: "domain.name" → e.g. "light.eg_buero"
        string domain = _config.EntityId.Split('.')[0];
        string service = state == DoorState.Open ? "turn_on" : "turn_off";
        string url = $"{_config.BaseUrl.TrimEnd('/')}/api/services/{domain}/{service}";

        var payload = new { entity_id = _config.EntityId };

        try
        {
            var response = await _http.PostAsJsonAsync(url, payload, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("HA service called: {Domain}/{Service} for {EntityId}", domain, service, _config.EntityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call HA service {Domain}/{Service} for {EntityId}.", domain, service, _config.EntityId);
        }
    }
}
