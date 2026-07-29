using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class TomlOverlayServiceTests
{
    [Fact]
    public void ApplyApiOverlay_UsesCurrentOpenAiBaseUrlAndPreservesExistingSections()
    {
        var input = """
            model = "gpt-old"
            preferred_auth_method = "chatgpt"
            disable_response_storage = true

            [model_providers.openai]
            base_url = "https://stale.example"

            [windows]
            sandbox = "elevated"
            """;

        var output = TomlOverlayService.ApplyApiOverlay(input, CreateSpec());

        Assert.Contains("model_provider = \"openai\"", output);
        Assert.Contains("openai_base_url = \"https://api.funai.vip\"", output);
        Assert.Contains("model_auto_compact_token_limit = 256000", output);
        Assert.DoesNotContain("preferred_auth_method", output);
        Assert.DoesNotContain("disable_response_storage", output);
        Assert.DoesNotContain("[model_providers.openai]", output);
        Assert.Contains("[windows]", output);
        Assert.Contains("sandbox = \"elevated\"", output);
        TomlOverlayService.Validate(output);
    }

    [Fact]
    public void ApplyApiOverlay_UsesCustomProviderWhenCompatibilityModeIsDisabled()
    {
        var input = """
            model = "gpt-old"
            openai_base_url = "https://stale.example"

            [mcp_servers.keep]
            url = "https://example.test/mcp"
            """;
        var spec = CreateSpec();
        spec.UseOpenAiThreadView = false;

        var output = TomlOverlayService.ApplyApiOverlay(input, spec);

        Assert.Contains("model_provider = \"crs\"", output);
        Assert.Contains("[model_providers.crs]", output);
        Assert.Contains("name = \"crs\"", output);
        Assert.Contains("base_url = \"https://api.funai.vip\"", output);
        Assert.Contains("wire_api = \"responses\"", output);
        Assert.Contains("requires_openai_auth = true", output);
        Assert.DoesNotContain("openai_base_url", output);
        Assert.DoesNotContain("stale.example", output);
        Assert.Contains("[mcp_servers.keep]", output);
        TomlOverlayService.Validate(output);
    }

    [Fact]
    public void ApplyChatGptOverlay_RestoresOnlyManagedKeys()
    {
        var current = """
            model_provider = "openai"
            model = "api-model"
            openai_base_url = "https://api.example"

            [mcp_servers.current]
            url = "https://current.example/mcp"
            """;
        var snapshot = """
            model_provider = "openai"
            model = "chatgpt-model"

            [mcp_servers.old]
            url = "https://old.example/mcp"
            """;

        var output = TomlOverlayService.ApplyChatGptOverlay(current, snapshot);

        Assert.Contains("model = \"chatgpt-model\"", output);
        Assert.DoesNotContain("openai_base_url", output);
        Assert.Contains("[mcp_servers.current]", output);
        Assert.DoesNotContain("[mcp_servers.old]", output);
        TomlOverlayService.Validate(output);
    }

    [Fact]
    public void TopLevelScalarOperations_DoNotRewriteSectionKeys()
    {
        var input = """
            [example]
            model = "section-model"
            """;

        var output = TomlOverlayService.SetScalar(input, "model", "top-level-model");

        Assert.Equal("top-level-model", TomlOverlayService.TryReadScalar(output, "model"));
        Assert.Equal("section-model", TomlOverlayService.TryReadSectionScalar(output, "example", "model"));
    }

    [Fact]
    public void ExtractManagedOverlay_ExcludesUnrelatedConfiguration()
    {
        var input = """
            model_provider = "openai"
            model = "gpt-5.4"

            [mcp_servers.private]
            url = "https://private.example/mcp"
            """;

        var overlay = TomlOverlayService.ExtractManagedOverlay(input);

        Assert.Contains("model_provider = \"openai\"", overlay);
        Assert.Contains("model = \"gpt-5.4\"", overlay);
        Assert.DoesNotContain("mcp_servers", overlay);
        Assert.DoesNotContain("private.example", overlay);
    }

    private static ApiProfileSpec CreateSpec()
    {
        return new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip/",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-123"
        };
    }
}
