using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace FabricationAssistant.App.Android;

public sealed class CloudSecureStore
{
    private const string FileName = "fa_cloud_secure";
    private const string KeyAlias = "fa_cloud_credentials_v1";
    private const string RememberedPasswordKey = "remembered_password";
    private const string RefreshTokenKey = "refresh_token";
    private const string RefreshTokenExpiryKey = "refresh_token_expires_at";

    private readonly ISharedPreferences _prefs;

    public CloudSecureStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _prefs = context.GetSharedPreferences(FileName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Context.GetSharedPreferences returned null.");
    }

    public string? LoadRememberedPassword(string serverUrl, string email)
    {
        string? payload = LoadEncryptedString(RememberedPasswordKey);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string storedServerUrl = ReadRequiredJsonString(root, "server_url");
            string storedEmail = ReadRequiredJsonString(root, "email");
            string password = ReadRequiredJsonString(root, "password");
            if (string.IsNullOrEmpty(password))
                throw new StoredCredentialInvalidException("Stored cloud password is empty.");

            return string.Equals(NormalizeCredentialServerKey(storedServerUrl), NormalizeCredentialServerKey(serverUrl), StringComparison.OrdinalIgnoreCase)
                   && string.Equals(storedEmail.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)
                ? password
                : null;
        }
        catch (Exception ex) when (ex is JsonException or StoredCredentialInvalidException)
        {
            LogDecryptFailure(ex);
            ClearRememberedPassword();
            return null;
        }
    }

    public void SaveRememberedPassword(string serverUrl, string email, string password)
    {
        string normalizedServerUrl = NormalizeCredentialServerKey(serverUrl);
        string trimmedEmail = email.Trim();
        if (string.IsNullOrWhiteSpace(normalizedServerUrl)
            || string.IsNullOrWhiteSpace(trimmedEmail)
            || string.IsNullOrEmpty(password))
        {
            ClearRememberedPassword();
            return;
        }

        string payload = JsonSerializer.Serialize(new
        {
            server_url = normalizedServerUrl,
            email = trimmedEmail,
            password,
        });
        _prefs.Edit()!
            .PutString(RememberedPasswordKey, EncryptString(payload))!
            .Apply();
    }

    public string? LoadRefreshToken()
        => LoadEncryptedString(RefreshTokenKey);

    public DateTimeOffset? LoadRefreshTokenExpiresAt()
    {
        string? value = _prefs.GetString(RefreshTokenExpiryKey, null);
        return DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    public void SaveRefreshToken(string? refreshToken, DateTimeOffset? expiresAt)
    {
        var editor = _prefs.Edit()!;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            editor.Remove(RefreshTokenKey);
            editor.Remove(RefreshTokenExpiryKey);
        }
        else
        {
            editor.PutString(RefreshTokenKey, EncryptString(refreshToken));
            editor.PutString(RefreshTokenExpiryKey, expiresAt?.ToString("O"));
        }

        editor.Apply();
    }

    public void Clear()
    {
        _prefs.Edit()!.Clear()!.Apply();
    }

    public void ClearRememberedPassword()
    {
        _prefs.Edit()!.Remove(RememberedPasswordKey)!.Apply();
    }

    public void ClearRefreshToken()
    {
        _prefs.Edit()!
            .Remove(RefreshTokenKey)!
            .Remove(RefreshTokenExpiryKey)!
            .Apply();
    }

    public static string StableCacheKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeCredentialServerKey(string serverUrl)
        => serverUrl.Trim().TrimEnd('/');

    private static string ReadRequiredJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new StoredCredentialInvalidException("Stored cloud password envelope is missing " + propertyName + ".");
        }

        return property.GetString() ?? "";
    }

    private string? LoadEncryptedString(string key)
    {
        string? encoded = _prefs.GetString(key, null);
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        try
        {
            return DecryptString(encoded);
        }
        catch (StoredCredentialInvalidException ex)
        {
            LogDecryptFailure(ex);
            _prefs.Edit()!.Remove(key)!.Apply();
            return null;
        }
        catch (KeyPermanentlyInvalidatedException ex)
        {
            LogDecryptFailure(ex);
            TryDeleteKey();
            _prefs.Edit()!.Remove(key)!.Apply();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogDecryptFailure(ex);
            return null;
        }
    }

    private static string DecryptString(string encoded)
    {
        string[] parts = encoded.Split(':');
        if (parts.Length != 2)
            throw new StoredCredentialInvalidException("Stored cloud credential has an invalid envelope.");

        byte[] iv;
        byte[] ciphertext;
        try
        {
            iv = Convert.FromBase64String(parts[0]);
            ciphertext = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new StoredCredentialInvalidException("Stored cloud credential is not valid base64.", ex);
        }

        try
        {
            using Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding")
                ?? throw new InvalidOperationException("AES/GCM cipher unavailable.");
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
            byte[] plain = cipher.DoFinal(ciphertext)
                ?? throw new InvalidOperationException("AES/GCM decrypt returned null.");
            return Encoding.UTF8.GetString(plain);
        }
        catch (AEADBadTagException ex)
        {
            throw new StoredCredentialInvalidException("Stored cloud credential authentication failed.", ex);
        }
    }

    private static void LogDecryptFailure(Exception ex)
        => global::Android.Util.Log.Warn("FA.Cloud", "Could not decrypt cloud credential: " + ex.GetType().FullName + ": " + ex.Message);

    private string EncryptString(string value)
    {
        using Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new InvalidOperationException("AES/GCM cipher unavailable.");
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
        byte[] ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(value))
            ?? throw new InvalidOperationException("AES/GCM encrypt returned null.");
        byte[] iv = cipher.GetIV()
            ?? throw new InvalidOperationException("AES/GCM encrypt did not produce an IV.");
        return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(ciphertext);
    }

    private static IKey GetOrCreateKey()
    {
        KeyStore keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new InvalidOperationException("AndroidKeyStore unavailable.");
        keyStore.Load(null);

        IKey? existing = keyStore.GetKey(KeyAlias, null);
        if (existing is not null)
            return existing;

        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
            ?? throw new InvalidOperationException("AndroidKeyStore AES generator unavailable.");
        var spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(spec);
        return generator.GenerateKey()
            ?? throw new InvalidOperationException("AndroidKeyStore did not create an AES key.");
    }

    private static void TryDeleteKey()
    {
        try
        {
            KeyStore keyStore = KeyStore.GetInstance("AndroidKeyStore")
                ?? throw new InvalidOperationException("AndroidKeyStore unavailable.");
            keyStore.Load(null);
            keyStore.DeleteEntry(KeyAlias);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Could not delete invalidated cloud credential key: " + ex.GetType().FullName + ": " + ex.Message);
        }
    }

    private sealed class StoredCredentialInvalidException : Exception
    {
        public StoredCredentialInvalidException(string message)
            : base(message)
        {
        }

        public StoredCredentialInvalidException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
