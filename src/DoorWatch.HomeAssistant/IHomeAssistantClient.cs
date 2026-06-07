using DoorWatch.Core;

namespace DoorWatch.HomeAssistant;

/// <summary>Posts door-state changes to the Home Assistant Services API.</summary>
public interface IHomeAssistantClient
{
    /// <summary>
    /// Calls <c>turn_on</c> (door opened) or <c>turn_off</c> (door closed) on the configured Home Assistant entity.
    /// </summary>
    /// <param name="state">The new door state. <see cref="DoorState.Unknown"/> is a no-op.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateDoorStateAsync(DoorState state, CancellationToken ct = default);
}
