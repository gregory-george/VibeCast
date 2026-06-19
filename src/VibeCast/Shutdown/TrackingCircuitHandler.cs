using Microsoft.AspNetCore.Components.Server.Circuits;

namespace VibeCast.Shutdown;

/// <summary>
/// One instance per Blazor circuit (registered scoped), feeding the singleton
/// <see cref="CircuitTracker"/>. OnConnectionUp/Down (not OnCircuitOpened/Closed)
/// is what tracks an actual live SignalR connection -- a circuit can persist
/// briefly across a disconnect (reconnect UI) without a new "up" event.
/// </summary>
internal sealed class TrackingCircuitHandler(CircuitTracker circuitTracker) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        circuitTracker.Increment();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        circuitTracker.Decrement();
        return Task.CompletedTask;
    }
}
