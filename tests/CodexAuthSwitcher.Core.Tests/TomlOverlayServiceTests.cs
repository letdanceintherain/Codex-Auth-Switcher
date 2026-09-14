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

        var spec = CreateSpec();
        spec.Provider = "openai";
        var output = TomlOverlayService.ApplyApiOverlay(input, spec);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyApiOverlay_UsesEnteredProviderRegardlessOfLegacyFlag(bool legacyFlag)
    {
        var input = """
            model = "gpt-old"
            openai_base_url = "https://stale.example"

            [mcp_servers.keep]
            url = "https://example.test/mcp"
            """;
        var spec = CreateSpec();
        spec.UseOpenAiThreadView = legacyFlag;

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

    [Theory]
    [InlineData("my.provider")]
    [InlineData("模型商 A")]
    [InlineData("provider-$1")]
    public void ProviderName_WithQuotedKeys_RoundTrips(string provider)
    {
        var spec = CreateSpec();
        spec.Provider = provider;
        var output = TomlOverlayService.ApplyApiOverlay("", spec);
        Assert.Equal(provider, TomlOverlayService.TryReadScalar(output, "model_provider"));
        Assert.Equal(provider, TomlOverlayService.TryReadSectionScalar(output, TomlOverlayService.ProviderSection(provider), "name"));
        Assert.Equal(output, TomlOverlayService.ApplyApiOverlay(output, spec));
    }

    [Fact]
    public void MultilineInstructionsAndQuotedProviderTable_ArePreserved()
    {
        var input = "\"\"\"";
        var instructions = "developer_instructions = " + input + "\n[not_a_table]\nmodel = 'do not change'\n\n\n" + input + "\n";
        var config = instructions + """
            "model_provider" = 'crs' # selected
            [model_providers."crs"] # keep comment
            base_url = 'http://old.invalid'
            request_max_retries = 3
            env_key = 'OLD_KEY'
            [model_providers.crs.http_headers]
            keep = "header"
            [model_providers.crs.auth]
            command = "old-token-command"
            """;
        var output = TomlOverlayService.ApplyApiOverlay(config, CreateSpec());
        Assert.Contains(instructions, output);
        Assert.Contains("request_max_retries = 3", output);
        Assert.Contains("keep = \"header\"", output);
        Assert.DoesNotContain("OLD_KEY", output);
        Assert.DoesNotContain("old-token-command", output);
        Assert.Equal("crs", TomlOverlayService.TryReadScalar(output, "model_provider"));
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
