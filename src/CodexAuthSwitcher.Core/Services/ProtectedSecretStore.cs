using System.Security.Cryptography;
using System.Text;

namespace CodexAuthSwitcher.Core.Services;

public sealed class ProtectedSecretStore
{
    public void SaveSecret(string path, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Secret cannot be empty.");
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, protectedBytes);
    }

    public string LoadSecret(string path)
    {
        var protectedBytes = File.ReadAllBytes(path);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
