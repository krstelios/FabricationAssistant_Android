namespace FabricationAssistant.Platform;

/// <summary>
/// Abstraction over a UI-thread dispatcher. New code MUST consume this via DI
/// rather than calling Application.Current.Dispatcher (which doesn't exist on Android).
/// </summary>
public interface IDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    void Send(Action action);
}
