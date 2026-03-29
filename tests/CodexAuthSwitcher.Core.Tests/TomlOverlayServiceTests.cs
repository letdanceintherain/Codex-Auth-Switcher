using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class TomlOverlayServiceTests
{
    [Fact]
    public void ApplyApiOverlay_PreservesExistingSectionsAndWritesRequiredKeys()
    {
        var input = """
                    model = "gpt-5.4"
                    model_reasoning_effort = "xhigh"

                    [model_providers.openai]
                    base_url = "https://stale.example"

                    [windows]
                    sandbox = "elevated"
                    """;

        var spec = new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            DisableResponseStorage = false,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-123"
        };

        var output = TomlOverlayService.ApplyApiOverlay(input, spec);

        Assert.Contains("model_provider = \"openai\"", output);
        Assert.Contains("preferred_auth_method = \"apikey\"", output);
        Assert.Contains("disable_response_storage = false", output);
        Assert.Contains("[windows]", output);
        Assert.Contains("sandbox = \"elevated\"", output);
        Assert.DoesNotContain("[model_providers.openai]", output);
        Assert.DoesNotContain("base_url =", output);
        Assert.DoesNotContain("stale.example", output);
    }

    [Fact]
    public void ApplyApiOverlay_UsesRealProviderWhenCompatibilityModeIsDisabled()
    {
        var input = """
                    model = "gpt-5.4"

                    [model_providers.openai]
                    base_url = "https://stale.example"
                    """ + Environment.NewLine;
        var spec = new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            DisableResponseStorage = false,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-123",
            UseOpenAiThreadView = false
        };

        var output = TomlOverlayService.ApplyApiOverlay(input, spec);

        Assert.Contains("model_provider = \"crs\"", output);
        Assert.Contains("[model_providers.crs]", output);
        Assert.DoesNotContain("[model_providers.openai]", output);
        Assert.DoesNotContain("stale.example", output);
    }
}
