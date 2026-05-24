namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Ported from the desktop renderer. Captures the GL render thread ID at
/// Initialize() and verifies every subsequent guard call happens on the same thread.
/// </summary>
public sealed class GlThreadGuard
{
    private int _renderThreadId = -1;

    public void Initialize()
    {
        _renderThreadId = Environment.CurrentManagedThreadId;
    }

    public void EnsureOnRenderThread()
    {
        if (_renderThreadId < 0)
            throw new InvalidOperationException("GlThreadGuard.Initialize() has not been called.");
        if (Environment.CurrentManagedThreadId != _renderThreadId)
            throw new InvalidOperationException(
                $"GL call from thread {Environment.CurrentManagedThreadId}; render thread is {_renderThreadId}.");
    }
}
