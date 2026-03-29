using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class CodexDesktopProcessServiceTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.325.2171.0_x64__2p2nqsd0c76g0\app\Codex.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.325.2171.0_x64__2p2nqsd0c76g0\app\resources\codex.exe", false)]
    [InlineData(@"C:\Users\me\.vscode\extensions\openai.chatgpt\bin\windows-x86_64\codex.exe", false)]
    public void IsDesktopCodexPath_ClassifiesExpectedTargets(string path, bool expected)
    {
        Assert.Equal(expected, CodexDesktopProcessService.IsDesktopCodexPath(path));
    }

    [Fact]
    public void BuildLaunchCandidates_UsesWindowsAppsPathFirst_ThenLastKnown_ThenAppId()
    {
        var service = new CodexDesktopProcessService();
        var candidates = service.BuildLaunchCandidates(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_foo\app\Codex.exe",
            @"D:\Saved\Codex.exe");

        Assert.Collection(candidates,
            item =>
            {
                Assert.Equal(RestartMethod.WindowsAppsPath, item.Method);
                Assert.Equal(@"C:\Program Files\WindowsApps\OpenAI.Codex_foo\app\Codex.exe", item.Target);
            },
            item =>
            {
                Assert.Equal(RestartMethod.LastKnownDesktopPath, item.Method);
                Assert.Equal(@"D:\Saved\Codex.exe", item.Target);
            },
            item =>
            {
                Assert.Equal(RestartMethod.AppId, item.Method);
                Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", item.Target);
            });
    }
}
