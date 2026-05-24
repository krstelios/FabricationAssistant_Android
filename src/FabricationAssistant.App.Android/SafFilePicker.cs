using Android.Content;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.AppCompat.App;
using AndroidUri = Android.Net.Uri;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Wraps Storage Access Framework ACTION_OPEN_DOCUMENT into a single async call.
/// The contract is registered once during MainActivity.OnCreate; PickAsync() can
/// be called repeatedly afterwards. The returned task completes with the picked
/// content URI or null if the user cancelled.
/// </summary>
public sealed class SafFilePicker
{
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<AndroidUri?>? _pending;

    public SafFilePicker(AppCompatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var contract = new ActivityResultContracts.OpenDocument();
        var callback = new ResultCallback(uri =>
        {
            var p = _pending;
            _pending = null;
            p?.TrySetResult(uri);
        });

        _launcher = activity.RegisterForActivityResult(contract, callback)
            ?? throw new InvalidOperationException("RegisterForActivityResult returned null");
    }

    public Task<AndroidUri?> PickAsync(string[] mimeTypes)
    {
        ArgumentNullException.ThrowIfNull(mimeTypes);
        if (_pending is not null)
            throw new InvalidOperationException("A file picker is already active.");

        _pending = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _launcher.Launch(mimeTypes);
        return _pending.Task;
    }

    private sealed class ResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly Action<AndroidUri?> _action;
        public ResultCallback(Action<AndroidUri?> action) { _action = action; }
        public void OnActivityResult(Java.Lang.Object? result) => _action(result as AndroidUri);
    }
}
