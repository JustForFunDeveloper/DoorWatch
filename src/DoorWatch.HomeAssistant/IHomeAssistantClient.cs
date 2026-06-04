using DoorWatch.Core;

namespace DoorWatch.HomeAssistant;

public interface IHomeAssistantClient
{
    Task UpdateDoorStateAsync(DoorState state, CancellationToken ct = default);
}
