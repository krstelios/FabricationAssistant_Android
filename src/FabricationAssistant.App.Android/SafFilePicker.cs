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
public sealed class SafFilePicker : IDisposable
{
    private static readonly TimeSpan PickTimeout = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<AndroidUri?>? _pending;
    private CancellationTokenSource? _timeoutCts;
    private bool _disposed;

    public SafFilePicker(AppCompatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var contract = new ActivityResultContracts.StartActivityForResult();
        var callback = new ResultCallback(CompletePending);

        _launcher = activity.RegisterForActivityResult(contract, callback)
            ?? throw new InvalidOperationException("RegisterForActivityResult returned null");
    }

    public Task<AndroidUri?> PickAsync(string[] mimeTypes)
    {
        ArgumentNullException.ThrowIfNull(mimeTypes);
        TaskCompletionSource<AndroidUri?> pending;
        CancellationTokenSource timeoutCts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null)
                throw new InvalidOperationException("A file picker is already active.");

            pending = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            timeoutCts = new CancellationTokenSource();
            _pending = pending;
            _timeoutCts = timeoutCts;
        }

        try
        {
            _launcher.Launch(CreateOpenDocumentIntent(mimeTypes));
        }
        catch
        {
            CancelPending("launch failed");
            throw;
        }
        _ = CancelPendingAfterTimeoutAsync(timeoutCts.Token);
        return pending.Task;
    }

    private static Intent CreateOpenDocumentIntent(string[] mimeTypes)
    {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, mimeTypes);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        return intent;
    }

    public void CancelActivePick(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CancelPending(reason);
    }

    public void Dispose()
    {
        TaskCompletionSource<AndroidUri?>? pending;
        CancellationTokenSource? timeoutCts;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            pending = _pending;
            timeoutCts = _timeoutCts;
            _pending = null;
            _timeoutCts = null;
        }

        timeoutCts?.Cancel();
        timeoutCts?.Dispose();
        pending?.TrySetCanceled();
        try { _launcher.Unregister(); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FA.Import", "Failed to unregister file picker: " + ex.Message);
        }
    }

    private async Task CancelPendingAfterTimeoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(PickTimeout, token).ConfigureAwait(false);
            CancelPending("file picker timed out");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CompletePending(AndroidUri? uri)
    {
        TaskCompletionSource<AndroidUri?>? pending;
        CancellationTokenSource? timeoutCts;
        lock (_gate)
        {
            pending = _pending;
            timeoutCts = _timeoutCts;
            _pending = null;
            _timeoutCts = null;
        }

        timeoutCts?.Cancel();
        timeoutCts?.Dispose();
        pending?.TrySetResult(uri);
    }

    private void CancelPending(string reason)
    {
        TaskCompletionSource<AndroidUri?>? pending;
        CancellationTokenSource? timeoutCts;
        lock (_gate)
        {
            pending = _pending;
            timeoutCts = _timeoutCts;
            _pending = null;
            _timeoutCts = null;
        }

        timeoutCts?.Cancel();
        timeoutCts?.Dispose();
        if (pending?.TrySetCanceled() == true)
            global::Android.Util.Log.Warn("FA.Import", "Canceled pending file picker: " + reason);
    }

    private sealed class ResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly Action<AndroidUri?> _action;
        public ResultCallback(Action<AndroidUri?> action) { _action = action; }
        public void OnActivityResult(Java.Lang.Object? result)
        {
            if (result is not ActivityResult activityResult ||
                activityResult.ResultCode != (int)global::Android.App.Result.Ok)
            {
                _action(null);
                return;
            }

            _action(activityResult.Data?.Data);
        }
    }
}
