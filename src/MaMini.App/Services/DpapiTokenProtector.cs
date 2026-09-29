using System.Security.Cryptography;
using System.Text;
using MaMini.Core.Settings;

namespace MaMini.App.Services;

/// <summary>Encrypts the access token with DPAPI for the current Windows user.</summary>
internal sealed class DpapiTokenProtector : ITokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MaMini.AccessToken.v1");

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
}
