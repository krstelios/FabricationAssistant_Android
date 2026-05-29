namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Ported from the desktop renderer. Captures the GL render thread ID at
/// InitializeOnCurrentThread() and verifies every subsequent guard call happens
/// on the same thread.
/// </summary>
public sealed class GlThreadGuard
{
    private int _renderThreadId = -1;

    public void InitializeOnCurrentThread()
        => Interlocked.Exchange(ref _renderThreadId, Environment.CurrentManagedThreadId);

    public void EnsureOnRenderThread()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int renderThreadId = Volatile.Read(ref _renderThreadId);
        if (renderThreadId < 0)
            throw new InvalidOperationException("GL render thread guard was used before initialization.");

        if (currentThreadId != renderThreadId)
            throw new InvalidOperationException(
                $"GL call from thread {currentThreadId}; render thread is {renderThreadId}.");
    }
}
