namespace FabricationAssistant.Rendering.Gles;

public sealed partial class MsaaSceneFramebuffer
{
    /// <summary>
    /// Snaps a desired sample count to the nearest Android-supported bucket
    /// (1, 2, 4, 8). Pure arithmetic - does not read GL state.
    /// The effective sample count is further capped against
    /// GL_MAX_SAMPLES inside <see cref="Ensure"/>.
    /// </summary>
    public static int ClampSamples(int desired)
    {
        return desired switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 4 => 4,
            _ => 8
        };
    }

    /// <summary>
    /// Applies a hardware GL_MAX_SAMPLES limit while staying on the legal
    /// buckets used by the renderer. Some drivers report non-bucket limits
    /// such as 3 or 6; do not pass those through to framebuffer allocation.
    /// </summary>
    public static int ClampSamplesToHardwareLimit(int desired, int maxSamples)
    {
        int clamped = ClampSamples(desired);
        if (maxSamples <= 1)
            return 1;
        if (clamped <= maxSamples)
            return clamped;
        if (maxSamples >= 8)
            return 8;
        if (maxSamples >= 4)
            return 4;
        return 2;
    }
}
