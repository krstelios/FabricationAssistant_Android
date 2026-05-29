namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Maps RenderMode values to what the Android Gles renderer supports.
/// RenderMode.Realistic is not implemented on Android (no PBR / HDR / IBL
/// pipeline); it maps to Shaded with a one-time log.
/// This shim only changes the *effective* render mode; the persisted setting
/// keeps the user's requested value (e.g. Realistic) so the choice survives
/// across sessions. (It does not guarantee a particular cross-platform mapping -
/// the Android and desktop RenderMode enum orderings are not assumed identical.)
/// </summary>
public static class AndroidRenderModeShim
{
    private static bool _logged;

    public static RenderMode Effective(RenderMode requested)
    {
        RenderMode effective = RenderModeSupport.EffectiveAndroidMode(requested);
        if (requested == RenderMode.Realistic)
        {
            if (!_logged)
            {
                Android.Util.Log.Info(
                    "FabricationAssistant",
                    "Realistic mode not supported by Rendering.Gles; falling back to Shaded.");
                _logged = true;
            }
        }

        return effective;
    }
}
