using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;
using AndroidUri = Android.Net.Uri;
using Bitmap = Android.Graphics.Bitmap;
using BitmapFactory = Android.Graphics.BitmapFactory;

namespace FabricationAssistant.App.Android;

public sealed class CloudApiClient : IDisposable
{
    private const int MaxTransientRetries = 3;
    private const long ThumbnailCacheMaxBytes = 50L * 1024L * 1024L;
    private const int ThumbnailCacheMaxEntries = 500;
    private const long SmallWriterSaveUploadMaxBytes = 100L * 1024L * 1024L;
    private const long MaxUnknownCloudDownloadBytes = 2L * 1024L * 1024L * 1024L;
    private const long DownloadFreeSpaceCheckIntervalBytes = 16L * 1024L * 1024L;
    private const long MinimumStreamingDownloadFreeBytes = 64L * 1024L * 1024L;
    private const int WriterSaveChunkBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan OperationPollTimeout = TimeSpan.FromMinutes(5);
    // S22#3: bound for control-plane calls (refresh, etc.) whose request token
    // would otherwise be unbounded against a half-dead server.
    private static readonly TimeSpan ControlPlaneTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Context _context;
    private readonly CloudSecureStore _secureStore;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseProxy = false,
        // S22#3: bound the TCP+TLS connect so an unreachable/half-dead server
        // fails fast instead of hanging SendAsync forever. The overall
        // HttpClient.Timeout stays infinite (intentional for large streaming
        // uploads/downloads). PooledConnectionLifetime recycles connections so
        // a stale route does not wedge later requests.
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });
    private readonly object _sync = new();
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ManualResetEventSlim _activeOperationsIdle = new(initialState: true);
    private CloudAuthSession? _session;
    private Task<bool>? _refreshTask;
    private int _activeOperations;
    private bool _disposed;

    public CloudApiClient(Context context, CloudSecureStore secureStore)
    {
        _context = context.ApplicationContext ?? context;
        _secureStore = secureStore;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public event Action? SessionChanged;

    public bool IsSignedIn => SnapshotSession() is not null;

    public string? SignedInEmail => SnapshotSession()?.Email;

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!AppSettings.CloudRememberCredentials)
            return false;

        string? refreshToken = _secureStore.LoadRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        DateTimeOffset? refreshTokenExpiresAt = _secureStore.LoadRefreshTokenExpiresAt();
        if (refreshTokenExpiresAt is not null && refreshTokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            _secureStore.ClearRefreshToken();
            return false;
        }

        string email = AppSettings.CloudUserEmail.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            string serverUrl = NormalizeServerUrl(AppSettings.CloudServerUrl);
            var seed = new CloudAuthSession(
                serverUrl,
                email,
                string.Empty,
                null,
                refreshToken,
                refreshTokenExpiresAt,
                null);

            if (await TryRefreshAsync(seed, ct).ConfigureAwait(false))
                return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Cloud session restore failed: " + ex.GetBaseException().Message);
        }

        _secureStore.ClearRefreshToken();
        lock (_sync)
            _session = null;
        RaiseSessionChanged();
        return false;
    }

    public async Task<CloudSignInOutcome> SignInAsync(
        string serverUrl,
        string email,
        string password,
        bool rememberCredentials,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        string normalizedServer = NormalizeServerUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(email))
            throw new CloudApiException("Enter your cloud email.");
        if (string.IsNullOrEmpty(password))
            throw new CloudApiException("Enter your cloud password.");

        var body = new { email = email.Trim(), password };
        CloudSignInResult result = await PostJsonNoAuthAsync<CloudSignInResult>(
            normalizedServer,
            "/auth/signin",
            body,
            ct).ConfigureAwait(false);

        if (result.MustChangePassword)
            throw new CloudApiException("This account must change its password before Android package access. Use the admin or desktop flow to change the password, then sign in again.");

        string trimmedEmail = email.Trim();
        if (result.RequiresTotpEnrollment)
        {
            if (string.IsNullOrWhiteSpace(result.AccessToken))
                throw new CloudApiException("Cloud sign-in requires TOTP enrollment but did not return an enrollment token.");

            AppSettings.CloudUserEmail = trimmedEmail;
            return new CloudSignInOutcome(
                CloudSignInOutcomeKind.TotpEnrollmentRequired,
                normalizedServer,
                trimmedEmail,
                rememberCredentials,
                result.AccessToken,
                result.AccessTokenExpiresAt,
                null);
        }

        if (result.RequiresTotp)
        {
            if (string.IsNullOrWhiteSpace(result.ChallengeId))
                throw new CloudApiException("Cloud sign-in requires a TOTP code but did not return a challenge id.");

            AppSettings.CloudUserEmail = trimmedEmail;
            return new CloudSignInOutcome(
                CloudSignInOutcomeKind.TotpRequired,
                normalizedServer,
                trimmedEmail,
                rememberCredentials,
                null,
                null,
                result.ChallengeId);
        }

        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new CloudApiException("Cloud sign-in did not return an access token.");

        StoreSignedInSession(normalizedServer, trimmedEmail, result, rememberCredentials);
        return new CloudSignInOutcome(
            CloudSignInOutcomeKind.SignedIn,
            normalizedServer,
            trimmedEmail,
            rememberCredentials,
            null,
            null,
            null);
    }

    public async Task<CloudSignInOutcome> VerifyTotpSignInAsync(
        string serverUrl,
        string email,
        string challengeId,
        string totpCode,
        bool rememberCredentials,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        string normalizedServer = NormalizeServerUrl(serverUrl);
        string trimmedEmail = email.Trim();
        string trimmedChallengeId = challengeId.Trim();
        string normalizedCode = NormalizeTotpCode(totpCode);
        if (string.IsNullOrWhiteSpace(trimmedChallengeId))
            throw new CloudApiException("Cloud sign-in did not return a TOTP challenge id.");

        CloudSignInResult result = await PostJsonNoAuthAsync<CloudSignInResult>(
            normalizedServer,
            "/auth/2fa-verify",
            new
            {
                challenge_id = trimmedChallengeId,
                totp_code = normalizedCode,
            },
            ct).ConfigureAwait(false);

        if (result.MustChangePassword)
            throw new CloudApiException("This account must change its password before Android package access. Use the admin or desktop flow to change the password, then sign in again.");
        if (result.RequiresTotp || result.RequiresTotpEnrollment)
            throw new CloudApiException("Cloud sign-in returned another MFA challenge after TOTP verification.");
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new CloudApiException("Cloud TOTP verification did not return an access token.");

        StoreSignedInSession(normalizedServer, trimmedEmail, result, rememberCredentials);
        return new CloudSignInOutcome(
            CloudSignInOutcomeKind.SignedIn,
            normalizedServer,
            trimmedEmail,
            rememberCredentials,
            null,
            null,
            null);
    }

    public Task<CloudTotpEnrollmentStartResult> StartTotpEnrollmentAsync(
        string serverUrl,
        string temporaryAccessToken,
        CancellationToken ct)
        => PostJsonWithBearerAsync<CloudTotpEnrollmentStartResult>(
            NormalizeServerUrl(serverUrl),
            "/auth/totp/enroll/start",
            temporaryAccessToken,
            body: null,
            ct);

    public Task<CloudTotpEnrollmentVerifyResult> VerifyTotpEnrollmentAsync(
        string serverUrl,
        string temporaryAccessToken,
        string totpCode,
        CancellationToken ct)
        => PostJsonWithBearerAsync<CloudTotpEnrollmentVerifyResult>(
            NormalizeServerUrl(serverUrl),
            "/auth/totp/enroll/verify",
            temporaryAccessToken,
            new { totp_code = NormalizeTotpCode(totpCode) },
            ct);

    public async Task<CloudPasswordResetChallenge> StartPasswordResetAsync(
        string serverUrl,
        string email,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        string normalizedServer = NormalizeServerUrl(serverUrl);
        string trimmedEmail = email.Trim();
        if (string.IsNullOrWhiteSpace(trimmedEmail))
            throw new CloudApiException("Enter your cloud email.");

        CloudPasswordResetStartResult result = await PostJsonNoAuthAsync<CloudPasswordResetStartResult>(
            normalizedServer,
            "/auth/password-reset/start",
            new { email = trimmedEmail },
            ct).ConfigureAwait(false);

        global::Android.Util.Log.Info("FA.Cloud.Auth", "Password reset challenge requested.");
        return new CloudPasswordResetChallenge(
            normalizedServer,
            trimmedEmail,
            CloudPasswordResetFlow.RequireChallengeId(result));
    }

    public Task VerifyPasswordResetAsync(
        string serverUrl,
        string challengeId,
        string authenticatorOrBackupCode,
        string newPassword,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        string normalizedServer = NormalizeServerUrl(serverUrl);
        string trimmedChallengeId = challengeId.Trim();
        if (string.IsNullOrWhiteSpace(trimmedChallengeId))
            throw new CloudApiException("Password reset challenge expired. Start the reset again.", code: "missing_challenge");
        if (string.IsNullOrEmpty(newPassword))
            throw new CloudApiException("Enter a new password.", code: "password_required");

        string code = CloudPasswordResetFlow.NormalizeAuthenticatorOrBackupCode(authenticatorOrBackupCode);
        return CloudPasswordResetFlow.CompleteSuccessfulResetAsync(
            async token =>
            {
                await PostJsonNoAuthAsync<CloudPasswordResetVerifyResult>(
                    normalizedServer,
                    "/auth/password-reset/verify",
                    new
                    {
                        challenge_id = trimmedChallengeId,
                        totp_code = code,
                        new_password = newPassword,
                    },
                    token).ConfigureAwait(false);
            },
            SignOut,
            () => global::Android.Util.Log.Info("FA.Cloud.Auth", "Password reset completed."),
            ct);
    }

    private void StoreSignedInSession(
        string normalizedServer,
        string email,
        CloudSignInResult result,
        bool rememberCredentials)
    {
        string accessToken = result.AccessToken
            ?? throw new CloudApiException("Cloud sign-in did not return an access token.");
        var session = new CloudAuthSession(
            normalizedServer,
            email,
            accessToken,
            result.AccessTokenExpiresAt,
            result.RefreshToken,
            result.RefreshTokenExpiresAt,
            result.SessionFamilyId);

        lock (_sync)
            _session = session;

        AppSettings.CloudUserEmail = email;
        AppSettings.CloudRememberCredentials = rememberCredentials;

        if (rememberCredentials)
        {
            _secureStore.SaveRefreshToken(result.RefreshToken, result.RefreshTokenExpiresAt);
        }
        else
        {
            _secureStore.ClearRefreshToken();
        }

        RaiseSessionChanged();
    }

    private static string NormalizeTotpCode(string value)
    {
        string code = new(value.Where(char.IsDigit).ToArray());
        if (code.Length != 6)
            throw new CloudApiException("Enter the 6-digit authenticator code.");
        return code;
    }

    public void SignOut()
    {
        lock (_sync)
            _session = null;

        _secureStore.Clear();
        AppSettings.CloudRememberCredentials = false;
        AppSettings.CloudRememberPassword = false;
        PurgeCloudCache();
        RaiseSessionChanged();
    }

    public async Task<CloudBrowserSnapshot> LoadBrowserAsync(CancellationToken ct)
    {
        CloudProjectsResponse projectResponse = await GetAuthorizedJsonAsync<CloudProjectsResponse>(
            "/api/v1/projects/accessible",
            ct).ConfigureAwait(false);
        CloudProject[] projects = (projectResponse.Projects ?? Array.Empty<CloudProject>())
            .Where(project => !string.IsNullOrWhiteSpace(project.ProjectId))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var packages = new List<CloudPackageSummary>();
        foreach (CloudProject project in projects)
        {
            CloudPackagesResponse packageResponse = await GetAuthorizedJsonAsync<CloudPackagesResponse>(
                "/api/v1/projects/" + Uri.EscapeDataString(project.ProjectId) + "/packages",
                ct).ConfigureAwait(false);

            foreach (CloudPackageDto package in packageResponse.Packages ?? Array.Empty<CloudPackageDto>())
            {
                string status = package.Status ?? "";
                if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    continue;

                packages.Add(new CloudPackageSummary(
                    project.ProjectId,
                    project.Name,
                    package.PackageId,
                    package.PartNumber ?? "Model",
                    package.Revision ?? "",
                    status,
                    package.CurrentVersionId,
                    package.CurrentCounter,
                    package.CurrentVersionStatus,
                    package.CurrentVersionValidationStatus,
                    package.HasPreview,
                    package.PreviewUrl,
                    package.UpdatedAt));
            }
        }

        return new CloudBrowserSnapshot(
            projects,
            packages
                .OrderBy(package => package.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Revision, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<Bitmap?> LoadPreviewAsync(CloudPackageSummary package, CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        ThrowIfDisposed();
        if (!package.HasPreview || string.IsNullOrWhiteSpace(package.PreviewUrl))
            return null;

        string cachePath = GetPreviewCachePath(package.PackageId);
        if (File.Exists(cachePath))
        {
            TouchCacheFile(cachePath);
            return BitmapFactory.DecodeFile(cachePath);
        }

        using HttpResponseMessage response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUri(SnapshotRequiredSession().ServerUrl, package.PreviewUrl)),
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
        PruneThumbnailCache();
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
    }

    public async Task<CloudDownloadedModel> DownloadForOpenAsync(
        CloudPackageSummary package,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!package.IsReadyToOpen)
            throw new CloudApiException("This cloud model is not current and validated yet.");

        progress?.Report("Reading cloud metadata...");
        CloudPackageDetailDto detail = await GetAuthorizedJsonAsync<CloudPackageDetailDto>(
            "/api/v1/packages/" + Uri.EscapeDataString(package.PackageId),
            ct).ConfigureAwait(false);

        CloudPackageVersionDto version = detail.CurrentVersion
            ?? throw new CloudApiException("Cloud package did not include a current version.");

        return await DownloadResolvedVersionForOpenAsync(
            package,
            detail,
            version,
            isReadOnly: false,
            progress,
            ct).ConfigureAwait(false);
    }

    public async Task<CloudDownloadedModel> DownloadVersionForOpenAsync(
        CloudPackageSummary package,
        int counter,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!package.IsReadyToOpen)
            throw new CloudApiException("This cloud model is not current and validated yet.");
        if (counter < 0)
            throw new CloudApiException("Cloud counter must be zero or greater.");

        progress?.Report("Reading cloud metadata...");
        CloudPackageDetailDto detail = await GetAuthorizedJsonAsync<CloudPackageDetailDto>(
            "/api/v1/packages/" + Uri.EscapeDataString(package.PackageId),
            ct).ConfigureAwait(false);

        int currentCounter = detail.CurrentVersion?.Counter ?? detail.CurrentCounter;
        CloudPackageVersionDto version = counter == currentCounter
            ? detail.CurrentVersion ?? throw new CloudApiException("Cloud package did not include a current version.")
            : await GetAuthorizedJsonAsync<CloudPackageVersionDto>(
                "/api/v1/packages/" + Uri.EscapeDataString(package.PackageId) + "/versions/" + counter.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);

        return await DownloadResolvedVersionForOpenAsync(
            package,
            detail,
            version,
            isReadOnly: counter != currentCounter,
            progress,
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudPackageVersionSummary>> LoadPackageVersionsAsync(string packageId, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(packageId))
            throw new CloudApiException("Cloud package id is missing.");

        CloudPackageVersionsResponse response = await GetAuthorizedJsonAsync<CloudPackageVersionsResponse>(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId) + "/versions",
            ct).ConfigureAwait(false);

        return (response.Versions ?? Array.Empty<CloudPackageVersionDto>())
            .Select(version => new CloudPackageVersionSummary(
                version.Counter,
                version.VersionId,
                version.Status,
                version.ValidationStatus,
                version.UploadedAt))
            .OrderByDescending(version => version.Counter)
            .ToArray();
    }

    private async Task<CloudDownloadedModel> DownloadResolvedVersionForOpenAsync(
        CloudPackageSummary package,
        CloudPackageDetailDto detail,
        CloudPackageVersionDto version,
        bool isReadOnly,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        string versionId = version.VersionId ?? (!isReadOnly ? detail.CurrentVersionId ?? package.CurrentVersionId : null)
            ?? throw new CloudApiException("Cloud package version id is missing.");
        string expectedSha = version.Sha256
            ?? throw new CloudApiException("Cloud package version SHA-256 is missing.");
        if (!string.Equals(version.ValidationStatus, "passed", StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("This cloud counter is not validated and cannot be opened.");
        if (!isReadOnly && !string.Equals(version.Status, "current", StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("Only the current cloud counter can be opened for editing.");

        int counter = version.Counter;
        string downloadUrl = version.DownloadUrl
            ?? $"/api/v1/packages/{Uri.EscapeDataString(package.PackageId)}/versions/{counter}/download";

        string? lockId = null;
        if (!isReadOnly)
        {
            progress?.Report("Acquiring reader session...");
            lockId = await AcquireReaderLockAsync(package.PackageId, versionId, ct).ConfigureAwait(false);
        }
        else
        {
            progress?.Report("Opening read-only cloud counter...");
        }

        string localPath = "";
        try
        {
            string fileName = string.IsNullOrWhiteSpace(version.FileName)
                ? package.DisplayName.Replace(" / ", "_") + ".fa"
                : version.FileName;
            localPath = CreateCloudTempPath(package.PackageId, counter, fileName);
            global::Android.Util.Log.Debug("FA.Cloud", "Cloud download temp path: " + localPath);
            EnsureDownloadSpace(localPath, version.SizeBytes);
            await DownloadFileAsync(downloadUrl, localPath, version.SizeBytes, progress, ct).ConfigureAwait(false);

            progress?.Report("Verifying cloud download...");
            string actualSha = await ComputeSha256Async(localPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(localPath);
                throw new CloudApiException("Downloaded cloud model failed SHA-256 verification.");
            }

            global::Android.Util.Log.Debug("FA.Cloud", "Cloud download verified: " + localPath);
            string displayName = $"{package.DisplayName} - C{counter}" + (isReadOnly ? " (read-only)" : "");
            return new CloudDownloadedModel(
                package,
                localPath,
                lockId,
                versionId,
                counter,
                displayName,
                isReadOnly,
                CreateImportCacheNameToken(package));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(lockId))
                await ReleaseLockAsync(package.PackageId, lockId, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(localPath))
                TryDelete(localPath);
            throw;
        }
    }

    private static string CreateImportCacheNameToken(CloudPackageSummary package)
        => string.IsNullOrWhiteSpace(package.Revision)
            ? package.PartNumber.Trim()
            : package.PartNumber.Trim() + "_" + package.Revision.Trim();

    public async Task<string> AcquireReaderLockAsync(string packageId, string versionId, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(packageId))
            throw new CloudApiException("Cloud package id is missing.");
        if (string.IsNullOrWhiteSpace(versionId))
            throw new CloudApiException("Cloud version id is missing.");

        CloudLockResponse readerLock = await PostAuthorizedJsonAsync<CloudLockResponse>(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId) + "/lock/acquire",
            new
            {
                version_id = versionId,
                mode = "reader",
                client_type = "android",
            },
            ct).ConfigureAwait(false);

        return readerLock.LockId
            ?? throw new CloudApiException("Cloud reader session did not return a lock id.");
    }

    internal async Task<CloudLockResponse> AcquireWriterLockAsync(string packageId, string versionId, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(packageId))
            throw new CloudApiException("Cloud package id is missing.");
        if (string.IsNullOrWhiteSpace(versionId))
            throw new CloudApiException("Cloud version id is missing.");

        CloudLockResponse writerLock = await PostAuthorizedJsonAsync<CloudLockResponse>(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId) + "/lock/acquire",
            new
            {
                version_id = versionId,
                mode = "writer-required",
                client_type = "android",
            },
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(writerLock.LockId))
            throw new CloudApiException("Cloud writer lock did not return a lock id.");
        if (!string.Equals(writerLock.Mode, "writer", StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("Cloud server did not grant a writer lock.");

        return writerLock;
    }

    public async Task<CloudPackageSummary> LoadPackageSummaryAsync(string packageId, CancellationToken ct)
    {
        ThrowIfDisposed();
        CloudPackageDetailDto detail = await GetAuthorizedJsonAsync<CloudPackageDetailDto>(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId),
            ct).ConfigureAwait(false);
        CloudPackageVersionDto version = detail.CurrentVersion
            ?? throw new CloudApiException("Cloud package did not include a current version.");

        return new CloudPackageSummary(
            detail.ProjectId ?? "",
            detail.ProjectId ?? "",
            detail.PackageId,
            detail.PartNumber ?? "Model",
            detail.Revision ?? "",
            "active",
            version.VersionId ?? detail.CurrentVersionId,
            version.Counter == 0 ? detail.CurrentCounter : version.Counter,
            version.Status ?? "current",
            version.ValidationStatus ?? "passed",
            HasPreview: false,
            PreviewUrl: null,
            UpdatedAt: null);
    }

    internal async Task<CloudOperationResponse> UploadWriterSaveAsync(
        string packageId,
        string baseVersionId,
        string lockId,
        string localPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(packageId))
            throw new CloudApiException("Cloud package id is missing.");
        if (string.IsNullOrWhiteSpace(baseVersionId))
            throw new CloudApiException("Cloud base version id is missing.");
        if (string.IsNullOrWhiteSpace(lockId))
            throw new CloudApiException("Cloud writer lock id is missing.");
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            throw new CloudApiException("Saved cloud model file is missing.");

        long length = new FileInfo(localPath).Length;
        CloudUploadResponse upload = length <= SmallWriterSaveUploadMaxBytes
            ? await UploadSmallWriterSaveAsync(packageId, baseVersionId, lockId, localPath, progress, ct).ConfigureAwait(false)
            : await UploadLargeWriterSaveAsync(packageId, baseVersionId, lockId, localPath, length, progress, ct).ConfigureAwait(false);

        return await WaitForUploadOperationAsync(upload, progress, ct).ConfigureAwait(false);
    }

    public Task HeartbeatAsync(string packageId, string lockId, CancellationToken ct)
        => PostAuthorizedJsonDiscardAsync(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId) + "/lock/heartbeat",
            new { lock_id = lockId },
            ct);

    public Task ReleaseLockAsync(string packageId, string lockId, CancellationToken ct)
        => PostAuthorizedJsonDiscardAsync(
            "/api/v1/packages/" + Uri.EscapeDataString(packageId) + "/lock/release",
            new { lock_id = lockId },
            ct);

    public async Task<string> PreserveWriterRecoveryCopyAsync(
        string localPath,
        string packageId,
        string baseVersionId,
        string? lockId,
        string? errorCode,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            throw new FileNotFoundException("Cloud recovery source file not found.", localPath);

        string owner = CloudSecureStore.StableCacheKey(SnapshotRequiredSession().Email);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        string root = Path.Combine(GetCloudRecoveryRoot(), owner, SanitizePathPart(packageId));
        Directory.CreateDirectory(root);
        string safeBaseVersion = SanitizePathPart(baseVersionId);
        string recoveryPath = Path.Combine(root, $"{safeBaseVersion}-{timestamp}.fa");
        File.Copy(localPath, recoveryPath, overwrite: false);

        var manifest = new
        {
            package_id = packageId,
            base_version_id = baseVersionId,
            lock_id = lockId,
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            error_code = errorCode,
            file_name = Path.GetFileName(recoveryPath),
        };
        string manifestPath = Path.ChangeExtension(recoveryPath, ".recovery_manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            Encoding.UTF8,
            ct).ConfigureAwait(false);
        return recoveryPath;
    }

    public void DeleteCloudTempFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            global::Android.Util.Log.Debug("FA.Cloud", "Deleting cloud temp file: " + path);
            TryDelete(path);
            TryDeleteEmptyParents(path, GetCloudOpenCacheRoot());
        }
    }

    public void PurgeCloudCache()
    {
        TryDeleteDirectory(Path.Combine(_context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "fa-cloud-thumbnails"));
        PurgeCloudOpenCache();
    }

    public void PurgeCloudOpenCache()
    {
        TryDeleteDirectory(GetCloudOpenCacheRoot());
    }

    public Uri BuildNotificationHubUri()
    {
        ThrowIfDisposed();
        return BuildUri(SnapshotRequiredSession().ServerUrl, "/hub/notifications");
    }

    public async Task<string?> GetAccessTokenForRealtimeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        CloudAuthSession session = SnapshotRequiredSession();
        if (session.AccessTokenExpiresAt is not null
            && session.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            if (!await TryRefreshAsync(session, ct).ConfigureAwait(false))
                return null;
        }

        return SnapshotRequiredSession().AccessToken;
    }

    public void Dispose()
    {
        _disposed = true;
        _disposeCts.Cancel();
        _activeOperationsIdle.Wait(TimeSpan.FromSeconds(3));
        _http.Dispose();
        _disposeCts.Dispose();
        _activeOperationsIdle.Dispose();
    }

    private async Task DownloadFileAsync(
        string downloadUrl,
        string localPath,
        long? expectedSize,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        ct = linkedCts.Token;
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        string tempPath = localPath + ".part";
        TryDelete(tempPath);

        try
        {
            using HttpResponseMessage response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, BuildUri(SnapshotRequiredSession().ServerUrl, downloadUrl)),
                ct,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);

            long? contentLength = response.Content.Headers.ContentLength ?? expectedSize;
            if (contentLength is null && expectedSize is null)
                progress?.Report("Downloading cloud model...");
            if (expectedSize is null && response.Content.Headers.ContentLength is > MaxUnknownCloudDownloadBytes)
                throw new CloudApiException("Cloud download is larger than the maximum supported size when package metadata omits size.");
            await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

            byte[] buffer = new byte[1024 * 128];
            long total = 0;
            long nextSpaceCheck = DownloadFreeSpaceCheckIntervalBytes;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                if (expectedSize is null && contentLength is null && total > MaxUnknownCloudDownloadBytes)
                    throw new CloudApiException("Cloud download exceeded the maximum supported size without package metadata.");
                if (total >= nextSpaceCheck)
                {
                    EnsureStreamingDownloadSpace(tempPath);
                    nextSpaceCheck += DownloadFreeSpaceCheckIntervalBytes;
                }

                if (contentLength is > 0)
                    progress?.Report($"Downloading {FormatBytes(total)} / {FormatBytes(contentLength.Value)}...");
                else
                    progress?.Report($"Downloading {FormatBytes(total)}...");
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task<CloudUploadResponse> UploadSmallWriterSaveAsync(
        string packageId,
        string baseVersionId,
        string lockId,
        string localPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Uploading saved model...");
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpResponseMessage response = await SendAuthorizedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(SnapshotRequiredSession().ServerUrl, "/api/v1/uploads"));
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

                var multipart = new MultipartFormDataContent();
                var metadata = new
                {
                    source_type = "writer-save",
                    package_id = packageId,
                    base_version_id = baseVersionId,
                    lock_id = lockId,
                };
                multipart.Add(new StringContent(JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8, "application/json"), "metadata");

                FileStream fileStream = File.OpenRead(localPath);
                var file = new StreamContent(fileStream);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(file, "file", Path.GetFileName(localPath));
                request.Content = multipart;
                return request;
            },
            ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<CloudUploadResponse>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new CloudApiException("Cloud upload returned an empty response.");
    }

    private async Task<CloudUploadResponse> UploadLargeWriterSaveAsync(
        string packageId,
        string baseVersionId,
        string lockId,
        string localPath,
        long length,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Preparing large saved model upload...");
        string wholeSha = await ComputeSha256Async(localPath, ct).ConfigureAwait(false);
        CloudUploadInitResponse init = await PostAuthorizedJsonAsync<CloudUploadInitResponse>(
            "/api/v1/uploads/init",
            new
            {
                source_type = "writer-save",
                expected_size_bytes = length,
                expected_sha256 = wholeSha,
                package_id = packageId,
                base_version_id = baseVersionId,
                lock_id = lockId,
            },
            ct).ConfigureAwait(false);

        string uploadId = init.UploadId
            ?? throw new CloudApiException("Cloud upload init did not return an upload id.");

        byte[] buffer = new byte[WriterSaveChunkBytes];
        await using FileStream input = File.OpenRead(localPath);
        int index = 0;
        long uploaded = 0;
        while (uploaded < length)
        {
            int requested = (int)Math.Min(buffer.Length, length - uploaded);
            int read = 0;
            while (read < requested)
            {
                int chunkRead = await input.ReadAsync(buffer.AsMemory(read, requested - read), ct).ConfigureAwait(false);
                if (chunkRead == 0)
                    throw new EndOfStreamException("Saved model ended before the expected upload size.");
                read += chunkRead;
            }

            byte[] chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
            string chunkDigest = Convert.ToBase64String(SHA256.HashData(chunk.AsSpan(0, read)));
            await PutUploadChunkAsync(uploadId, index, chunk, read, chunkDigest, ct).ConfigureAwait(false);
            uploaded += read;
            index++;
            progress?.Report($"Uploading {FormatBytes(uploaded)} / {FormatBytes(length)}...");
        }

        string completeIdempotencyKey = Guid.NewGuid().ToString("D");
        using HttpResponseMessage complete = await SendAuthorizedAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildUri(SnapshotRequiredSession().ServerUrl, "/api/v1/uploads/" + Uri.EscapeDataString(uploadId) + "/complete"));
                request.Headers.TryAddWithoutValidation("Idempotency-Key", completeIdempotencyKey);
                return request;
            },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(complete).ConfigureAwait(false);
        await using Stream stream = await complete.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        CloudUploadResponse? upload = await JsonSerializer.DeserializeAsync<CloudUploadResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
        return upload ?? new CloudUploadResponse(uploadId, init.OperationId, null, null, null, null);
    }

    private async Task PutUploadChunkAsync(
        string uploadId,
        int index,
        byte[] buffer,
        int length,
        string chunkDigest,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAuthorizedAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    BuildUri(
                        SnapshotRequiredSession().ServerUrl,
                        "/api/v1/uploads/" + Uri.EscapeDataString(uploadId) + "/chunks/" + index.ToString(CultureInfo.InvariantCulture)));
                request.Headers.TryAddWithoutValidation("Digest", "sha-256=" + chunkDigest);
                request.Content = new ByteArrayContent(buffer, 0, length);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    private async Task<CloudOperationResponse> WaitForUploadOperationAsync(
        CloudUploadResponse upload,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (IsTerminalOperationStatus(upload.Status))
        {
            return new CloudOperationResponse(
                upload.Status,
                upload.ValidationErrorCode,
                upload.ValidationErrorMessage,
                upload.FinalPackageVersionId);
        }

        string? operationId = upload.OperationId;
        if (string.IsNullOrWhiteSpace(operationId))
            throw new CloudApiException("Cloud upload did not return an operation id.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OperationPollTimeout);
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                progress?.Report("Validating saved model...");
                await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
                CloudOperationResponse operation = await GetAuthorizedJsonAsync<CloudOperationResponse>(
                    "/api/v1/operations/" + Uri.EscapeDataString(operationId),
                    timeoutCts.Token).ConfigureAwait(false);
                if (IsTerminalOperationStatus(operation.Status))
                    return operation;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // S22#2: the only non-user cancellation here is OperationPollTimeout
            // firing on timeoutCts. Surface a clear, actionable error instead of
            // a bare OperationCanceledException, which the caller's guarded catch
            // (when ex is not OperationCanceledException) skips - leaving no
            // recovery copy and a degraded reader session. Genuine user
            // cancellation (ct) still propagates.
            throw new CloudApiException(
                "Cloud validation is taking longer than expected. The save may still complete on the server; refresh before saving again.",
                code: "operation_poll_timeout");
        }
    }

    private static bool IsTerminalOperationStatus(string? status)
        => string.Equals(status, "committed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private async Task<T> GetAuthorizedJsonAsync<T>(string path, CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        using HttpResponseMessage response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUri(SnapshotRequiredSession().ServerUrl, path)),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new CloudApiException("Cloud server returned an empty response.");
    }

    private async Task<T> PostAuthorizedJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpResponseMessage response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, BuildUri(SnapshotRequiredSession().ServerUrl, path), body, idempotencyKey),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new CloudApiException("Cloud server returned an empty response.");
    }

    private async Task PostAuthorizedJsonDiscardAsync(string path, object body, CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpResponseMessage response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, BuildUri(SnapshotRequiredSession().ServerUrl, path), body, idempotencyKey),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    private async Task<T> PostJsonNoAuthAsync<T>(string serverUrl, string path, object body, CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        ct = linkedCts.Token;
        using HttpResponseMessage response = await _http.SendAsync(
            CreateJsonRequest(HttpMethod.Post, BuildUri(serverUrl, path), body, idempotencyKey: null),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new CloudApiException("Cloud server returned an empty response.");
    }

    private async Task<T> PostJsonWithBearerAsync<T>(
        string serverUrl,
        string path,
        string accessToken,
        object? body,
        CancellationToken ct)
    {
        using OperationLease operation = BeginOperation();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new CloudApiException("Cloud TOTP enrollment token is missing.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        ct = linkedCts.Token;
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            BuildUri(serverUrl, path),
            body,
            idempotencyKey: null);
        ValidateAuthorizedRequestUri(new CloudAuthSession(serverUrl, "", accessToken, null, null, null, null), request.RequestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new CloudApiException("Cloud server returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        ct = linkedCts.Token;
        int transientAttempts = 0;
        int authAttempts = 0;
        while (true)
        {
            CloudAuthSession session = SnapshotRequiredSession();
            HttpRequestMessage request = requestFactory();
            // S22#4: dispose the request (and its content - e.g. an upload
            // FileStream) on every exit path via finally, including non-transient
            // send failures that bypass the retry catch below. Ownership of the
            // returned response passes to the caller; disposing the request after
            // SendAsync returns does not affect the response stream.
            try
            {
                ValidateAuthorizedRequestUri(session, request.RequestUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

                HttpResponseMessage response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    if (authAttempts++ >= 1
                        || !await TryRefreshAsync(session, ct).ConfigureAwait(false))
                    {
                        ClearInMemorySession(clearRefreshToken: true);
                        throw new CloudApiException("Cloud session rejected. Sign in again.", 401);
                    }

                    continue;
                }

                if (ShouldRetryResponse(response)
                    && CanRetryRequest(request)
                    && transientAttempts < MaxTransientRetries)
                {
                    transientAttempts++;
                    TimeSpan delay = GetRetryDelay(transientAttempts, response);
                    response.Dispose();
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex, ct)
                                       && CanRetryRequest(request)
                                       && transientAttempts < MaxTransientRetries)
            {
                transientAttempts++;
                await Task.Delay(GetRetryDelay(transientAttempts, response: null), ct).ConfigureAwait(false);
            }
            finally
            {
                request.Dispose();
            }
        }
    }

    private static void ValidateAuthorizedRequestUri(CloudAuthSession session, Uri? requestUri)
    {
        if (requestUri is null)
            throw new CloudApiException("Cloud request URI is missing.");
        if (!Uri.TryCreate(session.ServerUrl, UriKind.Absolute, out Uri? serverUri))
            throw new CloudApiException("Cloud server URL is invalid.");
        if (!string.Equals(requestUri.Scheme, serverUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestUri.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase)
            || requestUri.Port != serverUri.Port)
        {
            throw new CloudApiException("Cloud response referenced an external URL. Refusing to attach the bearer token.");
        }
    }

    private void ClearInMemorySession(bool clearRefreshToken)
    {
        lock (_sync)
            _session = null;
        if (clearRefreshToken)
            _secureStore.ClearRefreshToken();
        RaiseSessionChanged();
    }

    private async Task<bool> TryRefreshAsync(CloudAuthSession staleSession, CancellationToken ct)
    {
        Task<bool> task;
        lock (_refreshSync)
        {
            if (_refreshTask is null || _refreshTask.IsCompleted)
                _refreshTask = RefreshCoreAsync(staleSession);
            task = _refreshTask;
        }

        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                lock (_refreshSync)
                {
                    if (ReferenceEquals(_refreshTask, task))
                        _refreshTask = null;
                }
            }
        }
    }

    private async Task<bool> RefreshCoreAsync(CloudAuthSession staleSession)
    {
        string? refreshToken = staleSession.RefreshToken ?? _secureStore.LoadRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        try
        {
            // S22#3: previously passed CancellationToken.None, so with no
            // per-request bound a half-dead server could hang the refresh
            // forever (the only escape was destroying the activity). Bound it
            // with a linked CancelAfter that is also canceled on client dispose.
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            refreshCts.CancelAfter(ControlPlaneTimeout);
            CloudRefreshResult result = await PostJsonNoAuthAsync<CloudRefreshResult>(
                staleSession.ServerUrl,
                "/auth/refresh",
                new { refresh_token = refreshToken },
                refreshCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.AccessToken))
                return false;

            var refreshed = staleSession with
            {
                AccessToken = result.AccessToken,
                AccessTokenExpiresAt = result.AccessTokenExpiresAt,
                RefreshToken = string.IsNullOrWhiteSpace(result.RefreshToken) ? refreshToken : result.RefreshToken,
                RefreshTokenExpiresAt = result.RefreshTokenExpiresAt ?? staleSession.RefreshTokenExpiresAt,
            };

            lock (_sync)
                _session = refreshed;

            if (AppSettings.CloudRememberCredentials)
                _secureStore.SaveRefreshToken(refreshed.RefreshToken, refreshed.RefreshTokenExpiresAt);
            RaiseSessionChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Bounded control-plane timeout (S22#3) or client dispose. Treat as
            // a failed refresh rather than surfacing a raw cancellation.
            global::Android.Util.Log.Warn("FA.Cloud", "Cloud token refresh timed out or was canceled.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Cloud token refresh failed: " + ex.GetBaseException().Message);
            if (IsAuthenticationRejection(ex))
                ClearInMemorySession(clearRefreshToken: true);
            return false;
        }
    }

    private static bool IsAuthenticationRejection(Exception ex)
    {
        CloudApiException? cloud = ex as CloudApiException ?? ex.InnerException as CloudApiException;
        if (cloud is null)
            return false;

        if (cloud.StatusCode is 401 or 403)
            return true;

        return !string.IsNullOrWhiteSpace(cloud.Code)
               && cloud.Code.StartsWith("auth_", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object? body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            string json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        string title = response.ReasonPhrase ?? "Cloud request failed";
        string? code = null;
        try
        {
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                CloudProblemDetails? problem = JsonSerializer.Deserialize<CloudProblemDetails>(text, JsonOptions);
                title = problem?.Title ?? problem?.Detail ?? title;
                code = problem?.Code;
            }
        }
        catch
        {
            // Problem bodies are best-effort; keep the HTTP status reason.
        }

        throw new CloudApiException(title, (int)response.StatusCode, code);
    }

    private static bool ShouldRetryResponse(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.RequestTimeout
           || response.StatusCode == HttpStatusCode.TooManyRequests
           || (int)response.StatusCode >= 500;

    private static bool CanRetryRequest(HttpRequestMessage request)
        => request.Method == HttpMethod.Get
           || request.Method == HttpMethod.Head
           || request.Method == HttpMethod.Put
           || request.Method == HttpMethod.Delete
           || request.Headers.Contains("Idempotency-Key");

    private static bool IsTransientNetworkError(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
           && (ex is HttpRequestException
               || ex is IOException
               || ex is TaskCanceledException);

    private static TimeSpan GetRetryDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta
            && delta > TimeSpan.Zero
            && delta <= TimeSpan.FromSeconds(5))
        {
            return delta;
        }

        int clamped = Math.Clamp(attempt, 1, MaxTransientRetries);
        int exponentialMs = 250 * (1 << (clamped - 1));
        int jitterMs = Random.Shared.Next(30, 140);
        return TimeSpan.FromMilliseconds(exponentialMs + jitterMs);
    }

    private static void EnsureDownloadSpace(string localPath, long? expectedSize)
    {
        string directory = Path.GetDirectoryName(localPath) ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        long required = expectedSize is > 0
            ? expectedSize.Value + Math.Max(expectedSize.Value / 10, 1L)
            : MinimumStreamingDownloadFreeBytes;
        try
        {
            var stat = new global::Android.OS.StatFs(directory);
            long available = stat.AvailableBytes;
            if (available < required)
            {
                throw new CloudApiException(
                    $"Not enough storage for this cloud model. Need {FormatBytes(required)}, available {FormatBytes(available)}.");
            }
        }
        catch (CloudApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Could not check cloud download storage: " + ex.Message);
        }
    }

    private static void EnsureStreamingDownloadSpace(string tempPath)
    {
        string directory = Path.GetDirectoryName(tempPath) ?? Path.GetTempPath();
        try
        {
            var stat = new global::Android.OS.StatFs(directory);
            long available = stat.AvailableBytes;
            if (available < MinimumStreamingDownloadFreeBytes)
            {
                throw new CloudApiException(
                    $"Not enough storage to continue this cloud download. Available {FormatBytes(available)}.");
            }
        }
        catch (CloudApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Could not recheck cloud download storage: " + ex.Message);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CreateCloudTempPath(string packageId, int counter, string fileName)
    {
        string root = Path.Combine(GetCloudOpenCacheRoot(), packageId);
        Directory.CreateDirectory(root);
        string safeName = ImportCacheFileName.Create($"cloud-{packageId}-c{counter}-{fileName}");
        return Path.Combine(root, safeName);
    }

    private string GetCloudOpenCacheRoot()
        => Path.Combine(_context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "fa-cloud-open");

    private string GetCloudRecoveryRoot()
        => Path.Combine(_context.FilesDir?.AbsolutePath ?? _context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "fa-cloud-recovery");

    private string GetPreviewCachePath(string packageId)
    {
        string owner = CloudSecureStore.StableCacheKey(SnapshotRequiredSession().Email);
        return Path.Combine(_context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "fa-cloud-thumbnails", owner, packageId + ".png");
    }

    private void PruneThumbnailCache()
    {
        string root = Path.Combine(_context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "fa-cloud-thumbnails");
        try
        {
            if (!Directory.Exists(root))
                return;

            FileInfo[] files = new DirectoryInfo(root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            long totalBytes = 0;
            int kept = 0;
            foreach (FileInfo file in files)
            {
                totalBytes += file.Length;
                kept++;
                if (kept <= ThumbnailCacheMaxEntries && totalBytes <= ThumbnailCacheMaxBytes)
                    continue;

                TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Failed to prune cloud thumbnail cache: " + ex.Message);
        }
    }

    private static void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Failed to touch cloud cache file: " + ex.Message);
        }
    }

    private CloudAuthSession SnapshotRequiredSession()
        => SnapshotSession() ?? throw new CloudApiException("Sign in to FA Cloud first.");

    private CloudAuthSession? SnapshotSession()
    {
        lock (_sync)
            return _session;
    }

    private void RaiseSessionChanged()
    {
        try { SessionChanged?.Invoke(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("FA.Cloud", "Cloud session listener failed: " + ex.Message); }
    }

    private OperationLease BeginOperation()
    {
        ThrowIfDisposed();
        _activeOperationsIdle.Reset();
        Interlocked.Increment(ref _activeOperations);
        return new OperationLease(this);
    }

    private void EndOperation()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0)
            _activeOperationsIdle.Set();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public static string NormalizeServerUrl(string serverUrl)
    {
        string value = CloudServerUrls.NormalizeConfiguredUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(value))
            throw new CloudApiException(
                $"FA Cloud {AppSettings.CloudServerProfileDisplayName} server is not configured. Set {AppSettings.CloudServerSelectedUrlKey} in {AppSettings.CloudServerConfigPath}.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CloudApiException("Cloud server must be an http or https URL.");
        }

        return value;
    }

    private static Uri BuildUri(string serverUrl, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        return new Uri(new Uri(serverUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Failed to delete cloud temp file: " + ex.Message);
        }
    }

    private static string SanitizePathPart(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        return trimmed.Replace('/', '_').Replace('\\', '_');
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Failed to purge cloud cache: " + ex.Message);
        }
    }

    private static void TryDeleteEmptyParents(string path, string stopRoot)
    {
        try
        {
            string fullStopRoot = Path.GetFullPath(stopRoot);
            DirectoryInfo? directory = Directory.GetParent(Path.GetFullPath(path));
            while (directory is not null
                   && !string.Equals(directory.FullName, fullStopRoot, StringComparison.OrdinalIgnoreCase)
                   && directory.FullName.StartsWith(fullStopRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (directory.EnumerateFileSystemInfos().Any())
                    break;

                DirectoryInfo? parent = directory.Parent;
                directory.Delete();
                directory = parent;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Failed to remove empty cloud temp folder: " + ex.Message);
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private CloudApiClient? _owner;

        public OperationLease(CloudApiClient owner)
            => _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndOperation();
    }
}
