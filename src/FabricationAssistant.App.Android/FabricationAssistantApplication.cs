using Android.App;
using Android.Runtime;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Minimal Application subclass whose only responsibility is to install the
/// global crash logger as early as possible - before MainActivity exists - so
/// crashes during early process startup are also persisted. All app settings
/// and services are still initialized in MainActivity.OnCreate as before
/// (AppSettings.Initialize remains the first step there).
/// </summary>
[Application]
public sealed class FabricationAssistantApplication : Application
{
    public FabricationAssistantApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidCrashLogger.Install(this);
    }
}
