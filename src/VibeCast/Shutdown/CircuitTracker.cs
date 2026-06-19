namespace VibeCast.Shutdown;

/// <summary>
/// Singleton count of active Blazor circuits, fed by <see cref="TrackingCircuitHandler"/>
/// (one instance per circuit, scoped). Closing a tab drops a circuit; an idle open tab
/// pins one indefinitely -- that's intended (CLAUDE.md: "circuit churn is expected").
/// </summary>
internal sealed class CircuitTracker
{
    private int activeCircuits;

    public event Action? Changed;

    public int ActiveCircuits => activeCircuits;

    public void Increment()
    {
        Interlocked.Increment(ref activeCircuits);
        Changed?.Invoke();
    }

    public void Decrement()
    {
        Interlocked.Decrement(ref activeCircuits);
        Changed?.Invoke();
    }
}
