namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Maps RenderMode values to what the Android Gles renderer supports.
/// RenderMode.Realistic is not implemented on Android (no PBR / HDR / IBL
/// pipeline); it maps to Shaded with a one-time log.
/// The persisted setting stays as Realistic so the choice survives across
/// sessions and activates correctly when the same project is opened on desktop.
/// </summary>
public static class AndroidRenderModeShim
{
    private static bool _logged;

    public static RenderMode Effective(RenderMode requested)
    {
        if (requested == RenderMode.Realistic)
        {
            if (!_logged)
            {
                Android.Util.Log.Info(
                    "FabricationAssistant",
                    "Realistic mode not supported by Rendering.Gles; falling back to Shaded.");
                _logged = true;
            }

            return RenderMode.Shaded;
        }

        return requested;
    }
}
