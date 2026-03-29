using System.Text;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class ProtectedSecretStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "CodexAuthSwitcherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveSecret_DoesNotStorePlaintextAndCanRoundTrip()
    {
        var path = Path.Combine(_tempRoot, "secret.bin");
        var store = new ProtectedSecretStore();
        store.SaveSecret(path, "sk-super-secret-123456");

        var rawBytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("sk-super-secret-123456", Encoding.UTF8.GetString(rawBytes));
        Assert.Equal("sk-super-secret-123456", store.LoadSecret(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
