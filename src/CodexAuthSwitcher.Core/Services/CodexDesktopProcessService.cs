using System.Diagnostics;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.Core.Services;

public sealed class CodexDesktopProcessService
{
    private const string AppId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly string[] DesktopProcessNames = ["ChatGPT", "Codex"];

    public DesktopAppState GetDesktopAppState()
    {
        var processes = GetDesktopProcesses();
        return new DesktopAppState
        {
            IsRunning = processes.Count > 0,
            DesktopAppPath = processes
                .OrderByDescending(process => GetMainWindowHandleSafe(process) != nint.Zero)
                .Select(GetProcessPathSafe)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            ProcessIds = processes.Select(process => process.Id).ToArray()
        };
    }

    public bool CloseDesktopApp(TimeSpan timeout)
    {
        var processes = GetDesktopProcesses();
        if (processes.Count == 0)
        {
            return true;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetDesktopProcesses().Count == 0)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        foreach (var process in GetDesktopProcesses())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)Math.Max(500, timeout.TotalMilliseconds));
                }
            }
            catch
            {
            }
        }

        return GetDesktopProcesses().Count == 0;
    }

    public RestartMethod RestartDesktopApp(string? lastKnownDesktopAppPath)
    {
        if (GetDesktopProcesses().Count > 0)
        {
            return RestartMethod.None;
        }

        foreach (var candidate in BuildLaunchCandidates(TryGetWindowsAppsExecutablePath(), lastKnownDesktopAppPath))
        {
            if (TryLaunchCandidate(candidate) && WaitForDesktopApp(TimeSpan.FromSeconds(12)))
            {
                return candidate.Method;
            }
        }

        return RestartMethod.None;
    }

    public IReadOnlyList<(RestartMethod Method, string Target)> BuildLaunchCandidates(string? windowsAppsPath, string? lastKnownDesktopAppPath)
    {
        var candidates = new List<(RestartMethod Method, string Target)>();
        void Add(RestartMethod method, string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            if (candidates.Any(item => string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add((method, target));
        }

        Add(RestartMethod.AppId, AppId);
        Add(RestartMethod.WindowsAppsPath, windowsAppsPath);
        Add(RestartMethod.LastKnownDesktopPath, lastKnownDesktopAppPath);
        return candidates;
    }

    public static bool IsDesktopCodexPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var isPackageExecutable = path.EndsWith(@"\app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                                  || path.EndsWith(@"\app\Codex.exe", StringComparison.OrdinalIgnoreCase);
        return isPackageExecutable
               && path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Process> GetDesktopProcesses()
    {
        return DesktopProcessNames
            .SelectMany(Process.GetProcessesByName)
            .Where(process => IsDesktopCodexPath(GetProcessPathSafe(process)))
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static nint GetMainWindowHandleSafe(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch
        {
            return nint.Zero;
        }
    }

    private static string? GetProcessPathSafe(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWindowsAppsExecutablePath()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"$pkg = Get-AppxPackage OpenAI.Codex | Select-Object -First 1 -ExpandProperty InstallLocation; if ($pkg) { $current = Join-Path $pkg 'app\\\\ChatGPT.exe'; $legacy = Join-Path $pkg 'app\\\\Codex.exe'; if (Test-Path -LiteralPath $current) { $current } elseif (Test-Path -LiteralPath $legacy) { $legacy } }\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return File.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryLaunchCandidate((RestartMethod Method, string Target) candidate)
    {
        try
        {
            if (candidate.Method == RestartMethod.AppId)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $@"shell:AppsFolder\{candidate.Target}",
                    UseShellExecute = true
                });
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = candidate.Target,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(candidate.Target) ?? Environment.CurrentDirectory
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForDesktopApp(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetDesktopProcesses().Count > 0)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }
}
