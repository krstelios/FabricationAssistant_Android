namespace FabricationAssistant.App.Android;

internal enum CloudPasswordResetFailureKind
{
    Other,
    InvalidCode,
    ExpiredChallenge,
    TotpUnavailable,
    RateLimitedOrLocked,
    PasswordPolicy,
}

internal static class CloudPasswordResetFlow
{
    public const string TotpUnavailableMessage = "Password reset is unavailable. Ask an administrator to reset your password.";
    public const string ExpiredChallengeMessage = "Password reset challenge expired. Start the reset again.";

    public static void ValidateNewPasswordStep(string email, string newPassword, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new CloudApiException("Enter your cloud email.");
        if (string.IsNullOrEmpty(newPassword))
            throw new CloudApiException("Enter a new password.", code: "password_required");
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            throw new CloudApiException("New passwords do not match.", code: "password_mismatch");
    }

    public static string RequireChallengeId(CloudPasswordResetStartResult result)
    {
        string challengeId = result.ChallengeId?.Trim() ?? "";
        if (challengeId.Length == 0)
            throw new CloudApiException(
                FirstNonEmpty(result.Message, result.Detail, result.Title, result.Error) ?? TotpUnavailableMessage,
                code: "totp_unavailable");

        return challengeId;
    }

    public static string NormalizeAuthenticatorOrBackupCode(string value)
    {
        string code = (value ?? "").Trim();
        if (code.Length == 0)
            throw new CloudApiException("Enter the authenticator or backup code.", code: "code_required");

        string withoutWhitespace = new(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (withoutWhitespace.All(char.IsDigit) && withoutWhitespace.Length == 6)
            return withoutWhitespace;

        return code;
    }

    public static CloudPasswordResetFailureKind ClassifyFailure(Exception ex)
    {
        CloudApiException? cloud = ex as CloudApiException ?? ex.InnerException as CloudApiException;
        if (cloud is null)
            return CloudPasswordResetFailureKind.Other;

        string code = cloud.Code ?? "";
        string message = cloud.Message ?? "";
        string text = (code + " " + message).ToLowerInvariant();

        if (cloud.StatusCode == 429
            || cloud.StatusCode == 423
            || text.Contains("rate")
            || text.Contains("locked")
            || text.Contains("too many"))
        {
            return CloudPasswordResetFailureKind.RateLimitedOrLocked;
        }

        if (cloud.StatusCode == 410
            || text.Contains("expired")
            || text.Contains("missing challenge")
            || text.Contains("invalid challenge"))
        {
            return CloudPasswordResetFailureKind.ExpiredChallenge;
        }

        if ((text.Contains("totp") || text.Contains("mfa") || text.Contains("authenticator"))
            && (text.Contains("not enrolled")
                || text.Contains("not_enrolled")
                || text.Contains("unavailable")
                || text.Contains("disabled")))
        {
            return CloudPasswordResetFailureKind.TotpUnavailable;
        }

        if (text.Contains("password")
            && (cloud.StatusCode == 400
                || cloud.StatusCode == 422
                || text.Contains("policy")
                || text.Contains("complex")
                || text.Contains("length")))
        {
            return CloudPasswordResetFailureKind.PasswordPolicy;
        }

        if (text.Contains("invalid")
            && (text.Contains("code")
                || text.Contains("totp")
                || text.Contains("backup")
                || text.Contains("authenticator")))
        {
            return CloudPasswordResetFailureKind.InvalidCode;
        }

        return CloudPasswordResetFailureKind.Other;
    }

    public static string UserMessageForFailure(Exception ex)
    {
        return ClassifyFailure(ex) switch
        {
            CloudPasswordResetFailureKind.InvalidCode => string.IsNullOrWhiteSpace(ex.GetBaseException().Message)
                ? "Invalid authenticator or backup code."
                : ex.GetBaseException().Message,
            CloudPasswordResetFailureKind.ExpiredChallenge => ExpiredChallengeMessage,
            CloudPasswordResetFailureKind.TotpUnavailable => TotpUnavailableMessage,
            _ => ex.GetBaseException().Message,
        };
    }

    public static async Task CompleteSuccessfulResetAsync(
        Func<CancellationToken, Task> verifyAsync,
        Action clearLocalTokens,
        Action logCompleted,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verifyAsync);
        ArgumentNullException.ThrowIfNull(clearLocalTokens);
        ArgumentNullException.ThrowIfNull(logCompleted);

        await verifyAsync(ct).ConfigureAwait(false);
        clearLocalTokens();
        logCompleted();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
