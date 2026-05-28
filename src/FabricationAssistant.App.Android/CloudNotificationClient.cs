using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace FabricationAssistant.App.Android;

internal sealed class CloudNotificationClient : IAsyncDisposable, IDisposable
{
    private readonly CloudApiClient _cloudClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _subscribedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdownCts = new();
    private HubConnection? _connection;
    private bool _disposed;

    public CloudNotificationClient(CloudApiClient cloudClient)
    {
        _cloudClient = cloudClient;
    }

    public event Action<CloudNotificationEnvelope>? EventReceived;

    public async Task SubscribePackageAsync(string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || _disposed)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        CancellationToken token = linkedCts.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_subscribedPackageIds.Add(packageId))
                return;

            HubConnection connection = await EnsureConnectionLockedAsync(token).ConfigureAwait(false);
            if (connection.State == HubConnectionState.Connected)
                await InvokeSubscribeAsync(connection, packageId, token).ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            _subscribedPackageIds.Remove(packageId);
            global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Subscribe rejected: " + ex.GetBaseException().Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnsubscribePackageAsync(string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || _disposed)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        CancellationToken token = linkedCts.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _subscribedPackageIds.Remove(packageId);
            if (_connection?.State == HubConnectionState.Connected)
                await InvokeUnsubscribeAsync(_connection, packageId, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Unsubscribe failed: " + ex.GetBaseException().Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopAsync()
        => StopAsync(CancellationToken.None);

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _subscribedPackageIds.Clear();
            if (_connection is null)
                return;

            HubConnection connection = _connection;
            _connection = null;
            await connection.DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Stop failed: " + ex.GetBaseException().Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Task.Run(() => StopAsync(timeoutCts.Token), timeoutCts.Token).Wait(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Dispose failed: " + ex.GetBaseException().Message);
        }

        _shutdownCts.Dispose();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await StopAsync(timeoutCts.Token).ConfigureAwait(false);
        _shutdownCts.Dispose();
        _gate.Dispose();
    }

    private async Task<HubConnection> EnsureConnectionLockedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { State: HubConnectionState.Connected } connected)
            return connected;

        if (_connection is null)
            _connection = CreateConnection();

        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync(ct).ConfigureAwait(false);
            global::Android.Util.Log.Info("FA.Cloud.SignalR", "Connected to cloud notifications.");
        }

        return _connection;
    }

    private HubConnection CreateConnection()
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(_cloudClient.BuildNotificationHubUri(), options =>
            {
                options.AccessTokenProvider = async () =>
                {
                    using var tokenCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                    tokenCts.CancelAfter(TimeSpan.FromSeconds(10));
                    return await _cloudClient.GetAccessTokenForRealtimeAsync(tokenCts.Token).ConfigureAwait(false);
                };
            })
            .WithAutomaticReconnect(IndefiniteReconnectPolicy.Instance)
            .Build();

        connection.On<CloudNotificationEnvelope>("event", envelope =>
        {
            try
            {
                EventReceived?.Invoke(envelope);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Notification handler failed: " + ex.GetBaseException().Message);
            }
        });

        connection.Closed += ex =>
        {
            if (ex is not null)
                global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Disconnected: " + ex.GetBaseException().Message);
            return Task.CompletedTask;
        };
        connection.Reconnected += _ => ResubscribeAfterReconnectAsync();
        return connection;
    }

    private async Task ResubscribeAfterReconnectAsync()
    {
        string[] packageIds;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection?.State != HubConnectionState.Connected)
                return;
            packageIds = _subscribedPackageIds.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (string packageId in packageIds)
        {
            try
            {
                if (_connection is { State: HubConnectionState.Connected } connection)
                    await InvokeSubscribeAsync(connection, packageId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                global::Android.Util.Log.Warn("FA.Cloud.SignalR", "Resubscribe failed: " + ex.GetBaseException().Message);
            }
        }
    }

    private static async Task InvokeSubscribeAsync(HubConnection connection, string packageId, CancellationToken ct)
    {
        await connection.InvokeAsync("SubscribePackage", packageId, ct).ConfigureAwait(false);
        global::Android.Util.Log.Info("FA.Cloud.SignalR", "Subscribed package " + packageId + ".");
    }

    private static async Task InvokeUnsubscribeAsync(HubConnection connection, string packageId, CancellationToken ct)
    {
        await connection.InvokeAsync("UnsubscribePackage", packageId, ct).ConfigureAwait(false);
        global::Android.Util.Log.Info("FA.Cloud.SignalR", "Unsubscribed package " + packageId + ".");
    }

    private sealed class IndefiniteReconnectPolicy : IRetryPolicy
    {
        public static readonly IndefiniteReconnectPolicy Instance = new();

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => retryContext.PreviousRetryCount switch
            {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
    }
}
