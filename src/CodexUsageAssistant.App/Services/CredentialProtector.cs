using System.Security.Cryptography;
using System.Text;

namespace CodexUsageAssistant.Services;

public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexUsageAssistant.Proxy.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var clearBytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(clearBytes);
        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(encryptedValue);
            var clearBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clearBytes); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
