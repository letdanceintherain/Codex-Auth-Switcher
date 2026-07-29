namespace CodexAuthSwitcher.Core.Models;

public sealed class ApiProfileSpec
{
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "crs";
    public string BaseUrl { get; set; } = "https://api.funai.vip";
    public string Model { get; set; } = "gpt-5.4";
    public string ReasoningEffort { get; set; } = "xhigh";
    public string WireApi { get; set; } = "responses";
    public bool RequiresOpenAiAuth { get; set; } = true;
    public int ModelAutoCompactTokenLimit { get; set; } = 256000;
    public string ApiKey { get; set; } = string.Empty;
    public bool UseOpenAiThreadView { get; set; } = true;

    public string GetEffectiveProviderId()
    {
        if (UseOpenAiThreadView)
        {
            return "openai";
        }

        return string.IsNullOrWhiteSpace(Provider) ? "crs" : Provider.Trim();
    }

    public string GetDisplayProvider()
    {
        return string.IsNullOrWhiteSpace(Provider) ? "crs" : Provider.Trim();
    }

    public ApiProfileSpec Clone()
    {
        return (ApiProfileSpec)MemberwiseClone();
    }
}
