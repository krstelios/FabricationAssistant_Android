using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabricationAssistant.App.Android;

public sealed record CloudProject(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name);

public sealed record CloudPackageSummary(
    string ProjectId,
    string ProjectName,
    string PackageId,
    string PartNumber,
    string Revision,
    string Status,
    string? CurrentVersionId,
    int CurrentCounter,
    string? CurrentVersionStatus,
    string? CurrentVersionValidationStatus,
    bool HasPreview,
    string? PreviewUrl,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>Human-readable part name (DB_PART_NAME attribute), when the server provides it.</summary>
    public string? PartName { get; init; }

    /// <summary>Revision name (Rev_Name attribute), when the server provides it.</summary>
    public string? RevName { get; init; }

    /// <summary>Display name of the user who uploaded the current version, when available.</summary>
    public string? UploadedBy { get; init; }

    public bool IsReadyToOpen =>
        string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase)
        && string.Equals(CurrentVersionStatus, "current", StringComparison.OrdinalIgnoreCase)
        && string.Equals(CurrentVersionValidationStatus, "passed", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CurrentVersionId);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Revision)
            ? PartNumber
            : $"{PartNumber} / {Revision}";

    public string DetailLine =>
        $"V{CurrentCounter} - {ProjectName}";
}

public sealed record CloudBrowserSnapshot(
    IReadOnlyList<CloudProject> Projects,
    IReadOnlyList<CloudPackageSummary> Packages);

public sealed record CloudDownloadedModel(
    CloudPackageSummary Package,
    string LocalPath,
    string? LockId,
    string VersionId,
    int Counter,
    string DisplayName,
    bool IsReadOnly,
    string ImportCacheNameToken);

public sealed record CloudOpenSession(
    string PackageId,
    string ProjectId,
    string VersionId,
    string? LockId,
    string LocalPath,
    string DisplayName,
    int Counter,
    bool IsReadOnly,
    string ImportCacheNameToken,
    DateTimeOffset? LastHeartbeatUtc);

public sealed record CloudPackageVersionSummary(
    int Counter,
    string? VersionId,
    string? Status,
    string? ValidationStatus,
    DateTimeOffset? UploadedAt)
{
    public bool IsCurrent => string.Equals(Status, "current", StringComparison.OrdinalIgnoreCase);

    public bool IsReadyToOpen => string.Equals(ValidationStatus, "passed", StringComparison.OrdinalIgnoreCase);
}

internal sealed record CloudNotificationEnvelope(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("scope")] CloudNotificationScope? Scope);

internal sealed record CloudNotificationScope(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("counter")] int? Counter);

internal sealed record CloudSignInResult(
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("access_token_expires_at")] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_at")] DateTimeOffset? RefreshTokenExpiresAt,
    [property: JsonPropertyName("session_family_id")] string? SessionFamilyId,
    [property: JsonPropertyName("requires_totp")] bool RequiresTotp,
    [property: JsonPropertyName("requires_totp_enrollment")] bool RequiresTotpEnrollment,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("challenge_id")] string? ChallengeId);

public enum CloudSignInOutcomeKind
{
    SignedIn,
    TotpRequired,
    TotpEnrollmentRequired,
}

public sealed record CloudSignInOutcome(
    CloudSignInOutcomeKind Kind,
    string ServerUrl,
    string Email,
    bool RememberCredentials,
    string? TemporaryAccessToken,
    DateTimeOffset? TemporaryAccessTokenExpiresAt,
    string? ChallengeId);

public sealed record CloudTotpEnrollmentStartResult(
    [property: JsonPropertyName("otpauth_uri")] string? OtpAuthUri,
    [property: JsonPropertyName("raw_secret")] string? RawSecret);

public sealed record CloudTotpEnrollmentVerifyResult(
    [property: JsonPropertyName("backup_codes")] IReadOnlyList<string>? BackupCodes);

internal sealed record CloudPasswordResetStartResult(
    [property: JsonPropertyName("challenge_id")] string? ChallengeId,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record CloudPasswordResetChallenge(
    string ServerUrl,
    string Email,
    string ChallengeId);

internal sealed record CloudPasswordResetVerifyResult(
    [property: JsonPropertyName("status")] string? Status);

internal sealed record CloudRefreshResult(
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("access_token_expires_at")] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_at")] DateTimeOffset? RefreshTokenExpiresAt);

internal sealed record CloudProjectsResponse(
    [property: JsonPropertyName("projects")] IReadOnlyList<CloudProject>? Projects);

internal sealed record CloudPackagesResponse(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("packages")] IReadOnlyList<CloudPackageDto>? Packages);

internal sealed record CloudPackageDto(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("part_number")] string? PartNumber,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("current_version_id")] string? CurrentVersionId,
    [property: JsonPropertyName("current_counter")] int CurrentCounter,
    [property: JsonPropertyName("current_version_status")] string? CurrentVersionStatus,
    [property: JsonPropertyName("current_version_validation_status")] string? CurrentVersionValidationStatus,
    [property: JsonPropertyName("has_preview")] bool HasPreview,
    [property: JsonPropertyName("preview_url")] string? PreviewUrl,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
{
    // Captures any extra fields the server returns (e.g. part name / uploader) so the
    // client can read them without the exact key being baked into the positional record.
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}

internal sealed record CloudPackageDetailDto(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("part_number")] string? PartNumber,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("current_version_id")] string? CurrentVersionId,
    [property: JsonPropertyName("current_counter")] int CurrentCounter,
    [property: JsonPropertyName("current_version")] CloudPackageVersionDto? CurrentVersion);

internal sealed record CloudPackageVersionDto(
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("counter")] int Counter,
    [property: JsonPropertyName("filename")] string? FileName,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("validation_status")] string? ValidationStatus,
    [property: JsonPropertyName("download_url")] string? DownloadUrl,
    [property: JsonPropertyName("uploaded_at")] DateTimeOffset? UploadedAt);

internal sealed record CloudPackageVersionsResponse(
    [property: JsonPropertyName("package_id")] string? PackageId,
    [property: JsonPropertyName("versions")] IReadOnlyList<CloudPackageVersionDto>? Versions);

internal sealed record CloudLockResponse(
    [property: JsonPropertyName("lock_id")] string? LockId,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("base_version_id")] string? BaseVersionId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

internal sealed record CloudUploadResponse(
    [property: JsonPropertyName("upload_id")] string? UploadId,
    [property: JsonPropertyName("operation_id")] string? OperationId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("validation_error_code")] string? ValidationErrorCode,
    [property: JsonPropertyName("validation_error_message")] string? ValidationErrorMessage,
    [property: JsonPropertyName("final_package_version_id")] string? FinalPackageVersionId);

internal sealed record CloudUploadInitResponse(
    [property: JsonPropertyName("upload_id")] string? UploadId,
    [property: JsonPropertyName("operation_id")] string? OperationId);

internal sealed record CloudOperationResponse(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("validation_error_code")] string? ValidationErrorCode,
    [property: JsonPropertyName("validation_error_message")] string? ValidationErrorMessage,
    [property: JsonPropertyName("final_package_version_id")] string? FinalPackageVersionId);

internal sealed record CloudProblemDetails(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("code")] string? Code);

internal sealed record CloudAuthSession(
    string ServerUrl,
    string Email,
    string AccessToken,
    DateTimeOffset? AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt,
    string? SessionFamilyId);

public sealed class CloudApiException : Exception
{
    public CloudApiException(string message, int? statusCode = null, string? code = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int? StatusCode { get; }
    public string? Code { get; }
}
