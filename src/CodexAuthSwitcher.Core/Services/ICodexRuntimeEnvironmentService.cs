using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.Core.Services;

public interface ICodexRuntimeEnvironmentService
{
    string? GetOpenAiBaseUrl();

    void ApplyForProfile(ApiProfileSpec? spec);
}
