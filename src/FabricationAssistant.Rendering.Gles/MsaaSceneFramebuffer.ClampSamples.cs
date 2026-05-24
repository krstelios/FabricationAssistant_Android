namespace FabricationAssistant.Rendering.Gles;

public sealed partial class MsaaSceneFramebuffer
{
    /// <summary>
    /// Snaps a desired sample count to the nearest supported bucket
    /// (1, 2, 4, 8, 16). Pure arithmetic - does not read GL state.
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
            <= 8 => 8,
            _ => 16
        };
    }
}
