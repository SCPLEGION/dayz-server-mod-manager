using System;
using System.Security.Cryptography;
using System.Text;

namespace DayZModManager.Services;

/// <summary>DPAPI-based protection (user scope) for the OpenAI API key. Windows-only.</summary>
public static class ApiKeyProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DayZModManager.AiBalancer.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return plaintext; // fallback (insecure) on non-Windows
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return protectedBase64;
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }
}
