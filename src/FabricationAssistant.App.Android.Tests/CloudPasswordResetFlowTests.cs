using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CloudPasswordResetFlowTests
{
    [Fact]
    public async Task CompleteSuccessfulResetAsync_runs_verify_and_clears_local_tokens()
    {
        bool verified = false;
        bool cleared = false;
        bool logged = false;

        await CloudPasswordResetFlow.CompleteSuccessfulResetAsync(
            _ =>
            {
                verified = true;
                return Task.CompletedTask;
            },
            () => cleared = true,
            () => logged = true,
            CancellationToken.None);

        Assert.True(verified);
        Assert.True(cleared);
        Assert.True(logged);
    }

    [Fact]
    public void ClassifyFailure_maps_invalid_code()
    {
        var ex = new CloudApiException("Invalid authenticator code.", 400, "invalid_totp_code");

        Assert.Equal(CloudPasswordResetFailureKind.InvalidCode, CloudPasswordResetFlow.ClassifyFailure(ex));
    }

    [Fact]
    public void ValidateNewPasswordStep_rejects_password_mismatch_client_side()
    {
        CloudApiException ex = Assert.Throws<CloudApiException>(() =>
            CloudPasswordResetFlow.ValidateNewPasswordStep("user@example.com", "new-password", "different-password"));

        Assert.Equal("password_mismatch", ex.Code);
        Assert.Equal("New passwords do not match.", ex.Message);
    }

    [Fact]
    public void Missing_challenge_is_unavailable_and_expired_challenge_returns_to_start()
    {
        CloudApiException missing = Assert.Throws<CloudApiException>(() =>
            CloudPasswordResetFlow.RequireChallengeId(new CloudPasswordResetStartResult(null, null)));
        var expired = new CloudApiException("Challenge expired.", 410, "password_reset_challenge_expired");

        Assert.Equal("totp_unavailable", missing.Code);
        Assert.Equal(CloudPasswordResetFlow.TotpUnavailableMessage, missing.Message);
        Assert.Equal(CloudPasswordResetFailureKind.ExpiredChallenge, CloudPasswordResetFlow.ClassifyFailure(expired));
        Assert.Equal(CloudPasswordResetFlow.ExpiredChallengeMessage, CloudPasswordResetFlow.UserMessageForFailure(expired));
    }

    [Fact]
    public void ClassifyFailure_maps_no_totp_enrolled_to_admin_message()
    {
        var ex = new CloudApiException("TOTP is not enrolled.", 409, "totp_not_enrolled");

        Assert.Equal(CloudPasswordResetFailureKind.TotpUnavailable, CloudPasswordResetFlow.ClassifyFailure(ex));
        Assert.Equal(CloudPasswordResetFlow.TotpUnavailableMessage, CloudPasswordResetFlow.UserMessageForFailure(ex));
    }

    [Fact]
    public void ClassifyFailure_maps_password_policy_failure()
    {
        var ex = new CloudApiException("Password does not meet policy.", 422, "password_policy_failed");

        Assert.Equal(CloudPasswordResetFailureKind.PasswordPolicy, CloudPasswordResetFlow.ClassifyFailure(ex));
    }
}
