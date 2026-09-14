using CodexAuthSwitcher.Core.Services;
using CodexAuthSwitcher.Core.Models;
var root = Path.GetFullPath(args[0]);
if (!Path.GetFileName(root).StartsWith("switcher-smoke-", StringComparison.Ordinal))
    throw new InvalidOperationException("Use an isolated switcher-smoke-* directory.");
var runtime = new FakeRuntime();
var inspector = new LiveAuthInspector(runtime);
var service = new CodexAuthSwitcherService(new ProfileStore(root, new ProtectedSecretStore(), inspector), inspector, runtime);
var baseUrl = Environment.GetEnvironmentVariable("SWITCHER_SMOKE_BASE_URL") ?? "http://127.0.0.1:1/v1";
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
    throw new InvalidOperationException("Smoke tests require a loopback endpoint.");
service.SaveApiProfile(new ApiProfileSpec { Name = "smoke", Provider = args[1], BaseUrl = baseUrl, ApiKey = "isolated-fake-key" });
var result = service.SwitchProfile("smoke");
Console.WriteLine($"provider={service.GetEnvironment().ModelProvider} synchronized={result.SynchronizedThreads}");
sealed class FakeRuntime : ICodexRuntimeEnvironmentService
{
    public string? GetOpenAiBaseUrl() => null;
    public void ApplyForProfile(ApiProfileSpec? spec) { }
}
